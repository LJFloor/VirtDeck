using System.Buffers.Binary;

namespace VirtDeck.Agent.X11
{
    /// <summary>
    /// The pointer and the keyboard, injected with XTEST.
    ///
    /// <para><b>The keyboard is the hard half.</b> RFB carries keysyms ("the user typed @") and XTEST
    /// takes keycodes ("press the key at position 11"), so every character has to be looked up in the
    /// host's live keymap: which key, at which shift level, with which modifiers held. The core
    /// protocol's <c>GetKeyboardMapping</c> and <c>GetModifierMapping</c> carry all of it, so the XKB
    /// extension is never touched.</para>
    ///
    /// <para><b>Modifiers are a delta, not a state.</b> The client sends modifiers as keys of their
    /// own, so when the user holds Shift the host already has Shift down. Before typing a character
    /// the agent asks the server what is held (QueryPointer answers with the live modifier mask),
    /// changes only what is wrong, types, and puts back exactly what it changed. A modifier the user
    /// is really holding is never touched.</para>
    ///
    /// <para><b>Keysyms the layout does not have</b> (an accented letter on a US keyboard, say) are
    /// typed by binding a spare keycode to them, pressing it and unbinding it, which is what x11vnc's
    /// <c>-add_keysyms</c> does. Nothing is ever left bound: every exit path puts the keymap back.
    /// </para>
    ///
    /// <para><b>Auto-repeat is turned off</b> while a viewer is connected and restored on the way
    /// out. The client already sends a held key as repeated key downs, so leaving the server's own
    /// repeat on would double every one of them. x11vnc does the same by default.</para>
    /// </summary>
    internal sealed class XInput : IDisposable
    {
        private const byte KeyPress = 2, KeyRelease = 3, ButtonPress = 4, ButtonRelease = 5, MotionNotify = 6;

        private const uint XkShiftLock = 0xFFE6;
        private const uint XkLevel3 = 0xFE03, XkModeSwitch = 0xFF7E;

        /// <summary>Shift is modifier 0 and Lock modifier 1; Mod1 to Mod5 are 3 to 7 and are named by what is bound to them.</summary>
        private const byte ShiftIndex = 0, LockIndex = 1;

        private readonly XConnection _x;
        private readonly XExtensions _extensions;
        private readonly uint _root;

        private byte _minKeycode, _maxKeycode, _perKeycode;
        private uint[] _keymap = [];            // (keycode - min) * _perKeycode + level
        private byte[] _modifiers = [];         // 8 modifiers * _perModifier keycodes
        private byte _perModifier;
        private byte _level3ModMask;
        private byte _lockIsShiftLock;          // Lock holds Shift_Lock rather than Caps_Lock

        private readonly Dictionary<uint, byte> _pressed = [];    // keysym -> the keycode we pressed for it
        private readonly Dictionary<uint, byte> _borrowed = [];   // keysym -> a spare keycode bound to it
        private byte _buttons;
        private bool _autoRepeatWasOn;
        private int _ownMappingChanges;
        private int _keymapStale;

        public XInput(XConnection x, XExtensions extensions)
        {
            _x = x;
            _extensions = extensions;
            _root = x.Setup.Root;
            _minKeycode = x.Setup.MinKeycode;
            _maxKeycode = x.Setup.MaxKeycode;
            ReadKeymap();
            SetAutoRepeat(false);
        }

        /// <summary>
        /// Called from the connection's reader thread: somebody changed the keymap under us.
        ///
        /// <para><b>It only marks it stale.</b> Reading the keymap means asking the X server, and an
        /// event handler runs on the very thread that would have to read the answer, so doing it here
        /// waits for ever for a reply nobody is left to collect. The re-read happens in
        /// <see cref="Key"/>, on the thread that uses the map, which also keeps the map to one
        /// thread.</para>
        /// </summary>
        public void OnEvent(byte[] packet)
        {
            const byte MappingNotify = 34;
            if ((packet[0] & 0x7F) != MappingNotify) return;

            // Every rebind of ours produces one of these. Ours are not news.
            if (Volatile.Read(ref _ownMappingChanges) > 0)
            {
                Interlocked.Decrement(ref _ownMappingChanges);
                return;
            }
            Interlocked.Exchange(ref _keymapStale, 1);
        }

        // ---- The keymap -------------------------------------------------------------------------

        private void ReadKeymap()
        {
            byte count = (byte)(_maxKeycode - _minKeycode + 1);
            Span<byte> request = stackalloc byte[8];
            request[0] = 101; // GetKeyboardMapping
            BinaryPrimitives.WriteUInt16LittleEndian(request[2..], 2);
            request[4] = _minKeycode;
            request[5] = count;
            var reply = _x.Request(request);

            _perKeycode = reply.U8(1);
            var keysyms = new uint[count * _perKeycode];
            for (int i = 0; i < keysyms.Length && i * 4 + 4 <= reply.ExtraLength; i++)
                keysyms[i] = BinaryPrimitives.ReadUInt32LittleEndian(reply.Extra.AsSpan(i * 4));
            _keymap = keysyms;

            Span<byte> modifiers = stackalloc byte[4];
            modifiers[0] = 119; // GetModifierMapping
            BinaryPrimitives.WriteUInt16LittleEndian(modifiers[2..], 1);
            var modReply = _x.Request(modifiers);
            _perModifier = modReply.U8(1);
            _modifiers = modReply.Extra.AsSpan(0, Math.Min(modReply.ExtraLength, 8 * _perModifier)).ToArray();

            // Which of the eight modifiers carries Shift, which carries Caps Lock, and which carries
            // the third level (AltGr). Mod1 to Mod5 are named by whatever keysym is bound to them, so
            // this is a search rather than a constant.
            _level3ModMask = 0;
            _lockIsShiftLock = 0;
            for (int modifier = 0; modifier < 8 && _modifiers.Length >= 8 * _perModifier; modifier++)
            {
                for (int slot = 0; slot < _perModifier; slot++)
                {
                    byte keycode = _modifiers[modifier * _perModifier + slot];
                    if (keycode == 0) continue;
                    for (int level = 0; level < _perKeycode; level++)
                    {
                        var keysym = KeysymAt(keycode, level);
                        if (keysym is XkLevel3 or XkModeSwitch) _level3ModMask |= (byte)(1 << modifier);
                        if (keysym == XkShiftLock && modifier == LockIndex) _lockIsShiftLock = 1;
                    }
                }
            }
        }

        private uint KeysymAt(byte keycode, int level)
        {
            if (keycode < _minKeycode || keycode > _maxKeycode) return 0;
            int at = (keycode - _minKeycode) * _perKeycode + level;
            return at >= 0 && at < _keymap.Length ? _keymap[at] : 0;
        }

        /// <summary>
        /// The key and shift level that produce this keysym, or null when the layout does not have it.
        /// Only levels 0 to 3 are offered: those are "as is", "with Shift", "with AltGr" and "with
        /// both", which are the ones reachable with the core modifiers. A keysym that lives higher up
        /// (a third or fourth keyboard group) is treated as absent and typed on a spare keycode,
        /// which always works.
        /// </summary>
        private (byte Keycode, int Level)? Find(uint keysym)
        {
            for (int level = 0; level < 4; level++)
                for (int keycode = _minKeycode; keycode <= _maxKeycode; keycode++)
                    if (KeysymAt((byte)keycode, level) == keysym)
                        return ((byte)keycode, level);
            return null;
        }

        private byte KeycodeFor(byte modifierIndex)
        {
            for (int slot = 0; slot < _perModifier; slot++)
            {
                int at = modifierIndex * _perModifier + slot;
                if (at < _modifiers.Length && _modifiers[at] != 0) return _modifiers[at];
            }
            return 0;
        }

        // ---- Keys -------------------------------------------------------------------------------

        /// <summary>A keysym from the viewer, going down or up.</summary>
        public void Key(uint keysym, bool down)
        {
            if (keysym == 0) return;
            try
            {
                if (Interlocked.Exchange(ref _keymapStale, 0) != 0) ReadKeymap();
                if (down) KeyDown(keysym); else KeyUp(keysym);
            }
            catch (XProtocolException ex)
            {
                Console.Error.WriteLine($"virtdeck-agent: cannot type keysym 0x{keysym:x}: {ex.Message}");
            }
        }

        private void KeyDown(uint keysym)
        {
            var found = Find(keysym);
            if (found is null)
            {
                var borrowed = Borrow(keysym);
                if (borrowed == 0) return;
                Fake(KeyPress, borrowed);
                _pressed[keysym] = borrowed;
                return;
            }

            var (keycode, level) = found.Value;
            bool wantShift = (level & 1) != 0;
            bool wantLevel3 = level >= 2;

            ushort live = PointerMask();

            // Caps Lock (or Shift Lock) inverts the shift level of a letter, so what we need to hold
            // to get the character the viewer asked for inverts with it.
            if ((live & (1 << LockIndex)) != 0 && (_lockIsShiftLock != 0 || IsCased(keysym)))
                wantShift = !wantShift;

            byte addedShift = 0, releasedShift = 0, addedLevel3 = 0, releasedLevel3 = 0;
            ChangeModifier(ShiftIndex, wantShift, live, ref addedShift, ref releasedShift);
            if (_level3ModMask != 0)
            {
                byte index = (byte)System.Numerics.BitOperations.TrailingZeroCount(_level3ModMask);
                ChangeModifier(index, wantLevel3, live, ref addedLevel3, ref releasedLevel3);
            }

            Fake(KeyPress, keycode);
            _pressed[keysym] = keycode;

            // Put back exactly what was changed, in reverse. Anything the viewer is really holding was
            // never touched, so nothing of theirs is disturbed.
            if (addedLevel3 != 0) Fake(KeyRelease, addedLevel3);
            if (releasedLevel3 != 0) Fake(KeyPress, releasedLevel3);
            if (addedShift != 0) Fake(KeyRelease, addedShift);
            if (releasedShift != 0) Fake(KeyPress, releasedShift);
        }

        /// <summary>
        /// Holds or lets go of one modifier, but only when it is not already the way we need it, and
        /// says which keycode it touched so the caller can put it back.
        /// </summary>
        private void ChangeModifier(byte index, bool wanted, ushort live, ref byte added, ref byte released)
        {
            bool held = (live & (1 << index)) != 0;
            if (wanted == held) return;
            byte keycode = KeycodeFor(index);
            if (keycode == 0) return;
            if (wanted) { Fake(KeyPress, keycode); added = keycode; }
            else { Fake(KeyRelease, keycode); released = keycode; }
        }

        private void KeyUp(uint keysym)
        {
            if (!_pressed.Remove(keysym, out var keycode))
            {
                var found = Find(keysym);
                if (found is null) return;
                keycode = found.Value.Keycode;
            }
            Fake(KeyRelease, keycode);
            Return(keysym, keycode);
        }

        /// <summary>Every key still down, let go of. Called when the viewer leaves, however it leaves.</summary>
        public void ReleaseEverything()
        {
            foreach (var (keysym, keycode) in _pressed.ToArray())
            {
                try
                {
                    Fake(KeyRelease, keycode);
                    Return(keysym, keycode);
                }
                catch (Exception) { /* going down anyway */ }
            }
            _pressed.Clear();

            for (int button = 1; button <= 7; button++)
                if ((_buttons & (1 << (button - 1))) != 0)
                    try { Fake(ButtonRelease, (byte)button); } catch (Exception) { /* going down anyway */ }
            _buttons = 0;
        }

        // ---- Spare keycodes ---------------------------------------------------------------------

        /// <summary>
        /// Binds a keysym the layout does not have to an unused keycode. Every level of that keycode
        /// gets the same keysym, so whatever modifiers happen to be held, the key types what was asked
        /// for.
        /// </summary>
        private byte Borrow(uint keysym)
        {
            if (_borrowed.TryGetValue(keysym, out var already)) return already;

            byte spare = 0;
            for (int keycode = _maxKeycode; keycode >= _minKeycode && spare == 0; keycode--)
            {
                bool empty = true;
                for (int level = 0; level < _perKeycode && empty; level++)
                    empty = KeysymAt((byte)keycode, level) == 0;
                if (!empty || _borrowed.ContainsValue((byte)keycode)) continue;
                if (IsModifier((byte)keycode)) continue;
                spare = (byte)keycode;
            }

            if (spare == 0)
            {
                Console.Error.WriteLine($"virtdeck-agent: cannot type keysym 0x{keysym:x}: " +
                                        "the host's keyboard has no free key to borrow for it.");
                return 0;
            }

            Rebind(spare, keysym);
            _borrowed[keysym] = spare;
            return spare;
        }

        /// <summary>Gives a borrowed keycode back, so the host's keyboard is as it was.</summary>
        private void Return(uint keysym, byte keycode)
        {
            if (!_borrowed.TryGetValue(keysym, out var borrowed) || borrowed != keycode) return;
            _borrowed.Remove(keysym);
            Rebind(keycode, 0);
        }

        private void Rebind(byte keycode, uint keysym)
        {
            var request = new byte[8 + _perKeycode * 4];
            request[0] = 100; // ChangeKeyboardMapping
            request[1] = 1;   // one keycode
            BinaryPrimitives.WriteUInt16LittleEndian(request.AsSpan(2), (ushort)(request.Length / 4));
            request[4] = keycode;
            request[5] = _perKeycode;
            for (int level = 0; level < _perKeycode; level++)
                BinaryPrimitives.WriteUInt32LittleEndian(request.AsSpan(8 + level * 4), keysym);

            Interlocked.Increment(ref _ownMappingChanges);
            _x.Send(request);

            int at = (keycode - _minKeycode) * _perKeycode;
            for (int level = 0; level < _perKeycode && at + level < _keymap.Length; level++)
                _keymap[at + level] = keysym;
        }

        private bool IsModifier(byte keycode) => Array.IndexOf(_modifiers, keycode) >= 0;

        private static bool IsCased(uint keysym) =>
            keysym is >= 'a' and <= 'z' or >= 'A' and <= 'Z' ||
            keysym is >= 0xC0 and <= 0xFF and not 0xD7 and not 0xF7;

        // ---- Pointer ----------------------------------------------------------------------------

        /// <summary>
        /// The pointer is here with these buttons held. RFB has no press and release, only a mask, so
        /// what changed is what is sent: bit 0 is the left button, 1 the middle, 2 the right, and 3 to
        /// 6 are the wheel, which X treats as buttons 4 to 7.
        /// </summary>
        public void Pointer(int x, int y, byte buttons)
        {
            try
            {
                Span<byte> request = stackalloc byte[36];
                request[0] = _extensions.Test.Major;
                request[1] = 2; // FakeInput
                BinaryPrimitives.WriteUInt16LittleEndian(request[2..], 9);
                request[4] = MotionNotify;
                request[5] = 0; // absolute
                BinaryPrimitives.WriteUInt32LittleEndian(request[12..], _root);
                BinaryPrimitives.WriteInt16LittleEndian(request[24..], (short)x);
                BinaryPrimitives.WriteInt16LittleEndian(request[26..], (short)y);
                _x.Send(request);

                byte changed = (byte)(buttons ^ _buttons);
                for (int button = 1; button <= 7; button++)
                {
                    byte bit = (byte)(1 << (button - 1));
                    if ((changed & bit) == 0) continue;
                    Fake((buttons & bit) != 0 ? ButtonPress : ButtonRelease, (byte)button);
                }
                _buttons = buttons;
            }
            catch (XProtocolException ex)
            {
                Console.Error.WriteLine($"virtdeck-agent: cannot move the pointer: {ex.Message}");
            }
        }

        private ushort PointerMask()
        {
            Span<byte> request = stackalloc byte[8];
            request[0] = 38; // QueryPointer
            BinaryPrimitives.WriteUInt16LittleEndian(request[2..], 2);
            BinaryPrimitives.WriteUInt32LittleEndian(request[4..], _root);
            return _x.Request(request).U16(24);
        }

        private void Fake(byte type, byte detail)
        {
            Span<byte> request = stackalloc byte[36];
            request[0] = _extensions.Test.Major;
            request[1] = 2; // FakeInput
            BinaryPrimitives.WriteUInt16LittleEndian(request[2..], 9);
            request[4] = type;
            request[5] = detail;
            BinaryPrimitives.WriteUInt32LittleEndian(request[12..], _root);
            _x.Send(request);
        }

        // ---- Auto-repeat ------------------------------------------------------------------------

        private void SetAutoRepeat(bool on)
        {
            const uint AutoRepeatMode = 1 << 7;
            if (!on)
            {
                Span<byte> query = stackalloc byte[4];
                query[0] = 103; // GetKeyboardControl
                BinaryPrimitives.WriteUInt16LittleEndian(query[2..], 1);
                _autoRepeatWasOn = _x.Request(query).U8(1) != 0;
                if (!_autoRepeatWasOn) return;
            }

            Span<byte> request = stackalloc byte[12];
            request[0] = 102; // ChangeKeyboardControl
            BinaryPrimitives.WriteUInt16LittleEndian(request[2..], 3);
            BinaryPrimitives.WriteUInt32LittleEndian(request[4..], AutoRepeatMode);
            BinaryPrimitives.WriteUInt32LittleEndian(request[8..], on ? 1u : 0u);
            _x.Send(request);
        }

        public void Dispose()
        {
            try { ReleaseEverything(); } catch (Exception) { /* the display may be gone */ }
            foreach (var (keysym, keycode) in _borrowed.ToArray())
            {
                _ = keysym;
                try { Rebind(keycode, 0); } catch (Exception) { /* the display may be gone */ }
            }
            _borrowed.Clear();
            if (_autoRepeatWasOn)
                try { SetAutoRepeat(true); } catch (Exception) { /* the display may be gone */ }
        }
    }
}
