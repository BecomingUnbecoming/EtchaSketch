using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace EtchaSketch.Data.Storage.KeyStores;

/// <summary>Keeps keys in the macOS Keychain as generic passwords, through Apple's Security framework.</summary>
[SupportedOSPlatform("macos")]
public sealed class MacKeyStore : IKeyStore
{
    private const string Service = "EtchaSketch";

    public byte[]? Load(string name)
    {
        using var query = Query(KeyNames.Validate(name),
            (Native.ReturnData, Native.True),
            (Native.MatchLimit, Native.MatchLimitOne));

        var status = Native.SecItemCopyMatching(query.Handle, out var data);
        if (status == Native.ItemNotFound)
        {
            return null;
        }

        Native.Check(status, "read a key from");
        try
        {
            var bytes = new byte[Native.CFDataGetLength(data)];
            Marshal.Copy(Native.CFDataGetBytePtr(data), bytes, 0, bytes.Length);
            return bytes;
        }
        finally
        {
            Native.CFRelease(data);
        }
    }

    public void Save(string name, byte[] key)
    {
        using var value = new CFObject(Native.CFDataCreate(IntPtr.Zero, key, key.Length));
        using var query = Query(KeyNames.Validate(name));
        using var update = CFObject.Dictionary((Native.ValueData, value.Handle));

        var status = Native.SecItemUpdate(query.Handle, update.Handle);
        if (status == Native.ItemNotFound)
        {
            using var item = Query(name, (Native.ValueData, value.Handle));
            status = Native.SecItemAdd(item.Handle, IntPtr.Zero);
        }

        Native.Check(status, "save a key to");
    }

    public void Delete(string name)
    {
        using var query = Query(KeyNames.Validate(name));
        var status = Native.SecItemDelete(query.Handle);
        if (status != Native.ItemNotFound)
        {
            Native.Check(status, "delete a key from");
        }
    }

    private static CFObject Query(string name, params (IntPtr Key, IntPtr Value)[] extra)
    {
        using var service = CFObject.String(Service);
        using var account = CFObject.String(name);
        return CFObject.Dictionary(
            [(Native.Class, Native.ClassGenericPassword), (Native.Service, service.Handle), (Native.Account, account.Handle), .. extra]);
    }

    /// <summary>Owns one Core Foundation reference and releases it when disposed.</summary>
    private sealed class CFObject(IntPtr handle) : IDisposable
    {
        public IntPtr Handle { get; } = handle != IntPtr.Zero
            ? handle
            : throw new InvalidOperationException("Core Foundation couldn't allocate an object.");

        public static CFObject String(string value) =>
            new(Native.CFStringCreateWithCString(IntPtr.Zero, value, Native.Utf8Encoding));

        // The dictionary retains its keys and values, so callers can release theirs afterwards.
        public static CFObject Dictionary(params (IntPtr Key, IntPtr Value)[] entries) =>
            new(Native.CFDictionaryCreate(
                IntPtr.Zero,
                entries.Select(entry => entry.Key).ToArray(),
                entries.Select(entry => entry.Value).ToArray(),
                entries.Length,
                Native.DictionaryKeyCallBacks,
                Native.DictionaryValueCallBacks));

        public void Dispose() => Native.CFRelease(Handle);
    }

    private static class Native
    {
        private const string SecurityPath = "/System/Library/Frameworks/Security.framework/Security";
        private const string CoreFoundationPath = "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";

        public const int ItemNotFound = -25300;
        public const uint Utf8Encoding = 0x08000100;

        private static readonly IntPtr Security = NativeLibrary.Load(SecurityPath);
        private static readonly IntPtr CoreFoundation = NativeLibrary.Load(CoreFoundationPath);

        public static readonly IntPtr Class = Constant(Security, "kSecClass");
        public static readonly IntPtr ClassGenericPassword = Constant(Security, "kSecClassGenericPassword");
        public static readonly IntPtr Service = Constant(Security, "kSecAttrService");
        public static readonly IntPtr Account = Constant(Security, "kSecAttrAccount");
        public static readonly IntPtr ValueData = Constant(Security, "kSecValueData");
        public static readonly IntPtr ReturnData = Constant(Security, "kSecReturnData");
        public static readonly IntPtr MatchLimit = Constant(Security, "kSecMatchLimit");
        public static readonly IntPtr MatchLimitOne = Constant(Security, "kSecMatchLimitOne");
        public static readonly IntPtr True = Constant(CoreFoundation, "kCFBooleanTrue");

        // These two are structs, so the dictionary wants their addresses rather than their contents.
        public static readonly IntPtr DictionaryKeyCallBacks = NativeLibrary.GetExport(CoreFoundation, "kCFTypeDictionaryKeyCallBacks");
        public static readonly IntPtr DictionaryValueCallBacks = NativeLibrary.GetExport(CoreFoundation, "kCFTypeDictionaryValueCallBacks");

        // Each exported constant is a variable holding a pointer, so read the pointer out of it.
        private static IntPtr Constant(IntPtr library, string symbol) =>
            Marshal.ReadIntPtr(NativeLibrary.GetExport(library, symbol));

        public static void Check(int status, string action)
        {
            if (status != 0)
            {
                throw new InvalidOperationException($"Couldn't {action} the Keychain (OSStatus {status}).");
            }
        }

        [DllImport(SecurityPath)]
        public static extern int SecItemCopyMatching(IntPtr query, out IntPtr result);

        [DllImport(SecurityPath)]
        public static extern int SecItemAdd(IntPtr attributes, IntPtr result);

        [DllImport(SecurityPath)]
        public static extern int SecItemUpdate(IntPtr query, IntPtr attributesToUpdate);

        [DllImport(SecurityPath)]
        public static extern int SecItemDelete(IntPtr query);

        [DllImport(CoreFoundationPath)]
        public static extern IntPtr CFDictionaryCreate(IntPtr allocator, IntPtr[] keys, IntPtr[] values, nint count, IntPtr keyCallBacks, IntPtr valueCallBacks);

        [DllImport(CoreFoundationPath)]
        public static extern IntPtr CFDataCreate(IntPtr allocator, byte[] bytes, nint length);

        [DllImport(CoreFoundationPath)]
        public static extern nint CFDataGetLength(IntPtr data);

        [DllImport(CoreFoundationPath)]
        public static extern IntPtr CFDataGetBytePtr(IntPtr data);

        [DllImport(CoreFoundationPath, CharSet = CharSet.Ansi)]
        public static extern IntPtr CFStringCreateWithCString(IntPtr allocator, string value, uint encoding);

        [DllImport(CoreFoundationPath)]
        public static extern void CFRelease(IntPtr handle);
    }
}
