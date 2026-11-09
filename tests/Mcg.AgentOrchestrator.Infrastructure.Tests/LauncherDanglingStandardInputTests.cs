using System.Diagnostics;
using System.Text;

// Parallel-safe: handles, environment, launcher copy and all artifacts belong to this invocation.
public sealed class LauncherDanglingStandardInputTests
{
    [Xunit.Fact]
    public void Adopted_launcher_returns_app_exit_code_with_non_inherited_standard_input()
    {
        if (!OperatingSystem.IsWindows()) return;
        var root = Path.Combine(Path.GetTempPath(), $"dangling stdin launcher {Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var launcher = Path.Combine(root, "mcg-orchestrator.cmd");
            File.Copy(Path.Combine(LauncherScriptTests.FindLauncherSourceRoot(), "mcg-orchestrator.cmd"), launcher);
            var adoptedDll = Path.Combine(root, "adopted run", "Mcg.AgentOrchestrator.App.dll");
            Directory.CreateDirectory(Path.GetDirectoryName(adoptedDll)!);
            File.WriteAllText(adoptedDll, "adopted marker");
            var pointer = Path.Combine(root, ".orchestrator", "continuity", "home-build-adoption.cmd.txt");
            Directory.CreateDirectory(Path.GetDirectoryName(pointer)!);
            File.WriteAllText(pointer, new string('a', 40) + "\r\n" + adoptedDll + "\r\n", new UTF8Encoding(false));
            var argumentsLog = Path.Combine(root, "arguments.log");
            var host = Path.Combine(root, "app-host.exe");
            var script = Path.Combine(root, "compile-host.ps1");
            // Use a real executable: a .cmd dotnet shim transfers control away from the launcher.
            File.WriteAllText(script, """
                $ErrorActionPreference = 'Stop'
                Add-Type -OutputType ConsoleApplication -OutputAssembly $env:FIXTURE_APP_HOST -TypeDefinition @'
                using System;
                using System.IO;
                public static class AppHost {
                    public static int Main(string[] args) {
                        File.WriteAllLines(Environment.GetEnvironmentVariable("FIXTURE_ARGUMENTS_LOG"), args);
                        Console.WriteLine("ADOPTED-APP-EXECUTED");
                        return 37;
                    }
                }
                '@
                """);
            var system = Environment.GetFolderPath(Environment.SpecialFolder.System);
            var compile = StartInfo(Path.Combine(system, "WindowsPowerShell", "v1.0", "powershell.exe"));
            foreach (var arg in new[] { "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", script })
                compile.ArgumentList.Add(arg);
            var compiled = TestChildProcessCapture.Run(compile, TimeSpan.FromMinutes(3));
            Assert.True(compiled.ExitCode == 0, $"App host compilation failed: {compiled}");
            Assert.True(File.Exists(host), "App host compilation did not produce its executable.");

            var start = StartInfo(Path.Combine(system, "cmd.exe"));
            start.Arguments = $"/d /s /c \"\"{launcher}\" goals --name \"two words\" --limit 5\"";
            Check("inheritable NUL control", null, inheritInput: true);
            Check("owned NUL with inheritance cleared", null);
            // Exhaust the small handle slots cmd can reuse while opening its batch file.
            // Each value is a distinct constructed condition, with no retry or scheduling race.
            for (var value = 4; value <= 0x400; value += 4)
                Check($"dangling stdin 0x{value:X}", value);

            void Check(string condition, int? handleValue, bool inheritInput = false)
            {
                File.Delete(argumentsLog);
                var result = DanglingStandardInputLaunch.Run(start, root, handleValue, inheritInput);
                Assert.True(result.ExitCode == 37, $"{condition}: expected app exit 37; {result}");
                Assert.Contains("ADOPTED-APP-EXECUTED", result.Stdout);
                foreach (var output in new[] { result.Stdout, result.Stderr })
                    Assert.DoesNotContain("goto intree_path", output, StringComparison.OrdinalIgnoreCase);
                Assert.Equal(new[] { adoptedDll, "goals", "--name", "two words", "--limit", "5" },
                    File.ReadAllLines(argumentsLog));
            }

            ProcessStartInfo StartInfo(string executable)
            {
                var info = new ProcessStartInfo(executable)
                {
                    WorkingDirectory = root, UseShellExecute = false, CreateNoWindow = true,
                    RedirectStandardOutput = true, RedirectStandardError = true,
                    // Compilation must not depend on the test host's inherited stdin either.
                    RedirectStandardInput = true
                };
                info.Environment.Clear();
                foreach (var name in new[] { "SystemRoot", "WINDIR", "COMSPEC", "PATH", "USERPROFILE", "LOCALAPPDATA", "APPDATA" })
                    info.Environment[name] = Environment.GetEnvironmentVariable(name);
                info.Environment["TEMP"] = root;
                info.Environment["TMP"] = root;
                info.Environment["MCG_ORCHESTRATOR_LAUNCHER_INTREE"] = "0";
                info.Environment["MCG_ORCHESTRATOR_DOTNET_PATH"] = host;
                info.Environment["FIXTURE_APP_HOST"] = host;
                info.Environment["FIXTURE_ARGUMENTS_LOG"] = argumentsLog;
                return info;
            }
        }
        finally { SharedTestSupport.RemoveTempDirectory(root); }
    }
}
