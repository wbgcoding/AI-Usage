using System.Runtime.InteropServices;
using System.Security.Cryptography.X509Certificates;

namespace AiUsage.Web;

/// <summary>
/// Whether a file carries a valid Authenticode signature of a named signer. Two separate facts, both
/// required: Windows' own trust check (<c>WinVerifyTrust</c>) says the signature is intact and
/// chains to a trusted root, no certificate of the chain is revoked and the file has not been changed
/// since signing, and the signer's name is the one expected - a valid signature of somebody else does
/// not count. An unreachable revocation server only passes while the machine has no internet.
/// </summary>
internal static class AuthenticodeSignature
{
    internal static SignatureCheckResult Check(string path) => Check(path, WebViewRuntimeInstaller.RequiredSigner);

    internal static SignatureCheckResult Check(string path, string requiredSigner)
    {
        var trust = VerifyTrust(path);
        if (!IsTrustResultAcceptable(trust, Services.NetworkStatus.HasInternet()))
            return new SignatureCheckResult(false, $"signature: WinVerifyTrust 0x{trust:X8}");

        return ReadSignerNames(path).Any(name => string.Equals(name, requiredSigner, StringComparison.OrdinalIgnoreCase))
            ? new SignatureCheckResult(true, "")
            : new SignatureCheckResult(false, "signature: signer is not the expected publisher");
    }

    /// <summary>Maps the trust check's answer to pass or fail: only 0 passes, plus the one case where
    /// the revocation server could not be reached and the machine really is offline. Online, an
    /// unreachable revocation server is a failure like any other.</summary>
    internal static bool IsTrustResultAcceptable(int trustResult, bool hasInternet) =>
        trustResult == 0 || (trustResult == CertERevocationFailure && !hasInternet);

    /// <summary>The signer's common name and organisation: Microsoft signs some products with the
    /// company name as the common name and others (the .NET runtime files) with a product name there
    /// and the company only as the organisation, so both count. Both come out of a certificate that
    /// Windows has already chained to a trusted root.</summary>
    private static List<string> ReadSignerNames(string path)
    {
        try
        {
#pragma warning disable SYSLIB0057 // No replacement reads the signer of a signed file; WinVerifyTrust above has already proven the signature.
            var certificate = X509Certificate.CreateFromSignedFile(path);
#pragma warning restore SYSLIB0057
            using var certificate2 = new X509Certificate2(certificate);
            var names = new List<string>();
            foreach (var part in certificate2.SubjectName.EnumerateRelativeDistinguishedNames())
            {
                var type = part.GetSingleElementType().Value;
                if (type is "2.5.4.3" or "2.5.4.10" && part.GetSingleElementValue() is { } value)
                    names.Add(value);
            }
            return names;
        }
        catch (System.Security.Cryptography.CryptographicException)
        {
            return [];
        }
    }

    private static readonly Guid GenericVerifyV2 = new("00AAC56B-CD44-11d0-8CC2-00C04FC295EE");

    private const uint WtdUiNone = 2;
    // The two values that make the trust check ask for revocation of the whole chain (WTD_REVOKE_WHOLECHAIN
    // and WTD_REVOCATION_CHECK_CHAIN); internal so a test can pin them.
    internal const uint WtdRevokeWholeChain = 1;
    private const int CertERevocationFailure = unchecked((int)0x800B010E);
    private const uint WtdChoiceFile = 1;
    private const uint WtdStateActionVerify = 1;
    private const uint WtdStateActionClose = 2;
    internal const uint WtdRevocationCheckChain = 0x40;

    [StructLayout(LayoutKind.Sequential)]
    private struct WintrustFileInfo
    {
        public uint StructSize;
        public IntPtr FilePath;
        public IntPtr File;
        public IntPtr KnownSubject;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WintrustData
    {
        public uint StructSize;
        public IntPtr PolicyCallbackData;
        public IntPtr SipClientData;
        public uint UiChoice;
        public uint RevocationChecks;
        public uint UnionChoice;
        public IntPtr FileInfo;
        public uint StateAction;
        public IntPtr StateData;
        public IntPtr UrlReference;
        public uint ProvFlags;
        public uint UiContext;
        public IntPtr SignatureSettings;
    }

    [DllImport("wintrust.dll", ExactSpelling = true, SetLastError = false)]
    private static extern int WinVerifyTrust(IntPtr window, [MarshalAs(UnmanagedType.LPStruct)] Guid action, ref WintrustData data);

    /// <summary>0 when Windows trusts the file's signature, otherwise the HRESULT it answered.</summary>
    private static int VerifyTrust(string path)
    {
        var pathPointer = Marshal.StringToHGlobalUni(path);
        var fileInfoPointer = IntPtr.Zero;
        try
        {
            var fileInfo = new WintrustFileInfo
            {
                StructSize = (uint)Marshal.SizeOf<WintrustFileInfo>(),
                FilePath = pathPointer,
            };
            fileInfoPointer = Marshal.AllocHGlobal(Marshal.SizeOf<WintrustFileInfo>());
            Marshal.StructureToPtr(fileInfo, fileInfoPointer, fDeleteOld: false);

            var data = new WintrustData
            {
                StructSize = (uint)Marshal.SizeOf<WintrustData>(),
                UiChoice = WtdUiNone,
                RevocationChecks = WtdRevokeWholeChain,
                UnionChoice = WtdChoiceFile,
                FileInfo = fileInfoPointer,
                StateAction = WtdStateActionVerify,
                ProvFlags = WtdRevocationCheckChain,
            };

            var result = WinVerifyTrust(IntPtr.Zero, GenericVerifyV2, ref data);

            // The verify call keeps state that only the close call frees.
            data.StateAction = WtdStateActionClose;
            _ = WinVerifyTrust(IntPtr.Zero, GenericVerifyV2, ref data);
            return result;
        }
        finally
        {
            if (fileInfoPointer != IntPtr.Zero)
                Marshal.FreeHGlobal(fileInfoPointer);
            Marshal.FreeHGlobal(pathPointer);
        }
    }
}
