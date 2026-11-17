using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text.Json;

public sealed class ResolveRunDirLifecycleTests
{
    [Xunit.Fact]
    public void Explicit_root_publishes_reuses_and_cleans_only_its_own_schema()
    {
        var root = NewTestRoot();
        try
        {
            var appDll = CreateOutput(root);
            var defaultRun = Resolve(root, appDll);
            Assert.Equal(0, defaultRun.ExitCode);
            Assert.Equal(Path.Combine(root, "mcg-run", "v2"), Path.GetDirectoryName(defaultRun.Stdout.Trim()));
            var selectedRoot = Path.Combine(root, "selected cache with spaces");
            var first = Resolve(root, appDll, selectedRoot, root);
            Assert.Equal(0, first.ExitCode);
            var run = first.Stdout.Trim();
            Assert.Equal(Path.Combine(selectedRoot, "v2"), Path.GetDirectoryName(run));
            Assert.Equal(Path.GetFileName(defaultRun.Stdout.Trim()), Path.GetFileName(run));
            var seal = File.ReadAllText(Path.Combine(run, ".mcg-run-closure.json"));
            var warm = Resolve(root, appDll, selectedRoot, Path.GetDirectoryName(appDll));
            Assert.Equal(0, warm.ExitCode);
            Assert.Equal(run, warm.Stdout.Trim());
            Assert.Equal(seal, File.ReadAllText(Path.Combine(run, ".mcg-run-closure.json")));

            File.WriteAllText(Path.Combine(Path.GetDirectoryName(appDll)!, "dependency.dll"), "changed dependency");
            var changed = Resolve(root, appDll, selectedRoot);
            Assert.Equal(0, changed.ExitCode);
            Assert.NotEqual(run, changed.Stdout.Trim());
            Assert.Equal("changed dependency", File.ReadAllText(Path.Combine(changed.Stdout.Trim(), "dependency.dll")));
            Assert.False(Directory.Exists(run));
            Assert.True(Directory.Exists(defaultRun.Stdout.Trim()));
            Assert.Equal("dependency", File.ReadAllText(Path.Combine(defaultRun.Stdout.Trim(), "dependency.dll")));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Xunit.Fact]
    public void Explicit_root_repairs_missing_files_and_refuses_conflicting_content()
    {
        var root = NewTestRoot();
        try
        {
            var appDll = CreateOutput(root);
            var selectedRoot = Path.Combine(root, "selected cache");
            var first = Resolve(root, appDll, selectedRoot);
            Assert.Equal(0, first.ExitCode);
            var run = first.Stdout.Trim();
            var dependency = Path.Combine(run, "dependency.dll");
            File.Delete(dependency);
            var repair = Resolve(root, appDll, selectedRoot);
            Assert.Equal(0, repair.ExitCode);
            Assert.Equal(run, repair.Stdout.Trim());
            Assert.Equal("dependency", File.ReadAllText(dependency));
            File.WriteAllText(dependency, "conflict");
            var refused = Resolve(root, appDll, selectedRoot);
            Assert.NotEqual(0, refused.ExitCode);
            Assert.True(string.IsNullOrWhiteSpace(refused.Stdout), refused.Stdout);
            Assert.Contains("invalid and will not be modified or launched", refused.Stderr);
            Assert.Equal("conflict", File.ReadAllText(dependency));
            Assert.False(Directory.Exists(Path.Combine(root, "mcg-run")));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Xunit.Theory]
    [Xunit.InlineData(@"C:\selected cache")]
    [Xunit.InlineData(@"\\fixture-server\share\selected cache")]
    public void Fully_qualified_roots_can_be_validated_without_filesystem_writes(string selectedRoot)
    {
        var root = NewTestRoot();
        try
        {
            var result = Resolve(root, Path.Combine(root, "not-built.dll"), selectedRoot, validateOnly: true);
            Assert.Equal(0, result.ExitCode);
            Assert.True(string.IsNullOrWhiteSpace(result.Stdout), result.Stdout);
            Assert.Empty(Directory.GetFileSystemEntries(root));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Xunit.Theory]
    [Xunit.InlineData("")]
    [Xunit.InlineData("   ")]
    [Xunit.InlineData("relative cache")]
    [Xunit.InlineData("C:cache")]
    [Xunit.InlineData("\\cache")]
    [Xunit.InlineData("C:\\bad|cache")]
    public void Invalid_explicit_roots_fail_before_directory_creation(string selectedRoot)
    {
        var root = NewTestRoot();
        try
        {
            var appDll = CreateOutput(root);
            var before = Directory.GetFileSystemEntries(root, "*", SearchOption.AllDirectories).Order().ToArray();
            foreach (var caller in new[] { root, Path.GetDirectoryName(appDll)! })
            {
                var result = Resolve(root, appDll, selectedRoot, caller);
                Assert.NotEqual(0, result.ExitCode);
                Assert.True(string.IsNullOrWhiteSpace(result.Stdout), result.Stdout);
                Assert.Contains("cache root", result.Stderr, StringComparison.OrdinalIgnoreCase);
                Assert.Equal(before, Directory.GetFileSystemEntries(root, "*", SearchOption.AllDirectories).Order());
            }
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Xunit.Theory]
    [Xunit.InlineData(false)]
    [Xunit.InlineData(true)]
    public void Missing_files_are_restored_before_reusing_a_published_run(bool holdAppDll)
    {
        var root = NewTestRoot();
        try
        {
            var appDll = CreateOutput(root);
            var first = Resolve(root, appDll);
            Assert.Equal(0, first.ExitCode);
            var run = first.Stdout.Trim();
            var dependency = Path.Combine(run, "dependency.dll");
            var native = Path.Combine(run, "runtimes", "win-x64", "native", "e_sqlite3.dll");
            File.Delete(dependency);
            File.Delete(native);

            (int ExitCode, string Stdout, string Stderr) repaired;
            using (holdAppDll
                ? File.Open(Path.Combine(run, Path.GetFileName(appDll)), FileMode.Open, FileAccess.Read, FileShare.Read)
                : null)
            {
                repaired = Resolve(root, appDll);
            }
            Assert.Equal(0, repaired.ExitCode);
            Assert.Equal(run, repaired.Stdout.Trim(), ignoreCase: true);
            Assert.Equal("dependency", File.ReadAllText(dependency));
            Assert.Equal("native", File.ReadAllText(native));
            Assert.Equal(0, Resolve(root, appDll).ExitCode);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Xunit.Fact]
    public async Task Damaged_published_run_records_missing_file_inventory_and_holding_process_before_repair()
    {
        var root = NewTestRoot();
        Process? holder = null;
        try
        {
            var appDll = CreateOutput(root);
            // Publish the helper as part of the expected closure so its own executable path
            // matches process capture without adding an extra file to the damaged run.
            File.Copy(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "cmd.exe"),
                Path.Combine(Path.GetDirectoryName(appDll)!, "cmd.exe"));
            var first = Resolve(root, appDll);
            Assert.True(first.ExitCode == 0, first.Stderr);
            var run = first.Stdout.Trim();
            var basePath = Path.GetDirectoryName(run)!;
            var markerPath = Path.Combine(run, ".mcg-run-closure.json");
            using var marker = JsonDocument.Parse(File.ReadAllText(markerPath));
            var holderScript = Path.Combine(root, "holder.cmd");
            File.WriteAllText(holderScript, """
                @echo off
                (
                    echo holding
                    set /p release=
                ) 3< "%~1"
                """);
            // Handle 3 holds the app DLL while stdin remains available for the release signal.
            var startInfo = new ProcessStartInfo(Path.Combine(run, "cmd.exe"))
            {
                Arguments = $"/d /q /c \"\"{holderScript}\" \"{Path.Combine(run, Path.GetFileName(appDll))}\"\"",
                UseShellExecute = false,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };
            startInfo.Environment.Clear();
            foreach (var name in new[] { "SystemRoot", "WINDIR", "COMSPEC", "PATH", "PATHEXT" })
                startInfo.Environment[name] = Environment.GetEnvironmentVariable(name);
            startInfo.Environment["TEMP"] = root;
            startInfo.Environment["TMP"] = root;
            holder = Process.Start(startInfo) ?? throw new InvalidOperationException("Holder did not start.");
            var holderErrors = holder.StandardError.ReadToEndAsync();
            var ready = await TestHangGuard.WaitAsync(holder.StandardOutput.ReadLineAsync(), "app DLL holder ready signal");
            Assert.Equal("holding", ready);
            var holderStartedAt = holder.StartTime.ToUniversalTime();
            File.Delete(Path.Combine(run, "dependency.dll"));

            var repaired = Resolve(root, appDll);
            Assert.True(repaired.ExitCode == 0, repaired.Stderr);
            Assert.Equal(run, repaired.Stdout.Trim());
            Assert.Equal("dependency", File.ReadAllText(Path.Combine(run, "dependency.dll")));
            var inventoryPath = Assert.Single(Directory.GetFiles(basePath, "run-dir-inventory-*.json"));
            Assert.Contains(Path.GetFileName(run)[..12], Path.GetFileName(inventoryPath));
            Assert.Empty(Directory.GetFiles(basePath, ".run-dir-inventory-*.tmp"));
            using var inventory = JsonDocument.Parse(File.ReadAllText(inventoryPath));
            var evidence = inventory.RootElement;
            Assert.Equal(1, evidence.GetProperty("schemaVersion").GetInt32());
            Assert.Equal(run, evidence.GetProperty("runDirectory").GetString());
            Assert.Equal(TimeSpan.Zero, evidence.GetProperty("capturedAtUtc").GetDateTimeOffset().Offset);
            Assert.Equal(new[] { "dependency.dll" }, InventoryPaths(evidence, "missingPaths"));
            Assert.Empty(InventoryPaths(evidence, "hashMismatchPaths"));
            Assert.Empty(InventoryPaths(evidence, "extraPaths"));
            foreach (var section in new[] { "expected", "marker" })
            {
                Assert.Equal(Path.GetFileName(run), evidence.GetProperty(section).GetProperty("digest").GetString());
                Assert.Equal(4, evidence.GetProperty(section).GetProperty("fileCount").GetInt32());
            }
            Assert.Equal(marker.RootElement.GetProperty("sealedAtUtc").GetDateTimeOffset(),
                evidence.GetProperty("marker").GetProperty("sealedAtUtc").GetDateTimeOffset());
            var process = Assert.Single(evidence.GetProperty("processes").EnumerateArray(),
                entry => entry.GetProperty("processId").GetInt32() == holder.Id);
            var recordedStart = process.GetProperty("startTimeUtc").GetDateTimeOffset();
            Assert.Equal(TimeSpan.Zero, recordedStart.Offset);
            Assert.Equal(holderStartedAt, recordedStart.UtcDateTime);
            Assert.Equal(Path.Combine(run, "cmd.exe"), process.GetProperty("executablePath").GetString());
            Assert.Equal(JsonValueKind.Null, process.GetProperty("commandLine").ValueKind);
            Assert.Contains(evidence.GetProperty("captureErrors").EnumerateArray(),
                error => error.GetString()!.Contains("command lines are unavailable", StringComparison.Ordinal));
            Assert.Equal(new[] { ".mcg-run-closure.json", "Mcg.AgentOrchestrator.App.dll", "cmd.exe", "dependency.dll",
                    "runtimes/win-x64/native/e_sqlite3.dll" }.Order(StringComparer.Ordinal),
                Directory.GetFiles(run, "*", SearchOption.AllDirectories)
                    .Select(path => Path.GetRelativePath(run, path).Replace('\\', '/')).Order(StringComparer.Ordinal));

            await holder.StandardInput.WriteLineAsync("release");
            await TestHangGuard.WaitAsync(holder.WaitForExitAsync(), "app DLL holder exit after release");
            Assert.Equal(0, holder.ExitCode);
            Assert.Equal(string.Empty, await holderErrors);
        }
        finally
        {
            if (holder is not null)
            {
                try
                {
                    if (!holder.HasExited) holder.Kill(entireProcessTree: true);
                    await TestHangGuard.WaitAsync(holder.WaitForExitAsync(), "app DLL holder cleanup exit");
                }
                finally { holder.Dispose(); }
            }
            Directory.Delete(root, recursive: true);
        }
    }

    [Xunit.Fact]
    public void Healthy_published_run_writes_no_inventory()
    {
        var root = NewTestRoot();
        try
        {
            var appDll = CreateOutput(root);
            var first = Resolve(root, appDll);
            Assert.True(first.ExitCode == 0, first.Stderr);
            var run = first.Stdout.Trim();
            var basePath = Path.GetDirectoryName(run)!;
            Assert.Empty(Directory.GetFiles(basePath, "*inventory*"));
            var warm = Resolve(root, appDll);
            Assert.True(warm.ExitCode == 0, warm.Stderr);
            Assert.Equal(run, warm.Stdout.Trim());
            Assert.Empty(Directory.GetFiles(basePath, "*inventory*"));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Xunit.Fact]
    public void Other_digest_is_removed_only_after_its_app_dll_is_released()
    {
        var root = NewTestRoot();
        try
        {
            var appDll = CreateOutput(root);
            var first = Resolve(root, appDll);
            Assert.Equal(0, first.ExitCode);
            var oldRun = first.Stdout.Trim();
            var oldDependency = Path.Combine(oldRun, "dependency.dll");
            File.WriteAllText(Path.Combine(root, "output", "dependency.dll"), "changed dependency");

            using (File.Open(Path.Combine(oldRun, Path.GetFileName(appDll)), FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                var second = Resolve(root, appDll);
                Assert.Equal(0, second.ExitCode);
                Assert.NotEqual(oldRun, second.Stdout.Trim());
                Assert.Equal("dependency", File.ReadAllText(oldDependency));
                Assert.Empty(Directory.GetDirectories(Path.GetDirectoryName(oldRun)!, ".trash-*"));
            }

            var third = Resolve(root, appDll);
            Assert.Equal(0, third.ExitCode);
            Assert.False(Directory.Exists(oldRun));
            Assert.True(Directory.Exists(third.Stdout.Trim()));
            Assert.Empty(Directory.GetDirectories(Path.GetDirectoryName(oldRun)!, ".trash-*"));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Xunit.Fact]
    public void Missing_marker_is_recreated_and_a_later_resolve_does_not_rewrite_files()
    {
        var root = NewTestRoot();
        try
        {
            var appDll = CreateOutput(root);
            var first = Resolve(root, appDll);
            Assert.Equal(0, first.ExitCode);
            var run = first.Stdout.Trim();
            var marker = Path.Combine(run, ".mcg-run-closure.json");
            var dependency = Path.Combine(run, "dependency.dll");
            File.Delete(marker);
            File.Delete(dependency);

            var repaired = Resolve(root, appDll);
            Assert.Equal(0, repaired.ExitCode);
            Assert.True(File.Exists(marker));
            Assert.Equal("dependency", File.ReadAllText(dependency));
            var writeTime = File.GetLastWriteTimeUtc(dependency);
            Assert.Equal(0, Resolve(root, appDll).ExitCode);
            Assert.Equal(writeTime, File.GetLastWriteTimeUtc(dependency));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Xunit.Fact]
    public async Task Concurrent_missing_file_repairs_both_accept_the_complete_closure()
    {
        var root = NewTestRoot();
        try
        {
            var appDll = CreateOutput(root);
            var first = Resolve(root, appDll);
            Assert.Equal(0, first.ExitCode);
            var run = first.Stdout.Trim();
            File.Delete(Path.Combine(run, "dependency.dll"));
            File.Delete(Path.Combine(run, ".mcg-run-closure.json"));

            using var start = new ManualResetEventSlim();
            var attempts = Enumerable.Range(0, 2)
                .Select(_ => Task.Run(() => { start.Wait(); return Resolve(root, appDll); }))
                .ToArray();
            start.Set();
            var results = await Task.WhenAll(attempts);

            foreach (var result in results)
            {
                Assert.True(result.ExitCode == 0, result.Stderr);
                Assert.Equal(run, result.Stdout.Trim(), ignoreCase: true);
            }
            Assert.Equal("dependency", File.ReadAllText(Path.Combine(run, "dependency.dll")));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Xunit.Fact]
    public void Retired_directory_is_reclaimed_only_after_its_owner_process_exits()
    {
        var root = NewTestRoot();
        try
        {
            var appDll = CreateOutput(root);
            var first = Resolve(root, appDll);
            Assert.Equal(0, first.ExitCode);
            var basePath = Path.GetDirectoryName(first.Stdout.Trim())!;
            var orphan = Path.Combine(basePath, $".trash-2147483647-{Guid.NewGuid():N}");
            var active = Path.Combine(basePath, $".trash-{Environment.ProcessId}-{Guid.NewGuid():N}");
            Directory.CreateDirectory(orphan);
            Directory.CreateDirectory(active);
            File.WriteAllText(Path.Combine(orphan, Path.GetFileName(appDll)), "orphan");
            File.WriteAllText(Path.Combine(active, Path.GetFileName(appDll)), "active");
            File.WriteAllText(Path.Combine(active, "dependency.dll"), "keep whole");

            var second = Resolve(root, appDll);
            Assert.Equal(0, second.ExitCode);
            Assert.False(Directory.Exists(orphan));
            Assert.True(Directory.Exists(active));
            Assert.Equal("active", File.ReadAllText(Path.Combine(active, Path.GetFileName(appDll))));
            Assert.Equal("keep whole", File.ReadAllText(Path.Combine(active, "dependency.dll")));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Xunit.Theory]
    [Xunit.InlineData("different-file")]
    [Xunit.InlineData("extra-file")]
    [Xunit.InlineData("nested-marker")]
    [Xunit.InlineData("wrong-marker-digest")]
    public void Invalid_present_content_is_refused_without_mutation(string damage)
    {
        var root = NewTestRoot();
        try
        {
            var appDll = CreateOutput(root);
            var first = Resolve(root, appDll);
            Assert.Equal(0, first.ExitCode);
            var run = first.Stdout.Trim();
            switch (damage)
            {
                case "different-file":
                    File.WriteAllText(Path.Combine(run, "dependency.dll"), "different");
                    break;
                case "extra-file":
                    File.WriteAllText(Path.Combine(run, "sentinel.txt"), "operator owned");
                    break;
                case "nested-marker":
                    Directory.CreateDirectory(Path.Combine(run, "nested"));
                    File.WriteAllText(Path.Combine(run, "nested", ".mcg-run-closure.json"), "extra");
                    break;
                default:
                    File.WriteAllText(Path.Combine(run, ".mcg-run-closure.json"),
                        "{\"schemaVersion\":2,\"digest\":\"wrong\",\"fileCount\":3}");
                    break;
            }
            var before = Directory.GetFiles(run, "*", SearchOption.AllDirectories)
                .ToDictionary(path => Path.GetRelativePath(run, path), File.ReadAllBytes);
            var refused = Resolve(root, appDll);
            Assert.NotEqual(0, refused.ExitCode);
            Assert.True(string.IsNullOrWhiteSpace(refused.Stdout), refused.Stdout);
            Assert.Contains("invalid and will not be modified or launched", refused.Stderr);
            var after = Directory.GetFiles(run, "*", SearchOption.AllDirectories)
                .ToDictionary(path => Path.GetRelativePath(run, path), File.ReadAllBytes);
            Assert.Equal(before.Keys.Order(), after.Keys.Order());
            foreach (var path in before.Keys)
            {
                Assert.Equal(before[path], after[path]);
            }
            var inventoryPath = Assert.Single(Directory.GetFiles(Path.GetDirectoryName(run)!, "run-dir-inventory-*.json"));
            using var inventory = JsonDocument.Parse(File.ReadAllText(inventoryPath));
            Assert.Empty(InventoryPaths(inventory.RootElement, "missingPaths"));
            Assert.Equal(damage == "different-file" ? new[] { "dependency.dll" } : Array.Empty<string>(),
                InventoryPaths(inventory.RootElement, "hashMismatchPaths"));
            Assert.Equal(damage switch
            {
                "extra-file" => new[] { "sentinel.txt" },
                "nested-marker" => new[] { "nested/.mcg-run-closure.json" },
                _ => Array.Empty<string>()
            }, InventoryPaths(inventory.RootElement, "extraPaths"));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static string?[] InventoryPaths(JsonElement inventory, string property) =>
        inventory.GetProperty(property).EnumerateArray().Select(path => path.GetString()).ToArray();

    private static string NewTestRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), $"resolve-run-lifecycle-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        return root;
    }

    private static string CreateOutput(string root)
    {
        var output = Path.Combine(root, "output");
        Directory.CreateDirectory(Path.Combine(output, "runtimes", "win-x64", "native"));
        var appDll = Path.Combine(output, "Mcg.AgentOrchestrator.App.dll");
        File.WriteAllText(appDll, "app");
        File.WriteAllText(Path.Combine(output, "dependency.dll"), "dependency");
        File.WriteAllText(Path.Combine(output, "runtimes", "win-x64", "native", "e_sqlite3.dll"), "native");
        return appDll;
    }

    private static (int ExitCode, string Stdout, string Stderr) Resolve(
        string root, string appDll, string? cacheRoot = null, string? caller = null, bool validateOnly = false)
    {
        var startInfo = new ProcessStartInfo("powershell.exe")
        {
            WorkingDirectory = caller ?? RepositoryRoot(),
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        var inherited = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        foreach (var name in new[] { "SystemRoot", "WINDIR", "COMSPEC", "PATH", "PATHEXT" })
            inherited[name] = Environment.GetEnvironmentVariable(name);
        startInfo.Environment.Clear();
        foreach (var pair in inherited) startInfo.Environment[pair.Key] = pair.Value;
        startInfo.Environment["TEMP"] = root;
        startInfo.Environment["TMP"] = root;
        startInfo.ArgumentList.Add("-NoProfile");
        startInfo.ArgumentList.Add("-ExecutionPolicy");
        startInfo.ArgumentList.Add("Bypass");
        startInfo.ArgumentList.Add("-File");
        startInfo.ArgumentList.Add(Path.Combine(RepositoryRoot(), "scripts", "resolve-run-dir.ps1"));
        startInfo.ArgumentList.Add(appDll);
        if (cacheRoot is not null)
        {
            startInfo.ArgumentList.Add("-CacheRoot");
            startInfo.ArgumentList.Add(cacheRoot);
        }
        if (validateOnly) startInfo.ArgumentList.Add("-ValidateCacheRootOnly");
        var result = TestChildProcessCapture.Run(startInfo);
        return (result.ExitCode, result.Stdout, result.Stderr);
    }

    private static string RepositoryRoot([CallerFilePath] string sourceFile = "")
    {
        if (VerifiedRepositoryRoot.TryGetVerifiedRoot(out var verifiedRoot))
            return verifiedRoot;

        for (var path = Path.GetDirectoryName(sourceFile); path is not null; path = Path.GetDirectoryName(path))
        {
            if (Directory.Exists(Path.Combine(path, ".git")) || File.Exists(Path.Combine(path, ".git")))
            {
                return path;
            }
        }
        throw new InvalidOperationException("Repository root not found from test source path.");
    }
}
