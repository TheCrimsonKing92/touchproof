using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Mcg.AgentOrchestrator.Infrastructure;

internal enum HermesIntegrityLevelKind
{
    Unavailable,
    Untrusted,
    Low,
    Medium,
    MediumPlus,
    High,
    System,
    Protected,
    Other
}

internal sealed record HermesIntegrityLevel(
    HermesIntegrityLevelKind Kind,
    uint? Rid,
    string? UnavailableReason)
{
    public static HermesIntegrityLevel FromRid(uint rid) => new(rid switch
    {
        0x0000 => HermesIntegrityLevelKind.Untrusted,
        0x1000 => HermesIntegrityLevelKind.Low,
        0x2000 => HermesIntegrityLevelKind.Medium,
        0x2100 => HermesIntegrityLevelKind.MediumPlus,
        0x3000 => HermesIntegrityLevelKind.High,
        0x4000 => HermesIntegrityLevelKind.System,
        0x5000 => HermesIntegrityLevelKind.Protected,
        _ => HermesIntegrityLevelKind.Other
    }, rid, null);

    public static HermesIntegrityLevel Unavailable(string? reason) => new(
        HermesIntegrityLevelKind.Unavailable,
        null,
        string.IsNullOrWhiteSpace(reason) ? "Integrity level could not be read." : reason);
}

// False means membership in this owned job was not positively observed.
internal sealed record HermesLaunchContext(HermesIntegrityLevel IntegrityLevel, bool ObservedInOwnedJob);

internal static class HermesLaunchContextReader
{
    private const uint TokenQuery = 0x0008;
    private const int TokenIntegrityLevel = 25;
    private const int ErrorInsufficientBuffer = 122;

    // Borrow the launch handles; only the temporary token and query buffer are owned here.
    public static HermesLaunchContext Capture(SafeHandle processHandle, OwnedProcessGroup processGroup, int processId)
    {
        var integrity = ReadIntegrityLevel(processHandle);
        var observedInOwnedJob = false;
        try
        {
            observedInOwnedJob = OperatingSystem.IsWindows() &&
                processGroup.TryGetActiveProcessIds(out var processIds) && processIds.Contains(processId);
        }
        catch (Exception ex) when (IsQueryFailure(ex))
        {
            // An unsuccessful job observation must not discard independently read integrity evidence.
        }

        return new HermesLaunchContext(integrity, observedInOwnedJob);
    }

    public static HermesIntegrityLevel ReadIntegrityLevel(SafeHandle? processHandle)
    {
        if (!OperatingSystem.IsWindows())
            return HermesIntegrityLevel.Unavailable("Windows process integrity queries are unsupported on this platform.");

        try
        {
            if (processHandle is null || processHandle.IsClosed || processHandle.IsInvalid)
                return HermesIntegrityLevel.Unavailable("Process handle is missing, closed or invalid.");

            if (!OpenProcessToken(processHandle, TokenQuery, out var token))
                return QueryFailed("OpenProcessToken", Marshal.GetLastWin32Error());

            using (token)
            {
                var sized = GetTokenInformation(token, TokenIntegrityLevel, IntPtr.Zero, 0, out var length);
                var error = Marshal.GetLastWin32Error();
                if (!sized && error != ErrorInsufficientBuffer)
                    return QueryFailed("GetTokenInformation(size)", error);
                if (length < Marshal.SizeOf<MandatoryLabel>())
                    return HermesIntegrityLevel.Unavailable("GetTokenInformation returned an invalid integrity-label size.");

                var buffer = Marshal.AllocHGlobal(length);
                try
                {
                    if (!GetTokenInformation(token, TokenIntegrityLevel, buffer, length, out _))
                        return QueryFailed("GetTokenInformation", Marshal.GetLastWin32Error());

                    var sid = Marshal.PtrToStructure<MandatoryLabel>(buffer).Sid;
                    if (sid == IntPtr.Zero || !IsValidSid(sid))
                        return HermesIntegrityLevel.Unavailable("GetTokenInformation returned an invalid integrity SID.");
                    var countPointer = GetSidSubAuthorityCount(sid);
                    if (countPointer == IntPtr.Zero || Marshal.ReadByte(countPointer) == 0)
                        return HermesIntegrityLevel.Unavailable("Integrity SID has no RID subauthority.");
                    var ridPointer = GetSidSubAuthority(sid, (uint)(Marshal.ReadByte(countPointer) - 1));
                    if (ridPointer == IntPtr.Zero)
                        return HermesIntegrityLevel.Unavailable("Integrity SID RID could not be read.");
                    return HermesIntegrityLevel.FromRid(unchecked((uint)Marshal.ReadInt32(ridPointer)));
                }
                finally
                {
                    Marshal.FreeHGlobal(buffer);
                }
            }
        }
        catch (Exception ex) when (IsQueryFailure(ex))
        {
            return HermesIntegrityLevel.Unavailable($"{ex.GetType().Name}: {ex.Message}");
        }
    }

    private static HermesIntegrityLevel QueryFailed(string api, int error) =>
        HermesIntegrityLevel.Unavailable($"{api} failed with Win32 error {error}.");

    private static bool IsQueryFailure(Exception ex) => ex is
        Win32Exception or InvalidOperationException or UnauthorizedAccessException or
        NotSupportedException or DllNotFoundException or EntryPointNotFoundException;

    [StructLayout(LayoutKind.Sequential)]
    private struct MandatoryLabel { public IntPtr Sid; public uint Attributes; }

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool OpenProcessToken(SafeHandle process, uint access, out SafeFileHandle token);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool GetTokenInformation(SafeFileHandle token, int kind, IntPtr buffer, int length, out int needed);

    [DllImport("advapi32.dll")]
    private static extern bool IsValidSid(IntPtr sid);

    [DllImport("advapi32.dll")]
    private static extern IntPtr GetSidSubAuthorityCount(IntPtr sid);

    [DllImport("advapi32.dll")]
    private static extern IntPtr GetSidSubAuthority(IntPtr sid, uint index);
}
