using Xunit;

// Parallel-safe: every test owns its repository, environment dictionaries and AsyncLocal scopes.
public sealed class GitProbeRefFileFastPathFallbackTests
{
    [Theory]
    [InlineData("abbreviated")]
    [InlineData("parent")]
    [InlineData("ambiguous")]
    [InlineData("missing")]
    public void ResolverDecline_PreservesRealGitResult(string shape)
    {
        using var fixture = new GitRefFileResolverParityTests.Fixture();
        fixture.Git("commit", "--allow-empty", "-m", "second");
        if (shape == "ambiguous") fixture.Git("tag", "fixture-main");
        var revision = shape switch
        {
            "abbreviated" => fixture.Oracle("HEAD")[..8],
            "parent" => "HEAD~1",
            "ambiguous" => "fixture-main",
            "missing" => "refs/heads/absent",
            _ => throw new ArgumentOutOfRangeException(nameof(shape))
        };
        string[] arguments = ["rev-parse", "--verify", revision];
        var expected = RunGitProbe(fixture.Repo, arguments, commandEnvironment: new Dictionary<string, string>());
        Assert.Equal(shape != "missing", expected.Succeeded);
        if (shape == "ambiguous") Assert.Contains("ambiguous", expected.StandardError, StringComparison.Ordinal);
        using var launches = CountGitProbeLaunches();
        using var answers = CountGitProbeRefFileAnswers();

        var actual = RunGitProbe(fixture.Repo, arguments);

        Assert.Equal(expected.StandardOutput, actual.StandardOutput);
        Assert.Equal(expected.StandardError, actual.StandardError);
        Assert.Equal(expected.ExitCode, actual.ExitCode);
        Assert.Equal(expected.Succeeded, actual.Succeeded);
        Assert.Equal(1, launches.Count);
        Assert.Equal(0, answers.Count);
    }

    [Theory]
    [InlineData("seam")]
    [InlineData("startProcess")]
    [InlineData("captureHook")]
    [InlineData("exitHook")]
    [InlineData("commandEnvironment")]
    [InlineData("inheritedEnvironment")]
    [InlineData("verificationEnvironment")]
    public void ExplicitOverrideOrHook_AlwaysLaunches(string bypass)
    {
        using var fixture = new GitRefFileResolverParityTests.Fixture();
        string[] arguments = ["rev-parse", "HEAD"];
        // Prove this revision qualifies before adding exactly one bypass condition.
        using (var baselineAnswers = CountGitProbeRefFileAnswers())
        {
            Assert.True(RunGitProbe(fixture.Repo, arguments).Succeeded);
            Assert.Equal(1, baselineAnswers.Count);
        }
        var expected = RunGitProbe(fixture.Repo, arguments, commandEnvironment: new Dictionary<string, string>());
        Assert.True(expected.Succeeded, expected.ToString());
        var environment = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        foreach (var name in new[] { "PATH", "PATHEXT", "SystemRoot", "WINDIR", "COMSPEC", "TEMP", "TMP" })
            environment[name] = Environment.GetEnvironmentVariable(name);
        var verificationEnvironment = new Dictionary<string, string?>(environment, StringComparer.OrdinalIgnoreCase)
        {
            ["GIT_CONFIG_NOSYSTEM"] = "1",
            ["GIT_CONFIG_GLOBAL"] = OperatingSystem.IsWindows() ? "NUL" : "/dev/null",
            ["GIT_TERMINAL_PROMPT"] = "0",
            ["GCM_INTERACTIVE"] = "Never",
            ["GIT_OPTIONAL_LOCKS"] = "0",
            ["LC_ALL"] = "C",
            ["LANG"] = "C"
        };
        using var launches = CountGitProbeLaunches();
        using var answers = CountGitProbeRefFileAnswers();
        using var seam = bypass == "seam" ? UseGitProbeSeam() : null;

        var actual = RunGitProbe(fixture.Repo, arguments,
            commandEnvironment: bypass == "commandEnvironment" ? new Dictionary<string, string>() : null,
            startProcess: bypass == "startProcess" ? process => process.Start() : null,
            beforeOwnedCaptureRead: bypass == "captureHook" ? (_, _) => { } : null,
            beforeOwnedExitObservation: bypass == "exitHook" ? _ => { } : null,
            inheritedEnvironment: bypass == "inheritedEnvironment" ? environment : null,
            verificationEnvironment: bypass == "verificationEnvironment" ? verificationEnvironment : null);

        Assert.True(actual.Succeeded, actual.ToString());
        Assert.Equal(expected.StandardOutput, actual.StandardOutput);
        Assert.Equal(expected.StandardError, actual.StandardError);
        Assert.Equal(expected.ExitCode, actual.ExitCode);
        Assert.DoesNotContain("resolved from ref files", actual.Command, StringComparison.Ordinal);
        Assert.Equal(1, launches.Count);
        Assert.Equal(0, answers.Count);
    }

    public static IEnumerable<object[]> OtherShapes()
    {
        yield return [new[] { "rev-parse" }];
        yield return [new[] { "rev-parse", "--verify" }];
        yield return [new[] { "rev-parse", "--show-toplevel" }];
        yield return [new[] { "rev-parse", "--short", "HEAD" }];
        yield return [new[] { "rev-parse", "--", "HEAD" }];
        yield return [new[] { "rev-parse", "HEAD", "--verify" }];
        yield return [new[] { "rev-parse", "HEAD", "HEAD" }];
        yield return [new[] { "rev-parse", "" }];
        yield return [new[] { "rev-parse", " HEAD " }];
        yield return [new[] { "rev-parse", "\"HEAD\"" }];
        yield return [new[] { "log", "-1", "--format=%H" }];
    }

    [Theory]
    [MemberData(nameof(OtherShapes))]
    public void OtherArgumentShape_PreservesRealGitResult(string[] arguments)
    {
        using var fixture = new GitRefFileResolverParityTests.Fixture();
        var expected = RunGitProbe(fixture.Repo, arguments, commandEnvironment: new Dictionary<string, string>());
        using var launches = CountGitProbeLaunches();
        using var answers = CountGitProbeRefFileAnswers();

        var actual = RunGitProbe(fixture.Repo, arguments);

        Assert.Equal(expected.StandardOutput, actual.StandardOutput);
        Assert.Equal(expected.StandardError, actual.StandardError);
        Assert.Equal(expected.ExitCode, actual.ExitCode);
        Assert.Equal(1, launches.Count);
        Assert.Equal(0, answers.Count);
    }
}
