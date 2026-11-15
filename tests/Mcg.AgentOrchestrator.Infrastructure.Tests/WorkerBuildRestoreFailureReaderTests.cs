using Mcg.AgentOrchestrator.App.Orchestration;

// Parallel-safe: every instance owns its log directory and file handles.
public sealed class WorkerBuildRestoreFailureReaderTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "mcg-restore-reader-" + Guid.NewGuid().ToString("N"));
    private string Output => Path.Combine(_root, "out.log");
    private string Error => Path.Combine(_root, "err.log");

    public WorkerBuildRestoreFailureReaderTests() => Directory.CreateDirectory(_root);

    [Theory]
    [InlineData("error : Access to the path 'C:\\Users\\fixture\\.nuget\\packages\\libgit2sharp\\0.32.0' is denied.", "ACCESS_DENIED")]
    [InlineData("error : Permission denied: '/home/fixture/.nuget/packages/libgit2sharp/0.32.0/lib/file.dll'", "ACCESS_DENIED")]
    [InlineData("error NU1301: Access to the path 'C:\\Users\\fixture\\.nuget\\packages\\libgit2sharp\\0.32.0' is denied.", "NU1301")]
    public void Read_CacheWriteRefusal_ReturnsPackageVersionAndCode(string line, string code)
    {
        File.WriteAllText(Error, line);
        var result = WorkerBuildRestoreFailureReader.Read(Output, Error);
        Assert.NotNull(result);
        Assert.Equal("libgit2sharp", result.PackageId);
        Assert.Equal("0.32.0", result.PackageVersion);
        Assert.Equal(code, result.ErrorCode);
    }

    [Theory]
    [InlineData("Feature.csproj : error NU1102: Unable to find package LibGit2Sharp with version (>= 0.32.0)", "LibGit2Sharp", "0.32.0", "NU1102")]
    [InlineData("error NU1102: Unable to find package 'LibGit2Sharp' with version (>= 0.32.0-beta.1)", "LibGit2Sharp", "0.32.0-beta.1", "NU1102")]
    [InlineData("error NU1101: Unable to find package LibGit2Sharp. No packages exist with this id in source(s): nuget.org", "LibGit2Sharp", null, "NU1101")]
    [InlineData("error NU1301: Unable to load the service index for source https://api.nuget.org/v3/index.json.", null, null, "NU1301")]
    public void Read_NuError_PreservesAvailableMetadata(string line, string? id, string? version, string code)
    {
        File.WriteAllText(Output, line);
        var result = WorkerBuildRestoreFailureReader.Read(Output, Error);
        Assert.NotNull(result);
        Assert.Equal(id, result.PackageId);
        Assert.Equal(version, result.PackageVersion);
        Assert.Equal(code, result.ErrorCode);
    }

    [Theory]
    [InlineData("Feature.cs(2,1): error CS1002: ; expected")]
    [InlineData("warning NU1603: Package LibGit2Sharp 0.32.0 was resolved approximately")]
    [InlineData("Documentation mentions NU1102 for package LibGit2Sharp 0.32.0")]
    [InlineData("error : Access to the path 'C:\\build\\bin\\Feature.dll' is denied.")]
    public void Read_UnrelatedFailure_ReturnsNothing(string line)
    {
        File.WriteAllText(Error, line);
        Assert.Null(WorkerBuildRestoreFailureReader.Read(Output, Error));
    }

    [Fact]
    public void Read_MissingFiles_ReturnsNothing() =>
        Assert.Null(WorkerBuildRestoreFailureReader.Read(Output, Error));

    [Fact]
    public void Read_UnreadableErrorLog_StillReadsOutput()
    {
        File.WriteAllText(Error, "unreadable");
        File.WriteAllText(Output, "error NU1102: Unable to find package LibGit2Sharp with version (>= 0.32.0)");
        using var locked = new FileStream(Error, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        var result = WorkerBuildRestoreFailureReader.Read(Output, Error);
        Assert.NotNull(result);
        Assert.Equal("NU1102", result.ErrorCode);
        Assert.Equal("LibGit2Sharp", result.PackageId);
        Assert.Equal("0.32.0", result.PackageVersion);
    }

    [Fact]
    public void Read_MultipleDiagnostics_UsesFirstErrorLogLineWithoutMerging()
    {
        File.WriteAllText(Error, "error NU1301: Unable to load service index.\nerror NU1102: Unable to find package Other with version 2.0.0");
        File.WriteAllText(Output, "error NU1102: Unable to find package LibGit2Sharp with version 0.32.0");
        var result = WorkerBuildRestoreFailureReader.Read(Output, Error);
        Assert.NotNull(result);
        Assert.Equal("NU1301", result.ErrorCode);
        Assert.Null(result.PackageId);
        Assert.Null(result.PackageVersion);
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);
}
