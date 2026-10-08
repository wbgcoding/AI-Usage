using System.Runtime.InteropServices;
using System.Text;

namespace AiUsage.Providers.LocalLogin;

/// <summary>
/// Reads one entry out of the Windows Credential Manager - the generic store where a signed-in tool
/// (Antigravity) keeps its own OAuth token. Read only: this class never writes, updates or deletes a
/// credential, and never logs the value it reads. The token it returns is used in memory for a
/// single usage fetch and then dropped.
/// </summary>
internal static class WindowsCredential
{
    private const int CredTypeGeneric = 1;

    /// <summary>Returns the credential blob for <paramref name="targetName"/> decoded as UTF-8, or
    /// null when there is no such credential or it cannot be read. Never throws.</summary>
    public static string? ReadUtf8(string targetName)
    {
        var bytes = ReadBlob(targetName);
        if (bytes is null || bytes.Length == 0)
            return null;

        // The Antigravity credential is stored as UTF-8 JSON. A blob that is not valid UTF-8 simply
        // will not parse downstream, which is handled the same as "no credential".
        try
        {
            return Encoding.UTF8.GetString(bytes);
        }
        finally
        {
            Array.Clear(bytes);
        }
    }

    private static byte[]? ReadBlob(string targetName)
    {
        var handle = IntPtr.Zero;
        try
        {
            if (!CredReadW(targetName, CredTypeGeneric, 0, out handle) || handle == IntPtr.Zero)
                return null;

            var credential = Marshal.PtrToStructure<CREDENTIALW>(handle);
            if (credential.CredentialBlobSize == 0 || credential.CredentialBlob == IntPtr.Zero)
                return null;

            var bytes = new byte[credential.CredentialBlobSize];
            Marshal.Copy(credential.CredentialBlob, bytes, 0, bytes.Length);
            return bytes;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or MarshalDirectiveException)
        {
            return null;
        }
        finally
        {
            if (handle != IntPtr.Zero)
                CredFree(handle);
        }
    }

    [DllImport("advapi32.dll", EntryPoint = "CredReadW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CredReadW(string target, int type, int reservedFlag, out IntPtr credentialPtr);

    [DllImport("advapi32.dll", EntryPoint = "CredFree")]
    private static extern void CredFree(IntPtr buffer);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct CREDENTIALW
    {
        public int Flags;
        public int Type;
        public IntPtr TargetName;
        public IntPtr Comment;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastWritten;
        public int CredentialBlobSize;
        public IntPtr CredentialBlob;
        public int Persist;
        public int AttributeCount;
        public IntPtr Attributes;
        public IntPtr TargetAlias;
        public IntPtr UserName;
    }
}
