using System.Diagnostics;
using Mcg.AgentOrchestrator.Infrastructure;

[Collection(TestCollections.GoalAcceptanceVerifier)]
public sealed class GoalAcceptanceVerifierTestsCmdCommandLineLimit : GoalAcceptanceVerifierTestBase
{
    [Fact]
    public void OverLimitReportsFullyComposedLengthAndCommand()
    {
        if (!OperatingSystem.IsWindows()) return;
        var payload = new string('x', 8200);
        const string stdout = @"\\.\pipe\limit-out";
        const string stderr = @"\\.\pipe\limit-err";
        string[] arguments = ["git", "diff", payload];
        // Diagnostics describe the prepared executable, including direct-git selection or opt-out.
        var prepared = new ProcessStartInfo { FileName = arguments[0], CreateNoWindow = true };
        GitExecutable.Prepare(prepared);
        var expectedLine = $"/c \"(chcp 65001 > nul && \"{prepared.FileName}\" \"diff\" \"{payload}\") > \"{stdout}\" 2> \"{stderr}\"\"";

        var error = Assert.Throws<CommandLineTooLongException>(() =>
            GoalAcceptanceVerifierCaptureCustody.BuildAcceptanceProcessStartInfo(
                arguments, Path.GetTempPath(), stdout, stderr, forceUtf8ConsoleOutput: true));

        Assert.Equal(expectedLine.Length, error.Length);
        Assert.Equal(8191, error.Limit);
        Assert.Equal(prepared.FileName, error.FirstArgument);
        Assert.Equal($"Windows cmd.exe command line is {expectedLine.Length} characters, which exceeds the cmd.exe limit of 8191 characters; first argument: {prepared.FileName}", error.Message);
        Assert.DoesNotContain(payload, error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(32766)]
    [InlineData(32767)]
    public void AtOrBelowLimitBuildsWindowsLaunch(int length)
    {
        if (!OperatingSystem.IsWindows()) return;
        var executable = Path.Combine(Environment.SystemDirectory, "whoami.exe");
        // Quoted image + separating space + terminating null; x requires no argument quoting.
        var overhead = OwnedProcessGroup.QuoteCommandArgument(executable).Length + 2;
        var payload = new string('x', length - overhead);
        var startInfo = GoalAcceptanceVerifierCaptureCustody.BuildAcceptanceProcessStartInfo(
            [executable, payload], Path.GetTempPath());

        Assert.Equal(executable, startInfo.FileName);
        Assert.Empty(startInfo.Arguments);
        Assert.Equal(new[] { payload }, startInfo.ArgumentList);
        Assert.Equal(length, OwnedProcessGroup.QuoteCommandArgument(startInfo.FileName).Length + 1 +
            OwnedProcessGroup.QuoteCommandArgument(Assert.Single(startInfo.ArgumentList)).Length + 1);
    }

    [Fact]
    public void OneCharacterOverLimitIsRejected()
    {
        if (!OperatingSystem.IsWindows()) return;
        var executable = Path.Combine(Environment.SystemDirectory, "whoami.exe");
        var overhead = OwnedProcessGroup.QuoteCommandArgument(executable).Length + 2;
        var error = Assert.Throws<CommandLineTooLongException>(() =>
            GoalAcceptanceVerifierCaptureCustody.BuildAcceptanceProcessStartInfo(
                [executable, new string('x', 32768 - overhead)], Path.GetTempPath()));
        Assert.Equal(32768, error.Length);
        Assert.Equal(32767, error.Limit);
        Assert.Equal(executable, error.FirstArgument);
        Assert.Equal($"Windows CreateProcessW command line is 32768 characters, which exceeds the CreateProcessW limit of 32767 characters; first argument: {executable}", error.Message);
    }

    [Fact]
    public async Task ProductionRunnerRejectsBeforeProcessStartOrPipeWait()
    {
        if (!OperatingSystem.IsWindows()) return;
        var observations = new List<AcceptanceProcessCleanupObservation>();
        var identitiesObserved = 0;
        var resumesObserved = 0;
        var executable = Path.Combine(Environment.SystemDirectory, "whoami.exe");
        var payload = new string('x', 32768);
        var expectedLength = OwnedProcessGroup.QuoteCommandArgument(executable).Length + payload.Length + 2;
        var error = await Assert.ThrowsAsync<CommandLineTooLongException>(() =>
            AcceptanceProcessRunner.RunProcessForTestsAsync(
                [executable, payload], Path.GetTempPath(), TimeSpan.FromMinutes(5),
                cleanupObserver: observations.Add,
                commandIdentityObserver: _ => identitiesObserved++,
                resumeObserver: _ => resumesObserved++));

        Assert.Equal(expectedLength, error.Length);
        Assert.Equal(32767, error.Limit);
        Assert.Equal(executable, error.FirstArgument);
        Assert.Contains(error.Length.ToString(System.Globalization.CultureInfo.InvariantCulture), error.Message, StringComparison.Ordinal);
        Assert.Contains("32767", error.Message, StringComparison.Ordinal);
        Assert.Contains($"first argument: {executable}", error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(payload, error.Message, StringComparison.Ordinal);
        Assert.Empty(observations);
        Assert.Equal(0, identitiesObserved);
        Assert.Equal(0, resumesObserved);
    }
}
