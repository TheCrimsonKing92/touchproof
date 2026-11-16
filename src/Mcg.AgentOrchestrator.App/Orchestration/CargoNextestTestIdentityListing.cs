using System.Diagnostics;
using System.Text.Json;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using Check = Mcg.AgentOrchestrator.Infrastructure.GoalAcceptanceVerifier.AcceptanceManifestCheck;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal static class CargoNextestTestIdentityListing
{
    internal static readonly TimeSpan ListingTimeout = TimeSpan.FromSeconds(30);
    internal sealed record Command(string Executable, IReadOnlyList<string> Arguments, string WorkingDirectory,
        IReadOnlyDictionary<string, string>? Environment);
    internal sealed record Result(int ExitCode, string StandardOutput, bool TimedOut = false);

    internal static Func<Goal, TaskSpec, IReadOnlyList<RunnerTestIdentity>?> For(string? projectHomeDirectory) =>
        For(projectHomeDirectory, (command, timeout) => RunAsync(command, timeout).GetAwaiter().GetResult());

    internal static Func<Goal, TaskSpec, IReadOnlyList<RunnerTestIdentity>?> For(string? projectHomeDirectory,
        Func<Command, TimeSpan, Result> run) => (_, task) =>
    {
        if (task.LastDispatch is not { } dispatch || !Directory.Exists(dispatch.WorkingDirectory)) return null;
        var budget = Stopwatch.StartNew();
        try
        {
            var manifest = GoalAcceptanceVerifier.AcceptanceManifest.Load(dispatch.WorkingDirectory,
                changedFiles: null, projectHomeDirectory: projectHomeDirectory);
            var identities = new HashSet<RunnerTestIdentity>();
            foreach (var check in (manifest.Checks ?? []).Where(IsNextest))
            {
                if (!TryArguments(check.Arguments, out var arguments) ||
                    !TryDirectory(dispatch.WorkingDirectory, check.Project, out var directory)) return null;
                var remaining = ListingTimeout - budget.Elapsed;
                if (remaining <= TimeSpan.Zero) return null;
                var result = run(new(check.Command!, arguments, directory, check.Environment), remaining);
                if (result.TimedOut || result.ExitCode != 0 || budget.Elapsed >= ListingTimeout) return null;
                var parsed = Parse(result.StandardOutput);
                if (parsed is null) return null; // Never validate against a partial catalog.
                identities.UnionWith(parsed);
            }
            return identities.Count == 0 || budget.Elapsed >= ListingTimeout ? null : identities.ToArray();
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return null;
        }
    };

    private static bool IsNextest(Check check) =>
        (string.Equals(check.Type, "command", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(check.Type, "junit-command", StringComparison.OrdinalIgnoreCase)) &&
        string.Equals(check.Command, "cargo", StringComparison.OrdinalIgnoreCase) &&
        check.Arguments is { Count: > 0 } && check.Arguments[0] == "nextest";

    private static bool TryDirectory(string dispatchDirectory, string? project, out string directory)
    {
        directory = "";
        project ??= ".";
        if (string.IsNullOrWhiteSpace(project) || Path.IsPathRooted(project)) return false;
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(dispatchDirectory));
        directory = Path.GetFullPath(Path.Combine(root, project));
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        return (directory.Equals(root, comparison) || directory.StartsWith(root + Path.DirectorySeparatorChar, comparison)) &&
            Directory.Exists(directory);
    }

    private static bool TryArguments(IReadOnlyList<string> declared, out IReadOnlyList<string> arguments)
    {
        arguments = [];
        if (declared.Count < 2 || declared[1] != "run") return false;
        var list = new List<string> { "nextest", "list", "--message-format", "json", "--list-type", "full" };
        for (var index = 2; index < declared.Count; index++)
        {
            var token = declared[index];
            if (string.IsNullOrEmpty(token)) return false;
            if (token == "--") break;
            if (!token.StartsWith('-')) continue; // Positional test-name filters.
            var equal = token.IndexOf('=');
            var option = equal < 0 ? token : token[..equal];
            var keepValue = option is "--manifest-path" or "-p" or "--package" or "-F" or "--features" or
                "-P" or "--profile" or "--cargo-profile" or "--target" or "--bin" or "--test" or "--example" or "--bench";
            var dropValue = option is "-E" or "--filterset" or "--filter-expr" or "--skip" or "--retries" or
                "--run-ignored" or "--test-threads" or "-j" or "--junit" or "--junit-path" or "--message-format" or
                "--failure-output" or "--success-output" or "--status-level" or "--final-status-level";
            if (keepValue || dropValue)
            {
                string value;
                if (equal >= 0) value = token[(equal + 1)..];
                else
                {
                    if (++index >= declared.Count || declared[index].StartsWith('-')) return false;
                    value = declared[index];
                }
                if (string.IsNullOrWhiteSpace(value)) return false;
                if (keepValue) { list.Add(option); list.Add(value); }
                continue;
            }
            if (equal < 0 && option is "--workspace" or "--all-features" or "--no-default-features" or
                "--lib" or "--bins" or "--tests" or "--examples" or "--benches" or "--all-targets")
            {
                list.Add(option);
                continue;
            }
            if (equal < 0 && option is "--exact" or "--no-fail-fast" or "--no-capture") continue;
            // Unknown syntax may affect the build configuration. Fail open rather than guess.
            return false;
        }
        arguments = list;
        return true;
    }

    private static IReadOnlyList<RunnerTestIdentity>? Parse(string output)
    {
        using var document = JsonDocument.Parse(output);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object ||
            !root.TryGetProperty("rust-suites", out var suites) || suites.ValueKind != JsonValueKind.Object ||
            !root.TryGetProperty("test-count", out var count) || !count.TryGetInt32(out var expected) || expected < 0)
            return null;
        var identities = new List<RunnerTestIdentity>();
        foreach (var suite in suites.EnumerateObject())
        {
            if (suite.Value.ValueKind != JsonValueKind.Object ||
                !suite.Value.TryGetProperty("binary-id", out var binary) || binary.ValueKind != JsonValueKind.String ||
                string.IsNullOrWhiteSpace(binary.GetString()) ||
                !suite.Value.TryGetProperty("status", out var status) || status.GetString() != "listed" ||
                !suite.Value.TryGetProperty("testcases", out var tests) || tests.ValueKind != JsonValueKind.Object)
                return null;
            foreach (var test in tests.EnumerateObject())
            {
                if (string.IsNullOrWhiteSpace(test.Name) || test.Value.ValueKind != JsonValueKind.Object ||
                    !test.Value.TryGetProperty("ignored", out var ignored) ||
                    ignored.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) return null;
                // nextest-metadata's TestListSummary: the testcase key is the unprefixed name.
                identities.Add(new(binary.GetString() + "::" + test.Name, test.Name));
            }
        }
        return identities.Count == expected ? identities : null;
    }

    private static async Task<Result> RunAsync(Command command, TimeSpan timeout)
    {
        using var deadline = new CancellationTokenSource(timeout);
        // The handle-list launcher restricts inheritance to these explicitly redirected streams.
        using var process = new HandleListLaunchedProcess
        {
            StartInfo = new ProcessStartInfo(command.Executable)
            {
                WorkingDirectory = command.WorkingDirectory, UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true
            }
        };
        foreach (var argument in command.Arguments) process.StartInfo.ArgumentList.Add(argument);
        foreach (var pair in command.Environment ?? new Dictionary<string, string>())
            process.StartInfo.Environment[pair.Key] = pair.Value;
        Task<string>? stdout = null;
        Task<string>? stderr = null;
        try
        {
            if (!process.Start()) return new(-1, "");
            process.StandardInput.Close();
            stdout = process.StandardOutput.ReadToEndAsync(deadline.Token);
            stderr = process.StandardError.ReadToEndAsync(deadline.Token);
            await process.WaitForExitAsync(deadline.Token).ConfigureAwait(false);
            await Task.WhenAll(stdout, stderr).WaitAsync(deadline.Token).ConfigureAwait(false);
            return new(process.ExitCode, stdout.Result);
        }
        catch (OperationCanceledException) when (deadline.IsCancellationRequested)
        {
            return new(-1, "", TimedOut: true);
        }
        finally
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
            catch (InvalidOperationException) { }
            catch (System.ComponentModel.Win32Exception) { }
            deadline.Cancel();
            if (stdout is not null && stderr is not null)
            {
                try { await Task.WhenAll(stdout, stderr).ConfigureAwait(false); }
                catch (OperationCanceledException) { }
                catch (IOException) { }
            }
        }
    }
}
