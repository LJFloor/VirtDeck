using System.Numerics;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using Tmds.DBus.Protocol;

namespace VirtDeck.Secrets
{
    /// <summary>
    /// The freedesktop Secret Service spoken directly on the session bus, which is what
    /// gnome-keyring, KWallet and KeePassXC all serve under the name in <see cref="BusName"/>.
    ///
    /// This is deliberately not libsecret. libsecret is LGPL, pulls glib in behind it, has to come
    /// from the distro because the AppImage bundles nothing, and reaching it meant blitting a
    /// 592-byte <c>SecretSchema</c> by hand because C# cannot express its inline <c>[32]</c> array.
    /// The protocol underneath it is six method calls on one D-Bus object, and
    /// <c>Tmds.DBus.Protocol</c> (already in the app, under Avalonia) writes the messages. So the
    /// dependency is gone and this file is the whole of what it did.
    ///
    /// **Everything here throws on failure**; <see cref="LinuxSecretStore"/> above it is the layer
    /// that never does. Nothing here logs: a caller that logged a value, or an error message
    /// carrying one, would put a password in the shared temp directory.
    ///
    /// The secret crosses the bus encrypted (see <see cref="OpenSessionAsync"/>), so the password
    /// itself is bytes rather than a D-Bus string: unlike the libsecret path, nothing in it is off
    /// limits, an embedded NUL included.
    /// </summary>
    [SupportedOSPlatform("linux")]
    internal sealed class SecretService : IDisposable
    {
        /// <summary>The well-known name every Secret Service implementation owns.</summary>
        public const string BusName = "org.freedesktop.secrets";

        private const string ServicePath = "/org/freedesktop/secrets";
        private const string ServiceInterface = "org.freedesktop.Secret.Service";
        private const string CollectionInterface = "org.freedesktop.Secret.Collection";
        private const string ItemInterface = "org.freedesktop.Secret.Item";
        private const string PromptInterface = "org.freedesktop.Secret.Prompt";
        private const string PropertiesInterface = "org.freedesktop.DBus.Properties";

        /// <summary>
        /// The object path that means "nothing": no prompt was needed, or no such object. Every
        /// method that can ask for user interaction answers with a real path or with this one.
        /// </summary>
        private const string None = "/";

        /// <summary>What <c>secret_password_store</c> tags a password with, so entries look the same.</summary>
        private const string ContentType = "text/plain";

        private readonly DBusConnection _bus;

        /// <summary>
        /// The session secrets are carried in and the key they are encrypted with, which is null
        /// when the service would only agree to "plain". One object rather than two fields so a
        /// second open cannot pair one session's key with another's path.
        /// </summary>
        private sealed record Session(string Path, byte[]? Key);

        /// <summary>Opened on first use: a lookup that finds nothing never needs one.</summary>
        private Session? _session;

        private SecretService(DBusConnection bus) => _bus = bus;

        public void Dispose() => _bus.Dispose();

        /// <summary>
        /// Connects to the session bus. The Secret Service itself is not touched here, because a
        /// call to a name nothing owns costs D-Bus's full activation timeout; see
        /// <see cref="HasServiceAsync"/>.
        /// </summary>
        public static async Task<SecretService> ConnectAsync(string address)
        {
            var bus = new DBusConnection(address);
            try
            {
                await bus.ConnectAsync().ConfigureAwait(false);
                return new SecretService(bus);
            }
            catch
            {
                bus.Dispose();
                throw;
            }
        }

        /// <summary>
        /// True when something on this bus serves secrets, whether it is running or would be
        /// started on demand. Asking the bus is what keeps a desktop with no keyring at all from
        /// paying the activation timeout on every call.
        /// </summary>
        public async Task<bool> HasServiceAsync()
        {
            foreach (string name in await _bus.ListServicesAsync().ConfigureAwait(false))
                if (name == BusName) return true;

            foreach (string name in await _bus.ListActivatableServicesAsync().ConfigureAwait(false))
                if (name == BusName) return true;

            return false;
        }

        // ---- Operations ---------------------------------------------------

        /// <summary>
        /// The secret of the first item matching every attribute given, or <c>""</c> when there is
        /// none. A locked item is unlocked first, which is where the keyring's own prompt appears
        /// and where this call can block for as long as the user takes to answer it.
        /// </summary>
        public async Task<string> LookupAsync(Dictionary<string, string> attributes)
        {
            var (unlocked, locked) = await SearchItemsAsync(attributes).ConfigureAwait(false);

            ObjectPath item;
            if (unlocked.Length != 0)
            {
                item = unlocked[0];
            }
            else if (locked.Length == 0)
            {
                return "";
            }
            else
            {
                var opened = await UnlockAsync(locked[0]).ConfigureAwait(false);
                if (opened.Length == 0) return "";
                item = opened[0];
            }

            return await ReadSecretAsync(item).ConfigureAwait(false);
        }

        /// <summary>
        /// Writes one item into the default collection, replacing whatever already carries the same
        /// attributes. False when the service refused or the user dismissed its prompt.
        /// </summary>
        public async Task<bool> StoreAsync(Dictionary<string, string> attributes, string label, string secret)
        {
            var collection = await DefaultCollectionAsync().ConfigureAwait(false);
            if (collection.ToString() == None) return false;

            // Unconditional, because a collection that is already open comes straight back with no
            // prompt: one round trip buys not having to read the Locked property to find out.
            if ((await UnlockAsync(collection).ConfigureAwait(false)).Length == 0) return false;

            return await CreateItemAsync(collection, attributes, label, secret).ConfigureAwait(false);
        }

        /// <summary>
        /// Deletes every item matching the attributes given. The Secret Service matches on a
        /// *superset*, so attributes that name only a purpose sweep every identity under it.
        /// Nothing matching is success: a slot that holds nothing is already clear.
        /// </summary>
        public async Task<bool> ClearAsync(Dictionary<string, string> attributes)
        {
            var (unlocked, locked) = await SearchItemsAsync(attributes).ConfigureAwait(false);

            bool cleared = true;
            foreach (var item in unlocked) cleared &= await DeleteItemAsync(item).ConfigureAwait(false);
            foreach (var item in locked) cleared &= await DeleteItemAsync(item).ConfigureAwait(false);
            return cleared;
        }

        // ---- The six calls ------------------------------------------------

        private async Task<(ObjectPath[] Unlocked, ObjectPath[] Locked)> SearchItemsAsync(Dictionary<string, string> attributes)
        {
            var message = BuildCall(ServicePath, ServiceInterface, "SearchItems", "a{ss}",
                (ref MessageWriter writer) => WriteAttributes(ref writer, attributes));

            return await _bus.CallMethodAsync(message, static (Message reply, object? _) =>
            {
                var reader = reply.GetBodyReader();
                return (reader.ReadArrayOfObjectPath(), reader.ReadArrayOfObjectPath());
            }).ConfigureAwait(false);
        }

        /// <summary>
        /// Unlocks one object, prompting if the keyring wants a password. The prompt does the
        /// unlocking itself and answers with what it opened; asking a second time rather than
        /// reading that answer out of its result variant costs one round trip on the rare path and
        /// keeps this from depending on a reply shape the implementations disagree about.
        /// </summary>
        private async Task<ObjectPath[]> UnlockAsync(ObjectPath target)
        {
            var (unlocked, prompt) = await UnlockOnceAsync(target).ConfigureAwait(false);
            if (unlocked.Length != 0 || prompt.ToString() == None) return unlocked;

            if (!await PromptAsync(prompt).ConfigureAwait(false)) return [];

            (unlocked, _) = await UnlockOnceAsync(target).ConfigureAwait(false);
            return unlocked;
        }

        private async Task<(ObjectPath[] Unlocked, ObjectPath Prompt)> UnlockOnceAsync(ObjectPath target)
        {
            var message = BuildCall(ServicePath, ServiceInterface, "Unlock", "ao",
                (ref MessageWriter writer) => writer.WriteArray(new[] { target }));

            return await _bus.CallMethodAsync(message, static (Message reply, object? _) =>
            {
                var reader = reply.GetBodyReader();
                return (reader.ReadArrayOfObjectPath(), reader.ReadObjectPath());
            }).ConfigureAwait(false);
        }

        private async Task<string> ReadSecretAsync(ObjectPath item)
        {
            var session = await SessionAsync().ConfigureAwait(false);

            var message = BuildCall(item.ToString(), ItemInterface, "GetSecret", "o",
                (ref MessageWriter writer) => writer.WriteObjectPath(session.Path));

            // The Secret struct is (oayays): our own session back, the cipher parameters, the
            // value, and a content type nothing reads.
            var (parameters, value) = await _bus.CallMethodAsync(message, static (Message reply, object? _) =>
            {
                var reader = reply.GetBodyReader();
                reader.AlignStruct();
                reader.ReadObjectPath();
                return (reader.ReadArrayOfByte(), reader.ReadArrayOfByte());
            }).ConfigureAwait(false);

            return Decrypt(session, parameters, value);
        }

        private async Task<bool> CreateItemAsync(ObjectPath collection, Dictionary<string, string> attributes,
                                                 string label, string secret)
        {
            var session = await SessionAsync().ConfigureAwait(false);

            var properties = new Dictionary<string, VariantValue>
            {
                ["org.freedesktop.Secret.Item.Label"] = label,
                ["org.freedesktop.Secret.Item.Attributes"] = new Dict<string, string>(attributes).AsVariantValue(),
            };

            var (parameters, value) = Encrypt(session, secret);
            var message = BuildCall(collection.ToString(), CollectionInterface, "CreateItem", "a{sv}(oayays)b",
                (ref MessageWriter writer) =>
                {
                    writer.WriteDictionary(properties);
                    writer.WriteStructureStart();
                    writer.WriteObjectPath(session.Path);
                    writer.WriteArray(parameters);
                    writer.WriteArray(value);
                    writer.WriteString(ContentType);
                    writer.WriteBool(true);
                });
            CryptographicOperations.ZeroMemory(value);

            var (item, prompt) = await _bus.CallMethodAsync(message, static (Message reply, object? _) =>
            {
                var reader = reply.GetBodyReader();
                return (reader.ReadObjectPath(), reader.ReadObjectPath());
            }).ConfigureAwait(false);

            if (item.ToString() != None) return true;
            return prompt.ToString() != None && await PromptAsync(prompt).ConfigureAwait(false);
        }

        private async Task<bool> DeleteItemAsync(ObjectPath item)
        {
            var message = BuildCall(item.ToString(), ItemInterface, "Delete", signature: null, body: null);

            var prompt = await _bus.CallMethodAsync(message, static (Message reply, object? _) =>
            {
                var reader = reply.GetBodyReader();
                return reader.ReadObjectPath();
            }).ConfigureAwait(false);

            return prompt.ToString() == None || await PromptAsync(prompt).ConfigureAwait(false);
        }

        /// <summary>
        /// Where a new item goes. The "default" alias is the answer on every desktop that registers
        /// one; the first collection the service lists is the fallback for those that do not, which
        /// is the same hole the libsecret version papered over by retrying with a null collection.
        /// </summary>
        private async Task<ObjectPath> DefaultCollectionAsync()
        {
            var alias = BuildCall(ServicePath, ServiceInterface, "ReadAlias", "s",
                (ref MessageWriter writer) => writer.WriteString("default"));

            var collection = await _bus.CallMethodAsync(alias, static (Message reply, object? _) =>
            {
                var reader = reply.GetBodyReader();
                return reader.ReadObjectPath();
            }).ConfigureAwait(false);

            if (collection.ToString() != None) return collection;

            var properties = BuildCall(ServicePath, PropertiesInterface, "Get", "ss",
                (ref MessageWriter writer) =>
                {
                    writer.WriteString(ServiceInterface);
                    writer.WriteString("Collections");
                });

            var collections = await _bus.CallMethodAsync(properties, static (Message reply, object? _) =>
            {
                var reader = reply.GetBodyReader();
                return reader.ReadVariantValue().GetArray<ObjectPath>();
            }).ConfigureAwait(false);

            return collections.Length != 0 ? collections[0] : new ObjectPath(None);
        }

        /// <summary>
        /// Runs one of the keyring's own prompts and waits for it to finish, which is the call that
        /// blocks for as long as the user leaves the unlock dialog on screen. False means dismissed.
        /// The watch goes on before the prompt is shown, because a prompt answered instantly would
        /// otherwise complete before anything was listening.
        /// </summary>
        private async Task<bool> PromptAsync(ObjectPath prompt)
        {
            var completed = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

            using var watch = await _bus.WatchSignalAsync(
                sender: BusName, path: prompt.ToString(), @interface: PromptInterface, signal: "Completed",
                reader: static (Message signal, object? _) =>
                {
                    var reader = signal.GetBodyReader();
                    return reader.ReadBool();
                },
                handler: (Notification<bool> notification) =>
                {
                    // The bus going away has to end the wait too, or a keyring that died mid-prompt
                    // would leave this call blocked on a dialog nobody can answer any more.
                    if (notification.Type != NotificationType.Value)
                        completed.TrySetException(notification.Exception ?? new IOException("the session bus closed"));
                    else
                        completed.TrySetResult(!notification.Value);
                },
                flags: ObserverFlags.EmitOnConnectionClosed | ObserverFlags.EmitOnConnectionFailed,
                emitOnCapturedContext: false).ConfigureAwait(false);

            // The window id is the desktop's way of parenting the dialog. We have no X11 window id
            // to give it, and every implementation treats the empty string as "no parent".
            var message = BuildCall(prompt.ToString(), PromptInterface, "Prompt", "s",
                (ref MessageWriter writer) => writer.WriteString(""));

            await _bus.CallMethodAsync(message).ConfigureAwait(false);
            return await completed.Task.ConfigureAwait(false);
        }

        // ---- The session and its cipher -----------------------------------

        /// <summary>
        /// RFC 2409's 1024-bit MODP group, the one the Secret Service names "ietf1024".
        /// </summary>
        private static readonly BigInteger Prime = new(Convert.FromHexString(
            "FFFFFFFFFFFFFFFFC90FDAA22168C234C4C6628B80DC1CD129024E088A67CC74" +
            "020BBEA63B139B22514A08798E3404DDEF9519B3CD3A431B302B0A6DF25F1437" +
            "4FE1356D6D51C245E485B576625E7EC6F44C42E9A637ED6B0BFF5CB6F406B7ED" +
            "EE386BFB5A899FA5AE9F24117C4B1FE649286651ECE65381FFFFFFFFFFFFFFFF"),
            isUnsigned: true, isBigEndian: true);

        private const string EncryptedAlgorithm = "dh-ietf1024-sha256-aes128-cbc-pkcs7";

        /// <summary>
        /// The open session, opening one if this is the first secret to cross. Not guarded: the
        /// store above serialises its calls, and a session opened twice would cost a round trip
        /// rather than mismatch anything, since each one is complete before it is published.
        /// </summary>
        private async Task<Session> SessionAsync() => _session ??= await OpenSessionAsync().ConfigureAwait(false);

        /// <summary>
        /// Opens the session every secret is carried in, negotiating the encrypted algorithm first
        /// and falling back to "plain", exactly as libsecret does.
        ///
        /// The encryption is worth the sixty lines below it. Without it the password crosses the
        /// session bus in the clear, where any process able to become a bus monitor reads it out of
        /// somebody else's traffic; with it, that process has to ask the keyring for the item
        /// itself, which is the bargain the Secret Service actually offers (see
        /// <c>SshCredentialStore</c>'s "what this protects against"). Diffie-Hellman over the group
        /// above, HKDF-SHA256 with no salt down to 128 bits, then AES-CBC: every primitive is in
        /// the BCL, and all three are pinned by the algorithm name the service agreed to.
        ///
        /// Both sides encode the numbers the way libgcrypt's <c>GCRYMPI_FMT_USG</c> does, shortest
        /// big-endian with no leading zero byte, which is what <see cref="BigInteger"/> writes.
        /// </summary>
        private async Task<Session> OpenSessionAsync()
        {
            var privateKey = NewPrivateKey();
            byte[] ours = Encode(BigInteger.ModPow(2, privateKey, Prime));

            var encrypted = BuildCall(ServicePath, ServiceInterface, "OpenSession", "sv",
                (ref MessageWriter writer) =>
                {
                    writer.WriteString(EncryptedAlgorithm);
                    writer.WriteVariant(new Array<byte>(ours).AsVariantValue());
                });

            try
            {
                var (output, session) = await _bus.CallMethodAsync(encrypted, static (Message reply, object? _) =>
                {
                    var reader = reply.GetBodyReader();
                    return (reader.ReadVariantValue(), reader.ReadObjectPathAsString());
                }).ConfigureAwait(false);

                var theirs = new BigInteger(output.GetArray<byte>(), isUnsigned: true, isBigEndian: true);
                byte[] shared = Encode(BigInteger.ModPow(theirs, privateKey, Prime));
                try { return new Session(session, HKDF.DeriveKey(HashAlgorithmName.SHA256, shared, outputLength: 16)); }
                finally { CryptographicOperations.ZeroMemory(shared); }
            }
            catch (DBusErrorReplyException)
            {
                // A service that does not implement the algorithm says so here, and plain is the
                // one every implementation must support.
                var plain = BuildCall(ServicePath, ServiceInterface, "OpenSession", "sv",
                    (ref MessageWriter writer) =>
                    {
                        writer.WriteString("plain");
                        writer.WriteVariantString("");
                    });

                string session = await _bus.CallMethodAsync(plain, static (Message reply, object? _) =>
                {
                    var reader = reply.GetBodyReader();
                    reader.ReadVariantValue();
                    return reader.ReadObjectPathAsString();
                }).ConfigureAwait(false);
                return new Session(session, Key: null);
            }
        }

        /// <summary>
        /// A random exponent. The top bit is cleared so the value is below the prime whatever the
        /// draw, and the next one set so it is never a small number.
        /// </summary>
        private static BigInteger NewPrivateKey()
        {
            byte[] bytes = RandomNumberGenerator.GetBytes(128);
            bytes[0] &= 0x7F;
            bytes[0] |= 0x40;
            var key = new BigInteger(bytes, isUnsigned: true, isBigEndian: true);
            CryptographicOperations.ZeroMemory(bytes);
            return key;
        }

        private static byte[] Encode(BigInteger value) => value.ToByteArray(isUnsigned: true, isBigEndian: true);

        private static (byte[] Parameters, byte[] Value) Encrypt(Session session, string secret)
        {
            byte[] plain = Encoding.UTF8.GetBytes(secret);
            if (session.Key == null) return ([], plain);

            try
            {
                byte[] iv = RandomNumberGenerator.GetBytes(16);
                using var aes = Aes.Create();
                aes.Key = session.Key;
                return (iv, aes.EncryptCbc(plain, iv, PaddingMode.PKCS7));
            }
            finally
            {
                CryptographicOperations.ZeroMemory(plain);
            }
        }

        private static string Decrypt(Session session, byte[] parameters, byte[] value)
        {
            if (session.Key == null) return Encoding.UTF8.GetString(value);

            using var aes = Aes.Create();
            aes.Key = session.Key;
            byte[] plain = aes.DecryptCbc(value, parameters, PaddingMode.PKCS7);
            try { return Encoding.UTF8.GetString(plain); }
            finally { CryptographicOperations.ZeroMemory(plain); }
        }

        // ---- Message plumbing ---------------------------------------------

        /// <summary>
        /// Writes the body of one call. A delegate rather than a lambda parameter because
        /// <see cref="MessageWriter"/> is a ref struct and cannot be a generic argument.
        /// </summary>
        private delegate void BodyWriter(ref MessageWriter writer);

        private MessageBuffer BuildCall(string path, string @interface, string member, string? signature, BodyWriter? body)
        {
            var writer = _bus.GetMessageWriter();
            try
            {
                writer.WriteMethodCallHeader(destination: BusName, path: path, @interface: @interface,
                                             member: member, signature: signature);
                body?.Invoke(ref writer);
                return writer.CreateMessage();
            }
            finally
            {
                writer.Dispose();
            }
        }

        private static void WriteAttributes(ref MessageWriter writer, Dictionary<string, string> attributes)
        {
            var start = writer.WriteDictionaryStart();
            foreach (var pair in attributes)
            {
                writer.WriteDictionaryEntryStart();
                writer.WriteString(pair.Key);
                writer.WriteString(pair.Value);
            }
            writer.WriteDictionaryEnd(start);
        }
    }
}
