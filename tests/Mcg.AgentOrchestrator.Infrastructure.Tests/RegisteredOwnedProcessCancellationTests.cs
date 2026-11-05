using System.ComponentModel;
using System.Diagnostics;
using Mcg.AgentOrchestrator.Infrastructure;
using Microsoft.Win32.SafeHandles;

// Each test owns its registered child and job observer; no shared hooks or environment changes.
public sealed class RegisteredOwnedProcessCancellationTests
{
    public static bool IsWindows => OperatingSystem.IsWindows();

    [Fact(Timeout = TestHangGuard.WholeTestMilliseconds,
        Skip = "Requires Windows Job Objects.", SkipUnless = nameof(IsWindows))]
    public async Task KillTree_MemberPinningThrows_StillTerminatesAndEmptiesJob()
    {
        var start = new ProcessStartInfo
        {
            FileName = Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe"),
            Arguments = "-NoProfile -NonInteractive -Command \"[void]([System.Threading.ManualResetEvent]::new($false)).WaitOne()\"",
            WorkingDirectory = Path.GetTempPath(),
            UseShellExecute = false,
            CreateNoWindow = true
        };
        start.Environment.Clear();
        start.Environment["SystemRoot"] = Directory.GetParent(Environment.SystemDirectory)!.FullName;
        using var child = WorkerProcessJobs.StartRegisteredOwnedOrThrow(
            start, "member-pinning-failure", containDescendants: true);
        SafeFileHandle? observer = null;
        var pinningFailure = new Win32Exception(5);
        try
        {
            Assert.True(child.OwnsRegisteredJob);
            var failure = await Assert.ThrowsAsync<Win32Exception>(() => child.KillTreeAndWaitForExitAsync(
                TestHangGuard.HangSafetyBound, (group, _) =>
                {
                    Assert.True(group.TryGetActiveProcessIds(out var members));
                    Assert.Contains(child.Id, members); // Prove this was a live job before the fault.
                    Assert.True(group.TryDuplicateAccountingHandle(out observer));
                    throw pinningFailure;
                }));

            Assert.Same(pinningFailure, failure);
            Assert.NotNull(observer);
            // Observe before Release/Dispose can kill the job and mask a missing termination.
            Assert.True(OwnedProcessGroup.WaitForJobExit(observer, TestHangGuard.HangSafetyBound),
                "Member pinning failure left the owned job occupied.");
            await child.WaitForExitAsync(CancellationToken.None).WaitAsync(TestHangGuard.HangSafetyBound);
            Assert.True(child.OwnsRegisteredJob);
        }
        finally
        {
            child.Dispose();
            observer?.Dispose();
        }
    }
}
