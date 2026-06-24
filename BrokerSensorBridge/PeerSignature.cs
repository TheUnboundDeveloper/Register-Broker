using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace BrokerSensorBridge;

/*---------------------------------------------------------------------------*\
| PeerSignature                                                              |
|                                                                            |
|   Confirms that a client binary carries an intact Authenticode signature   |
|   and extracts the signing certificate's thumbprint, so the broker can     |
|   pin authorization to a specific signed binary (see ClientAuthorization). |
|                                                                            |
|   WinVerifyTrust does the real work: it hashes the image and verifies the   |
|   embedded PKCS#7 against that hash, so a *tampered* file fails             |
|   (TRUST_E_BAD_DIGEST) and an *unsigned* file fails (TRUST_E_NOSIGNATURE).  |
|                                                                            |
|   Root-trust is deliberately optional. We accept CERT_E_UNTRUSTEDROOT and   |
|   authorize on an exact thumbprint match instead of requiring the signer    |
|   to chain to a machine-trusted CA. That pins the *specific* cert (a dev    |
|   test cert or, later, an EV cert) without installing a self-signed root    |
|   into LocalMachine\Root — which would let that cert vouch for arbitrary    |
|   code. Integrity (tamper/unsigned) is still enforced; only chain trust is  |
|   relaxed, and the thumbprint pin is what actually grants access.           |
\*---------------------------------------------------------------------------*/
internal static class PeerSignature
{
    private static Guid WINTRUST_ACTION_GENERIC_VERIFY_V2 = new("00AAC56B-CD44-11D0-8CC2-00C04FC295EE");

    private const uint WTD_UI_NONE            = 2;
    private const uint WTD_REVOKE_NONE        = 0;
    private const uint WTD_CHOICE_FILE        = 1;
    private const uint WTD_STATEACTION_VERIFY = 1;
    private const uint WTD_STATEACTION_CLOSE  = 2;
    private const uint WTD_SAFER_FLAG         = 0x100;

    private const int S_OK                 = 0;
    private const int CERT_E_UNTRUSTEDROOT = unchecked((int)0x800B0109);

    [StructLayout(LayoutKind.Sequential)]
    private struct WINTRUST_FILE_INFO
    {
        public uint cbStruct;
        [MarshalAs(UnmanagedType.LPWStr)] public string pcwszFilePath;
        public IntPtr hFile;
        public IntPtr pgKnownSubject;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WINTRUST_DATA
    {
        public uint   cbStruct;
        public IntPtr pPolicyCallbackData;
        public IntPtr pSIPClientData;
        public uint   dwUIChoice;
        public uint   fdwRevocationChecks;
        public uint   dwUnionChoice;
        public IntPtr pFile;              // union member -> WINTRUST_FILE_INFO*
        public uint   dwStateAction;
        public IntPtr hWVTStateData;
        public IntPtr pwszURLReference;
        public uint   dwProvFlags;
        public uint   dwUIContext;
        public IntPtr pSignatureSettings;
    }

    [DllImport("wintrust.dll", CharSet = CharSet.Unicode, SetLastError = false)]
    private static extern int WinVerifyTrust(IntPtr hWnd, ref Guid pgActionID, IntPtr pWVTData);

    /// <summary>
    /// True if <paramref name="imagePath"/> has an intact Authenticode signature.
    /// On success, <paramref name="thumbprint"/> is the signer's SHA-1 thumbprint and
    /// <paramref name="thumbprintSha256"/> the SHA-256 thumbprint (both uppercase hex,
    /// matching certmgr); pin on the SHA-256 value going forward (SHA-1 is collision-weak,
    /// kept only for back-compat with existing allowlists). <paramref name="chainTrusted"/>
    /// tells whether the cert chained to a trusted root (false for a pinned test cert).
    /// Returns false for unsigned, tampered, expired, or revoked images.
    /// </summary>
    public static bool TryGetSigner(string? imagePath, out string? thumbprint, out string? thumbprintSha256,
                                    out string? subject, out bool chainTrusted)
    {
        thumbprint = null;
        thumbprintSha256 = null;
        subject = null;
        chainTrusted = false;

        try
        {
            if (string.IsNullOrWhiteSpace(imagePath) || !File.Exists(imagePath))
                return false;

            int status = VerifyFile(imagePath);
            if (status == S_OK)
                chainTrusted = true;
            else if (status == CERT_E_UNTRUSTEDROOT)
                chainTrusted = false;            // intact signature, untrusted root -> pin by thumbprint
            else
                return false;                    // NOSIGNATURE / BAD_DIGEST / EXPIRED / REVOKED -> reject

            // CreateFromSignedFile is the only managed API that extracts the Authenticode SIGNER
            // certificate from a signed PE. X509CertificateLoader (the SYSLIB0057 replacement) loads
            // certificate FILES and has no PE-signature equivalent, so suppress the deprecation rather
            // than drop to raw CryptQueryObject P/Invoke in this security-sensitive pin path. The
            // WinTrust check above already validated the signature before we read the signer.
#pragma warning disable SYSLIB0057
            using var raw = X509Certificate.CreateFromSignedFile(imagePath);
#pragma warning restore SYSLIB0057
            thumbprint = raw.GetCertHashString();                          // SHA-1, uppercase hex
            thumbprintSha256 = raw.GetCertHashString(HashAlgorithmName.SHA256); // SHA-256, uppercase hex
            subject = raw.Subject;
            return !string.IsNullOrEmpty(thumbprint);
        }
        catch
        {
            thumbprint = null;
            thumbprintSha256 = null;
            subject = null;
            chainTrusted = false;
            return false;
        }
    }

    private static int VerifyFile(string path)
    {
        var fileInfo = new WINTRUST_FILE_INFO
        {
            cbStruct = (uint)Marshal.SizeOf<WINTRUST_FILE_INFO>(),
            pcwszFilePath = path,
            hFile = IntPtr.Zero,
            pgKnownSubject = IntPtr.Zero
        };

        IntPtr pFile = Marshal.AllocHGlobal(Marshal.SizeOf<WINTRUST_FILE_INFO>());
        IntPtr pData = Marshal.AllocHGlobal(Marshal.SizeOf<WINTRUST_DATA>());
        try
        {
            Marshal.StructureToPtr(fileInfo, pFile, false);

            var data = new WINTRUST_DATA
            {
                cbStruct            = (uint)Marshal.SizeOf<WINTRUST_DATA>(),
                dwUIChoice          = WTD_UI_NONE,
                fdwRevocationChecks = WTD_REVOKE_NONE,
                dwUnionChoice       = WTD_CHOICE_FILE,
                pFile               = pFile,
                dwStateAction       = WTD_STATEACTION_VERIFY,
                dwProvFlags         = WTD_SAFER_FLAG
            };
            Marshal.StructureToPtr(data, pData, false);

            int status = WinVerifyTrust(IntPtr.Zero, ref WINTRUST_ACTION_GENERIC_VERIFY_V2, pData);

            /* Re-read the struct (the provider filled in hWVTStateData) and ask it to
               release the per-call state it allocated. */
            var state = Marshal.PtrToStructure<WINTRUST_DATA>(pData);
            state.dwStateAction = WTD_STATEACTION_CLOSE;
            Marshal.StructureToPtr(state, pData, false);
            WinVerifyTrust(IntPtr.Zero, ref WINTRUST_ACTION_GENERIC_VERIFY_V2, pData);

            return status;
        }
        finally
        {
            Marshal.DestroyStructure<WINTRUST_FILE_INFO>(pFile);
            Marshal.FreeHGlobal(pFile);
            Marshal.FreeHGlobal(pData);
        }
    }
}
