using System.Diagnostics;
using Mcg.AgentOrchestrator.Infrastructure;

public sealed class GitExecutableResolutionTests : IDisposable
{
    private readonly string root = InfrastructureTestSupport.CreateTempDirectory();

    [Theory]
    [InlineData("cmd", "mingw64", "MINGW64")]
    [InlineData("cmd", "mingw32", "MINGW32")]
    [InlineData("cmd", "clangarm64", "CLANGARM64")]
    [InlineData("bin", "mingw64", "MINGW64")]
    public void Recognize_ShimLayout_UsesExistingArchitectureTarget(string shimDirectory,
        string system, string expectedSystem)
    {
        var executable = CreateTarget(system);
        var installation = GitExecutable.Recognize(Path.Combine(root, shimDirectory, "git.exe"));
        Assert.NotNull(installation);
        Assert.Equal(executable, installation.RealExecutable);
        Assert.Equal(root, installation.Root);
        Assert.Equal(expectedSystem, installation.Msystem);
    }

    [Fact]
    public void Recognize_MissingTargetOrNonShim_PreservesResolution()
    {
        Assert.Null(GitExecutable.Recognize(Path.Combine(root, "cmd", "git.exe")));
        var real = CreateTarget("mingw64");
        Assert.Null(GitExecutable.Recognize(real));
        Assert.Null(GitExecutable.Recognize(Path.Combine(root, "other", "git.exe")));
        Assert.Null(GitExecutable.Recognize(Path.Combine(root, "cmd", "other.exe")));
        Assert.Null(GitExecutable.Recognize("git"));
    }

    [Fact]
    public void Environment_UsesEachLaunchValuesAndPreservesCallerSettings()
    {
        var installation = Installation();
        var configDirectory = Path.Combine(root, "etc");
        Directory.CreateDirectory(configDirectory);
        File.WriteAllText(Path.Combine(configDirectory, "git-bash.config"), "unrelated=value\nMSYS=first\nMSYS=second\n");
        var environment = new Dictionary<string, string?>
        {
            ["PATH"] = "caller-path", ["HOME"] = root, ["PLINK_PROTOCOL"] = "custom",
            ["MSYSTEM"] = "wrong-system", ["MSYS"] = "caller-msys", ["GIT_INDEX_FILE"] = "owned-index"
        };
        installation.ApplyEnvironment(environment);
        Assert.Equal("MINGW64", environment["MSYSTEM"]);
        Assert.Equal("custom", environment["PLINK_PROTOCOL"]);
        Assert.Equal(root, environment["HOME"]);
        Assert.Equal("owned-index", environment["GIT_INDEX_FILE"]);
        Assert.Equal("second first caller-msys", environment["MSYS"]);
        Assert.Equal(installation.MsystemBin + ";" + Path.Combine(root, "usr", "bin") + ";" +
            root + "\\bin;caller-path", environment["PATH"]);
        Assert.False(environment.ContainsKey("EXEPATH"));

        var later = new Dictionary<string, string?> { ["PATH"] = "later-path", ["HOME"] = "later-home" };
        installation.ApplyEnvironment(later);
        Assert.EndsWith("later-home\\bin;later-path", later["PATH"]);
        Assert.Equal("ssh", later["PLINK_PROTOCOL"]);
        Assert.Equal("second first", later["MSYS"]);
    }

    [Fact]
    public void Environment_DefaultsHomeToExistingHomePathThenUserProfile()
    {
        var installation = Installation();
        var environment = new Dictionary<string, string?>
        {
            ["HOMEDRIVE"] = Path.GetPathRoot(root), ["HOMEPATH"] = root[Path.GetPathRoot(root)!.Length..],
            ["USERPROFILE"] = "profile-fallback"
        };
        installation.ApplyEnvironment(environment);
        Assert.Equal(root, environment["HOME"]);
        environment.Remove("HOME");
        environment["HOMEPATH"] = "missing-directory";
        installation.ApplyEnvironment(environment);
        Assert.Equal("profile-fallback", environment["HOME"]);

        if (OperatingSystem.IsWindows())
        {
            environment.Remove("HOME");
            environment["HOMEDRIVE"] = "";
            environment["HOMEPATH"] = Environment.SystemDirectory;
            installation.ApplyEnvironment(environment);
            Assert.Equal("profile-fallback", environment["HOME"]);
        }
    }

    [Fact]
    public void Prepare_NonGitExecutable_DoesNotChangeEnvironmentOrFlags()
    {
        var info = new ProcessStartInfo("a-test-probe") { CreateNoWindow = true };
        var before = info.Environment.ToArray();
        Assert.False(GitExecutable.Prepare(info));
        Assert.Equal("a-test-probe", info.FileName);
        Assert.True(info.CreateNoWindow);
        Assert.Equal(before, info.Environment.ToArray());
    }

    private GitExecutable.GitInstallation Installation()
    {
        CreateTarget("mingw64");
        return GitExecutable.Recognize(Path.Combine(root, "cmd", "git.exe"))!;
    }

    private string CreateTarget(string system)
    {
        var path = Path.Combine(root, system, "bin", "git.exe");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, []);
        return path;
    }

    public void Dispose() => SharedTestSupport.RemoveTempDirectory(root);
}
