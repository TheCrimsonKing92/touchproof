using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;

public sealed class ConsoleOwnershipHelperTests : IDisposable
{
    private const string Started = "2026-10-01T00:00:00.000Z";
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"console-ownership-{Guid.NewGuid():N}");

    [Theory]
    [InlineData("resolved", false)]
    [InlineData("powershell.exe", false)]
    [InlineData("resolved", true)]
    public void AmbiguitySuppressesAllCandidatesUntilUnmatchedHostIsRemoved(string shell, bool wrapper)
    {
        object[] snapshot = [
            Entry(51001, 0, "WindowsTerminal"), Entry(51002, 51001, "OpenConsole.exe"),
            Entry(51003, 51002, "codex"), Entry(51004, 51003, "CONHOST.EXE"),
            Entry(52001), Entry(52002), Entry(52003), Entry(52999)
        ];
        object[] receipts = [Receipt(52001, "dispatch-a"), Receipt(52002, "dispatch-b"),
            Receipt(52003, "dispatch-c"), Receipt(51002, "harness-must-win")];
        var refused = Run(snapshot, receipts, shell, wrapper: wrapper);
        AssertRefusal(refused, 52999);
        foreach (var id in new[] { 52001, 52002, 52003 }) AssertIdAbsent(refused.Output, id);

        var resolved = Run(snapshot[..^1], receipts, shell, wrapper: wrapper);
        Assert.Equal(0, resolved.ExitCode);
        Assert.Equal(new[] { 52001, 52002, 52003 }, CandidateIds(resolved.Output));
        Assert.Contains("WITHHELD id=51002", resolved.Output);
        Assert.Contains("reason=harness ancestor", resolved.Output);
        Assert.Contains("WITHHELD id=51004", resolved.Output);
        Assert.Contains("reason=harness descendant-console", resolved.Output);
        Assert.DoesNotContain("harness-must-win", resolved.Output);
        foreach (var dispatch in new[] { "dispatch-a", "dispatch-b", "dispatch-c" })
            Assert.Contains($"dispatchId={dispatch}", resolved.Output);
        Assert.Contains("matched(processId=52001,startTime=2026-10-01T00:00:00.000Z", resolved.Output);
    }

    [Theory]
    [InlineData("omitted")]
    [InlineData("missing")]
    [InlineData("malformed")]
    [InlineData("object")]
    [InlineData("bad-id")]
    [InlineData("blank-dispatch")]
    [InlineData("nonstring-time")]
    public void UnavailableOrInvalidReceiptsRefuseWithoutLeakingMatchedIds(string fault)
    {
        var raw = fault switch
        {
            "malformed" => "[{",
            "object" => "{}",
            "bad-id" => "[{\"processId\":\"52001\",\"startTime\":\"2026-10-01T00:00:00Z\",\"dispatchId\":\"a\"}]",
            "blank-dispatch" => JsonSerializer.Serialize(new[] { Receipt(52001, " ") }),
            "nonstring-time" => "[{\"processId\":52001,\"startTime\":42,\"dispatchId\":\"a\"}]",
            _ => null
        };
        var result = Run([Entry(51003, 0, "codex"), Entry(52001)], [Receipt(52001)],
            receiptMode: fault, rawReceipts: raw);
        AssertRefusal(result, 52001);
        Assert.Contains("receipts", result.Output);
    }

    [Fact]
    public void MissingReceiptsRefuseEvenWhenOnlyHarnessConsoleExists()
    {
        var result = Run([Entry(51003, 0, "conhost")], [], receiptMode: "omitted");
        AssertRefusal(result);
        Assert.Contains("WITHHELD id=51003", result.Output);
    }

    [Fact]
    public void OldParentlessHosts_OnlyReceiptMatchesBecomeCandidates()
    {
        object[] snapshot = [Entry(51003), Entry(52001, name: "conhost.exe"),
            Entry(52002, name: "OpenConsole.exe"), Entry(52003)];
        object[] receipts = [Receipt(52001, "dispatch-a"), Receipt(52002, "dispatch-b"),
            Receipt(52003, "dispatch-c")];

        var resolved = Run(snapshot, receipts);
        Assert.Equal(0, resolved.ExitCode);
        Assert.Equal(new[] { 52001, 52002, 52003 }, CandidateIds(resolved.Output));
        Assert.Contains("WITHHELD id=51003", resolved.Output);
        Assert.Contains("reason=harness process", resolved.Output);

        // Keep age, parentlessness and host count fixed; remove only one receipt.
        var refused = Run(snapshot, [receipts[0], receipts[2]]);
        AssertRefusal(refused, 52002);
        AssertIdAbsent(refused.Output, 52001);
        AssertIdAbsent(refused.Output, 52003);
        Assert.Contains("WITHHELD id=51003", refused.Output);

        var harnessReceipted = Run(snapshot, [.. receipts, Receipt(51003, "harness-must-win")]);
        Assert.Equal(0, harnessReceipted.ExitCode);
        Assert.Equal(new[] { 52001, 52002, 52003 }, CandidateIds(harnessReceipted.Output));
        Assert.Contains("WITHHELD id=51003", harnessReceipted.Output);
        Assert.DoesNotContain("harness-must-win", harnessReceipted.Output);
    }

    [Theory]
    [InlineData("resolved")]
    [InlineData("powershell.exe")]
    public void ReusedConsoleId_DifferentStartTimeSuppressesCandidates(string shell)
    {
        var result = Run([Entry(51003, 0, "codex"), Entry(52001), Entry(52002)],
            [Receipt(52001, time: "2026-10-01T00:00:02.000Z"), Receipt(52002)], shell);

        AssertRefusal(result, 52001);
        Assert.Contains("reason=start-time mismatch (reused id)", result.Output);
        AssertIdAbsent(result.Output, 52002);
    }

    [Theory]
    [InlineData("resolved", "2026-10-01T01:00:00.000+01:00", 0)]
    [InlineData("powershell.exe", "2026-10-01T01:00:00.000+01:00", 0)]
    [InlineData("resolved", "2026-10-01T00:00:01.000Z", 0)]
    [InlineData("powershell.exe", "2026-10-01T00:00:01.000Z", 0)]
    [InlineData("resolved", "2026-09-30T23:59:59.000Z", 0)]
    [InlineData("powershell.exe", "2026-09-30T23:59:59.000Z", 0)]
    [InlineData("resolved", "2026-10-01T00:00:02.000Z", 2)]
    [InlineData("powershell.exe", "2026-10-01T00:00:02.000Z", 2)]
    [InlineData("resolved", "2026-10-01T00:00:00", 2)]
    [InlineData("powershell.exe", "2026-10-01T00:00:00", 2)]
    public void ReceiptTimesUseUtcInstantsWithInclusiveOneSecondTolerance(string shell, string time, int exit)
    {
        var result = Run([Entry(51003, 0, "codex"), Entry(52001)],
            [Receipt(52001, time: time)], shell);
        Assert.Equal(exit, result.ExitCode);
        if (exit == 0)
        {
            Assert.Equal(new[] { 52001 }, CandidateIds(result.Output));
            Assert.Contains("startTime=2026-10-01T00:00:00.000Z", result.Output);
        }
        else AssertRefusal(result, 52001);
    }

    [Theory]
    [InlineData("resolved", "2026-10-01T00:00:00")]
    [InlineData("powershell.exe", "2026-10-01T00:00:00")]
    [InlineData("resolved", "not-a-time")]
    public void InvalidHostTimeCannotMatchReceipt(string shell, string time)
    {
        var result = Run([Entry(51003, 0, "codex"), Entry(52001, time: time)], [Receipt(52001)], shell);
        AssertRefusal(result, 52001);
        Assert.Contains("host start time invalid", result.Output);
    }

    [Fact]
    public void LaunchedParentNeedsItsOwnMatchingReceiptAndValidChronology()
    {
        object[] snapshot = [Entry(51003, 0, "codex"), Entry(53001, 0, "worker"), Entry(52001, 53001)];
        var resolved = Run(snapshot, [Receipt(53001)]);
        Assert.Equal(0, resolved.ExitCode);
        Assert.Equal(new[] { 52001 }, CandidateIds(resolved.Output));
        Assert.Contains("route=launched-process matched(processId=53001", resolved.Output);
        AssertRefusal(Run(snapshot, []), 52001);
        AssertRefusal(Run(snapshot, [Receipt(53001, time: "2026-10-01T00:00:02Z")]), 52001);
        snapshot[1] = Entry(53001, 0, "worker", "2026-10-02T00:00:00Z");
        AssertRefusal(Run(snapshot, [Receipt(53001, time: "2026-10-02T00:00:00Z")]), 52001);
    }

    [Fact]
    public void SiblingsAndReusedParentIdsDoNotExtendHarnessProtection()
    {
        object[] snapshot = [Entry(51001, 0, "WindowsTerminal"), Entry(51003, 51001, "codex"),
            Entry(52001, 51001), Entry(52002, 51003, time: "2026-09-01T00:00:00Z")];
        var result = Run(snapshot, [Receipt(52001), Receipt(52002, time: "2026-09-01T00:00:00Z")]);
        Assert.Equal(0, result.ExitCode);
        Assert.Equal(new[] { 52001, 52002 }, CandidateIds(result.Output));
    }

    [Theory]
    [InlineData("missing-harness")]
    [InlineData("duplicate-id")]
    [InlineData("bad-parent")]
    [InlineData("malformed")]
    public void InvalidSnapshotRefusesBeforePrintingCandidates(string fault)
    {
        object[] snapshot = fault switch
        {
            "missing-harness" => [Entry(52001)],
            "duplicate-id" => [Entry(51003, 0, "codex"), Entry(52001), Entry(52001)],
            _ => [new { processId = 51003, parentProcessId = "bad", name = "codex", startTime = Started }]
        };
        var result = Run(snapshot, [Receipt(52001)], rawSnapshot: fault == "malformed" ? "[" : null);
        AssertRefusal(result);
        AssertIdAbsent(result.Output, 52001);
    }

    [Fact]
    public void DuplicateReceiptsDeduplicateEvidenceAndRetainDistinctDispatches()
    {
        var result = Run([Entry(51003, 0, "codex"), Entry(52001)],
            [Receipt(52001, "a"), Receipt(52001, "a"), Receipt(52001, "b")]);
        Assert.Equal(0, result.ExitCode);
        Assert.Single(CandidateIds(result.Output));
        Assert.Equal(1, Regex.Matches(result.Output, "dispatchId=a ").Count);
        Assert.Equal(1, Regex.Matches(result.Output, "dispatchId=b ").Count);
    }

    private (int ExitCode, string Output) Run(object[] snapshot, object[] receipts, string shell = "resolved",
        bool wrapper = false, string receiptMode = "valid", string? rawReceipts = null, string? rawSnapshot = null)
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Console helper fixtures require Windows PowerShell.");
        Directory.CreateDirectory(_root);
        var snapshotPath = Path.Combine(_root, "snapshot.json");
        var receiptPath = Path.Combine(_root, "receipts.json");
        File.WriteAllText(snapshotPath, rawSnapshot ?? JsonSerializer.Serialize(snapshot));
        File.WriteAllText(receiptPath, rawReceipts ?? JsonSerializer.Serialize(receipts));
        var repo = InfrastructureTestSupport.FindRepositoryRoot();
        var startInfo = new ProcessStartInfo
        {
            FileName = shell == "resolved" ? TestPowerShell.Executable : shell, WorkingDirectory = repo,
            UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true,
            CreateNoWindow = true
        };
        foreach (var argument in new[] { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File" })
            startInfo.ArgumentList.Add(argument);
        startInfo.ArgumentList.Add(Path.Combine(repo, "scripts", wrapper ? "Invoke-RepoScript.ps1" : "Get-ConsoleOwnership.ps1"));
        if (wrapper) startInfo.ArgumentList.Add("scripts/Get-ConsoleOwnership.ps1");
        foreach (var argument in new[] { "-SnapshotPath", snapshotPath, "-HarnessProcessId", "51003" })
            startInfo.ArgumentList.Add(argument);
        if (receiptMode != "omitted")
        {
            startInfo.ArgumentList.Add("-LaunchReceiptsPath");
            startInfo.ArgumentList.Add(receiptMode == "missing" ? Path.Combine(_root, "missing.json") : receiptPath);
        }
        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("Console helper did not start.");
        var output = ScriptFixtureProcessRunner.AssertExited(process, "Console helper did not exit or produce output.");
        Assert.Equal(string.Empty, output.Stderr);
        return (process.ExitCode, output.Stdout);
    }

    private static object Entry(int id, int parent = 0, string name = "OpenConsole", string time = Started) =>
        new { processId = id, parentProcessId = parent, name, startTime = time, commandLine = "fixture" };

    private static object Receipt(int id, string dispatch = "dispatch", string time = Started) =>
        new { processId = id, startTime = time, dispatchId = dispatch };

    private static int[] CandidateIds(string output) => Regex.Matches(output, @"(?m)^  CANDIDATE id=(\d+)\b")
        .Select(match => int.Parse(match.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture)).ToArray();

    private static void AssertRefusal((int ExitCode, string Output) result, params int[] unplaced)
    {
        Assert.Equal(2, result.ExitCode);
        Assert.Contains("Ownership ambiguous", result.Output);
        Assert.Empty(CandidateIds(result.Output));
        Assert.DoesNotContain("orchestrator-created candidates:", result.Output);
        foreach (var id in unplaced) Assert.Contains($"UNPLACED id={id}", result.Output);
    }

    private static void AssertIdAbsent(string output, int id) =>
        Assert.DoesNotMatch($@"(?<!\d){id}(?!\d)", output);

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}
