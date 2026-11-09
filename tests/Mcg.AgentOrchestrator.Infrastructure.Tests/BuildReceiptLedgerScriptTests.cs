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
            var driver = Path.Combine(root, "write.ps1");
            File.WriteAllText(driver, """
                param($Module, $Root, $Tree)
                $ErrorActionPreference = 'Stop'
                Import-Module $Module -Force
                $meter = [pscustomobject]@{ Wall = [Diagnostics.Stopwatch]::new(); Cpu = 42 }
                Write-BuildReceipt -Root $Root -GoalPrefix 'goal' -Tree $Tree -Projects @('app.csproj') -Meter $meter
                """);
            var info = new ProcessStartInfo
            {
                FileName = TestPowerShell.Executable,
                WorkingDirectory = root,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            info.Environment.Clear();
            foreach (var key in new[] { "PATH", "SystemRoot", "WINDIR", "TEMP", "TMP", "USERPROFILE", "HOME", "ProgramFiles", "PSModulePath" })
                if (Environment.GetEnvironmentVariable(key) is { } value) info.Environment[key] = value;
            foreach (var argument in new[] { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass",
                "-File", driver, Path.Combine(RepositoryRoot(), "scripts", "BuildReceiptLedger.psm1"), root, tree })
                info.ArgumentList.Add(argument);
            var result = TestChildProcessCapture.Run(info, hangBound: TimeSpan.FromSeconds(60));
            Assert.True(result.ExitCode == 0, result.Stdout + result.Stderr);
            Assert.Equal("", result.Stderr);
            using var row = JsonDocument.Parse(Assert.Single(File.ReadAllLines(
                Path.Combine(root, ".orchestrator", "build-receipts.jsonl"))));
            Assert.Equal(expected, row.RootElement.GetProperty("tree_digest").GetString());
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    private static string RepositoryRoot([CallerFilePath] string source = "")
    {
        if (VerifiedRepositoryRoot.TryGetVerifiedRoot(out var root)) return root;
        for (var directory = new DirectoryInfo(Path.GetDirectoryName(source)!); directory is not null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "Mcg.AgentOrchestrator.sln"))) return directory.FullName;
        throw new DirectoryNotFoundException("Repository root missing.");
    }
}
