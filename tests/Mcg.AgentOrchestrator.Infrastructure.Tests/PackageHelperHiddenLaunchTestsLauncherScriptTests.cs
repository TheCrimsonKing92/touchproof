using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

// Real child processes; all environment overrides and artifacts belong to this invocation.
[Xunit.Collection("ProcessSpawning")]
public sealed class PackageHelperHiddenLaunchTestsLauncherScriptTests
{
    private static readonly TimeSpan HangGuard = TimeSpan.FromSeconds(60);
    private const string ProbeProject = "Mcg.AgentOrchestrator.NativeArgumentProbe";
    private static readonly byte[] Stdout = Enumerable.Range(0, 8192).Select(i => (byte)i).ToArray();
    private static readonly byte[] Stderr = Enumerable.Range(0, 8192).Select(i => (byte)(255 - i)).ToArray();
    private static readonly byte[] StdoutTail = [0, 255, 0xc3, 0x28, 27, 13, 10, 1];
    private static readonly byte[] StderrTail = [2, 10, 13, 27, 0xfe, 0x80, 0];

    [Theory]
    [InlineData("Invoke-PackageBootstrap.ps1", "-p:NuGetAudit=false", 0)]
    [InlineData("Invoke-PackageAudit.ps1", "-p:AuditPipeline=true", 0)]
    [InlineData("Invoke-PackageBootstrap.ps1", "-p:NuGetAudit=false", 37)]
    [InlineData("Invoke-PackageAudit.ps1", "-p:AuditPipeline=true", 37)]
    public async Task Helpers_preserve_native_arguments_streams_directory_and_exit(
        string helper, string property, int exitCode)
    {
        var releaseName = "Local\\mcg-package-helper-" + Guid.NewGuid().ToString("N");
        using var release = new EventWaitHandle(false, EventResetMode.ManualReset, releaseName, out var created);
        Assert.True(created, "The probe release event must be invocation-owned.");
        using var run = new Invocation();
        var target = Path.Combine(run.Root, "target with spaces", "quoted\"name\\\"tail.sln\\");
        var probe = ProbeExecutable();
        Assert.Equal(".exe", Path.GetExtension(probe), ignoreCase: true);
        using (var executable = File.OpenRead(probe))
        {
            Assert.Equal((int)'M', executable.ReadByte());
            Assert.Equal((int)'Z', executable.ReadByte());
        }
        var start = run.StartInfo(helper, probe, target);
        start.Environment["NATIVE_ARGUMENT_PROBE_RECORD"] = run.RecordPath;
        start.Environment["NATIVE_ARGUMENT_PROBE_RELEASE"] = releaseName;
        start.Environment["NATIVE_ARGUMENT_PROBE_EXIT_CODE"] = exitCode.ToString(System.Globalization.CultureInfo.InvariantCulture);
        start.Environment["NATIVE_ARGUMENT_PROBE_STDOUT"] = Convert.ToBase64String(Stdout);
        start.Environment["NATIVE_ARGUMENT_PROBE_STDERR"] = Convert.ToBase64String(Stderr);
        start.Environment["NATIVE_ARGUMENT_PROBE_STDOUT_TAIL"] = Convert.ToBase64String(StdoutTail);
        start.Environment["NATIVE_ARGUMENT_PROBE_STDERR_TAIL"] = Convert.ToBase64String(StderrTail);
        run.Start(start);
        var stdoutSeen = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var stderrSeen = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var output = Capture(run.Process.StandardOutput.BaseStream, Stdout.Length, stdoutSeen);
        var error = Capture(run.Process.StandardError.BaseStream, Stderr.Length, stderrSeen);
        try
        {
            await Observe(Task.WhenAll(stdoutSeen.Task, stderrSeen.Task), "both forwarded prefixes before probe release");
            Assert.False(run.Process.HasExited, "The probe must still be waiting for its release event.");
        }
        catch (EndOfStreamException exception)
        {
            // EOF is evidence that the child stopped, not why it stopped. Drain
            // both pipes before reporting so native-host and PowerShell errors
            // survive in the gate receipt instead of being lost in cleanup.
            await Observe(run.Process.WaitForExitAsync(), "helper exit after premature output EOF");
            await Observe(Task.WhenAll(output, error), "premature-exit output pipes reaching EOF");
            var stderr = await error;
            var diagnostic = Encoding.UTF8.GetString(stderr, 0, Math.Min(stderr.Length, 2048));
            throw new InvalidOperationException(
                $"{exception.Message} Helper={helper}; exitCode={run.Process.ExitCode}; " +
                $"probe={probe}; recordExists={File.Exists(run.RecordPath)}; " +
                $"stdoutBytes={(await output).Length}; stderrBytes={stderr.Length}; " +
                $"stderrPrefix={JsonSerializer.Serialize(diagnostic)}", exception);
        }
        finally
        {
            release.Set();
        }
        await Observe(run.Process.WaitForExitAsync(), "helper exit after probe release");
        await Observe(Task.WhenAll(output, error), "both output pipes reaching EOF");
        Assert.Equal(exitCode, run.Process.ExitCode);
        Assert.Equal(Stdout.Concat(StdoutTail).ToArray(), await output);
        Assert.Equal(Stderr.Concat(StderrTail).ToArray(), await error);
        Assert.True(File.Exists(run.RecordPath), "The compiled apphost must write its own invocation record.");
        var record = JsonSerializer.Deserialize<ProbeRecord>(File.ReadAllText(run.RecordPath))!;
        Assert.Equal(new[] { "restore", target, "--force-evaluate", "--nologo", "--verbosity", "minimal", property }, record.Args);
        Assert.Equal(run.Root, record.WorkingDirectory);
        Assert.True(record.Pid > 0);
        Assert.DoesNotContain((uint)run.Process.Id, record.ConsolePids);
    }

    [Theory]
    [InlineData("Invoke-PackageBootstrap.ps1", false)]
    [InlineData("Invoke-PackageAudit.ps1", false)]
    [InlineData("Invoke-PackageBootstrap.ps1", true)]
    [InlineData("Invoke-PackageAudit.ps1", true)]
    public async Task Missing_host_returns_one_bounded_stderr_line(string helper, bool longPath)
    {
        using var run = new Invocation();
        var missing = Path.Combine(run.Root, "missing.exe");
        if (longPath) missing += "\r\n" + new string('\u00e9', 1600);
        run.Start(run.StartInfo(helper, missing, "synthetic.sln"));
        var output = Capture(run.Process.StandardOutput.BaseStream);
        var error = Capture(run.Process.StandardError.BaseStream);
        await Observe(run.Process.WaitForExitAsync(), "missing-host helper exit");
        await Observe(Task.WhenAll(output, error), "missing-host output pipes reaching EOF");
        Assert.Equal(1, run.Process.ExitCode);
        Assert.Empty(await output);
        var diagnostic = new UTF8Encoding(false, true).GetString(await error);
        Assert.EndsWith(Environment.NewLine, diagnostic);
        var line = diagnostic[..^Environment.NewLine.Length];
        Assert.StartsWith("hidden_native_process_start_failed ", line);
        Assert.DoesNotContain('\r', line);
        Assert.DoesNotContain('\n', line);
        Assert.InRange(Encoding.UTF8.GetByteCount(line), 1, 1024);
        Assert.DoesNotContain("CategoryInfo", line);
        Assert.DoesNotContain("FullyQualifiedErrorId", line);
    }

    [Fact]
    public void Launch_contract_requires_module_assignments_and_rejects_shims()
    {
        var scripts = Path.Combine(LauncherScriptTests.FindLauncherSourceRoot(), "scripts");
        var module = File.ReadAllText(Path.Combine(scripts, "HiddenNativeProcess.psm1"));
        foreach (var helper in new[] { "Invoke-PackageBootstrap.ps1", "Invoke-PackageAudit.ps1" })
        {
            var text = File.ReadAllText(Path.Combine(scripts, helper));
            Assert.True(HasLaunchContract(text, module), helper);
            Assert.False(HasLaunchContract(text.Replace("Invoke-HiddenNativeProcess -FilePath $dotnetHost", "& $dotnetHost"), module));
            Assert.False(HasLaunchContract(text + "\n" + "Start-Process" + " $dotnetHost", module));
            Assert.False(HasLaunchContract(text, module.Replace("$start.UseShellExecute = $false", "# UseShellExecute = $false")));
            Assert.False(HasLaunchContract(text, module.Replace("$start.CreateNoWindow = $true", "# CreateNoWindow = $true")));
            Assert.False(HasLaunchContract(text, "& $FilePath @ArgumentList"));
        }
    }

    [Fact]
    public void Probe_child_environment_uses_executing_runtime_dotnet_root()
    {
        var runtimeDirectory = RuntimeEnvironment.GetRuntimeDirectory();
        var root = Path.GetFullPath(Path.Combine(runtimeDirectory, "..", "..", ".."));
        var inherited = new ProcessStartInfo { CreateNoWindow = true };
        inherited.Environment.Clear();
        foreach (var name in new[] { "DOTNET_ROOT", "DOTNET_ROOT_X64", "DOTNET_ROOT(x86)" })
            inherited.Environment[name] = "stale inherited runtime root";
        SetProbeRuntimeEnvironment(inherited, runtimeDirectory);

        using var run = new Invocation();
        var launch = run.StartInfo("Invoke-PackageBootstrap.ps1", "probe.exe", "synthetic.sln");
        foreach (var start in new[] { inherited, launch })
        {
            Assert.Equal(root, start.Environment["DOTNET_ROOT"]);
            if (RuntimeInformation.ProcessArchitecture == Architecture.X64)
                Assert.Equal(root, start.Environment["DOTNET_ROOT_X64"]);
            if (RuntimeInformation.ProcessArchitecture == Architecture.X86)
                Assert.Equal(root, start.Environment["DOTNET_ROOT(x86)"]);
        }
        Assert.Null(run.Process);
    }

    [Fact]
    public void Missing_runtime_host_fails_before_probe_launch_with_named_path()
    {
        using var run = new Invocation();
        var runtimeDirectory = Path.Combine(run.Root, "shared", "Microsoft.NETCore.App", "0.0.0");
        Directory.CreateDirectory(runtimeDirectory);
        var host = Path.Combine(run.Root, OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet");

        var error = Assert.Throws<Xunit.Sdk.TrueException>(() =>
            run.StartInfo("Invoke-PackageBootstrap.ps1", "probe.exe", "synthetic.sln", runtimeDirectory));

        Assert.Contains(host, error.Message);
        Assert.Contains("no runtime was found for the probe apphost", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Null(run.Process);
    }

    private static void SetProbeRuntimeEnvironment(ProcessStartInfo start, string runtimeDirectory)
    {
        var host = Path.GetFullPath(Path.Combine(runtimeDirectory, "..", "..", "..",
            OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet"));
        Assert.True(File.Exists(host), "No runtime was found for the probe apphost: no dotnet host beside the executing runtime at " + host);
        var root = Path.GetDirectoryName(host)!;
        start.Environment["DOTNET_ROOT"] = root;
        if (RuntimeInformation.ProcessArchitecture == Architecture.X64)
            start.Environment["DOTNET_ROOT_X64"] = root;
        if (RuntimeInformation.ProcessArchitecture == Architecture.X86)
            start.Environment["DOTNET_ROOT(x86)"] = root;
    }

    private static bool HasLaunchContract(string helper, string module) =>
        helper.Contains("Import-Module (Join-Path $PSScriptRoot 'HiddenNativeProcess.psm1')", StringComparison.Ordinal) &&
        helper.Contains("Invoke-HiddenNativeProcess -FilePath $dotnetHost -ArgumentList", StringComparison.Ordinal) &&
        helper.Contains("UseShellExecute = $false", StringComparison.Ordinal) &&
        helper.Contains("CreateNoWindow = $true", StringComparison.Ordinal) &&
        !Regex.IsMatch(helper, @"&\s*\$dotnetHost\b|\bStart[-]Process\b|\bInvoke-Expression\b") &&
        Regex.IsMatch(module, @"(?m)^\s*\$start\.UseShellExecute\s*=\s*\$false\s*$") &&
        Regex.IsMatch(module, @"(?m)^\s*\$start\.CreateNoWindow\s*=\s*\$true\s*$") &&
        Regex.IsMatch(module, @"(?m)^\s*\$start\.RedirectStandardOutput\s*=\s*\$true\s*$") &&
        Regex.IsMatch(module, @"(?m)^\s*\$start\.RedirectStandardError\s*=\s*\$true\s*$") &&
        module.Contains("$start.WorkingDirectory = $location.ProviderPath", StringComparison.Ordinal) &&
        module.Contains("$start.ArgumentList.Add", StringComparison.Ordinal) &&
        module.Contains("[System.Diagnostics.Process]::Start($start)", StringComparison.Ordinal) &&
        module.Contains("Export-ModuleMember -Function Invoke-HiddenNativeProcess", StringComparison.Ordinal);

    private static async Task<byte[]> Capture(Stream stream, int prefixLength = 0, TaskCompletionSource? observed = null)
    {
        using var bytes = new MemoryStream();
        var buffer = new byte[8192];
        int count;
        while ((count = await stream.ReadAsync(buffer)) != 0)
        {
            bytes.Write(buffer, 0, count);
            if (bytes.Length >= prefixLength) observed?.TrySetResult();
        }
        if (bytes.Length < prefixLength)
            observed?.TrySetException(new EndOfStreamException("Helper exited before forwarding the probe prefix."));
        return bytes.ToArray();
    }

    private static async Task Observe(Task task, string expectedEvent)
    {
        try { await task.WaitAsync(HangGuard); }
        catch (TimeoutException exception)
        {
            throw new TimeoutException("Did not observe " + expectedEvent + ".", exception);
        }
    }

    private static string ProbeExecutable()
    {
        const string executable = ProbeProject + ".exe";
        string[] requiredFiles = [executable, ProbeProject + ".dll", ProbeProject + ".deps.json", ProbeProject + ".runtimeconfig.json"];
        var directory = FixtureProbeLocator.FindDirectory(typeof(PackageHelperHiddenLaunchTestsLauncherScriptTests), ProbeProject, requiredFiles);
        Assert.True(directory is not null, "Native argument probe apphost or runtime files were not built. Required: " +
            string.Join(", ", requiredFiles) + ". Tried: " +
            string.Join(", ", FixtureProbeLocator.TriedPaths(typeof(PackageHelperHiddenLaunchTestsLauncherScriptTests), ProbeProject, requiredFiles)));
        return Path.Combine(directory!, executable);
    }

    private sealed record ProbeRecord(string[] Args, string WorkingDirectory, int Pid, uint[] ConsolePids);

    private sealed class Invocation : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "mcg package helper " + Guid.NewGuid().ToString("N"));
        public string RecordPath => Path.Combine(Root, "invocation.json");
        public Process Process { get; private set; } = null!;

        public Invocation() => Directory.CreateDirectory(Root);

        public ProcessStartInfo StartInfo(string helper, string host, string target, string? runtimeDirectory = null)
        {
            // Supply the target as a PowerShell string, isolating the native argv
            // contract from powershell.exe's own -File command-line parser.
            var wrapper = Path.Combine(Root, "invoke.ps1");
            File.WriteAllText(wrapper, "$ErrorActionPreference = 'Stop'\r\n$global:LASTEXITCODE = 0\r\n& $env:PACKAGE_HELPER_SCRIPT -Target $env:PACKAGE_HELPER_TARGET\r\nexit $LASTEXITCODE\r\n");
            var start = new ProcessStartInfo("powershell.exe")
            {
                WorkingDirectory = Root,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            start.Environment.Clear();
            foreach (var name in new[] { "SystemRoot", "SystemDrive", "WINDIR", "PATH", "PATHEXT", "COMSPEC",
                "TEMP", "TMP", "DOTNET_ROOT", "DOTNET_ROOT_X64", "DOTNET_ROOT(x86)" })
                if (Environment.GetEnvironmentVariable(name) is { } value) start.Environment[name] = value;
            SetProbeRuntimeEnvironment(start, runtimeDirectory ?? RuntimeEnvironment.GetRuntimeDirectory());
            start.Environment["MCG_ORCHESTRATOR_DOTNET_PATH"] = host;
            start.Environment["PACKAGE_HELPER_SCRIPT"] = Path.Combine(LauncherScriptTests.FindLauncherSourceRoot(), "scripts", helper);
            start.Environment["PACKAGE_HELPER_TARGET"] = target;
            foreach (var argument in new[] { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", wrapper })
                start.ArgumentList.Add(argument);
            return start;
        }

        public void Start(ProcessStartInfo start) => Process = System.Diagnostics.Process.Start(start)
            ?? throw new InvalidOperationException("Could not start package helper PowerShell.");

        public void Dispose()
        {
            if (Process is not null)
            {
                if (!Process.HasExited)
                {
                    Process.Kill(entireProcessTree: true);
                    if (!Process.WaitForExit((int)HangGuard.TotalMilliseconds))
                        throw new TimeoutException("Package helper process tree did not exit during cleanup.");
                }
                Process.Dispose();
            }
            Directory.Delete(Root, recursive: true);
        }
    }
}
