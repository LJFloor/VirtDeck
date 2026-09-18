using System.Diagnostics;
using System.Runtime.Versioning;

namespace VirtDeck.Secrets
{
    /// <summary>
    /// Linux half of <see cref="SecretStores"/>: the freedesktop Secret Service, which is what
    /// gnome-keyring, KWallet and KeePassXC all speak.
    ///
    /// <see cref="SecretService"/> underneath is the protocol and throws; this is the policy and
    /// never does. It owns the one connection, decides what the store is keyed by, and turns every
    /// failure into a falsy answer plus a log line.
    ///
    /// This used to be libsecret through P/Invoke, and the absence of libsecret was one of the
    /// reasons the checkbox could be disabled. It no longer is: the session bus is the only thing
    /// needed, so the reasons left are a desktop that has no bus at all and a bus with nothing
    /// serving secrets on it.
    ///
    /// Nothing in this file puts a secret, or anything derived from one, into a log line: a D-Bus
    /// error names a method and an object, never a value, and <c>SpiceLog</c> writes to the shared
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
        private static SecretService? _service;

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
            if (!Ready(out var service)) return "";

            var watch = Stopwatch.StartNew();
            try { return Block(service.LookupAsync(Attributes(slot.Purpose, slot.Identity))); }
            catch (Exception ex) { Fail($"lookup failed: {ex.Message}", watch.Elapsed); return ""; }
        }

        public bool Store(SecretSlot slot, string secret, string label)
        {
            if (!Ready(out var service)) return false;

            var watch = Stopwatch.StartNew();
            try
            {
                if (Block(service.StoreAsync(Attributes(slot.Purpose, slot.Identity), label, secret))) return true;

                // The service answered, and the answer was no: a dismissed unlock prompt, or a
                // desktop with no collection to write to. Nothing is wrong with the connection.
                Log("store refused by the keyring");
                return false;
            }
            catch (Exception ex) { Fail($"store failed: {ex.Message}", watch.Elapsed); return false; }
        }

        public bool Delete(SecretSlot slot) => Clear(slot.Purpose, slot.Identity);

        public bool DeletePurpose(string purpose) => Clear(purpose, identity: null);

        /// <summary>
        /// Clears every item matching the attributes given. With no identity the query is just the
        /// purpose, and the Secret Service matches items whose attributes are a <i>superset</i> of
        /// it, so one call sweeps every identity for that purpose.
        /// </summary>
        private bool Clear(string purpose, string? identity)
        {
            if (!Ready(out var service)) return false;

            var watch = Stopwatch.StartNew();
            try { return Block(service.ClearAsync(Attributes(purpose, identity))); }
            catch (Exception ex) { Fail($"clear failed: {ex.Message}", watch.Elapsed); return false; }
        }

        // ---- Plumbing -----------------------------------------------------

        /// <summary>
        /// The connection to run a call on, or false when there is none to run it on.
        /// </summary>
        private bool Ready(out SecretService service)
        {
            if (IsAvailable(out _) && _service is { } connected)
            {
                service = connected;
                return true;
            }
            service = null!;
            return false;
        }

        /// <summary>
        /// Waits for one asynchronous call, which is the whole of what <see cref="ISecretStore"/>
        /// promises its callers: these block, and a locked keyring blocks until the prompt is
        /// answered. Safe to wait on from any thread because every await in
        /// <see cref="SecretService"/> is <c>ConfigureAwait(false)</c>, so nothing in flight ever
        /// wants the waiting thread back.
        /// </summary>
        private static T Block<T>(Task<T> work) => work.GetAwaiter().GetResult();

        /// <summary>
        /// Logs a failure, drops the connection so the next call builds a fresh one, and latches the
        /// store off when the failure was slow enough to be a keyring that is not answering.
        /// </summary>
        private void Fail(string message, TimeSpan elapsed)
        {
            Log(message);

            lock (ProbeGate)
            {
                _service?.Dispose();
                _service = null;
                _available = null;
                _unavailableReason = null;

                if (elapsed < SlowFailure) return;
                _available = false;
                _unavailableReason = "Saving passwords is unavailable: the desktop keyring is not responding.";
            }

            Log("keyring is not responding; saving passwords is off for the rest of this session");
        }

        // ---- Probe --------------------------------------------------------

        private static (bool, string?) Probe()
        {
            try
            {
                string? address = SessionBusAddress();
                if (address == null)
                    return (false, "Saving passwords needs a desktop session (no D-Bus session bus was found).");

                var service = Block(ConnectAsync(address));
                if (service == null)
                    return (false, "Saving passwords needs a desktop keyring (GNOME Keyring, KWallet or " +
                                   "KeePassXC); nothing on this desktop offers one.");

                _service = service;
                return (true, null);
            }
            catch (Exception ex)
            {
                return (false, $"Saving passwords is unavailable: {ex.Message}");
            }
        }

        /// <summary>
        /// Connects, and hands back nothing when the bus has no keyring on it: asking costs two
        /// round trips once, where a call to a name nothing owns costs D-Bus's activation timeout
        /// every time.
        /// </summary>
        private static async Task<SecretService?> ConnectAsync(string address)
        {
            var service = await SecretService.ConnectAsync(address).ConfigureAwait(false);
            if (await service.HasServiceAsync().ConfigureAwait(false)) return service;

            service.Dispose();
            return null;
        }

        /// <summary>
        /// Where the session bus is. The variable is the answer on a normal desktop session; the
        /// socket under XDG_RUNTIME_DIR is where systemd's user instance puts it and what a session
        /// that never exported the variable still has. Answering null here rather than letting the
        /// library look is deliberate: a bus address that is missing must be a stated answer, not an
        /// autolaunched dbus-daemon left behind on a headless box.
        /// </summary>
        private static string? SessionBusAddress()
        {
            string? address = Environment.GetEnvironmentVariable("DBUS_SESSION_BUS_ADDRESS");
            if (!string.IsNullOrEmpty(address)) return address;

            string? runtimeDir = Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR");
            if (string.IsNullOrEmpty(runtimeDir)) return null;

            string socket = Path.Combine(runtimeDir, "bus");
            return File.Exists(socket) ? "unix:path=" + socket : null;
        }

        // ---- Attributes ---------------------------------------------------

        /// <summary>
        /// Matches the AppStream id in <c>packaging/io.github.ljfloor.VirtDeck.metainfo.xml</c>.
        /// </summary>
        private const string SchemaName = "io.github.ljfloor.VirtDeck";

        /// <summary>
        /// **Never drop this attribute.** libsecret adds <c>xdg:schema</c> to the attributes on
        /// store *and* on search whenever it is given a named schema, and that implicit attribute is
        /// what scopes our items: it stops a lookup matching another application's item that happens
        /// to share a purpose and identity, and it stops <see cref="DeletePurpose"/> deleting
        /// somebody else's credentials. Writing it by hand is what keeps entries saved by the
        /// libsecret version of this file readable by this one, and keeps <c>secret-tool</c> able to
        /// find them both.
        /// </summary>
        private const string SchemaAttribute = "xdg:schema";

        private static Dictionary<string, string> Attributes(string purpose, string? identity)
        {
            var attributes = new Dictionary<string, string>(3)
            {
                [SchemaAttribute] = SchemaName,
                ["purpose"] = purpose,
            };
            if (identity != null) attributes["identity"] = identity;
            return attributes;
        }
    }
}
