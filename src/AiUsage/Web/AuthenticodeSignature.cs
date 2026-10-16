using System.Runtime.InteropServices;
using System.Security.Cryptography.X509Certificates;

namespace AiUsage.Web;

/// <summary>
/// Whether a file carries a valid Authenticode signature of a named signer. Two separate facts, both
/// required: Windows' own trust check (<c>WinVerifyTrust</c>) says the signature is intact and
/// chains to a trusted root, no certificate of the chain is revoked and the file has not been changed
/// since signing, the signer's name is the one expected - a valid signature of somebody else does
/// not count - and the signer's chain ends in one of Microsoft's own root certificates, so a root added
/// to this machine's store cannot vouch for a leaf that merely names Microsoft. An unreachable
/// revocation server only passes while the machine has no internet.
/// </summary>
internal static class AuthenticodeSignature
{
    // Microsoft's Root Certificate Authority 2010 and 2011 (SHA-1 thumbprints): the roots the bootstrapper
    // chains to. The same two roots installer/RuntimeSetup.ps1 pins for the .NET runtime download
    // ($MicrosoftRoots); a change to one list is a change to the other.
    internal static readonly string[] PinnedRootThumbprints =
    [
        "3B1EFD3A66EA28B16697394703A72CA340A05BD5",
        "8F43288AD272F3103B6FB1428485EA3014C0BCFE",
    ];

    internal static SignatureCheckResult Check(string path) => Check(path, WebViewRuntimeInstaller.RequiredSigner);

    internal static SignatureCheckResult Check(string path, string requiredSigner)
    {
        var trust = VerifyTrust(path);
        if (!IsTrustResultAcceptable(trust, Services.NetworkStatus.HasInternet()))
            return new SignatureCheckResult(false, $"signature: WinVerifyTrust 0x{trust:X8}");

        if (!ReadSignerNames(path).Any(name => string.Equals(name, requiredSigner, StringComparison.OrdinalIgnoreCase)))
            return new SignatureCheckResult(false, "signature: signer is not the expected publisher");

        return IsPinnedRoot(ReadChainRootThumbprint(path))
            ? new SignatureCheckResult(true, "")
            : new SignatureCheckResult(false, "signature: the signer's chain does not end in a Microsoft root");
    }

    /// <summary>True when <paramref name="thumbprint"/> is one of <see cref="PinnedRootThumbprints"/>.</summary>
    internal static bool IsPinnedRoot(string? thumbprint) =>
        thumbprint is not null && PinnedRootThumbprints.Contains(thumbprint, StringComparer.OrdinalIgnoreCase);

    /// <summary>The thumbprint of the topmost certificate in the chain built for the file's signer; null
    /// when the signer cannot be read or no chain can be built. Revocation was asked for by the trust
    /// check above, so none is repeated here, and a signer certificate that has expired since signing
    /// still has its chain read: the signature is timestamped, the root is what is asked for.</summary>
    private static string? ReadChainRootThumbprint(string path)
    {
        try
        {
#pragma warning disable SYSLIB0057 // No replacement reads the signer of a signed file; WinVerifyTrust above has already proven the signature.
            var certificate = X509Certificate.CreateFromSignedFile(path);
#pragma warning restore SYSLIB0057
            using var signer = new X509Certificate2(certificate);
            return ChainRootThumbprint(signer, trustedRoots: null, EmbeddedCertificates(path));
        }
        catch (System.Security.Cryptography.CryptographicException)
        {
            return null;
        }
    }

    /// <summary>The certificates the file's signature carries beside the signer's own (its
    /// intermediates), so the chain reaches the root without the machine having to know or download
    /// them. Empty when the file carries none or they cannot be read.</summary>
    internal static X509Certificate2Collection EmbeddedCertificates(string path)
    {
        var certificates = new X509Certificate2Collection();
        try
        {
#pragma warning disable SYSLIB0057 // No loader reads the certificates of a signed file; they only help build the chain, trust still comes from the pinned root.
            certificates.Import(path);
#pragma warning restore SYSLIB0057
        }
        catch (System.Security.Cryptography.CryptographicException)
        {
            // Nothing embedded to add: the chain is built from the machine's store alone.
        }

        return certificates;
    }

    /// <summary>Builds the chain of <paramref name="signer"/> and returns the thumbprint of its last
    /// certificate. With <paramref name="trustedRoots"/> the chain is judged against exactly those roots
    /// instead of the machine's store (a test seam: a made-up root for a made-up signer).
    /// <paramref name="extraCertificates"/> are offered as possible intermediates.</summary>
    internal static string? ChainRootThumbprint(
        X509Certificate2 signer, X509Certificate2Collection? trustedRoots, X509Certificate2Collection? extraCertificates = null)
    {
        using var chain = new X509Chain();
        if (extraCertificates is not null)
            chain.ChainPolicy.ExtraStore.AddRange(extraCertificates);
        chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
        chain.ChainPolicy.VerificationFlags = X509VerificationFlags.AllFlags;
        if (trustedRoots is not null)
        {
            chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
            chain.ChainPolicy.CustomTrustStore.AddRange(trustedRoots);
        }

        chain.Build(signer);
        return chain.ChainElements.Count > 0 ? chain.ChainElements[^1].Certificate.Thumbprint : null;
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
            using var certificate = X509Certificate.CreateFromSignedFile(path);
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
