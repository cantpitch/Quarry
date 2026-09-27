using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Quarry.Core.Credentials;

/// <summary>
/// Linux Secret Service (GNOME Keyring, KWallet) via libsecret. Uses the non-variadic
/// <c>*v_sync</c> functions, which take attributes as a GHashTable.
/// </summary>
[SupportedOSPlatform("linux")]
public sealed unsafe partial class LibSecretCredentialStore : ICredentialStore
{
    private const string LibSecret = "libsecret-1.so.0";
    private const string LibGlib = "libglib-2.0.so.0";
    private const string SchemaName = "dev.quarry.Credential";
    private const string AttributeName = "key";

    private readonly Lazy<bool> _available = new(Probe);


    public bool IsAvailable => _available.Value;

    private static bool Probe()
    {
        if (!NativeLibrary.TryLoad(LibSecret, out _) || !NativeLibrary.TryLoad(LibGlib, out _))
            return false;
        // The library can be installed without a running Secret Service (headless servers, CI);
        // a lookup fails fast in that case.
        try
        {
            new LibSecretCredentialStore(probe: false).Get("Quarry/probe");
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    public LibSecretCredentialStore() : this(probe: true) { }

    private LibSecretCredentialStore(bool probe)
    {
        if (!probe)
            _available = new Lazy<bool>(true);
    }

    public string? Get(string key)
    {
        using var schema = new Schema();
        nint attributes = CreateAttributes(key, out var strings);
        try
        {
            nint error = 0;
            nint result = secret_password_lookupv_sync(schema.Pointer, attributes, 0, &error);
            ThrowIfError(error);
            if (result == 0)
                return null;
            try
            {
                return Marshal.PtrToStringUTF8(result);
            }
            finally
            {
                secret_password_free(result);
            }
        }
        finally
        {
            g_hash_table_unref(attributes);
            strings.Free();
        }
    }

    public void Set(string key, string secret)
    {
        using var schema = new Schema();
        nint attributes = CreateAttributes(key, out var strings);
        nint label = Marshal.StringToCoTaskMemUTF8($"Quarry connection {key}");
        nint password = Marshal.StringToCoTaskMemUTF8(secret);
        try
        {
            nint error = 0;
            secret_password_storev_sync(schema.Pointer, attributes, 0, label, password, 0, &error);
            ThrowIfError(error);
        }
        finally
        {
            g_hash_table_unref(attributes);
            strings.Free();
            Marshal.FreeCoTaskMem(label);
            Marshal.ZeroFreeCoTaskMemUTF8(password);
        }
    }

    public void Delete(string key)
    {
        using var schema = new Schema();
        nint attributes = CreateAttributes(key, out var strings);
        try
        {
            nint error = 0;
            secret_password_clearv_sync(schema.Pointer, attributes, 0, &error);
            ThrowIfError(error);
        }
        finally
        {
            g_hash_table_unref(attributes);
            strings.Free();
        }
    }

    private static nint CreateAttributes(string key, out NativeStrings strings)
    {
        var glib = NativeLibrary.Load(LibGlib);
        nint hash = NativeLibrary.GetExport(glib, "g_str_hash");
        nint equal = NativeLibrary.GetExport(glib, "g_str_equal");
        nint table = g_hash_table_new(hash, equal);
        strings = new NativeStrings(Marshal.StringToCoTaskMemUTF8(AttributeName), Marshal.StringToCoTaskMemUTF8(key));
        g_hash_table_insert(table, strings.Name, strings.Value);
        return table;
    }

    private static void ThrowIfError(nint error)
    {
        if (error == 0)
            return;
        // struct GError { GQuark domain; gint code; gchar *message; }
        string? message = Marshal.PtrToStringUTF8(*(nint*)(error + 8));
        g_error_free(error);
        throw new InvalidOperationException($"Secret Service error: {message}");
    }

    private readonly record struct NativeStrings(nint Name, nint Value)
    {
        public void Free()
        {
            Marshal.FreeCoTaskMem(Name);
            Marshal.FreeCoTaskMem(Value);
        }
    }

    /// <summary>
    /// Unmanaged SecretSchema:
    /// { const gchar *name; SecretSchemaFlags flags; SecretSchemaAttribute attributes[32];
    ///   gint reserved; gpointer reserved1..reserved7; }
    /// where SecretSchemaAttribute is { const gchar *name; SecretSchemaAttributeType type; }.
    /// </summary>
    private sealed class Schema : IDisposable
    {
        private const int AttributeSize = 16; // pointer + enum, padded
        private const int Size = 8 + 8 + 32 * AttributeSize + 8 + 7 * 8;

        private readonly nint _name = Marshal.StringToCoTaskMemUTF8(SchemaName);
        private readonly nint _attributeName = Marshal.StringToCoTaskMemUTF8(AttributeName);

        public Schema()
        {
            Pointer = (nint)NativeMemory.AllocZeroed(Size);
            *(nint*)Pointer = _name;
            *(int*)(Pointer + 8) = 0; // SECRET_SCHEMA_NONE
            *(nint*)(Pointer + 16) = _attributeName;
            *(int*)(Pointer + 24) = 0; // SECRET_SCHEMA_ATTRIBUTE_STRING
            // The remaining attribute entries stay zeroed, which terminates the list.
        }

        public nint Pointer { get; }

        public void Dispose()
        {
            NativeMemory.Free((void*)Pointer);
            Marshal.FreeCoTaskMem(_name);
            Marshal.FreeCoTaskMem(_attributeName);
        }
    }

    [LibraryImport(LibSecret)]
    private static partial nint secret_password_lookupv_sync(nint schema, nint attributes, nint cancellable, nint* error);

    [LibraryImport(LibSecret)]
    private static partial int secret_password_storev_sync(nint schema, nint attributes, nint collection, nint label, nint password, nint cancellable, nint* error);

    [LibraryImport(LibSecret)]
    private static partial int secret_password_clearv_sync(nint schema, nint attributes, nint cancellable, nint* error);

    [LibraryImport(LibSecret)]
    private static partial void secret_password_free(nint password);

    [LibraryImport(LibGlib)]
    private static partial nint g_hash_table_new(nint hashFunc, nint keyEqualFunc);

    [LibraryImport(LibGlib)]
    [return: MarshalAs(UnmanagedType.I4)]
    private static partial int g_hash_table_insert(nint table, nint key, nint value);

    [LibraryImport(LibGlib)]
    private static partial void g_hash_table_unref(nint table);

    [LibraryImport(LibGlib)]
    private static partial void g_error_free(nint error);
}
