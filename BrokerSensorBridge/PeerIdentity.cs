using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace BrokerSensorBridge;

/*---------------------------------------------------------------------------*\
| PeerIdentity                                                               |
|                                                                            |
|   Resolves the process on the *client* end of a connected named pipe, so   |
|   the broker can bind authentication to a specific binary instead of       |
|   trusting any same-user process that read the handshake secret.           |
|                                                                            |
|   GetNamedPipeClientProcessId returns the connecting PID; the image path    |
|   comes from QueryFullProcessImageName, which works across integrity        |
|   levels for the same user and reports the *real* backing image (it cannot  |
|   be spoofed by merely naming a process). Residual risk: PID reuse (TOCTOU) |
|   between connect and query — noted in BROKER-DESIGN.md.                    |
\*---------------------------------------------------------------------------*/
internal static class PeerIdentity
{
    private const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetNamedPipeClientProcessId(SafePipeHandle Pipe, out uint ClientProcessId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern SafeProcessHandle OpenProcess(uint dwDesiredAccess, [MarshalAs(UnmanagedType.Bool)] bool bInheritHandle, uint dwProcessId);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryFullProcessImageNameW(SafeProcessHandle hProcess, uint dwFlags, StringBuilder lpExeName, ref uint lpdwSize);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetNamedPipeClientComputerNameW(SafePipeHandle Pipe, StringBuilder ClientComputerName, uint ClientComputerNameLength);

    /// <summary>
    /// True if the pipe client connected from another machine (named pipes are reachable
    /// over SMB as \\host\pipe\name). This broker is local-only, so remote peers must be
    /// dropped regardless of the audit-only enforcement flag. Fails OPEN on an API error
    /// (treats it as local) so a quirk can't lock out legitimate local clients — it only
    /// rejects when it POSITIVELY resolves a non-local computer name.
    /// </summary>
    public static bool IsRemote(SafePipeHandle pipe)
    {
        try
        {
            var sb = new StringBuilder(260);
            if (!GetNamedPipeClientComputerNameW(pipe, sb, (uint)(sb.Capacity * 2)))
                return false;   // can't tell -> treat as local (don't break local clients)
            string client = sb.ToString();
            if (string.IsNullOrEmpty(client)) return false;
            return !string.Equals(client, Environment.MachineName, StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    /// <summary>
    /// Best-effort: resolves the client PID and full image path of a connected
    /// server pipe. Returns false only if the PID itself could not be read.
    /// <paramref name="imagePath"/> may be null if the PID was found but its
    /// image could not be queried.
    /// </summary>
    public static bool TryGetClient(SafePipeHandle pipe, out uint pid, out string? imagePath)
    {
        pid = 0;
        imagePath = null;
        try
        {
            if (!GetNamedPipeClientProcessId(pipe, out pid) || pid == 0)
                return false;

            using SafeProcessHandle h = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
            if (h.IsInvalid)
                return true;   // we have the PID but cannot read the image

            var sb = new StringBuilder(1024);
            uint size = (uint)sb.Capacity;
            if (QueryFullProcessImageNameW(h, 0, sb, ref size))
                imagePath = sb.ToString(0, (int)size);

            return true;
        }
        catch
        {
            return false;
        }
    }
}
