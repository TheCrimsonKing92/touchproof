using System.Diagnostics;
using System.Text;

// Parallel-safe: every case owns its copied resolver, pointer, output and runtime cache.
public sealed class ResolveRunDirAdoptedDigestTests
{
    [Xunit.Theory]
    [Xunit.InlineData("valid", true)]
    [Xunit.InlineData("normalized-path", true)]
    [Xunit.InlineData("empty-commit", true)]
    [Xunit.InlineData("missing-dll", true)]
    [Xunit.InlineData("missing-pointer", false)]
    [Xunit.InlineData("one-line-pointer", false)]
    [Xunit.InlineData("empty-dll", false)]
    [Xunit.InlineData("malformed-path", false)]
    [Xunit.InlineData("different-cache-root", false)]
    public void Cleanup_PointerDirectoryIdentity_ProtectsOnlyTheAdoptedDigest(
        string scenario, bool protectAdopted)
    {
        if (!OperatingSystem.IsWindows()) return;
        var root = Path.Combine(Path.GetTempPath(), $"adopted digest {Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var scripts = Directory.CreateDirectory(Path.Combine(root, "scripts")).FullName;
            var resolver = Path.Combine(scripts, "resolve-run-dir.ps1");
            File.Copy(Path.Combine(LauncherScriptTests.FindLauncherSourceRoot(), "scripts", "resolve-run-dir.ps1"), resolver);
            var output = Directory.CreateDirectory(Path.Combine(root, "output")).FullName;
            const string appLeaf = "Mcg.AgentOrchestrator.App.dll";
            var appDll = Path.Combine(output, appLeaf);
            File.WriteAllText(appDll, "app");
            File.WriteAllText(Path.Combine(output, "dependency.dll"), "dependency");
            File.WriteAllText(Path.Combine(output, "e_sqlite3.dll"), "native");

            var cacheBase = Path.Combine(root, "mcg-run", "v2");
            var adopted = Directory.CreateDirectory(Path.Combine(cacheBase, new string('a', 64))).FullName;
            var unused = Directory.CreateDirectory(Path.Combine(cacheBase, new string('b', 64))).FullName;
            var adoptedDll = Path.Combine(adopted, appLeaf);
            File.WriteAllText(adoptedDll, "adopted");
            File.WriteAllText(Path.Combine(adopted, "dependency.dll"), "adopted dependency");
            File.WriteAllText(Path.Combine(unused, appLeaf), "unused");
            Assert.True(Directory.Exists(adopted));
            Assert.True(Directory.Exists(unused));

            var pointerDirectory = Directory.CreateDirectory(Path.Combine(root, ".orchestrator", "continuity")).FullName;
            var pointer = Path.Combine(pointerDirectory, "home-build-adoption.cmd.txt");
            var commit = scenario == "empty-commit" ? "" : new string('c', 40);
            var pointerDll = scenario switch
            {
                "normalized-path" => Path.Combine(adopted, "..", Path.GetFileName(adopted), appLeaf)
                    .ToUpperInvariant().Replace('\\', '/'),
                "empty-dll" => "",
                "malformed-path" => "invalid\0path",
                "different-cache-root" => Path.Combine(root, "other-cache", "v2", Path.GetFileName(adopted), appLeaf),
                _ => adoptedDll
            };
            if (scenario != "missing-pointer")
                File.WriteAllText(pointer, scenario == "one-line-pointer" ? commit + "\r\n" : $"{commit}\r\n{pointerDll}\r\n",
                    new UTF8Encoding(false));
            if (scenario == "missing-dll") File.Delete(adoptedDll);

            var start = new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System),
                "WindowsPowerShell", "v1.0", "powershell.exe"))
            {
                WorkingDirectory = root,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };
            start.Environment.Clear();
            foreach (var name in new[] { "SystemRoot", "WINDIR", "COMSPEC", "PATH", "PATHEXT" })
                start.Environment[name] = Environment.GetEnvironmentVariable(name);
            start.Environment["TEMP"] = root;
            start.Environment["TMP"] = root;
            foreach (var argument in new[] { "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", resolver, appDll })
                start.ArgumentList.Add(argument);
            var result = TestChildProcessCapture.Run(start);

            Assert.True(result.ExitCode == 0, $"exit={result.ExitCode}: {result.Stderr}");
            Assert.True(string.IsNullOrWhiteSpace(result.Stderr), result.Stderr);
            var published = Assert.Single(result.Stdout.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries));
            Assert.Equal(cacheBase, Path.GetDirectoryName(published));
            Assert.NotEqual(adopted, published);
            Assert.NotEqual(unused, published);
            Assert.Equal("app", File.ReadAllText(Path.Combine(published, appLeaf)));
            Assert.Equal(protectAdopted, Directory.Exists(adopted));
            if (protectAdopted)
            {
                Assert.Equal("adopted dependency", File.ReadAllText(Path.Combine(adopted, "dependency.dll")));
                Assert.Equal(scenario != "missing-dll", File.Exists(adoptedDll));
                if (scenario != "missing-dll") Assert.Equal("adopted", File.ReadAllText(adoptedDll));
            }
            Assert.False(Directory.Exists(unused));
            Assert.Empty(Directory.GetDirectories(cacheBase, ".trash-*"));
        }
        finally
        {
            SharedTestSupport.RemoveTempDirectory(root);
        }
    }
}
