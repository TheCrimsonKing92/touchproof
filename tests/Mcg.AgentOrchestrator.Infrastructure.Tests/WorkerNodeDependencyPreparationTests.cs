using System.Diagnostics;
using System.Text.Json;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: unique filesystem roots, no global mutation. The install process is injected;
// git discovery and local excludes use the fixture's own linked-worktree metadata.
public sealed class WorkerNodeDependencyPreparationTests
{
    [Fact]
    public void Prepare_NestedWorktree_ResolvesLocalPackagesAndViteOutput()
    {
        using var fixture = new Fixture();
        fixture.Declare("npm", "ci", "--ignore-scripts");
        var install = fixture.DeclaredInstall();
        Assert.NotNull(install);
        Assert.True(File.Exists(Path.Combine(fixture.Worktree, ".git")));
        Assert.False(Directory.Exists(Path.Combine(fixture.Worktree, "node_modules")));
        Assert.Equal(fixture.ParentVite, fixture.ResolveVite());
        var worker = fixture.Worker();
        var calls = 0;

        WorkerNodeDependencyPreparation.Prepare(worker, fixture.Parameters(install), start =>
        {
            calls++;
            Assert.Equal(fixture.Worktree, start.WorkingDirectory);
            Assert.False(start.UseShellExecute);
            var script = start.ArgumentList.Last();
            Assert.Contains("& 'npm' 'ci' '--ignore-scripts'; exit $LASTEXITCODE", script);
            if (OperatingSystem.IsWindows())
            {
                var drop = ". '" + Path.Combine(fixture.Sandbox, "drop-to-low.ps1") + "';";
                Assert.Contains(drop, script);
                Assert.True(script.IndexOf(drop, StringComparison.Ordinal) <
                    script.IndexOf("& 'npm'", StringComparison.Ordinal));
            }
            Assert.Equal(worker.Environment.OrderBy(pair => pair.Key),
                start.Environment.OrderBy(pair => pair.Key));
            AssertCachesInside(start.Environment, fixture.Sandbox);
            fixture.InstallLocalVite();
            return new WorkerProcessRunResult(0, "installed local packages", "");
        });

        Assert.Equal(1, calls);
        Assert.True(WorkerNodeDependencyPreparation.HasOwnNodeModules(fixture.Worktree));
        Assert.Equal(fixture.LocalVite, fixture.ResolveVite());
        // Model the package-adjacent Vite writes using the first upward package match.
        var modules = Directory.GetParent(fixture.ResolveVite())!.FullName;
        foreach (var cache in new[] { ".vite-temp", ".vite" })
        {
            var output = Path.Combine(modules, cache, "vite.config.ts.timestamp-1.mjs");
            Directory.CreateDirectory(Path.GetDirectoryName(output)!);
            File.WriteAllText(output, "generated config");
            Assert.StartsWith(fixture.Worktree + Path.DirectorySeparatorChar, output);
            Assert.False(Directory.Exists(Path.Combine(fixture.Checkout, "node_modules", cache)));
        }
        AssertCachesInside(worker.Environment, fixture.Sandbox);
        Assert.False(File.Exists(fixture.Incomplete));
        Assert.Contains("/node_modules/", File.ReadAllLines(Path.Combine(fixture.Checkout, ".git", "info", "exclude")));
        Assert.Equal("parent package", File.ReadAllText(Path.Combine(fixture.ParentVite, "package.json")));
    }

    [Fact]
    public void Prepare_ReadOnlyWorktree_SkipsInstallAndEnvironmentChanges()
    {
        foreach (var lowIntegrity in new[] { true, false })
        {
            using var fixture = new Fixture();
            fixture.Declare("npm", "ci");
            var install = fixture.DeclaredInstall();
            Assert.NotNull(install);
            Assert.False(WorkerNodeDependencyPreparation.HasOwnNodeModules(fixture.Worktree));
            var worker = fixture.Worker();
            var before = worker.Environment.ToArray();
            var calls = 0;
            var parameters = fixture.Parameters(install) with
            {
                SandboxWorktreeWritable = false,
                SandboxLowIntegrity = lowIntegrity
            };

            WorkerNodeDependencyPreparation.Prepare(worker, parameters, _ =>
            {
                calls++;
                fixture.InstallLocalVite();
                return new WorkerProcessRunResult(0, "", "");
            });

            Assert.Equal(0, calls);
            Assert.Equal(before, worker.Environment.ToArray());
            Assert.False(Directory.Exists(Path.Combine(fixture.Worktree, "node_modules")));
            Assert.False(Directory.Exists(Path.Combine(fixture.Sandbox, "node")));
            Assert.False(File.Exists(fixture.Incomplete));
        }
    }

    [Fact]
    public void Prepare_ExistingOwnModules_SkipsInstallAndEnvironmentChanges()
    {
        using var fixture = new Fixture();
        fixture.InstallLocalVite();
        var worker = fixture.Worker();
        var before = worker.Environment.ToArray();

        WorkerNodeDependencyPreparation.Prepare(worker,
            fixture.Parameters(new NodeDependencyInstall("npm", ["ci"])), _ =>
                throw new InvalidOperationException("Existing dependencies must not be reinstalled."));

        Assert.Equal(before, worker.Environment.ToArray());
        Assert.Equal(fixture.LocalVite, fixture.ResolveVite());
        Assert.False(File.Exists(fixture.Incomplete));
    }

    [Theory]
    [InlineData("dotnet", "test")]
    [InlineData("go", "test")]
    [InlineData("cargo", "test")]
    [InlineData("lua", "tests.lua")]
    [InlineData("npm", "test")]
    [InlineData("npx", "install")]
    public void Prepare_NoDeclaredInstall_LeavesProjectAndEnvironmentUntouched(
        string command, string argument)
    {
        using var fixture = new Fixture();
        fixture.Declare(command, argument);
        // Presence of a Node manifest alone does not authorize an install.
        File.WriteAllText(Path.Combine(fixture.Worktree, "package.json"), "{}");
        Assert.Null(fixture.DeclaredInstall());
        var worker = fixture.Worker();
        var before = worker.Environment.ToArray();

        WorkerNodeDependencyPreparation.Prepare(worker, fixture.Parameters(null), _ =>
            throw new InvalidOperationException("An undeclared install must not run."));

        Assert.Equal(before, worker.Environment.ToArray());
        Assert.False(Directory.Exists(Path.Combine(fixture.Worktree, "node_modules")));
        Assert.False(Directory.Exists(Path.Combine(fixture.Sandbox, "node")));
    }

    [Theory]
    [InlineData("npm", "ci")]
    [InlineData("npm.cmd", "install")]
    [InlineData("pnpm", "install")]
    [InlineData("yarn", "install")]
    [InlineData("bun", "install")]
    public void Prepare_DeclaredPackageManager_PreservesCommandAndArguments(
        string command, string verb)
    {
        using var fixture = new Fixture();
        fixture.Declare(command, verb, "--cache=folder with ' quote");
        var install = fixture.DeclaredInstall();
        Assert.NotNull(install);
        Assert.Equal(command, install.Command);
        Assert.Equal(new[] { verb, "--cache=folder with ' quote" }, install.Arguments);
        var calls = 0;

        WorkerNodeDependencyPreparation.Prepare(fixture.Worker(), fixture.Parameters(install), start =>
        {
            calls++;
            Assert.Contains($"& '{command}' '{verb}' '--cache=folder with '' quote'; exit $LASTEXITCODE",
                start.ArgumentList.Last());
            fixture.InstallLocalVite();
            return new WorkerProcessRunResult(0, "", "");
        });

        Assert.Equal(1, calls);
    }

    [Theory]
    [InlineData(1, false, false, true)]
    [InlineData(0, true, false, true)]
    [InlineData(0, false, true, true)]
    [InlineData(0, false, false, false)]
    public void Prepare_UnsuccessfulInstall_RefusesLaunchAndRetainsRetryMarker(
        int exit, bool timedOut, bool captureLimited, bool createsModules)
    {
        using var fixture = new Fixture();
        var calls = 0;
        var parameters = fixture.Parameters(new NodeDependencyInstall("npm", ["ci"]));

        var refusal = Assert.Throws<WorkerSandboxRefusedException>(() =>
            WorkerNodeDependencyPreparation.Prepare(fixture.Worker(), parameters, _ =>
            {
                calls++;
                if (createsModules) fixture.InstallLocalVite();
                return new WorkerProcessRunResult(exit, "install stdout", "install stderr",
                    timedOut, captureLimited);
            }));

        Assert.Equal(1, calls);
        Assert.Contains("WORKER_SANDBOX_REFUSED reason=node-dependency-preparation-failed", refusal.Message);
        Assert.Contains(fixture.Worktree, refusal.Message);
        Assert.NotNull(refusal.InnerException);
        Assert.Contains("install stdout", refusal.InnerException.Message);
        Assert.Contains("install stderr", refusal.InnerException.Message);
        Assert.True(File.Exists(fixture.Incomplete));
        // Partial dependencies cannot make the next dispatch skip preparation.
        WorkerNodeDependencyPreparation.Prepare(fixture.Worker(), parameters, _ =>
        {
            calls++;
            fixture.InstallLocalVite();
            return new WorkerProcessRunResult(0, "", "");
        });
        Assert.Equal(2, calls);
        Assert.False(File.Exists(fixture.Incomplete));
    }

    private static void AssertCachesInside(IDictionary<string, string?> environment, string sandbox)
    {
        foreach (var key in new[] { "npm_config_cache", "npm_config_store_dir", "COREPACK_HOME",
            "YARN_CACHE_FOLDER", "YARN_GLOBAL_FOLDER", "BUN_INSTALL_CACHE_DIR", "TEMP", "TMP", "TMPDIR" })
        {
            Assert.StartsWith(sandbox + Path.DirectorySeparatorChar, environment[key]);
        }
        Assert.Equal("untouched", environment["GOFLAGS"]);
        Assert.Equal("fixture-profile", environment["USERPROFILE"]);
    }

    private sealed class Fixture : IDisposable
    {
        private readonly string root = Path.Combine(Path.GetTempPath(), "node-prep-" + Guid.NewGuid().ToString("N"));
        internal string Checkout => Path.Combine(root, "checkout");
        internal string Worktree => Path.Combine(Checkout, ".orchestrator-worktrees", "goal");
        internal string Sandbox => Path.Combine(Worktree, ".mcg-sandbox");
        internal string Incomplete => Path.Combine(Sandbox, "node-install-incomplete");
        internal string ParentVite => Path.Combine(Checkout, "node_modules", "vite");
        internal string LocalVite => Path.Combine(Worktree, "node_modules", "vite");

        internal Fixture()
        {
            Directory.CreateDirectory(Sandbox);
            Directory.CreateDirectory(ParentVite);
            File.WriteAllText(Path.Combine(ParentVite, "package.json"), "parent package");
            // Minimal valid Git metadata avoids invoking git init/worktree add. The shared git
            // directory belongs to this fixture too, so exclude writes cannot reach the real repo.
            var common = Path.Combine(Checkout, ".git");
            Directory.CreateDirectory(Path.Combine(common, "objects"));
            Directory.CreateDirectory(Path.Combine(common, "refs", "heads"));
            File.WriteAllText(Path.Combine(common, "HEAD"), "ref: refs/heads/main\n");
            File.WriteAllText(Path.Combine(common, "config"), "[core]\nrepositoryformatversion = 0\nbare = false\n");
            var metadata = Directory.CreateDirectory(Path.Combine(common, "worktrees", "goal")).FullName;
            File.WriteAllText(Path.Combine(metadata, "HEAD"), "ref: refs/heads/goal\n");
            File.WriteAllText(Path.Combine(metadata, "commondir"), "../..\n");
            File.WriteAllText(Path.Combine(metadata, "gitdir"), Path.Combine(Worktree, ".git") + "\n");
            File.WriteAllText(Path.Combine(Worktree, ".git"), "gitdir: " + metadata + "\n");
        }

        internal void Declare(string command, params string[] arguments)
        {
            var config = Directory.CreateDirectory(Path.Combine(Worktree, "config")).FullName;
            File.WriteAllText(Path.Combine(config, "acceptance-manifest.json"), JsonSerializer.Serialize(new
            {
                checks = new[] { new { name = "fixture check", type = "command", command, arguments } }
            }));
        }

        internal NodeDependencyInstall? DeclaredInstall() => WorkerNodeDependencyInstallSource.For(null)(Worktree);

        internal ProcessStartInfo Worker()
        {
            var worker = new ProcessStartInfo { WorkingDirectory = Worktree, UseShellExecute = false, CreateNoWindow = true };
            worker.Environment.Clear();
            worker.Environment["GOFLAGS"] = "untouched";
            worker.Environment["USERPROFILE"] = "fixture-profile";
            return worker;
        }

        internal DispatchProcessHost.DispatchRunParameters Parameters(NodeDependencyInstall? install) =>
            new("worker", Worktree, Path.Combine(root, "out"), Path.Combine(root, "err"),
                Path.Combine(root, "exit"), null, DisableSharedCompilation: false,
                SandboxLowIntegrity: true, NodeDependencyInstall: install);

        internal void InstallLocalVite()
        {
            Directory.CreateDirectory(LocalVite);
            File.WriteAllText(Path.Combine(LocalVite, "package.json"), "local package");
        }

        internal string ResolveVite()
        {
            for (var directory = new DirectoryInfo(Worktree); directory is not null; directory = directory.Parent)
            {
                var package = Path.Combine(directory.FullName, "node_modules", "vite");
                if (File.Exists(Path.Combine(package, "package.json"))) return package;
                if (directory.FullName == Checkout) break;
            }
            throw new InvalidOperationException("Fixture did not expose a resolvable Vite package.");
        }

        public void Dispose() => Directory.Delete(root, recursive: true);
    }
}
