// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Dausuva Labs
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace ClassroomManager.Modules;

/// <summary>The result of checking the Authenticode signature of one file.</summary>
public sealed record SignatureCheck(bool Valid, string SignerSha256, string Reason)
{
    public static SignatureCheck Failed(string reason) => new(false, "", reason);
}

/// <summary>Checks that a file carries a valid embedded Authenticode signature and says who signed it.</summary>
public interface IModuleSignatureVerifier
{
    SignatureCheck Check(string path);
}

/// <summary>
/// Windows' own signature check (<c>WinVerifyTrust</c>) with no online revocation lookup, so it works on an offline school network.
/// The signer's certificate is reported as the SHA-256 of its leaf certificate, which the caller compares with the signers it trusts.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WinTrustSignatureVerifier : IModuleSignatureVerifier
{
    static readonly Guid GenericVerifyV2 = new("00AAC56B-CD44-11d0-8CC2-00C04FC295EE");

    public SignatureCheck Check(string path)
    {
        if (!File.Exists(path)) return SignatureCheck.Failed("The file does not exist.");
        var status = Verify(path);
        if (status != 0) return SignatureCheck.Failed(Describe(status));
        try
        {
            // The only .NET call that reads the signer out of a signed program file; it has no replacement, and the
            // result is used for its raw bytes only, after WinVerifyTrust has accepted the signature.
#pragma warning disable SYSLIB0057
            using var signer = X509Certificate.CreateFromSignedFile(path);
#pragma warning restore SYSLIB0057
            return new SignatureCheck(true, Convert.ToHexString(SHA256.HashData(signer.GetRawCertData())), "");
        }
        catch (CryptographicException exception) { return SignatureCheck.Failed("The signer could not be read: " + exception.Message); }
    }

    static string Describe(int status) => unchecked((uint)status) switch
    {
        0x800B0100 => "The file is not signed.",
        0x80096010 => "The file was changed after it was signed.",
        0x800B0109 or 0x800B0101 or 0x800B0111 => "Windows does not trust the signer's certificate.",
        _ => $"Windows rejected the signature (0x{status:X8}).",
    };

    static int Verify(string path)
    {
        var fileInfo = new WinTrustFileInfo { cbStruct = (uint)Marshal.SizeOf<WinTrustFileInfo>(), pcwszFilePath = path };
        var filePointer = Marshal.AllocHGlobal(Marshal.SizeOf<WinTrustFileInfo>());
        try
        {
            Marshal.StructureToPtr(fileInfo, filePointer, false);
            var data = new WinTrustData
            {
                cbStruct = (uint)Marshal.SizeOf<WinTrustData>(),
                dwUIChoice = 2,                // WTD_UI_NONE
                fdwRevocationChecks = 0,       // WTD_REVOKE_NONE
                dwUnionChoice = 1,             // WTD_CHOICE_FILE
                pFile = filePointer,
                dwStateAction = 0,             // WTD_STATEACTION_IGNORE: one call, no state to close
                dwProvFlags = 0x00000010,      // WTD_REVOCATION_CHECK_NONE
            };
            var action = GenericVerifyV2;
            return WinVerifyTrust(IntPtr.Zero, ref action, ref data);
        }
        finally { Marshal.FreeHGlobal(filePointer); }
    }

    [DllImport("wintrust.dll", ExactSpelling = true, SetLastError = false)]
    static extern int WinVerifyTrust(IntPtr hwnd, ref Guid action, ref WinTrustData data);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct WinTrustFileInfo
    {
        public uint cbStruct;
        [MarshalAs(UnmanagedType.LPWStr)] public string pcwszFilePath;
        public IntPtr hFile;
        public IntPtr pgKnownSubject;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct WinTrustData
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
}
