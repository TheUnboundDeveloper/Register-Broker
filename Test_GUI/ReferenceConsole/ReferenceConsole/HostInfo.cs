using System;
using System.Runtime.InteropServices;
using System.Security.Cryptography.X509Certificates;
using System.Security.Principal;

namespace ReferenceConsole;

/*---------------------------------------------------------------------------*\
| HostInfo                                                                   |
|                                                                            |
|   Read-only facts about THIS process, derived from the session / OS rather |
|   than hard-coded: the Windows integrity level, whether the process is     |
|   elevated, and the executable's Authenticode signer. The Security panel   |
|   uses these so its fields always reflect the real running context.        |
|   Each value is computed once and cached (it can't change mid-session).    |
\*---------------------------------------------------------------------------*/
internal static class HostInfo
{
    private const int TokenIntegrityLevel = 25;   // TOKEN_INFORMATION_CLASS.TokenIntegrityLevel

    private static string? _integrity;
    private static bool? _elevated;
    private static string? _signature;

    /// <summary>Windows integrity level of this process: Low / Medium / High / System.</summary>
    public static string IntegrityLevel => _integrity ??= ComputeIntegrity();

    /// <summary>True when the process token is in the local Administrators role (elevated).</summary>
    public static bool IsElevated => _elevated ??= ComputeElevated();

    /// <summary>Authenticode signer common-name of this executable, or "Unsigned".</summary>
    public static string Signature => _signature ??= ComputeSignature();

    /// <summary>"Elevated (admin)" or "Non-admin" — for the Security panel's Session row.</summary>
    public static string SessionLabel => IsElevated ? "Elevated (admin)" : "Non-admin";

    /// <summary>"Administrator" or "User Mode" — for the compact status-bar Session field.</summary>
    public static string ModeLabel => IsElevated ? "Administrator" : "User Mode";

    private static bool ComputeElevated()
    {
        if (!OperatingSystem.IsWindows()) return false;
        try
        {
            using var id = WindowsIdentity.GetCurrent();
            return new WindowsPrincipal(id).IsInRole(WindowsBuiltInRole.Administrator);
        }
        catch { return false; }
    }

    private static string ComputeIntegrity()
    {
        if (!OperatingSystem.IsWindows()) return "—";
        try
        {
            using var id = WindowsIdentity.GetCurrent();
            var token = id.Token;

            GetTokenInformation(token, TokenIntegrityLevel, IntPtr.Zero, 0, out int size);
            if (size <= 0) return "Unknown";

            var buf = Marshal.AllocHGlobal(size);
            try
            {
                if (!GetTokenInformation(token, TokenIntegrityLevel, buf, size, out _)) return "Unknown";
                // TOKEN_MANDATORY_LABEL { SID_AND_ATTRIBUTES Label } -> Label.Sid is the first pointer.
                var sid = Marshal.ReadIntPtr(buf);
                int count = Marshal.ReadByte(GetSidSubAuthorityCount(sid));
                int rid = Marshal.ReadInt32(GetSidSubAuthority(sid, (uint)(count - 1)));
                return rid switch
                {
                    >= 0x4000 => "System",
                    >= 0x3000 => "High",
                    >= 0x2000 => "Medium",
                    >= 0x1000 => "Low",
                    _ => "Untrusted",
                };
            }
            finally { Marshal.FreeHGlobal(buf); }
        }
        catch { return "Unknown"; }
    }

    private static string ComputeSignature()
    {
        if (!OperatingSystem.IsWindows()) return "Unsigned";
        try
        {
            var path = Environment.ProcessPath;
            if (string.IsNullOrEmpty(path)) return "Unsigned";
#pragma warning disable SYSLIB0057   // CreateFromSignedFile is the supported way to read a PE's Authenticode cert
            var cert = X509Certificate.CreateFromSignedFile(path);
#pragma warning restore SYSLIB0057
            var cn = ParseCommonName(cert.Subject);
            return string.IsNullOrEmpty(cn) ? "Signed" : cn;
        }
        catch { return "Unsigned"; }   // not signed (or unreadable) -> honest "Unsigned"
    }

    private static string? ParseCommonName(string subject)
    {
        // subject e.g. "CN=Some Publisher, O=Acme, C=US"
        foreach (var part in subject.Split(','))
        {
            var p = part.Trim();
            if (p.StartsWith("CN=", StringComparison.OrdinalIgnoreCase)) return p[3..].Trim();
        }
        return null;
    }

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool GetTokenInformation(IntPtr tokenHandle, int tokenInformationClass,
        IntPtr tokenInformation, int tokenInformationLength, out int returnLength);

    [DllImport("advapi32.dll")]
    private static extern IntPtr GetSidSubAuthorityCount(IntPtr pSid);

    [DllImport("advapi32.dll")]
    private static extern IntPtr GetSidSubAuthority(IntPtr pSid, uint nSubAuthority);
}
