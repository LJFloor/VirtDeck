using System.Drawing;
using System.Windows.Forms;
using SpiceClient;
using SpiceClient.Imaging;
using SpiceClient.Protocol;
using VmManager.Interop;

namespace VmManager.Controls
{
    /// <summary>
    /// Renders the SPICE framebuffer and forwards mouse input. Owns ALL cursor
    /// assignment so exactly one cursor is ever visible over the display:
    /// it replaces <see cref="Control.Cursor"/> (the OS never stacks cursors).
    /// </summary>
    public sealed class SpiceDisplayControl : Control
    {
        public enum CursorPolicy { SpiceCursor, HostCursor }

        private SpiceSession? _session;
        private SpiceFramebuffer? _fb;
        private readonly System.Windows.Forms.Timer _repaintTimer;
        private volatile bool _frameDirty;

        // Cursor state machine
        private CursorPolicy _policy = CursorPolicy.SpiceCursor;
        private bool _haveCursorState;
        private bool _spiceVisible;
        private Cursor? _spiceCursor;
        private IntPtr _spiceHIcon;
        private Cursor? _blankCursor;   // transparent cursor for the "guest hid cursor" state
        private IntPtr _blankHIcon;

        private ushort _buttonsState;

        /// <summary>Raised (on the UI thread) when the guest resolution changes.</summary>
        public event Action<int, int>? ResolutionChanged;

        public SpiceDisplayControl()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                     ControlStyles.OptimizedDoubleBuffer | ControlStyles.Selectable, true);
            BackColor = Color.Black;
            TabStop = true;
            AllowDrop = true;

            _repaintTimer = new System.Windows.Forms.Timer { Interval = 16 };
            _repaintTimer.Tick += RepaintTick;
            _repaintTimer.Start();
        }

        public void Attach(SpiceSession session)
        {
            _session = session;
            session.FrameDirty += OnFrameDirty;
            session.ResolutionChanged += OnResolutionChanged;
            session.CursorSet += OnCursorSet;
            session.CursorHidden += OnCursorHidden;
            session.CursorReset += OnCursorReset;
        }

        public CursorPolicy Policy
        {
            get => _policy;
            set { _policy = value; ApplyCursor(); }
        }

        // ---- Frame rendering ----------------------------------------------

        private void OnFrameDirty() => _frameDirty = true;

        private void RepaintTick(object? sender, EventArgs e)
        {
            if (_fb == null || !_frameDirty) return;
            _frameDirty = false;
            Rectangle dirty;
            lock (_fb.SyncRoot)
            {
                if (!_fb.TakeDirty(out dirty)) return;
            }
            if (dirty.Width > 0 && dirty.Height > 0)
                Invalidate(dirty);
        }

        private void OnResolutionChanged(int w, int h)
        {
            RunUI(() =>
            {
                var prev = _fb;
                _fb = _session?.Framebuffer;
                if (prev != null && !ReferenceEquals(prev, _fb))
                    prev.Dispose(); // safe: we're on the UI thread, no paint in flight
                ResolutionChanged?.Invoke(w, h);
                Invalidate();
            });
        }

        /// <summary>Stops referencing the framebuffer (UI thread) before the session disposes it.</summary>
        public void ClearFramebuffer()
        {
            _fb = null;
            if (IsHandleCreated && !IsDisposed) Invalidate();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var fb = _fb;
            if (fb == null)
            {
                e.Graphics.Clear(Color.Black);
                return;
            }
            try
            {
                var rect = new Rectangle(0, 0, fb.Width, fb.Height);
                lock (fb.SyncRoot)
                {
                    // Explicit pixel rect → 1:1 blit independent of the bitmap's DPI metadata.
                    e.Graphics.DrawImage(fb.Bitmap, rect, rect, GraphicsUnit.Pixel);
                }
            }
            catch (Exception)
            {
                // Framebuffer was disposed mid-teardown; fall back to black.
                e.Graphics.Clear(Color.Black);
                return;
            }
            // Black-fill any control area beyond the framebuffer.
            if (Width > fb.Width)
                e.Graphics.FillRectangle(Brushes.Black, fb.Width, 0, Width - fb.Width, Height);
            if (Height > fb.Height)
                e.Graphics.FillRectangle(Brushes.Black, 0, fb.Height, Width, Height - fb.Height);
        }

        protected override void OnPaintBackground(PaintEventArgs pevent)
        {
            // Painted in OnPaint; avoid flicker.
        }

        // ---- Mouse --------------------------------------------------------

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            Focus();
            byte button = MapButton(e.Button);
            if (button == 0) return;
            _buttonsState |= MaskFor(button);
            _session?.Inputs?.SendMousePress(button, _buttonsState);
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            byte button = MapButton(e.Button);
            if (button == 0) return;
            _buttonsState &= unchecked((ushort)~MaskFor(button));
            _session?.Inputs?.SendMouseRelease(button, _buttonsState);
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            var fb = _fb;
            if (fb == null) return;
            int x = Math.Clamp(e.X, 0, fb.Width - 1);
            int y = Math.Clamp(e.Y, 0, fb.Height - 1);
            _session?.Inputs?.SendMouseMove(x, y, _buttonsState);
        }

        protected override void OnMouseWheel(MouseEventArgs e)
        {
            base.OnMouseWheel(e);
            byte button = e.Delta > 0 ? SpiceConstants.MOUSE_BUTTON_UP : SpiceConstants.MOUSE_BUTTON_DOWN;
            _session?.Inputs?.SendMousePress(button, _buttonsState);
            _session?.Inputs?.SendMouseRelease(button, _buttonsState);
        }

        // ---- File drag & drop (client -> guest) ---------------------------

        protected override void OnDragEnter(DragEventArgs e)
        {
            base.OnDragEnter(e);
            bool ok = e.Data?.GetDataPresent(DataFormats.FileDrop) == true
                      && _session?.AgentConnected == true;
            e.Effect = ok ? DragDropEffects.Copy : DragDropEffects.None;
        }

        protected override void OnDragOver(DragEventArgs e)
        {
            base.OnDragOver(e);
            bool ok = e.Data?.GetDataPresent(DataFormats.FileDrop) == true
                      && _session?.AgentConnected == true;
            e.Effect = ok ? DragDropEffects.Copy : DragDropEffects.None;
        }

        protected override void OnDragDrop(DragEventArgs e)
        {
            base.OnDragDrop(e);
            if (_session == null) return;
            if (e.Data?.GetData(DataFormats.FileDrop) is not string[] paths) return;
            foreach (var p in paths)
            {
                try { if (System.IO.Directory.Exists(p)) continue; } catch { continue; }
                _session.SendFile(p);
            }
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            base.OnMouseLeave(e);
            // Release any held buttons so they don't stick in the guest.
            if (_buttonsState != 0)
            {
                _buttonsState = 0;
                _session?.Inputs?.SendMouseRelease(SpiceConstants.MOUSE_BUTTON_LEFT, 0);
            }
        }

        private static byte MapButton(MouseButtons b) => b switch
        {
            MouseButtons.Left => SpiceConstants.MOUSE_BUTTON_LEFT,
            MouseButtons.Middle => SpiceConstants.MOUSE_BUTTON_MIDDLE,
            MouseButtons.Right => SpiceConstants.MOUSE_BUTTON_RIGHT,
            _ => 0
        };

        private static ushort MaskFor(byte button) => (ushort)(1 << (button - 1));

        // ---- Cursor (exactly one) -----------------------------------------

        private void OnCursorSet(CursorShape shape) => RunUI(() =>
        {
            _haveCursorState = true;
            _spiceVisible = true;
            SetSpiceCursor(shape);
            ApplyCursor();
        });

        private void OnCursorHidden() => RunUI(() =>
        {
            _haveCursorState = true;
            _spiceVisible = false;
            ApplyCursor();
        });

        private void OnCursorReset() => RunUI(() =>
        {
            _haveCursorState = true;
            _spiceVisible = true;
            ClearSpiceCursor(); // reset -> system default
            ApplyCursor();
        });

        private void SetSpiceCursor(CursorShape shape)
        {
            if (CursorInterop.TryCreate(shape, out var cursor, out var hIcon))
            {
                ClearSpiceCursor();
                _spiceCursor = cursor;
                _spiceHIcon = hIcon;
            }
        }

        private void ClearSpiceCursor()
        {
            if (_spiceCursor != null)
            {
                _spiceCursor.Dispose();
                _spiceCursor = null;
            }
            if (_spiceHIcon != IntPtr.Zero)
            {
                CursorInterop.Destroy(_spiceHIcon);
                _spiceHIcon = IntPtr.Zero;
            }
        }

        private void ApplyCursor()
        {
            Cursor c;
            if (_policy == CursorPolicy.HostCursor)
                c = Cursors.Default;            // user override: always the host arrow
            else if (!_haveCursorState)
                c = Cursors.Default;            // startup: never a missing cursor
            else if (!_spiceVisible)
                c = BlankCursor();              // guest hid the cursor → show nothing over the display
            else
                c = _spiceCursor ?? Cursors.Default;
            Cursor = c;
        }

        // A fully transparent cursor — WinForms has no Cursors.None, so we build one.
        private Cursor BlankCursor()
        {
            if (_blankCursor == null)
            {
                var shape = new CursorShape(32, 32, 0, 0, new byte[32 * 32 * 4]);
                if (CursorInterop.TryCreate(shape, out var c, out var h))
                {
                    _blankCursor = c;
                    _blankHIcon = h;
                }
                else
                {
                    _blankCursor = Cursors.Default;
                }
            }
            return _blankCursor;
        }

        // ---- Plumbing -----------------------------------------------------

        private void RunUI(Action action)
        {
            if (IsDisposed || Disposing || !IsHandleCreated) return;
            try
            {
                if (InvokeRequired) BeginInvoke(action);
                else action();
            }
            catch (ObjectDisposedException) { /* form closing */ }
            catch (InvalidOperationException) { /* handle gone */ }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _repaintTimer.Stop();
                _repaintTimer.Dispose();
                if (_session != null)
                {
                    _session.FrameDirty -= OnFrameDirty;
                    _session.ResolutionChanged -= OnResolutionChanged;
                    _session.CursorSet -= OnCursorSet;
                    _session.CursorHidden -= OnCursorHidden;
                    _session.CursorReset -= OnCursorReset;
                }
                ClearSpiceCursor();
                if (_blankCursor != null && _blankCursor != Cursors.Default)
                    _blankCursor.Dispose();
                if (_blankHIcon != IntPtr.Zero)
                    CursorInterop.Destroy(_blankHIcon);
            }
            base.Dispose(disposing);
        }
    }
}
