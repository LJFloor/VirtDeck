using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace VirtDeck.Secrets
{
    /// <summary>
    /// Linux half of <see cref="SecretStores"/>: the freedesktop Secret Service, reached through
    /// libsecret, which is what gnome-keyring, KWallet and KeePassXC all speak.
    ///
    /// libsecret comes from the distro, like libusb and libpulse, and like them its absence is a
    /// feature that reports what to install rather than an error (see <c>packaging/README.md</c>).
    /// That works precisely <i>because</i> the AppImage bundles nothing but the .NET runtime: if it
    /// ever bundled glib, the distro's libsecret would be loaded against a bundled glib of a
    /// different version, and the result would be a crash rather than a graceful failure.
    ///
    /// The bare versioned soname in <see cref="Secret"/> is the same idiom as
    /// <c>X11KeyboardGrab</c>'s <c>libX11.so.6</c>; there is deliberately no
    /// <c>SetDllImportResolver</c> here, because there can be only one per assembly and mapping two
    /// names that are already exact would buy nothing.
    ///
    /// Nothing in this file puts a secret, or anything derived from one, into a log line: a GError
    /// message names an item and a service, never a value, and <c>SpiceLog</c> writes to the shared
    /// temp directory. That is the trap for anyone adding "just one debug line" here.
    /// </summary>
    [SupportedOSPlatform("linux")]
    internal sealed class LinuxSecretStore : ISecretStore
    {
        /// <summary>
        /// A failure that takes at least this long is a Secret Service that is not there: D-Bus
        /// spends its full activation timeout on a well-known name nothing owns. One of those is a
        /// pause; six of them (a clean-slate save) is a hang, so the first one latches the store off
        /// for the rest of the session. A quick failure is a real answer (a dismissed unlock prompt,
        /// a locked item) and must not latch.
        /// </summary>
        private static readonly TimeSpan SlowFailure = TimeSpan.FromSeconds(3);

        private static readonly Lock ProbeGate = new();
        private static bool? _available;
        private static string? _unavailableReason;
        private static IntPtr _strHash, _strEqual;

        private readonly Action<string>? _log;

        public LinuxSecretStore(Action<string>? log) => _log = log;

        private void Log(string message) => _log?.Invoke("[secrets] " + message);

        // ---- API ----------------------------------------------------------

        public bool IsAvailable(out string? reason)
        {
            lock (ProbeGate)
            {
                if (_available == null)
                {
                    (bool ok, string? why) = Probe();
                    _available = ok;
                    _unavailableReason = why;
                }
                reason = _unavailableReason;
                return _available.Value;
            }
        }

        public string Load(SecretSlot slot)
        {
            if (!IsAvailable(out _)) return "";

            var owned = new List<IntPtr>();
            IntPtr table = IntPtr.Zero, error = IntPtr.Zero;
            var watch = Stopwatch.StartNew();
            try
            {
                table = BuildAttributes(slot.Purpose, slot.Identity, owned);
                var p = secret_password_lookupv_sync(Schema, table, IntPtr.Zero, ref error);
                if (p == IntPtr.Zero)
                {
                    // NULL means "no entry" or "failed", and only the GError tells them apart. No
                    // entry is the normal answer before anything has been saved, so it must not log.
                    string message = TakeError(ref error);
                    if (message.Length != 0) Fail($"lookup failed: {message}", watch.Elapsed);
                    return "";
                }

                // secret_password_free wipes before freeing, unlike g_free.
                try { return Marshal.PtrToStringUTF8(p) ?? ""; }
                finally { secret_password_free(p); }
            }
            catch (Exception ex)
            {
                Fail($"lookup failed: {ex.Message}", watch.Elapsed);
                return "";
            }
            finally
            {
                TakeError(ref error);
                Release(table, owned);
            }
        }

        public bool Store(SecretSlot slot, string secret, string label)
        {
            // The password crosses as a const gchar*, so an embedded NUL cannot round-trip. A
            // TextBox cannot produce one; the guard is here to say why the constraint exists.
            if (secret.Contains('\0'))
            {
                Log("value contains a NUL byte and cannot be stored");
                return false;
            }
            if (!IsAvailable(out _)) return false;

            var owned = new List<IntPtr>();
            IntPtr table = IntPtr.Zero, error = IntPtr.Zero, password = IntPtr.Zero;
            var watch = Stopwatch.StartNew();
            try
            {
                table = BuildAttributes(slot.Purpose, slot.Identity, owned);
                password = Marshal.StringToCoTaskMemUTF8(secret);

                if (secret_password_storev_sync(Schema, table, DefaultCollection, label, password,
                                                IntPtr.Zero, ref error) != 0)
                    return true;

                string first = TakeError(ref error);

                // Some Secret Service implementations never register the "default" collection
                // alias. A null collection lets libsecret pick, which is the difference between
                // working and not working on those desktops.
                if (secret_password_storev_sync(Schema, table, null, label, password,
                                                IntPtr.Zero, ref error) != 0)
                    return true;

                string second = TakeError(ref error);
                Fail($"store failed: {(second.Length != 0 ? second : first)}", watch.Elapsed);
                return false;
            }
            catch (Exception ex)
            {
                Fail($"store failed: {ex.Message}", watch.Elapsed);
                return false;
            }
            finally
            {
                TakeError(ref error);
                // Wipes the native copy before the memory goes back. It does not reach the managed
                // string it came from, which .NET can neither pin nor clear.
                if (password != IntPtr.Zero) Marshal.ZeroFreeCoTaskMemUTF8(password);
                Release(table, owned);
            }
        }

        public bool Delete(SecretSlot slot) => Clear(slot.Purpose, slot.Identity);

        public bool DeletePurpose(string purpose) => Clear(purpose, identity: null);

        /// <summary>
        /// Clears every unlocked item matching the attributes given. With no identity the query is
        /// just the purpose, and the Secret Service matches items whose attributes are a
        /// <i>superset</i> of it, so one call sweeps every identity for that purpose.
        /// </summary>
        private bool Clear(string purpose, string? identity)
        {
            if (!IsAvailable(out _)) return false;

            var owned = new List<IntPtr>();
            IntPtr table = IntPtr.Zero, error = IntPtr.Zero;
            var watch = Stopwatch.StartNew();
            try
            {
                table = BuildAttributes(purpose, identity, owned);
                if (secret_password_clearv_sync(Schema, table, IntPtr.Zero, ref error) != 0) return true;

                // False also means "nothing matched", which is the outcome a delete wanted.
                string message = TakeError(ref error);
                if (message.Length == 0) return true;
                Fail($"clear failed: {message}", watch.Elapsed);
                return false;
            }
            catch (Exception ex)
            {
                Fail($"clear failed: {ex.Message}", watch.Elapsed);
                return false;
            }
            finally
            {
                TakeError(ref error);
                Release(table, owned);
            }
        }

        private void Fail(string message, TimeSpan elapsed)
        {
            Log(message);
            if (elapsed < SlowFailure) return;

            lock (ProbeGate)
            {
                if (_available == false) return;
                _available = false;
                _unavailableReason = "Saving passwords is unavailable: the desktop keyring is not responding.";
            }
            Log("keyring is not responding; saving passwords is off for the rest of this session");
        }

        // ---- Probe --------------------------------------------------------

        private (bool, string?) Probe()
        {
            try
            {
                // Without a session bus, GLib tries to autolaunch one: slow on a headless box, and
                // capable of leaving a stray dbus-daemon behind. Answer before entering libsecret.
                string? runtimeDir = Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR");
                if (Environment.GetEnvironmentVariable("DBUS_SESSION_BUS_ADDRESS") == null &&
                    (string.IsNullOrEmpty(runtimeDir) || !File.Exists(Path.Combine(runtimeDir, "bus"))))
                    return (false, "Saving passwords needs a desktop session (no D-Bus session bus was found).");

                // The handles are deliberately not freed: the load is the thing we want to keep,
                // the same reasoning as NativeLibraryResolver.CanLoad.
                if (!NativeLibrary.TryLoad(Secret, out var secret) || !NativeLibrary.TryLoad(Glib, out var glib))
                    return (false, "Saving passwords needs libsecret, which is not installed " +
                                   "(package libsecret-1-0 on Debian/Ubuntu, libsecret on Fedora).");

                // Resolving the exact symbols turns a surprise into this message instead of an
                // EntryPointNotFoundException on the first store.
                if (!NativeLibrary.TryGetExport(secret, "secret_password_lookupv_sync", out _) ||
                    !NativeLibrary.TryGetExport(glib, "g_str_hash", out _strHash) ||
                    !NativeLibrary.TryGetExport(glib, "g_str_equal", out _strEqual))
                    return (false, "Saving passwords is unavailable: the installed libsecret is too old.");

                return (true, null);
            }
            catch (Exception ex)
            {
                return (false, $"Saving passwords is unavailable: {ex.Message}");
            }
        }

        // ---- Attributes ---------------------------------------------------

        private static IntPtr BuildAttributes(string purpose, string? identity, List<IntPtr> owned)
        {
            var table = g_hash_table_new(_strHash, _strEqual);
            Put(table, owned, "purpose", purpose);
            if (identity != null) Put(table, owned, "identity", identity);
            return table;
        }

        // g_hash_table_new takes no destroy notifiers, so the table does not own its keys and
        // values; we allocated them, we free them. libsecret copies whatever it needs during the
        // synchronous call, so freeing once it has returned is safe.
        private static void Put(IntPtr table, List<IntPtr> owned, string key, string value)
        {
            var k = Marshal.StringToCoTaskMemUTF8(key);
            owned.Add(k);
            var v = Marshal.StringToCoTaskMemUTF8(value);
            owned.Add(v);
            g_hash_table_insert(table, k, v);
        }

        private static void Release(IntPtr table, List<IntPtr> owned)
        {
            if (table != IntPtr.Zero) g_hash_table_unref(table);
            foreach (var p in owned) Marshal.FreeCoTaskMem(p);
        }

        // ---- Schema -------------------------------------------------------

        /// <summary>
        /// The schema, built once and never freed: it and every string it points at must outlive
        /// every call, and the process is the natural scope for that.
        ///
        /// Passing NULL instead is allowed by libsecret and would be a mistake. With a non-NULL
        /// schema and SECRET_SCHEMA_NONE, libsecret adds <c>xdg:schema = &lt;name&gt;</c> to the
        /// attributes on store <i>and</i> on search, and that implicit attribute is what scopes our
        /// items: it stops a lookup matching another application's item that happens to share a
        /// purpose and identity, and it stops <see cref="DeletePurpose"/> deleting somebody else's
        /// credentials. (SECRET_SCHEMA_DONT_MATCH_NAME is what turns that off; it is what
        /// <c>secret-tool</c> sets, which is why secret-tool can still find these entries. If it is
        /// ever needed here, note that it is 1 &lt;&lt; 1, so 2 and not 1: writing 1 compiles and
        /// does nothing.)
        /// </summary>
        private static readonly IntPtr Schema = BuildSchema();

        /// <summary>Matches the AppStream id in <c>packaging/io.github.ljfloor.VirtDeck.metainfo.xml</c>.</summary>
        private const string SchemaName = "io.github.ljfloor.VirtDeck";

        /// <summary>SECRET_COLLECTION_DEFAULT.</summary>
        private const string DefaultCollection = "default";

        /// <summary>
        /// sizeof(SecretSchema) on x86-64, at natural alignment:
        ///
        ///     0  const gchar *name
        ///     8  SecretSchemaFlags flags (int)  + 4 padding
        ///    16  SecretSchemaAttribute[32]      32 * 16 = 512
        ///          each: +0 const gchar *name, +8 int type, +4 padding
        ///   528  gint reserved                  + 4 padding
        ///   536  gpointer reserved1..reserved7  7 * 8 = 56
        ///   592  end
        ///
        /// None of that is checkable by reading the C#, which is why the table is written out here.
        /// </summary>
        private const int SchemaSize = 592;

        /// <summary>
        /// Only the head of SecretSchema, because C# cannot express the inline <c>[32]</c> array
        /// without unsafe code, which this repo does not enable anywhere. The two attribute slots
        /// this declares are the two we use; the 30 unused ones and the reserved tail stay zero,
        /// and a NULL attribute name is what ends the list.
        ///
        /// Every attribute name ever passed in the hash table must appear here, or
        /// <c>_secret_attributes_validate</c> fails the whole call.
        /// </summary>
        [StructLayout(LayoutKind.Sequential)]
        private struct SchemaHead
        {
            public IntPtr Name;
            public int Flags;                                  // SECRET_SCHEMA_NONE
            public int Pad0;
            public IntPtr Attribute0Name; public int Attribute0Type; public int Pad1;
            public IntPtr Attribute1Name; public int Attribute1Type; public int Pad2;
        }

        private static IntPtr BuildSchema()
        {
            var p = Marshal.AllocHGlobal(SchemaSize);
            Marshal.Copy(new byte[SchemaSize], 0, p, SchemaSize);
            Marshal.StructureToPtr(new SchemaHead
            {
                Name = Marshal.StringToCoTaskMemUTF8(SchemaName),
                Flags = 0,
                // 0 is SECRET_SCHEMA_ATTRIBUTE_STRING.
                Attribute0Name = Marshal.StringToCoTaskMemUTF8("purpose"), Attribute0Type = 0,
                Attribute1Name = Marshal.StringToCoTaskMemUTF8("identity"), Attribute1Type = 0,
            }, p, fDeleteOld: false);
            return p;
        }

        // ---- GError -------------------------------------------------------

        /// <summary>
        /// Reads and frees a GError, leaving the pointer null so a second call is a no-op. GLib
        /// hands ownership to the caller, so it must be freed even when the message is discarded.
        /// GError is <c>{ GQuark domain; gint code; gchar *message; }</c>, putting the message at
        /// offset 8 on x86-64.
        /// </summary>
        private static string TakeError(ref IntPtr error)
        {
            if (error == IntPtr.Zero) return "";
            string message;
            try { message = Marshal.PtrToStringUTF8(Marshal.ReadIntPtr(error, 8)) ?? ""; }
            catch { message = ""; }
            g_error_free(error);
            error = IntPtr.Zero;
            return message;
        }

        // ---- Interop ------------------------------------------------------

        private const string Secret = "libsecret-1.so.0";
        private const string Glib = "libglib-2.0.so.0";

        // libgobject is deliberately absent: secret_password_* is the object-free convenience API,
        // so there is no GObject to reference here and nothing to unref.
        //
        // The variadic secret_password_store_sync / lookup_sync / clear_sync cannot be called
        // portably through DllImport; their v siblings take the attributes pre-built as a
        // GHashTable and are ordinary fixed-arity functions.
        //
        // gboolean is gint, declared as int rather than bool so it says what the header says. The
        // GError** is `ref`, never `out`: GLib requires *error to be NULL on entry, and an out
        // parameter is the address of a local the compiler has not definitely assigned.

        [DllImport(Secret, CallingConvention = CallingConvention.Cdecl)]
        private static extern int secret_password_storev_sync(
            IntPtr schema, IntPtr attributes,
            [MarshalAs(UnmanagedType.LPUTF8Str)] string? collection,
            [MarshalAs(UnmanagedType.LPUTF8Str)] string label,
            IntPtr password, IntPtr cancellable, ref IntPtr error);

        [DllImport(Secret, CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr secret_password_lookupv_sync(
            IntPtr schema, IntPtr attributes, IntPtr cancellable, ref IntPtr error);

        [DllImport(Secret, CallingConvention = CallingConvention.Cdecl)]
        private static extern int secret_password_clearv_sync(
            IntPtr schema, IntPtr attributes, IntPtr cancellable, ref IntPtr error);

        [DllImport(Secret, CallingConvention = CallingConvention.Cdecl)]
        private static extern void secret_password_free(IntPtr password);

        // g_str_hash and g_str_equal are function pointers, so they cannot be DllImports; their
        // addresses come out of the handle the probe already opened.
        [DllImport(Glib, CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr g_hash_table_new(IntPtr hashFunc, IntPtr keyEqualFunc);

        [DllImport(Glib, CallingConvention = CallingConvention.Cdecl)]
        private static extern void g_hash_table_insert(IntPtr table, IntPtr key, IntPtr value);

        [DllImport(Glib, CallingConvention = CallingConvention.Cdecl)]
        private static extern void g_hash_table_unref(IntPtr table);

        [DllImport(Glib, CallingConvention = CallingConvention.Cdecl)]
        private static extern void g_error_free(IntPtr error);
    }
}
