using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Threading;
using SpiceClient.Interop;

namespace SpiceClient.Usb;

/// <summary>
/// Managed wrapper around one native usbredirhost instance (one per usbredir SPICE channel).
/// Opened with NO device so the usb_redir hello negotiates at link; a device attaches/detaches
/// via <see cref="SetDevice"/>/<see cref="DetachDevice"/>. usbredirhost owns any device handle
/// and libusb_close()es it.
///
/// THREADING: usbredirhost and libusb are NOT safe to call from several threads at once in the
/// way we need, so everything is driven SINGLE-THREADED on the shared libusb worker thread (the
/// model usbredirserver/spice-gtk use). Other threads only hand work over:
///   • the SPICE channel read thread enqueues incoming bytes (<see cref="Feed"/>) and wakes the worker;
///   • the UI thread requests attach/detach (<see cref="SetDevice"/>/<see cref="DetachDevice"/>) as
///     commands the worker executes, then waits for the result.
/// <see cref="Service"/> runs on the worker thread (registered with <see cref="LibUsbContext"/>) and is
/// the ONLY place usbredirhost read/write/set_device/close are called. The construction-time open
/// runs before the instance is registered, so no other thread touches the host yet.
/// </summary>
internal sealed class UsbredirHostInstance : IDisposable
{
    private readonly UsbRedirHost.LogFunc _log;
    private readonly UsbRedirHost.ReadFunc _read;
    private readonly UsbRedirHost.WriteFunc _write;
    private readonly UsbRedirHost.FlushFunc _flush;

    private readonly Action<byte[]> _send;
    private readonly Action<string> _logSink;
    private readonly Action _onDeviceLost;
    private readonly LibUsbContext _ctx;
    private readonly Action _service;

    private IntPtr _host;
    private volatile bool _closed;

    // Incoming bytes from the SPICE channel thread → consumed on the worker thread.
    private readonly ConcurrentQueue<byte[]> _incoming = new();
    private byte[] _inBuf = Array.Empty<byte>();
    private int _inOff;

    // Commands marshaled to the worker thread.
    private readonly object _cmd = new();
    private bool _attachReq;
    private IntPtr _attachHandle;
    private int _attachResult;
    private readonly ManualResetEventSlim _attachDone = new(false);
    private bool _detachReq;
    private readonly ManualResetEventSlim _detachDone = new(false);
    private bool _closeReq;
    private readonly ManualResetEventSlim _closeDone = new(false);

    public bool HasDevice { get; private set; }
    public bool IsOpen => _host != IntPtr.Zero && !_closed;

    public UsbredirHostInstance(LibUsbContext ctx, Action<byte[]> send, Action<string> log, Action onDeviceLost)
    {
        _ctx = ctx;
        _send = send;
        _logSink = log;
        _onDeviceLost = onDeviceLost;
        _service = Service;

        _log = OnLog;
        _read = OnRead;
        _write = OnWrite;
        _flush = OnFlush;

        // Open BEFORE registering the service, so the worker thread never races construction.
        _host = UsbRedirHost.usbredirhost_open_full(
            ctx.Handle, IntPtr.Zero,
            _log, _read, _write, _flush,
            UsbRedirHost.AllocLock, UsbRedirHost.Lock, UsbRedirHost.Unlock, UsbRedirHost.FreeLock,
            IntPtr.Zero, "SpiceVirtDeck 1.0", UsbRedirHost.LOG_WARNING, 0);

        if (_host == IntPtr.Zero)
        {
            _closed = true;
            _logSink("[usbredir] usbredirhost_open_full failed");
            return;
        }

        ctx.RegisterService(_service);
        ctx.Wake(); // run Service once to flush the hello queued during open
    }

    // ---- Hand-off from other threads ---------------------------------------

    public void Feed(byte[] payload)
    {
        if (_closed) return;
        _incoming.Enqueue(payload);
        _ctx.Wake();
    }

    public int SetDevice(IntPtr deviceHandle)
    {
        if (!IsOpen) return UsbRedirHost.READ_IO_ERROR;
        lock (_cmd)
        {
            _attachHandle = deviceHandle;
            _attachResult = UsbRedirHost.READ_IO_ERROR;
            _attachDone.Reset();
            _attachReq = true;
        }
        _ctx.Wake();
        try { if (!_attachDone.Wait(5000)) return UsbRedirHost.READ_IO_ERROR; }
        catch { return UsbRedirHost.READ_IO_ERROR; }
        return _attachResult;
    }

    public void DetachDevice()
    {
        if (!IsOpen || !HasDevice) return;
        lock (_cmd) { _detachDone.Reset(); _detachReq = true; }
        _ctx.Wake();
        try { _detachDone.Wait(3000); } catch { /* ignore */ }
    }

    // ---- Worker thread (the ONLY caller of usbredirhost read/write/set_device/close) --------

    private void Service()
    {
        if (_host == IntPtr.Zero) return;

        bool attach, detach, close;
        IntPtr handle;
        lock (_cmd)
        {
            attach = _attachReq; _attachReq = false; handle = _attachHandle;
            detach = _detachReq; _detachReq = false;
            close = _closeReq;
        }

        if (detach)
        {
            try
            {
                if (HasDevice) { UsbRedirHost.usbredirhost_set_device(_host, IntPtr.Zero); HasDevice = false; }
            }
            catch (Exception ex) { _logSink($"[usbredir] detach error: {ex.Message}"); }
            DrainWrites();
            _detachDone.Set();
        }

        if (attach)
        {
            try
            {
                _attachResult = UsbRedirHost.usbredirhost_set_device(_host, handle);
                HasDevice = _attachResult == 0;
            }
            catch (Exception ex)
            {
                _attachResult = UsbRedirHost.READ_IO_ERROR;
                _logSink($"[usbredir] attach error: {ex.Message}");
            }
            DrainWrites();
            _attachDone.Set();
        }

        bool lost = false;
        while (_incoming.TryDequeue(out var chunk))
        {
            _inBuf = chunk;
            _inOff = 0;
            int rc;
            try { rc = UsbRedirHost.usbredirhost_read_guest_data(_host); }
            catch (Exception ex) { _logSink($"[usbredir] read error: {ex.Message}"); rc = UsbRedirHost.READ_IO_ERROR; }
            finally { _inBuf = Array.Empty<byte>(); _inOff = 0; }
            if (rc == UsbRedirHost.READ_DEVICE_LOST || rc == UsbRedirHost.READ_DEVICE_REJECTED) { lost = true; break; }
        }

        DrainWrites();

        if (close)
        {
            try { UsbRedirHost.usbredirhost_close(_host); }
            catch (Exception ex) { _logSink($"[usbredir] close error: {ex.Message}"); }
            _host = IntPtr.Zero;
            _closed = true;
            _closeDone.Set();
            return;
        }

        if (lost)
        {
            HasDevice = false;
            try { _onDeviceLost(); } catch { /* ignore */ }
        }
    }

    private void DrainWrites()
    {
        if (_host == IntPtr.Zero) return;
        try
        {
            int guard = 0;
            while (UsbRedirHost.usbredirhost_has_data_to_write(_host) != 0 && guard++ < 8192)
            {
                int rc = UsbRedirHost.usbredirhost_write_guest_data(_host);
                if (rc < 0) { _logSink($"[usbredir] write_guest_data error {rc}"); break; }
            }
        }
        catch (Exception ex) { _logSink($"[usbredir] pump error: {ex.Message}"); }
    }

    // ---- Native callbacks (worker thread only; must never throw into native code) -----------

    private void OnLog(IntPtr priv, int level, IntPtr msg)
    {
        if (level > UsbRedirHost.LOG_WARNING) return;
        try
        {
            var s = msg == IntPtr.Zero ? null : Marshal.PtrToStringAnsi(msg);
            if (!string.IsNullOrEmpty(s)) _logSink($"[usbredir] {s}");
        }
        catch { /* ignore */ }
    }

    private int OnRead(IntPtr priv, IntPtr data, int count)
    {
        try
        {
            int n = _inBuf.Length - _inOff;
            if (n <= 0 || count <= 0) return 0;
            if (n > count) n = count;
            Marshal.Copy(_inBuf, _inOff, data, n);
            _inOff += n;
            return n;
        }
        catch (Exception ex) { _logSink($"[usbredir] read cb error: {ex.Message}"); return -1; }
    }

    private int OnWrite(IntPtr priv, IntPtr data, int count)
    {
        if (count <= 0) return 0;
        try
        {
            var buf = new byte[count];
            Marshal.Copy(data, buf, 0, count);
            _send(buf);
        }
        catch (Exception ex) { _logSink($"[usbredir] send error: {ex.Message}"); }
        return count;
    }

    // No-op: usbredirhost calls flush from inside its own code; draining here would re-enter it.
    private void OnFlush(IntPtr priv) { }

    public void Dispose()
    {
        if (_closed && _host == IntPtr.Zero) { try { _ctx.UnregisterService(_service); } catch { } return; }

        // Marshal the close to the worker thread so usbredirhost stays single-threaded.
        lock (_cmd) { _closeReq = true; _closeDone.Reset(); }
        try { _ctx.Wake(); } catch { /* ctx may be gone */ }
        try { _closeDone.Wait(3000); } catch { /* ignore */ }
        try { _ctx.UnregisterService(_service); } catch { /* ignore */ }
        _closed = true;
    }
}
