using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;

namespace Quarry.Core.Credentials;

/// <summary>macOS login keychain (generic passwords), via Security.framework.</summary>
[SupportedOSPlatform("macos")]
public sealed partial class MacKeychainCredentialStore : ICredentialStore
{
    private const string Security = "/System/Library/Frameworks/Security.framework/Security";
    private const string CoreFoundation = "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";
    private const int ErrSecItemNotFound = -25300;
    private const string Service = "Quarry";

    public bool IsAvailable
    {
        get
        {
            try
            {
                return NativeLibrary.TryLoad(Security, out _);
            }
            catch
            {
                return false;
            }
        }
    }

    public unsafe string? Get(string key)
    {
        byte[] service = Encoding.UTF8.GetBytes(Service);
        byte[] account = Encoding.UTF8.GetBytes(key);
        int status = SecKeychainFindGenericPassword(0, (uint)service.Length, service, (uint)account.Length, account,
            out uint length, out nint data, out nint item);
        if (status == ErrSecItemNotFound)
            return null;
        Check(status);
        try
        {
            return Encoding.UTF8.GetString((byte*)data, (int)length);
        }
        finally
        {
            SecKeychainItemFreeContent(0, data);
            if (item != 0)
                CFRelease(item);
        }
    }

    public void Set(string key, string secret)
    {
        byte[] service = Encoding.UTF8.GetBytes(Service);
        byte[] account = Encoding.UTF8.GetBytes(key);
        byte[] password = Encoding.UTF8.GetBytes(secret);

        int status = SecKeychainFindGenericPassword(0, (uint)service.Length, service, (uint)account.Length, account,
            out _, out nint existingData, out nint item);
        if (status == 0)
        {
            try
            {
                SecKeychainItemFreeContent(0, existingData);
                Check(SecKeychainItemModifyAttributesAndData(item, 0, (uint)password.Length, password));
            }
            finally
            {
                CFRelease(item);
            }
            return;
        }
        if (status != ErrSecItemNotFound)
            Check(status);

        Check(SecKeychainAddGenericPassword(0, (uint)service.Length, service, (uint)account.Length, account,
            (uint)password.Length, password, out nint added));
        if (added != 0)
            CFRelease(added);
    }

    public void Delete(string key)
    {
        byte[] service = Encoding.UTF8.GetBytes(Service);
        byte[] account = Encoding.UTF8.GetBytes(key);
        int status = SecKeychainFindGenericPassword(0, (uint)service.Length, service, (uint)account.Length, account,
            out _, out nint data, out nint item);
        if (status == ErrSecItemNotFound)
            return;
        Check(status);
        try
        {
            SecKeychainItemFreeContent(0, data);
            Check(SecKeychainItemDelete(item));
        }
        finally
        {
            CFRelease(item);
        }
    }

    private static void Check(int status)
    {
        if (status != 0)
            throw new InvalidOperationException($"Keychain operation failed (OSStatus {status}).");
    }

    // The SecKeychain* generic password API is deprecated but still supported, and far simpler
    // to call than SecItem*, which needs CFDictionary marshalling.
    [LibraryImport(Security)]
    private static partial int SecKeychainFindGenericPassword(nint keychainOrArray,
        uint serviceNameLength, byte[] serviceName, uint accountNameLength, byte[] accountName,
        out uint passwordLength, out nint passwordData, out nint itemRef);

    [LibraryImport(Security)]
    private static partial int SecKeychainAddGenericPassword(nint keychain,
        uint serviceNameLength, byte[] serviceName, uint accountNameLength, byte[] accountName,
        uint passwordLength, byte[] passwordData, out nint itemRef);

    [LibraryImport(Security)]
    private static partial int SecKeychainItemModifyAttributesAndData(nint itemRef, nint attrList, uint length, byte[] data);

    [LibraryImport(Security)]
    private static partial int SecKeychainItemFreeContent(nint attrList, nint data);

    [LibraryImport(Security)]
    private static partial int SecKeychainItemDelete(nint itemRef);

    [LibraryImport(CoreFoundation)]
    private static partial void CFRelease(nint cf);
}
