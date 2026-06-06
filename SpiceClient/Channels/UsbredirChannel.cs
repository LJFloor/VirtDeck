using SpiceClient.Interop;
using SpiceClient.Protocol;
using SpiceClient.Usb;

namespace SpiceClient.Channels;

/// <summary>
/// SPICE usbredir channel (type 9) — a spicevmc byte-stream tunnel that carries the raw
/// usbredir wire protocol. One channel exists per host-side &lt;redirdev&gt; and can host a
/// single redirected device at a time.
///
/// On link it creates a persistent <see cref="UsbredirHostInstance"/> with no device (so the
/// usb_redir hello is negotiated immediately); the <see cref="UsbDeviceManager"/> later
/// attaches/detaches a physical device. spice-html5 has no usbredir support, so this has no
/// JS reference — it follows usbredirhost's documented contract.
/// </summary>
public sealed class UsbredirChannel : SpiceChannel
{
    private UsbredirHostInstance? _host;
    private volatile bool _linked;

    public UsbredirChannel(SpiceSession session, string host, int port,
        byte channelId, uint connectionId, string password)
        : base(session, host, port, SpiceConstants.CHANNEL_USBREDIR, channelId, connectionId, password)
    {
    }

    /// <summary>True once the channel handshake completed.</summary>
    public bool Linked => _linked;

    /// <summary>True when the usbredirhost is open and ready to attach a device.</summary>
    internal bool HostReady => _host?.IsOpen ?? false;

    /// <summary>True when a device is currently redirected over this channel.</summary>
    public bool HasDevice => _host?.HasDevice ?? false;

    protected override void OnLinked()
    {
        _linked = true;
        Session.OnUsbChannelLinked(this);
    }

    /// <summary>Creates the usbredirhost for this channel (called by the device manager on link).</summary>
    internal void CreateHost(LibUsbContext usbContext)
    {
        if (_host != null) return;
        _host = new UsbredirHostInstance(
            usbContext,
            data => SendMessage(SpiceConstants.MSGC_SPICEVMC_DATA, data),
            Session.Log,
            () => Session.OnUsbDeviceLost(this));
    }

    /// <summary>Attach a libusb device handle (usbredirhost takes ownership). Returns 0 on success.</summary>
    internal int AttachDevice(IntPtr deviceHandle) =>
        _host?.SetDevice(deviceHandle) ?? UsbRedirHost.READ_IO_ERROR;

    /// <summary>Detach the current device, keeping the channel linked for reuse.</summary>
    internal void DetachDevice() => _host?.DetachDevice();

    protected override void ProcessChannelMessage(ushort type, byte[] payload)
    {
        if (type != SpiceConstants.MSG_SPICEVMC_DATA) return;
        // Hand the bytes to the usbredir worker thread; it owns all usbredirhost interaction.
        _host?.Feed(payload);
    }

    public override void Dispose()
    {
        _linked = false;
        try { _host?.Dispose(); } catch { /* ignore */ }
        _host = null;
        base.Dispose();
    }
}
