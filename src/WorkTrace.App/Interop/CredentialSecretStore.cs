using System.Runtime.InteropServices;
using System.Text;
using WorkTrace.Core.Board;

namespace WorkTrace.App.Interop;

/// <summary>
/// Secrets in Windows Credential Manager (generic credentials, per user, encrypted by Windows). Visible under
/// Control Panel › Credential Manager › Windows Credentials as "WorkTrace/…". Never written to settings or backups.
/// </summary>
internal sealed partial class CredentialSecretStore : ISecretStore
{
    private const int CredTypeGeneric = 1;
    private const int CredPersistLocalMachine = 2;

    public string? Read(string name)
    {
        if (!CredReadW(name, CredTypeGeneric, 0, out var pointer))
        {
            return null;
        }

        try
        {
            var credential = Marshal.PtrToStructure<Credential>(pointer);
            return credential.CredentialBlobSize == 0
                ? null
                : Marshal.PtrToStringUni(credential.CredentialBlob, (int)credential.CredentialBlobSize / 2);
        }
        finally
        {
            CredFree(pointer);
        }
    }

    public void Write(string name, string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            _ = CredDeleteW(name, CredTypeGeneric, 0);
            return;
        }

        var bytes = Encoding.Unicode.GetBytes(value);
        var blob = Marshal.AllocHGlobal(bytes.Length);
        var target = Marshal.StringToHGlobalUni(name);
        var user = Marshal.StringToHGlobalUni(Environment.UserName);
        try
        {
            Marshal.Copy(bytes, 0, blob, bytes.Length);
            var credential = new Credential
            {
                Type = CredTypeGeneric,
                TargetName = target,
                CredentialBlobSize = (uint)bytes.Length,
                CredentialBlob = blob,
                Persist = CredPersistLocalMachine,
                UserName = user,
            };
            if (!CredWriteW(ref credential, 0))
            {
                throw new InvalidOperationException($"Windows Credential Manager refused the key (error {Marshal.GetLastPInvokeError()}).");
            }
        }
        finally
        {
            Array.Clear(bytes);
            Marshal.FreeHGlobal(blob);
            Marshal.FreeHGlobal(target);
            Marshal.FreeHGlobal(user);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Credential
    {
        public uint Flags;
        public int Type;
        public IntPtr TargetName;
        public IntPtr Comment;
        public long LastWritten;
        public uint CredentialBlobSize;
        public IntPtr CredentialBlob;
        public int Persist;
        public uint AttributeCount;
        public IntPtr Attributes;
        public IntPtr TargetAlias;
        public IntPtr UserName;
    }

    [LibraryImport("advapi32.dll", EntryPoint = "CredReadW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CredReadW(string target, int type, int flags, out IntPtr credential);

    [LibraryImport("advapi32.dll", EntryPoint = "CredWriteW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CredWriteW(ref Credential credential, int flags);

    [LibraryImport("advapi32.dll", EntryPoint = "CredDeleteW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CredDeleteW(string target, int type, int flags);

    [LibraryImport("advapi32.dll", EntryPoint = "CredFree")]
    private static partial void CredFree(IntPtr buffer);
}
