using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: real module contract using only an owned child and temporary ledger.
public sealed class BuildReceiptLedgerScriptTests
{
    [Theory]
    [InlineData("wbc1:git-tree:abcdef", "wbc1:git-tree:abcdef")]
    [InlineData("abcdef", "wbc1:git-tree:abcdef")]
    [InlineData("", "unresolved")]
    public void WriteReceipt_NormalizesDigestWithoutDuplicatingPrefix(string tree, string expected)
    {
        var root = Path.Combine(Path.GetTempPath(), "build-receipt-script-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var result = WriteReceipt(root, tree);
            Assert.True(result.ExitCode == 0, result.Stdout + result.Stderr);
            Assert.Equal("", result.Stderr);
            using var row = JsonDocument.Parse(Assert.Single(File.ReadAllLines(
                Path.Combine(root, ".orchestrator", "build-receipts.jsonl"))));
            Assert.Equal(expected, row.RootElement.GetProperty("tree_digest").GetString());
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void WriteReceipt_GitCheckout_PreservesDigestAndSharesWriterPath(bool linked)
    {
        using var fixture = new WorkerBuildProjectScopeTests.Fixture();
        var root = fixture.Root;
        if (linked)
        {
            root = Path.Combine(fixture.Root, ".orchestrator-worktrees", "linked");
            fixture.Git("worktree", "add", "--detach", root, "HEAD");
        }
        // The driver belongs to ignored context, just as it does for real helpers.
        Directory.CreateDirectory(Path.Combine(root, ".orchestrator-context"));
        Assert.DoesNotContain(".orchestrator/", File.ReadAllText(Path.Combine(root, ".gitignore")));
        Assert.True(WorktreeTreeDigest.TryCompute(root, out var before, out var reason), reason);

        var result = WriteReceipt(root, before);

        Assert.True(result.ExitCode == 0, result.Stdout + result.Stderr);
        Assert.Equal("", result.Stderr);
        var expected = Path.Combine(fixture.Root, ".git", ".orchestrator", "build-receipts.jsonl");
        Assert.Equal(expected, BuildReceiptLedger.ResolveStorePath(root));
        using var row = JsonDocument.Parse(Assert.Single(File.ReadAllLines(expected)));
        Assert.Equal(before, row.RootElement.GetProperty("tree_digest").GetString());
        Assert.False(Directory.Exists(Path.Combine(root, ".orchestrator")));
        Assert.True(WorktreeTreeDigest.TryCompute(root, out var after, out reason), reason);
        Assert.Equal(before, after);
        File.AppendAllText(Path.Combine(root, "Core", "Core.cs"), "\n// changed source\n");
        Assert.True(WorktreeTreeDigest.TryCompute(root, out var changed, out reason), reason);
        Assert.NotEqual(before, changed);
    }

    private static TestChildProcessCapture.Result WriteReceipt(string root, string tree)
    {
        var context = Path.Combine(root, ".orchestrator-context");
        Directory.CreateDirectory(context);
        var driver = Path.Combine(context, "write.ps1");
        File.WriteAllText(driver, """
            param($Module, $Root, $Tree)
            $ErrorActionPreference = 'Stop'
            Import-Module $Module -Force
            $meter = [pscustomobject]@{ Wall = [Diagnostics.Stopwatch]::new(); Cpu = 42 }
            Write-BuildReceipt -Root $Root -GoalPrefix 'goal' -Tree $Tree -Projects @('app.csproj') -Meter $meter
            """);
        var info = new ProcessStartInfo
        {
            FileName = TestPowerShell.Executable, WorkingDirectory = root,
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true
        };
        info.Environment.Clear();
        foreach (var key in new[] { "PATH", "SystemRoot", "WINDIR", "TEMP", "TMP", "USERPROFILE", "HOME", "ProgramFiles", "PSModulePath" })
            if (Environment.GetEnvironmentVariable(key) is { } value) info.Environment[key] = value;
        foreach (var argument in new[] { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass",
            "-File", driver, Path.Combine(RepositoryRoot(), "scripts", "BuildReceiptLedger.psm1"), root, tree })
            info.ArgumentList.Add(argument);
        return TestChildProcessCapture.Run(info, hangBound: TimeSpan.FromSeconds(60));
    }

    private static string RepositoryRoot([CallerFilePath] string source = "")
    {
        if (VerifiedRepositoryRoot.TryGetVerifiedRoot(out var root)) return root;
        for (var directory = new DirectoryInfo(Path.GetDirectoryName(source)!); directory is not null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "Mcg.AgentOrchestrator.sln"))) return directory.FullName;
        throw new DirectoryNotFoundException("Repository root missing.");
    }
}
