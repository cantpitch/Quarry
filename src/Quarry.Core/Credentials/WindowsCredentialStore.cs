using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;

namespace Quarry.Core.Credentials;

/// <summary>Windows Credential Manager (generic credentials), via advapi32.</summary>
[SupportedOSPlatform("windows")]
public sealed partial class WindowsCredentialStore : ICredentialStore
{
    private const int CredTypeGeneric = 1;
    private const int CredPersistLocalMachine = 2;
    private const int ErrorNotFound = 1168;

    public bool IsAvailable => true;

    public unsafe string? Get(string key)
    {
        if (!CredRead(key, CredTypeGeneric, 0, out var credential))
        {
            int error = Marshal.GetLastPInvokeError();
            if (error == ErrorNotFound)
                return null;
            throw new Win32Exception(error);
        }
        try
        {
            var native = (Credential*)credential;
            if (native->CredentialBlobSize == 0)
                return "";
            return Encoding.Unicode.GetString((byte*)native->CredentialBlob, (int)native->CredentialBlobSize);
        }
        finally
        {
            CredFree(credential);
        }
    }

    public unsafe void Set(string key, string secret)
    {
        byte[] blob = Encoding.Unicode.GetBytes(secret);
        fixed (char* target = key)
        fixed (char* user = Environment.UserName)
        fixed (byte* data = blob)
        {
            var credential = new Credential
            {
                Type = CredTypeGeneric,
                TargetName = (nint)target,
                CredentialBlobSize = (uint)blob.Length,
                CredentialBlob = (nint)data,
                Persist = CredPersistLocalMachine,
                UserName = (nint)user,
            };
            if (!CredWrite(ref credential, 0))
                throw new Win32Exception(Marshal.GetLastPInvokeError());
        }
    }

    public void Delete(string key)
    {
        if (!CredDelete(key, CredTypeGeneric, 0))
        {
            int error = Marshal.GetLastPInvokeError();
            if (error != ErrorNotFound)
                throw new Win32Exception(error);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Credential
    {
        public uint Flags;
        public uint Type;
        public nint TargetName;
        public nint Comment;
        public long LastWritten;
        public uint CredentialBlobSize;
        public nint CredentialBlob;
        public uint Persist;
        public uint AttributeCount;
        public nint Attributes;
        public nint TargetAlias;
        public nint UserName;
    }

    [LibraryImport("advapi32.dll", EntryPoint = "CredReadW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CredRead(string target, int type, int flags, out nint credential);

    [LibraryImport("advapi32.dll", EntryPoint = "CredWriteW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CredWrite(ref Credential credential, int flags);

    [LibraryImport("advapi32.dll", EntryPoint = "CredDeleteW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CredDelete(string target, int type, int flags);

    [LibraryImport("advapi32.dll")]
    private static partial void CredFree(nint buffer);
}
