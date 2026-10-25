using System.Runtime.InteropServices;

namespace ServiceLib.Services;

internal interface IObserverSecretStore
{
    Task SaveAsync(string id, string secret);
    Task<string?> ReadAsync(string id);
    Task DeleteAsync(string id);
}

internal sealed class ObserverStorageException(string message, Exception? inner = null) : IOException(message, inner);

// Secrets never travel through shell arguments, environment variables or temporary plaintext files.
internal sealed class ObserverSecretStore : IObserverSecretStore
{
    public Task SaveAsync(string id, string secret) => Task.Run(() => Access(() => { Run("save", id, secret); return true; }));
    public Task<string?> ReadAsync(string id) => Task.Run(() => Access(() => Run("read", id, null)));
    public Task DeleteAsync(string id) => Task.Run(() => Access(() => { Run("delete", id, null); return true; }));
    private static T Access<T>(Func<T> action)
    {
        try { return action(); }
        catch (ObserverStorageException) { throw; }
        catch (Exception ex) { throw new ObserverStorageException("System secure storage unavailable or access denied; no plaintext fallback.", ex); }
    }
    private static string? Run(string action, string id, string? value)
    {
        if (id.Length != 32 || !id.All(Uri.IsHexDigit)) throw new ArgumentException("Invalid secret reference");
        if (OperatingSystem.IsMacOS()) return MacSecret.Run(action, id, value);
        if (OperatingSystem.IsWindows()) return WindowsSecret.Run(action, id, value);
        if (OperatingSystem.IsLinux()) return LinuxSecret.Run(action, id, value);
        throw new ObserverStorageException("Platform secure storage is not supported");
    }

    private static class WindowsSecret
    {
        [StructLayout(LayoutKind.Sequential)] private struct Blob { public int Length; public IntPtr Data; }
        [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        [return: MarshalAs(UnmanagedType.Bool)] private static extern bool CryptProtectData(ref Blob input, string description, IntPtr entropy, IntPtr reserved, IntPtr prompt, int flags, out Blob output);
        [DllImport("crypt32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)] private static extern bool CryptUnprotectData(ref Blob input, IntPtr description, IntPtr entropy, IntPtr reserved, IntPtr prompt, int flags, out Blob output);
        [DllImport("kernel32.dll")] private static extern IntPtr LocalFree(IntPtr memory);
        private static byte[] Transform(byte[] data, bool protect)
        {
            var input = new Blob { Length = data.Length, Data = Marshal.AllocHGlobal(data.Length) };
            Blob output = default;
            try
            {
                Marshal.Copy(data, 0, input.Data, data.Length);
                var ok = protect ? CryptProtectData(ref input, "v2rayN observer device", IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 1, out output)
                    : CryptUnprotectData(ref input, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 1, out output);
                if (!ok) throw new ObserverStorageException("Windows DPAPI rejected access: " + Marshal.GetLastWin32Error());
                var result = new byte[output.Length]; Marshal.Copy(output.Data, result, 0, result.Length); return result;
            }
            finally
            {
                for (var i = 0; i < input.Length; i++) Marshal.WriteByte(input.Data, i, 0);
                Marshal.FreeHGlobal(input.Data);
                if (output.Data != IntPtr.Zero)
                {
                    for (var i = 0; i < output.Length; i++) Marshal.WriteByte(output.Data, i, 0);
                    LocalFree(output.Data);
                }
            }
        }
        internal static string? Run(string action, string id, string? value)
        {
            var directory = Utils.GetConfigPath("observer-secrets"); var path = Path.Combine(directory, id + ".dpapi");
            if (action == "delete") { File.Delete(path); return null; }
            if (action == "read")
            {
                if (!File.Exists(path)) return null;
                var bytes = Transform(File.ReadAllBytes(path), false);
                try { return Encoding.UTF8.GetString(bytes); } finally { CryptographicOperations.ZeroMemory(bytes); }
            }
            Directory.CreateDirectory(directory);
            var plain = Encoding.UTF8.GetBytes(value!);
            try { var bytes = Transform(plain, true); File.WriteAllBytes(path + ".tmp", bytes); File.Move(path + ".tmp", path, true); }
            finally { CryptographicOperations.ZeroMemory(plain); }
            return null;
        }
    }

    private static class MacSecret
    {
        private const string Security = "/System/Library/Frameworks/Security.framework/Security";
        private const string Core = "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";
        private static readonly IntPtr SecLib = NativeLibrary.Load(Security), CfLib = NativeLibrary.Load(Core);
        [DllImport(Core)] private static extern IntPtr CFStringCreateWithCString(IntPtr allocator, [MarshalAs(UnmanagedType.LPUTF8Str)] string text, uint encoding);
        [DllImport(Core)] private static extern IntPtr CFDataCreate(IntPtr allocator, byte[] bytes, nint length);
        [DllImport(Core)] private static extern nint CFDataGetLength(IntPtr data);
        [DllImport(Core)] private static extern IntPtr CFDataGetBytePtr(IntPtr data);
        [DllImport(Core)] private static extern IntPtr CFDictionaryCreate(IntPtr allocator, IntPtr[] keys, IntPtr[] values, nint count, IntPtr keyCallbacks, IntPtr valueCallbacks);
        [DllImport(Core)] private static extern void CFRelease(IntPtr value);
        [DllImport(Security)] private static extern int SecItemAdd(IntPtr attributes, IntPtr result);
        [DllImport(Security)] private static extern int SecItemUpdate(IntPtr query, IntPtr attributes);
        [DllImport(Security)] private static extern int SecItemCopyMatching(IntPtr query, out IntPtr result);
        [DllImport(Security)] private static extern int SecItemDelete(IntPtr query);
        private static IntPtr Constant(string name) => Marshal.ReadIntPtr(NativeLibrary.GetExport(name.StartsWith("kCF", StringComparison.Ordinal) ? CfLib : SecLib, name));
        private static IntPtr Dictionary(params (string Key, IntPtr Value)[] entries) => CFDictionaryCreate(IntPtr.Zero, entries.Select(e => Constant(e.Key)).ToArray(), entries.Select(e => e.Value).ToArray(), entries.Length,
            NativeLibrary.GetExport(CfLib, "kCFTypeDictionaryKeyCallBacks"), NativeLibrary.GetExport(CfLib, "kCFTypeDictionaryValueCallBacks"));
        internal static string? Run(string action, string id, string? value)
        {
            var service = CFStringCreateWithCString(IntPtr.Zero, "io.github.idefav.v2rayN.egress-observer", 0x08000100);
            var account = CFStringCreateWithCString(IntPtr.Zero, id, 0x08000100);
            var query = IntPtr.Zero; var data = IntPtr.Zero; var result = IntPtr.Zero; var attributes = IntPtr.Zero;
            try
            {
                var entries = new List<(string, IntPtr)> { ("kSecClass", Constant("kSecClassGenericPassword")), ("kSecAttrService", service), ("kSecAttrAccount", account) };
                if (action == "read") { entries.Add(("kSecReturnData", Constant("kCFBooleanTrue"))); entries.Add(("kSecMatchLimit", Constant("kSecMatchLimitOne"))); }
                query = Dictionary(entries.ToArray());
                int status;
                if (action == "read")
                {
                    status = SecItemCopyMatching(query, out result);
                    if (status == -25300) return null;
                    if (status != 0) throw new ObserverStorageException("Keychain locked or access denied (" + status + ")");
                    var length = checked((int)CFDataGetLength(result)); if (length > 65536) throw new ObserverStorageException("Keychain value exceeds limit");
                    var bytes = new byte[length]; Marshal.Copy(CFDataGetBytePtr(result), bytes, 0, length);
                    try { return Encoding.UTF8.GetString(bytes); } finally { CryptographicOperations.ZeroMemory(bytes); }
                }
                if (action == "delete") { status = SecItemDelete(query); if (status is 0 or -25300) return null; }
                else
                {
                    var bytes = Encoding.UTF8.GetBytes(value!);
                    try { data = CFDataCreate(IntPtr.Zero, bytes, bytes.Length); } finally { CryptographicOperations.ZeroMemory(bytes); }
                    attributes = Dictionary(("kSecValueData", data)); status = SecItemUpdate(query, attributes);
                    if (status == -25300)
                    {
                        CFRelease(attributes); entries.Add(("kSecValueData", data));
                        entries.Add(("kSecAttrAccessible", Constant("kSecAttrAccessibleAfterFirstUnlockThisDeviceOnly")));
                        attributes = Dictionary(entries.ToArray()); status = SecItemAdd(attributes, IntPtr.Zero);
                    }
                    if (status == 0) return null;
                }
                throw new ObserverStorageException("Keychain rejected operation (" + status + ")");
            }
            finally { foreach (var pointer in new[] { query, attributes, data, result, account, service }) if (pointer != IntPtr.Zero) CFRelease(pointer); }
        }
    }

    private static class LinuxSecret
    {
        private const string Secret = "libsecret-1.so.0";
        [DllImport(Secret, CallingConvention = CallingConvention.Cdecl)] private static extern IntPtr secret_schema_new([MarshalAs(UnmanagedType.LPUTF8Str)] string name, int flags, [MarshalAs(UnmanagedType.LPUTF8Str)] string attribute, int type, IntPtr end);
        [DllImport(Secret)] private static extern void secret_schema_unref(IntPtr schema);
        [DllImport(Secret, CallingConvention = CallingConvention.Cdecl)] private static extern int secret_password_store_sync(IntPtr schema, [MarshalAs(UnmanagedType.LPUTF8Str)] string collection, [MarshalAs(UnmanagedType.LPUTF8Str)] string label, [MarshalAs(UnmanagedType.LPUTF8Str)] string password, IntPtr cancellable, out IntPtr error, [MarshalAs(UnmanagedType.LPUTF8Str)] string attribute, [MarshalAs(UnmanagedType.LPUTF8Str)] string value, IntPtr end);
        [DllImport(Secret, CallingConvention = CallingConvention.Cdecl)] private static extern IntPtr secret_password_lookup_sync(IntPtr schema, IntPtr cancellable, out IntPtr error, [MarshalAs(UnmanagedType.LPUTF8Str)] string attribute, [MarshalAs(UnmanagedType.LPUTF8Str)] string value, IntPtr end);
        [DllImport(Secret, CallingConvention = CallingConvention.Cdecl)] private static extern int secret_password_clear_sync(IntPtr schema, IntPtr cancellable, out IntPtr error, [MarshalAs(UnmanagedType.LPUTF8Str)] string attribute, [MarshalAs(UnmanagedType.LPUTF8Str)] string value, IntPtr end);
        [DllImport(Secret)] private static extern void secret_password_free(IntPtr value);
        [DllImport("libglib-2.0.so.0")] private static extern void g_error_free(IntPtr error);
        internal static string? Run(string action, string id, string? value)
        {
            var schema = secret_schema_new("io.github.idefav.v2rayN.egress-observer", 0, "id", 0, IntPtr.Zero);
            if (schema == IntPtr.Zero) throw new ObserverStorageException("Secret Service schema unavailable");
            var error = IntPtr.Zero; var result = IntPtr.Zero;
            try
            {
                var ok = 1;
                if (action == "save") ok = secret_password_store_sync(schema, "default", "v2rayN observer device", value!, IntPtr.Zero, out error, "id", id, IntPtr.Zero);
                else if (action == "delete") secret_password_clear_sync(schema, IntPtr.Zero, out error, "id", id, IntPtr.Zero);
                else result = secret_password_lookup_sync(schema, IntPtr.Zero, out error, "id", id, IntPtr.Zero);
                if (error != IntPtr.Zero || ok == 0) throw new ObserverStorageException("Secret Service locked, unavailable or access denied");
                return result == IntPtr.Zero ? null : Marshal.PtrToStringUTF8(result);
            }
            finally { if (result != IntPtr.Zero) secret_password_free(result); if (error != IntPtr.Zero) g_error_free(error); secret_schema_unref(schema); }
        }
    }
}
