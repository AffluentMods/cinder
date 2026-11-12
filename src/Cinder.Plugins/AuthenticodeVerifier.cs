using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Cryptography.X509Certificates;

namespace Cinder.Plugins;

/// <summary>
/// Reads and <em>verifies</em> the Authenticode signature embedded in a file.
///
/// <para>Getting the signer's certificate out of a PE file and checking that the certificate
/// chains to a trusted root says nothing about the file: the certificate table can be copied
/// from any signed binary onto any other. The question that matters is whether the signature
/// covers <em>these bytes</em>, and only <c>WinVerifyTrust</c> answers it. A name is shown as
/// verified only when it does.</para>
///
/// <para>Revocation is not checked online — an examination workstation is often air-gapped,
/// and a plugin listing must not stall on, or announce itself to, the network.</para>
/// </summary>
public static class AuthenticodeVerifier
{
    /// <summary>
    /// Signature details for <paramref name="path"/>, or null when the file carries no
    /// Authenticode signature (or this is not Windows). <see cref="AuthenticodeInfo.ChainValid"/>
    /// is true only if Windows accepted the signature for the file's current contents;
    /// <see cref="AuthenticodeInfo.FileIntact"/> is false when the file was changed after it
    /// was signed.
    /// </summary>
    public static AuthenticodeInfo? Inspect(string path)
    {
        if (!OperatingSystem.IsWindows() || !File.Exists(path))
        {
            return null;
        }

        X509Certificate2 certificate;
        try
        {
            // SYSLIB0057: CreateFromSignedFile is the only managed way to read the signer
            // certificate out of a PE. It is used for the names only; it verifies nothing.
#pragma warning disable SYSLIB0057
            using var raw = X509Certificate.CreateFromSignedFile(path);
            certificate = new X509Certificate2(raw);
#pragma warning restore SYSLIB0057
        }
        catch (Exception ex) when (ex is System.Security.Cryptography.CryptographicException or IOException or UnauthorizedAccessException)
        {
            return null; // unsigned, or not a PE
        }

        using (certificate)
        {
            var result = VerifyEmbeddedSignature(path);
            return new AuthenticodeInfo(
                Subject: certificate.Subject,
                Issuer: certificate.Issuer,
                NotAfter: certificate.NotAfter,
                ChainValid: result == 0,
                Thumbprint: certificate.Thumbprint,
                FileIntact: result != TrustEBadDigest);
        }
    }

    // TRUST_E_BAD_DIGEST: the file's hash does not match the hash in its signature.
    private const int TrustEBadDigest = unchecked((int)0x80096010);

    private const uint WtdUiNone = 2;
    private const uint WtdRevokeNone = 0;
    private const uint WtdChoiceFile = 1;
    private const uint WtdStateActionVerify = 1;
    private const uint WtdStateActionClose = 2;
    private const uint WtdRevocationCheckNone = 0x00000010;
    private const uint WtdCacheOnlyUrlRetrieval = 0x00001000;

    /// <summary>Returns 0 when the embedded signature is valid for the file and trusted.</summary>
    [SupportedOSPlatform("windows")]
    private static int VerifyEmbeddedSignature(string path)
    {
        var action = new Guid("00AAC56B-CD44-11d0-8CC2-00C04FC295EE"); // WINTRUST_ACTION_GENERIC_VERIFY_V2
        var filePath = Marshal.StringToHGlobalUni(Path.GetFullPath(path));
        var fileInfoPtr = Marshal.AllocHGlobal(Marshal.SizeOf<WintrustFileInfo>());
        try
        {
            Marshal.StructureToPtr(new WintrustFileInfo
            {
                cbStruct = (uint)Marshal.SizeOf<WintrustFileInfo>(),
                pcwszFilePath = filePath,
                hFile = IntPtr.Zero,
                pgKnownSubject = IntPtr.Zero,
            }, fileInfoPtr, fDeleteOld: false);

            var data = new WintrustData
            {
                cbStruct = (uint)Marshal.SizeOf<WintrustData>(),
                dwUIChoice = WtdUiNone,
                fdwRevocationChecks = WtdRevokeNone,
                dwUnionChoice = WtdChoiceFile,
                pFile = fileInfoPtr,
                dwStateAction = WtdStateActionVerify,
                dwProvFlags = WtdRevocationCheckNone | WtdCacheOnlyUrlRetrieval,
            };

            var invalidHandle = new IntPtr(-1); // no window: never show UI
            var result = WinVerifyTrust(invalidHandle, ref action, ref data);

            data.dwStateAction = WtdStateActionClose;
            _ = WinVerifyTrust(invalidHandle, ref action, ref data);
            return result;
        }
        finally
        {
            Marshal.FreeHGlobal(fileInfoPtr);
            Marshal.FreeHGlobal(filePath);
        }
    }

    [DllImport("wintrust.dll", ExactSpelling = true, PreserveSig = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern int WinVerifyTrust(IntPtr hwnd, ref Guid pgActionID, ref WintrustData pWVTData);

#pragma warning disable IDE1006 // native field names
    [StructLayout(LayoutKind.Sequential)]
    private struct WintrustFileInfo
    {
        public uint cbStruct;
        public IntPtr pcwszFilePath;
        public IntPtr hFile;
        public IntPtr pgKnownSubject;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WintrustData
    {
        public uint cbStruct;
        public IntPtr pPolicyCallbackData;
        public IntPtr pSIPClientData;
        public uint dwUIChoice;
        public uint fdwRevocationChecks;
        public uint dwUnionChoice;
        public IntPtr pFile;
        public uint dwStateAction;
        public IntPtr hWVTStateData;
        public IntPtr pwszURLReference;
        public uint dwProvFlags;
        public uint dwUIContext;
        public IntPtr pSignatureSettings;
    }
#pragma warning restore IDE1006
}
