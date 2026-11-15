using System.Diagnostics;
using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: each test owns its tree and handles; injected probes are scoped to one call.
public sealed class SharedTestSupportRemoveTempDirectoryHolderAttributionTests
{
    public static bool IsWindows => OperatingSystem.IsWindows();

    [Fact(Skip = "Requires Windows deletion-blocking file handles.", SkipUnless = nameof(IsWindows))]
    public void RemoveTempDirectory_FileHeldPastBudget_NamesThisProcessAsHolder()
    {
        WithHeldFile((root, _, _) =>
        {
            var delays = 0;
            var diagnostics = new LockAttributionDiagnosticCollector();
            var error = Assert.Throws<IOException>(() => SharedTestSupport.RemoveTempDirectory(
                root, () => TimeSpan.FromMilliseconds(1), _ => delays++,
                file => LockAttribution.AttributeWithDiagnosticsForTests(
                    file, null, "test-cleanup", "remove-temp-directory", diagnostics),
                budget: TimeSpan.FromMilliseconds(1)));

            // Retain the branch and hook evidence when an executor cannot identify
            // this known, still-held file.
            Console.WriteLine(diagnostics.Receipt.Format());
            Console.WriteLine(error.Message);

            var line = HolderLine(error);
            using var process = Process.GetCurrentProcess();
            // Focused-gate feedback retains assertion failures, but may omit stdout.
            // Keep the selected branch and captured hooks with the failing assertion.
            var processPath = process.MainModule!.FileName;
            var description = FileVersionInfo.GetVersionInfo(processPath).FileDescription;
            // The attribution mechanisms report an application description, executable
            // filename or process name. Require the actual holder's identity in each case.
            var expectedNames = new[] { process.ProcessName, Path.GetFileName(processPath), description }
                .Where(name => !string.IsNullOrWhiteSpace(name))
                .Select(name => $"pid={process.Id} name={name}")
                .ToArray();
            var holders = line.Split("; ", StringSplitOptions.None);
            Assert.True(holders.Any(holder => expectedNames.Contains(holder, StringComparer.Ordinal)),
                $"Expected this process's holder id and name ({string.Join(" or ", expectedNames)}). Actual: {line}"
                + Environment.NewLine + diagnostics.Receipt.Format());
            Assert.Contains("source=", line, StringComparison.Ordinal);
            Assert.DoesNotContain("unavailable", line, StringComparison.Ordinal);
            Assert.Equal(0, delays);
            AssertOriginalFailure(error, root);
        });
    }

    [Fact(Skip = "Requires Windows deletion-blocking file handles.", SkipUnless = nameof(IsWindows))]
    public void RemoveTempDirectory_ProbeReturnsHolders_ListsSourceAndEachHolder()
    {
        WithHeldFile((root, file, _) =>
        {
            var error = FailWithProbe(root, file, path => new BuildLockAttribution(
                path + "\r\nreported", [
                    new BuildLockHolder(123, "first\r\nprocess", null, false),
                    new BuildLockHolder(null, null, null, false)
                ], "injected\r\nsource"));

            var line = HolderLine(error);
            Assert.Equal($"Holder attribution: source=injected\\r\\nsource; path={file}\\r\\nreported; "
                + "pid=123 name=first\\r\\nprocess; pid=unknown name=unknown", line);
        });
    }

    [Theory(Skip = "Requires Windows deletion-blocking file handles.", SkipUnless = nameof(IsWindows))]
    [InlineData(false)]
    [InlineData(true)]
    public void RemoveTempDirectory_ProbeFindsNoHolder_ReportsUnavailable(bool nullResult)
    {
        WithHeldFile((root, file, _) =>
        {
            var error = FailWithProbe(root, file, path => nullResult
                ? null!
                : new BuildLockAttribution(path, [], "empty-probe"));

            var line = HolderLine(error);
            Assert.Contains("unavailable; no holder found", line, StringComparison.Ordinal);
            Assert.Contains($"source={(nullResult ? "unknown" : "empty-probe")}", line, StringComparison.Ordinal);
        });
    }

    [Fact(Skip = "Requires Windows deletion-blocking file handles.", SkipUnless = nameof(IsWindows))]
    public void RemoveTempDirectory_ProbeThrows_ReportsUnavailable()
    {
        WithHeldFile((root, file, _) =>
        {
            var probeError = new InvalidOperationException("probe\r\nfault");
            var error = FailWithProbe(root, file, _ => throw probeError);

            Assert.Equal("Holder attribution: unavailable; probe failed: InvalidOperationException: probe\\r\\nfault",
                HolderLine(error));
            Assert.NotSame(probeError, error.InnerException);
        });
    }

    [Fact(Skip = "Requires Windows deletion-blocking file handles.", SkipUnless = nameof(IsWindows))]
    public void RemoveTempDirectory_NoRemainingFile_ReportsUnavailableWithoutProbe()
    {
        WithHeldFile((root, file, holder) =>
        {
            var probes = 0;
            var elapsedCalls = 0;
            var delays = 0;
            var error = Assert.Throws<IOException>(() => SharedTestSupport.RemoveTempDirectory(
                root,
                () =>
                {
                    elapsedCalls++;
                    holder.Dispose();
                    File.Delete(file);
                    return TimeSpan.FromMilliseconds(1);
                },
                _ => delays++,
                path =>
                {
                    probes++;
                    return new BuildLockAttribution(path, [], "unexpected");
                },
                budget: TimeSpan.FromMilliseconds(1)));

            Assert.Equal("Holder attribution: unavailable; no remaining file could be selected.", HolderLine(error));
            Assert.Equal(0, probes);
            Assert.Equal(1, elapsedCalls);
            Assert.Equal(0, delays);
            AssertOriginalFailure(error, root);
        });
    }

    [Fact]
    public void RemoveTempDirectory_ImmediateSuccess_NeverProbes()
    {
        var root = SharedTestSupport.CreateTempDirectory();
        File.WriteAllText(Path.Combine(root, "unlocked.txt"), "unlocked");
        var probes = 0;
        var elapsedCalls = 0;
        var delays = 0;
        try
        {
            SharedTestSupport.RemoveTempDirectory(root,
                () => { elapsedCalls++; return TimeSpan.Zero; },
                _ => delays++,
                path => { probes++; return new BuildLockAttribution(path, [], "unexpected"); });

            Assert.False(Directory.Exists(root));
            Assert.Equal(0, probes);
            Assert.Equal(0, elapsedCalls);
            Assert.Equal(0, delays);
        }
        finally
        {
            SharedTestSupport.RemoveTempDirectory(root);
        }
    }

    [Fact(Skip = "Requires Windows deletion-blocking file handles.", SkipUnless = nameof(IsWindows))]
    public void RemoveTempDirectory_SuccessOnLaterRetry_NeverProbes()
    {
        WithHeldFile((root, _, holder) =>
        {
            var probes = 0;
            var delays = 0;
            var elapsedCalls = 0;
            SharedTestSupport.RemoveTempDirectory(root,
                () =>
                {
                    elapsedCalls++;
                    // Bound a broken retry loop without using wall-clock time.
                    return elapsedCalls > 2 ? TimeSpan.FromMilliseconds(1) : TimeSpan.Zero;
                },
                _ =>
                {
                    delays++;
                    if (delays == 2) holder.Dispose();
                },
                path => { probes++; return new BuildLockAttribution(path, [], "unexpected"); },
                budget: TimeSpan.FromMilliseconds(1));

            Assert.False(Directory.Exists(root));
            Assert.Equal(2, elapsedCalls);
            Assert.Equal(2, delays);
            Assert.Equal(0, probes);
        });
    }

    private static IOException FailWithProbe(string root, string file, Func<string, BuildLockAttribution> probe)
    {
        var probedPaths = new List<string>();
        var elapsedCalls = 0;
        var elapsedCallsAtProbe = 0;
        var delays = 0;
        var error = Assert.Throws<IOException>(() => SharedTestSupport.RemoveTempDirectory(root,
            () => { elapsedCalls++; return TimeSpan.FromMilliseconds(1); },
            _ => delays++,
            path =>
            {
                probedPaths.Add(path);
                elapsedCallsAtProbe = elapsedCalls;
                return probe(path);
            },
            budget: TimeSpan.FromMilliseconds(1)));

        Assert.Equal(file, Assert.Single(probedPaths));
        Assert.Equal(1, elapsedCalls);
        Assert.Equal(1, elapsedCallsAtProbe);
        Assert.Equal(0, delays);
        AssertOriginalFailure(error, root);
        return error;
    }

    private static string HolderLine(IOException error)
    {
        var lines = error.Message.Split(Environment.NewLine, StringSplitOptions.None);
        var line = Assert.Single(lines, value => value.StartsWith("Holder attribution:", StringComparison.Ordinal));
        Assert.Equal(lines[^1], line);
        Assert.DoesNotContain("\r", line, StringComparison.Ordinal);
        Assert.DoesNotContain("\n", line, StringComparison.Ordinal);
        return line;
    }

    private static void AssertOriginalFailure(IOException error, string root)
    {
        var original = Assert.IsType<IOException>(error.InnerException);
        Assert.StartsWith($"Failed to remove temp directory '{root}' after 1 attempts over 1 ms.", error.Message);
        Assert.Contains($"Last error: {original.Message}", error.Message, StringComparison.Ordinal);
        Assert.Contains("Removal calls: removal 1 (attempt 1):", error.Message, StringComparison.Ordinal);
        Assert.Contains("Attribute pass: not run.", error.Message, StringComparison.Ordinal);
        Assert.Contains($"Residual root '{root}'", error.Message, StringComparison.Ordinal);
    }

    private static void WithHeldFile(Action<string, string, FileStream> action)
    {
        var root = SharedTestSupport.CreateTempDirectory();
        try
        {
            var nested = Directory.CreateDirectory(Path.Combine(root, "nested"));
            var file = Path.Combine(nested.FullName, "held.txt");
            File.WriteAllText(file, "held");
            using var holder = new FileStream(file, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            action(root, file, holder);
        }
        finally
        {
            SharedTestSupport.RemoveTempDirectory(root);
        }
    }
}
