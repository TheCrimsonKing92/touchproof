using Mcg.AgentOrchestrator.Infrastructure;

public sealed class GateToolResolutionPreflightTests
{
    [Fact]
    public void MissingSystemDirectoriesReportAllMissingSystemToolsAndPathLength()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Requires Windows bare command resolution.");
        var root = SharedTestSupport.CreateTempDirectory();
        try
        {
            var environment = CreateToolEnvironment(root);
            var path = Path.Combine(root, "tools");
            environment["PATH"] = path;

            var result = Assert.IsType<GateToolResolutionResult>(
                GateToolResolutionPreflight.Evaluate(environment, root));

            Assert.Equal(["powershell", "cmd", "chcp"], result.MissingTools);
            Assert.Equal(path.Length, result.PathLength);
            Assert.False(result.SystemDirectoryOnPath);
            Assert.False(result.WindowsPowerShellDirectoryOnPath);
            Assert.True(result.GitDirectoryOnPath);
            Assert.True(result.DotnetDirectoryOnPath);
        }
        finally { SharedTestSupport.RemoveTempDirectory(root); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SystemDirectoriesResolveRequiredToolsRegardlessOfPwshAvailability(bool includePwsh)
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Requires Windows bare command resolution.");
        var root = SharedTestSupport.CreateTempDirectory();
        try
        {
            var environment = CreateToolEnvironment(root);
            if (includePwsh) File.WriteAllText(Path.Combine(root, "tools", "pwsh.exe"), string.Empty);
            var systemDirectory = Path.Combine(environment["SystemRoot"]!, "System32");
            var powershellDirectory = Path.Combine(systemDirectory, "WindowsPowerShell", "v1.0");
            var toolsDirectory = Path.Combine(root, "tools");
            var path = $"\"{systemDirectory.ToUpperInvariant()}\\\";{powershellDirectory};{toolsDirectory}";
            environment["PATH"] = path;
            environment["WINDIR"] = environment["SystemRoot"];
            environment["SystemRoot"] = string.Empty;

            var result = Assert.IsType<GateToolResolutionResult>(
                GateToolResolutionPreflight.EnsureToolsResolve(environment, root));

            Assert.Empty(result.MissingTools);
            Assert.Equal(includePwsh, result.PwshResolved);
            Assert.Equal(path.Length, result.PathLength);
            Assert.True(result.SystemDirectoryOnPath);
            Assert.True(result.WindowsPowerShellDirectoryOnPath);
        }
        finally { SharedTestSupport.RemoveTempDirectory(root); }
    }

    [Fact]
    public void MissingDirectoryIdentitiesStayUnknown()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Requires Windows bare command resolution.");
        var result = Assert.IsType<GateToolResolutionResult>(
            GateToolResolutionPreflight.Evaluate(new Dictionary<string, string?>(), Path.GetTempPath()));

        Assert.Equal(["powershell", "cmd", "chcp", "git", "dotnet"], result.MissingTools);
        Assert.Equal(0, result.PathLength);
        Assert.Null(result.SystemDirectoryOnPath);
        Assert.Null(result.WindowsPowerShellDirectoryOnPath);
        Assert.Null(result.GitDirectoryOnPath);
        Assert.Null(result.DotnetDirectoryOnPath);
    }

    [Fact]
    public void NonWindowsSkipsToolResolutionAndEnvironmentSetup()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Exercises the non-Windows attempt path.");

        Assert.Null(GateToolResolutionPreflight.EnterHermeticAttempt(
            Path.GetTempPath(), () => throw new InvalidOperationException("Environment was created")));
        Assert.Null(GateToolResolutionPreflight.EnsureToolsResolve(
            new Dictionary<string, string?>(), Path.GetTempPath()));
    }

    internal static Dictionary<string, string?> CreateToolEnvironment(string root)
    {
        var systemRoot = Path.Combine(root, "Windows");
        var systemDirectory = Path.Combine(systemRoot, "System32");
        var powershellDirectory = Path.Combine(systemDirectory, "WindowsPowerShell", "v1.0");
        var tools = Path.Combine(root, "tools");
        Directory.CreateDirectory(powershellDirectory);
        Directory.CreateDirectory(tools);
        File.WriteAllText(Path.Combine(powershellDirectory, "powershell.exe"), string.Empty);
        File.WriteAllText(Path.Combine(systemDirectory, "cmd.exe"), string.Empty);
        File.WriteAllText(Path.Combine(systemDirectory, "chcp.exe"), string.Empty);
        File.WriteAllText(Path.Combine(tools, "git.exe"), string.Empty);
        File.WriteAllText(Path.Combine(tools, "dotnet.exe"), string.Empty);
        return new(StringComparer.OrdinalIgnoreCase)
        {
            ["PATH"] = string.Join(';', systemDirectory, powershellDirectory, tools),
            ["PATHEXT"] = ".EXE",
            ["SystemRoot"] = systemRoot
        };
    }
}
