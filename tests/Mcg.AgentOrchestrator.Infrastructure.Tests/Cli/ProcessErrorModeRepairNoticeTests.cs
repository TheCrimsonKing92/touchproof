using Mcg.AgentOrchestrator.Infrastructure;
using Xunit;

[Collection(ProcessErrorModeCollection.Name)]
public sealed class ProcessErrorModeRepairNoticeTests
{
    public static bool IsWindows => OperatingSystem.IsWindows();

    [Fact(Skip = "Requires Windows process error mode.", SkipUnless = nameof(IsWindows))]
    public void ClearedMode_GitCliLaunch_RecordsNoticeWithEmptyStderr()
    {
        var root = Directory.CreateTempSubdirectory("mcg-git-error-mode-");
        try
        {
            var (stderr, notices) = CaptureClearedModeLaunch(access =>
            {
                var result = GitCli.RunExecutable("git", root.FullName, 60_000, ["--version"], access, null);
                Assert.True(result.Succeeded, result.Error);
            });

            Assert.Empty(stderr);
            Assert.StartsWith("PROCESS_ERROR_MODE_REPAIRED entry=GitCli ", Assert.Single(notices));
        }
        finally
        {
            root.Delete(recursive: true);
        }
    }

    [Fact(Skip = "Requires Windows process error mode.", SkipUnless = nameof(IsWindows))]
    public void ClearedModeLaunchThenRepeatedResume_LeavesResumeErrorEmpty()
    {
        ClearedMode_GitCliLaunch_RecordsNoticeWithEmptyStderr();

        var (stderr, notices) = CaptureClearedModeLaunch(_ =>
            new GoalBranchCommitResumeTests().SecondRebindFails_CommitSurvivesAndResumeCompletesWithoutDuplicateEffects());

        Assert.DoesNotContain("PROCESS_ERROR_MODE_", stderr);
        Assert.StartsWith("PROCESS_ERROR_MODE_REPAIRED entry=GitCli ", Assert.Single(notices));
    }

    private static (string Stderr, string[] Notices) CaptureClearedModeLaunch(
        Action<ProcessTreeGuiSuppression.ErrorModeAccess> launch)
    {
        var access = ProcessTreeGuiSuppression.ErrorModeAccess.Native;
        var flags = ProcessTreeGuiSuppression.SuppressedErrorModeFlags;
        var originalMode = access.Get();
        Assert.Null(access.Diagnostic);
        Assert.Equal(flags, originalMode & flags);
        var previousObserver = ProcessErrorModeRepairLog.Observer;
        try
        {
            ProcessErrorModeRepairLog.Observer = null;
            ProcessErrorModeRepairLog.Clear();
            var stderr = AsyncLocalConsoleRouter.CaptureError(() =>
            {
                _ = access.Set(originalMode & ~flags);
                launch(access);
            });

            Assert.Equal(flags, access.Get() & flags);
            return (stderr, ProcessErrorModeRepairLog.GetSnapshot());
        }
        finally
        {
            _ = access.Set(originalMode);
            ProcessErrorModeRepairLog.Clear();
            ProcessErrorModeRepairLog.Observer = previousObserver;
        }
    }
}
