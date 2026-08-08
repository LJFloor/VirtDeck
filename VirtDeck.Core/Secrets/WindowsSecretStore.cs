using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;

namespace VirtDeck.Secrets
{
    /// <summary>
    /// Windows half of <see cref="SecretStores"/>: generic credentials in Credential Manager, which
    /// DPAPI-encrypts them under the user's logon session.
    ///
    /// Unlike the Linux half this never prompts and never blocks; the logon session already holds
    /// the key. Everything <see cref="ISecretStore"/> says about blocking is a Linux hazard
    /// travelling through a shared interface.
    /// </summary>
    [SupportedOSPlatform("windows")]
    internal sealed class WindowsSecretStore : ISecretStore
    {
        private const string Prefix = "VirtDeck:";
        private const string Comment = "Saved by VirtDeck";

        private readonly Action<string>? _log;

        public WindowsSecretStore(Action<string>? log) => _log = log;

        private void Log(string message) => _log?.Invoke("[secrets] " + message);

        private static string TargetName(SecretSlot slot) => $"{Prefix}{slot.Purpose}:{slot.Identity}";

        // ---- API ----------------------------------------------------------

        public bool IsAvailable(out string? reason)
        {
            // Reading a target that does not exist is the cheapest way to ask whether the API
            // answers at all: ERROR_NOT_FOUND means Credential Manager is there and said no.
            try
            {
                CredReadW(Prefix + "probe", CRED_TYPE_GENERIC, 0, out var p);
                int err = Marshal.GetLastWin32Error();
                if (p != IntPtr.Zero) CredFree(p);
                if (err is 0 or ERROR_NOT_FOUND)
                {
                    reason = null;
                    return true;
                }
                reason = $"Windows Credential Manager is unavailable: {new Win32Exception(err).Message}";
                return false;
            }
            catch (Exception ex)
            {
                reason = $"Windows Credential Manager is unavailable: {ex.Message}";
                return false;
            }
        }

        public string Load(SecretSlot slot)
        {
            try
            {
                if (!CredReadW(TargetName(slot), CRED_TYPE_GENERIC, 0, out var p))
                {
                    int err = Marshal.GetLastWin32Error();
                    // 1168 is the normal answer before anything has been saved; logging it would
                    // report three failures on every first launch.
                    if (err != ERROR_NOT_FOUND) Log($"CredRead failed (error {err})");
                    return "";
                }

                try
                {
                    var cred = Marshal.PtrToStructure<CREDENTIALW>(p);
                    if (cred.CredentialBlobSize == 0 || cred.CredentialBlob == IntPtr.Zero) return "";

                    var buffer = new byte[cred.CredentialBlobSize];
                    Marshal.Copy(cred.CredentialBlob, buffer, 0, buffer.Length);
                    try { return Encoding.UTF8.GetString(buffer); }
                    finally { Array.Clear(buffer); }
                }
                finally { CredFree(p); }
            }
            catch (Exception ex)
            {
                Log($"read failed: {ex.Message}");
                return "";
            }
        }

        public bool Store(SecretSlot slot, string secret, string label)
        {
            var bytes = Encoding.UTF8.GetBytes(secret);
            if (bytes.Length > MaxBlobBytes)
            {
                Array.Clear(bytes);
                Log($"value is too long for Credential Manager ({bytes.Length} > {MaxBlobBytes} bytes)");
                return false;
            }

            IntPtr blob = IntPtr.Zero, target = IntPtr.Zero, comment = IntPtr.Zero, user = IntPtr.Zero;
            try
            {
                blob = Marshal.AllocHGlobal(bytes.Length == 0 ? 1 : bytes.Length);
                Marshal.Copy(bytes, 0, blob, bytes.Length);
                target = Marshal.StringToHGlobalUni(TargetName(slot));
                comment = Marshal.StringToHGlobalUni(Comment);
                // Cosmetic, but Credential Manager shows this column and a blank one reads as a
                // broken entry. Generic credentials put no format rules on it.
                user = Marshal.StringToHGlobalUni(label);

                var cred = new CREDENTIALW
                {
                    Type = CRED_TYPE_GENERIC,
                    TargetName = target,
                    Comment = comment,
                    CredentialBlobSize = (uint)bytes.Length,
                    CredentialBlob = blob,
                    // Not ENTERPRISE, which roams with the profile and would follow one host's sudo
                    // password onto every domain PC; not SESSION, which would evaporate at logoff
                    // and make "Remember" a lie.
                    Persist = CRED_PERSIST_LOCAL_MACHINE,
                    UserName = user,
                };

                if (CredWriteW(ref cred, 0)) return true;
                Log($"CredWrite failed (error {Marshal.GetLastWin32Error()})");
                return false;
            }
            catch (Exception ex)
            {
                Log($"write failed: {ex.Message}");
                return false;
            }
            finally
            {
                Array.Clear(bytes);
                if (blob != IntPtr.Zero)
                {
                    Zero(blob, bytes.Length);
                    Marshal.FreeHGlobal(blob);
                }
                if (target != IntPtr.Zero) Marshal.FreeHGlobal(target);
                if (comment != IntPtr.Zero) Marshal.FreeHGlobal(comment);
                if (user != IntPtr.Zero) Marshal.FreeHGlobal(user);
            }
        }

        public bool Delete(SecretSlot slot) => Delete(TargetName(slot));

        public bool DeletePurpose(string purpose)
        {
            string prefix = $"{Prefix}{purpose}:";
            try
            {
                if (!CredEnumerateW(Prefix + "*", 0, out uint count, out var array))
                {
                    int err = Marshal.GetLastWin32Error();
                    if (err == ERROR_NOT_FOUND) return true;   // nothing matched, which is success
                    Log($"CredEnumerate failed (error {err})");
                    return false;
                }

                var targets = new List<string>();
                try
                {
                    // An array of CREDENTIALW*, freed as one block by a single CredFree(array).
                    for (uint i = 0; i < count; i++)
                    {
                        var p = Marshal.ReadIntPtr(array, (int)i * IntPtr.Size);
                        if (p == IntPtr.Zero) continue;
                        var name = Marshal.PtrToStringUni(Marshal.PtrToStructure<CREDENTIALW>(p).TargetName);
                        if (name != null && name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                            targets.Add(name);
                    }
                }
                finally { CredFree(array); }

                bool ok = true;
                foreach (var name in targets) ok &= Delete(name);
                return ok;
            }
            catch (Exception ex)
            {
                Log($"sweep failed: {ex.Message}");
                return false;
            }
        }

        private bool Delete(string targetName)
        {
            try
            {
                if (CredDeleteW(targetName, CRED_TYPE_GENERIC, 0)) return true;
                int err = Marshal.GetLastWin32Error();
                if (err == ERROR_NOT_FOUND) return true;   // already gone is the outcome we wanted
                Log($"CredDelete failed (error {err})");
                return false;
            }
            catch (Exception ex)
            {
                Log($"delete failed: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Wipes the native copy of a secret before the memory goes back. It does not reach the
        /// managed string it came from, which .NET can neither pin nor clear, so this is a courtesy
        /// rather than a guarantee; it costs one allocation.
        /// </summary>
        private static void Zero(IntPtr p, int length)
        {
            if (length > 0) Marshal.Copy(new byte[length], 0, p, length);
        }

        // ---- Interop ------------------------------------------------------

        private const uint CRED_TYPE_GENERIC = 1;
        private const uint CRED_PERSIST_LOCAL_MACHINE = 2;
        private const int ERROR_NOT_FOUND = 1168;

        /// <summary>CRED_MAX_CREDENTIAL_BLOB_SIZE: 5 * 512 <b>bytes</b>, not characters.</summary>
        private const int MaxBlobBytes = 5 * 512;

        /// <summary>
        /// x64 offsets, which is why <c>Pack</c> is left at natural alignment: the 4 bytes of
        /// padding after <c>CredentialBlobSize</c> are what put <c>CredentialBlob</c> at 40.
        /// Setting <c>Pack = 1</c> "to be safe" moves it to 36 and silently corrupts every write.
        ///
        ///   0 Flags, 4 Type, 8 TargetName, 16 Comment, 24/28 LastWritten (FILETIME as two DWORDs),
        ///  32 CredentialBlobSize, [36 padding], 40 CredentialBlob, 48 Persist, 52 AttributeCount,
        ///  56 Attributes, 64 TargetAlias, 72 UserName. sizeof = 80.
        ///
        /// The strings are IntPtr rather than string on purpose: this struct is both written (with
        /// memory we allocate) and read back (from memory Credential Manager owns), and letting the
        /// marshaller build strings for the read would let it try to free the OS's memory.
        /// </summary>
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct CREDENTIALW
        {
            public uint Flags;
            public uint Type;
            public IntPtr TargetName;
            public IntPtr Comment;
            public uint LastWrittenLow;
            public uint LastWrittenHigh;
            public uint CredentialBlobSize;
            public IntPtr CredentialBlob;
            public uint Persist;
            public uint AttributeCount;
            public IntPtr Attributes;
            public IntPtr TargetAlias;
            public IntPtr UserName;
        }

        // Every flags argument below is 0. CredWriteW's CRED_PRESERVE_CREDENTIAL_BLOB (0x1) in
        // particular would keep the existing blob and quietly ignore the one being written.

        [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool CredWriteW(ref CREDENTIALW credential, uint flags);

        [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool CredReadW(string targetName, uint type, uint flags, out IntPtr credential);

        [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool CredDeleteW(string targetName, uint type, uint flags);

        [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool CredEnumerateW(string? filter, uint flags, out uint count, out IntPtr credentials);

        [DllImport("advapi32.dll")]
        private static extern void CredFree(IntPtr buffer);
    }
}
