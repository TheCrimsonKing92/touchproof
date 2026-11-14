using System.Diagnostics;
using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.Infrastructure;
using System.Text.Json;
using System.Text.RegularExpressions;

public sealed class ConsoleOwnershipCliCommandTests : IDisposable
{
    private const string Started = "2026-10-01T00:00:00.000Z";
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"console-ownership-{Guid.NewGuid():N}");

    [Fact]
    public void AmbiguitySuppressesAllCandidatesUntilUnmatchedHostIsRemoved()
    {
        object[] snapshot = [
            Entry(51001, 0, "WindowsTerminal"), Entry(51002, 51001, "OpenConsole.exe"),
            Entry(51003, 51002, "codex"), Entry(51004, 51003, "CONHOST.EXE"),
            Entry(52001), Entry(52002), Entry(52003), Entry(52999)
        ];
        object[] receipts = [Receipt(52001, "dispatch-a"), Receipt(52002, "dispatch-b"),
            Receipt(52003, "dispatch-c"), Receipt(51002, "harness-must-win")];
        var refused = Run(snapshot, receipts);
        AssertRefusal(refused, 52999);
        foreach (var id in new[] { 52001, 52002, 52003 }) AssertIdAbsent(refused.Output, id);

        var resolved = Run(snapshot[..^1], receipts);
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

    [Fact]
    public void ReusedConsoleId_DifferentStartTimeSuppressesCandidates()
    {
        var result = Run([Entry(51003, 0, "codex"), Entry(52001), Entry(52002)],
            [Receipt(52001, time: "2026-10-01T00:00:02.000Z"), Receipt(52002)]);

        AssertRefusal(result, 52001);
        Assert.Contains("reason=start-time mismatch (reused id)", result.Output);
        AssertIdAbsent(result.Output, 52002);
    }

    [Theory]
    [InlineData("2026-10-01T01:00:00.000+01:00", 0)]
    [InlineData("2026-10-01T00:00:01.000Z", 0)]
    [InlineData("2026-09-30T23:59:59.000Z", 0)]
    [InlineData("2026-10-01T00:00:02.000Z", 2)]
    [InlineData("2026-10-01T00:00:01.001Z", 2)]
    [InlineData("2026-10-01T00:00:00", 2)]
    public void ReceiptTimesUseUtcInstantsWithInclusiveOneSecondTolerance(string time, int exit)
    {
        var result = Run([Entry(51003, 0, "codex"), Entry(52001)],
            [Receipt(52001, time: time)]);
        Assert.Equal(exit, result.ExitCode);
        if (exit == 0)
        {
            Assert.Equal(new[] { 52001 }, CandidateIds(result.Output));
            Assert.Contains("startTime=2026-10-01T00:00:00.000Z", result.Output);
        }
        else AssertRefusal(result, 52001);
    }

    [Theory]
    [InlineData("2026-10-01T00:00:00")]
    [InlineData("not-a-time")]
    public void InvalidHostTimeCannotMatchReceipt(string time)
    {
        var result = Run([Entry(51003, 0, "codex"), Entry(52001, time: time)], [Receipt(52001)]);
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

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void TextReport_MatchesScriptExactlyForResolvedAndAmbiguousRuns(bool ambiguous)
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Script parity requires Windows PowerShell.");
        var (snapshot, receipts) = ReportFixture(ambiguous);
        var verb = Run(snapshot, receipts);
        var paths = WriteFixture(snapshot, receipts);
        var repo = VerifiedRepositoryRoot.Find();
        var start = new ProcessStartInfo
        {
            FileName = TestPowerShell.Executable, WorkingDirectory = repo,
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true
        };
        start.Environment.Clear();
        foreach (var key in new[] { "PATH", "SystemRoot", "WINDIR", "TEMP", "TMP", "USERPROFILE", "ProgramFiles", "PSModulePath" })
            if (Environment.GetEnvironmentVariable(key) is { } value) start.Environment[key] = value;
        start.Environment[VerifiedRepositoryRoot.VariableName] = repo;
        foreach (var argument in new[] { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File",
            Path.Combine(repo, "scripts", "Get-ConsoleOwnership.ps1"), "-SnapshotPath", paths.Snapshot,
            "-HarnessProcessId", "51003", "-LaunchReceiptsPath", paths.Receipts })
            start.ArgumentList.Add(argument);
        var script = TestChildProcessCapture.Run(start);
        Assert.Equal(string.Empty, script.Stderr);
        Assert.Equal(ambiguous ? 2 : 0, verb.ExitCode);
        Assert.Equal(script.ExitCode, verb.ExitCode);
        Assert.Equal(script.Stdout, verb.Output);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void JsonReport_PreservesTextFieldsAndSuppressesCandidateIdentities(bool ambiguous)
    {
        var (snapshot, receipts) = ReportFixture(ambiguous);
        var text = Run(snapshot, receipts);
        var json = Run(snapshot, receipts, json: true);
        using var document = JsonDocument.Parse(json.Output);
        var root = document.RootElement;
        Assert.Equal(JsonValueKind.Object, root.ValueKind);
        Assert.Equal(new[] { "harnessProcessId", "harnessOwned", "orchestratorCreated", "unattributed", "result",
            "refusal", "withheld", "unplaced", "candidates" }, root.EnumerateObject().Select(p => p.Name));
        Assert.Equal(text.ExitCode, json.ExitCode);
        Assert.Equal(ambiguous ? 2 : 0, json.ExitCode);
        Assert.Equal(51003, root.GetProperty("harnessProcessId").GetInt32());
        Assert.Equal(2, root.GetProperty("harnessOwned").GetInt32());
        Assert.Equal(3, root.GetProperty("orchestratorCreated").GetInt32());
        Assert.Equal(ambiguous ? 1 : 0, root.GetProperty("unattributed").GetInt32());
        Assert.Equal(ambiguous ? "ambiguous" : "resolved", root.GetProperty("result").GetString());
        Assert.Equal(JsonValueKind.Null, root.GetProperty("refusal").ValueKind);
        foreach (var (property, prefix) in new[] { ("withheld", "WITHHELD"), ("unplaced", "UNPLACED"), ("candidates", "CANDIDATE") })
        {
            var entries = root.GetProperty(property).EnumerateArray().ToArray();
            var lines = text.Output.Split(Environment.NewLine).Where(l => l.StartsWith($"  {prefix} ", StringComparison.Ordinal)).ToArray();
            Assert.Equal(lines.Length, entries.Length);
            for (var index = 0; index < entries.Length; index++)
            {
                var entry = entries[index];
                var suffix = property == "candidates"
                    ? "evidence=" + string.Join("; ", entry.GetProperty("evidence").EnumerateArray().Select(e => e.GetString()))
                    : "reason=" + entry.GetProperty("reason").GetString();
                Assert.Equal($"  {prefix} id={entry.GetProperty("id").GetInt32()} name={entry.GetProperty("name").GetString()} startTime={entry.GetProperty("startTime").GetString()} {suffix}", lines[index]);
                Assert.Equal(new[] { "id", "name", "startTime", property == "candidates" ? "evidence" : "reason" },
                    entry.EnumerateObject().Select(p => p.Name));
            }
        }
        Assert.Equal(new[] { 51002, 51004 }, root.GetProperty("withheld").EnumerateArray().Select(e => e.GetProperty("id").GetInt32()));
        Assert.Equal(ambiguous ? new[] { 52999 } : [], root.GetProperty("unplaced").EnumerateArray().Select(e => e.GetProperty("id").GetInt32()));
        if (ambiguous)
        {
            Assert.Empty(root.GetProperty("candidates").EnumerateArray());
            foreach (var id in new[] { 52001, 52002, 52003 }) AssertIdAbsent(json.Output, id);
        }
        else Assert.Equal(new[] { 52001, 52002, 52003 },
            root.GetProperty("candidates").EnumerateArray().Select(e => e.GetProperty("id").GetInt32()));
    }

    [Theory]
    [InlineData("[", "snapshot-invalid", "Expected a JSON array.")]
    [InlineData("[]", "harness-missing", "Harness process missing from snapshot.")]
    public void JsonRefusal_DistinguishesUnclassifiedFromEmptyCounts(string raw, string reason, string detail)
    {
        var result = Run([], [], rawSnapshot: raw, json: true);
        Assert.Equal(2, result.ExitCode);
        using var document = JsonDocument.Parse(result.Output);
        var root = document.RootElement;
        Assert.Equal(51003, root.GetProperty("harnessProcessId").GetInt32());
        Assert.Equal("ambiguous", root.GetProperty("result").GetString());
        Assert.Equal(reason, root.GetProperty("refusal").GetProperty("reason").GetString());
        Assert.Equal(detail, root.GetProperty("refusal").GetProperty("detail").GetString());
        foreach (var field in new[] { "harnessOwned", "orchestratorCreated", "unattributed" })
            Assert.Equal(JsonValueKind.Null, root.GetProperty(field).ValueKind);
        foreach (var field in new[] { "withheld", "unplaced", "candidates" })
            Assert.Empty(root.GetProperty(field).EnumerateArray());
        Assert.Equal($"Ownership ambiguous: snapshot unavailable or invalid ({detail}); candidate group suppressed.{Environment.NewLine}",
            Run([], [], rawSnapshot: raw).Output);
    }

    [Fact]
    public void LiveInventory_CapturesExactlyOnceAndUsesSameAttribution()
    {
        var paths = WriteFixture([], [Receipt(52001)]);
        var calls = 0;
        var result = Invoke(["console-ownership", "--harness-pid", "51003", "--receipts", paths.Receipts], () =>
        {
            calls++;
            return Inventory(ProcessInspectionStatus.Available);
        });
        Assert.Equal(1, calls);
        Assert.Equal(Run([Entry(51003, 0, "codex"), Entry(52001)], [Receipt(52001)]), result);
    }

    [Theory]
    [InlineData("throw", "Live process inventory unavailable.")]
    [InlineData("failure", "Live process inventory unavailable.")]
    [InlineData("inaccessible", "Live process inventory contains inaccessible evidence.")]
    [InlineData("empty", "Live process inventory empty.")]
    public void LiveInventory_UnavailableEvidenceRefusesWithoutRetry(string fault, string detail)
    {
        var calls = 0;
        var result = Invoke(["console-ownership", "--harness-pid", "51003", "--json"], () =>
        {
            calls++;
            return fault switch
            {
                "throw" => throw new IOException("fixture"),
                "failure" => new ProcessCommandLineSnapshot(new Dictionary<int, ProcessInspectionRecord>(),
                    new ProcessInspectionFailure(ProcessInspectionStatus.NativeFailure, 5, "enumerate")),
                "empty" => ProcessCommandLineSnapshot.Empty,
                _ => Inventory(ProcessInspectionStatus.AccessDenied)
            };
        });
        Assert.Equal(1, calls);
        Assert.Equal(2, result.ExitCode);
        using var document = JsonDocument.Parse(result.Output);
        Assert.Equal("inventory-unavailable", document.RootElement.GetProperty("refusal").GetProperty("reason").GetString());
        Assert.Equal(detail, document.RootElement.GetProperty("refusal").GetProperty("detail").GetString());
        Assert.Empty(document.RootElement.GetProperty("candidates").EnumerateArray());
    }

    [Theory]
    [InlineData("--unknown", "x")]
    [InlineData("--harness-pid", "bad")]
    [InlineData("--harness-pid", null)]
    [InlineData("--snapshot", "file.json")]
    public void InvalidArguments_ReturnUsageErrorBeforeReadingInputs(string flag, string? value)
    {
        using var output = new StringWriter();
        using var error = new StringWriter();
        var arguments = value is null ? new[] { "console-ownership", flag } : new[] { "console-ownership", flag, value };
        var result = ConsoleOwnershipCliCommand.Run(arguments, output, error,
            () => throw new InvalidOperationException("Usage errors must not capture inventory."));
        Assert.Equal(1, result);
        Assert.Equal(string.Empty, output.ToString());
        Assert.StartsWith("Error: ", error.ToString());
    }

    [Fact]
    public void Registration_AcceptsDocumentedFlagsAndRejectsUnknownOptions()
    {
        Assert.Contains("console-ownership", CliArgumentParser.RecognizedCommands);
        CliCommandHelp.ThrowIfInvalidFlags(["console-ownership", "--harness-pid", "51003", "--snapshot", "snapshot.json", "--receipts", "receipts.json", "--json"]);
        Assert.Throws<ArgumentException>(() => CliCommandHelp.ThrowIfInvalidFlags(["console-ownership", "--unknown"]));
    }

    [Fact]
    public void SnapshotFields_CaseInsensitiveLastValueWinsAndOffsetsRemainEvidence()
    {
        var result = Run([], [Receipt(52001)], rawSnapshot: """
            [{"processId":51003,"parentProcessId":0,"name":"codex","startTime":"2026-10-01T00:00:00Z"},
             {"processId":1,"PROCESSID":52001,"PARENTPROCESSID":0,"NAME":"CONHOST.EXE","STARTTIME":"2026-10-01T01:00:00+01:00"}]
            """);
        Assert.Equal(0, result.ExitCode);
        Assert.Equal(new[] { 52001 }, CandidateIds(result.Output));
        Assert.Contains("name=CONHOST.EXE startTime=2026-10-01T00:00:00.000Z", result.Output);
    }

    [Theory]
    [InlineData("[1]", "Expected a JSON object record.")]
    [InlineData("[{\"processId\":51003,\"nested\":{}}]", "Record fields must be scalar strings or integers.")]
    [InlineData("[{\"processId\":51003,\"extra\":true}]", "Record fields must be scalar strings or integers.")]
    [InlineData("[{\"processId\":51003.0,\"parentProcessId\":0,\"name\":\"codex\"}]", "Invalid snapshot identity fields.")]
    public void SnapshotParsing_RejectsNonRecordsAndInvalidScalars(string raw, string detail)
    {
        var result = Run([], [], rawSnapshot: raw);
        Assert.Equal(2, result.ExitCode);
        Assert.Equal($"Ownership ambiguous: snapshot unavailable or invalid ({detail}); candidate group suppressed.{Environment.NewLine}", result.Output);
    }

    [Fact]
    public void ReceiptAttribution_DoesNotPropagatePastImmediateParent()
    {
        object[] snapshot = [Entry(51003, 0, "codex"), Entry(53001, 0, "worker"),
            Entry(53002, 53001, "worker"), Entry(52001, 53002)];
        AssertRefusal(Run(snapshot, [Receipt(53001)]), 52001);
        var resolved = Run(snapshot, [Receipt(53002)]);
        Assert.Equal(0, resolved.ExitCode);
        Assert.Equal(new[] { 52001 }, CandidateIds(resolved.Output));
        Assert.Contains("route=launched-process matched(processId=53002", resolved.Output);
    }

    private static ProcessCommandLineSnapshot Inventory(ProcessInspectionStatus status) => new(
        new Dictionary<int, ProcessInspectionRecord>
        {
            [51003] = new(51003, 0, "codex", null, DateTimeOffset.Parse(Started), "fixture", ProcessInspectionStatus.Available),
            [52001] = new(52001, 0, "OpenConsole", null, DateTimeOffset.Parse(Started), "fixture", status)
        });

    private static (object[] Snapshot, object[] Receipts) ReportFixture(bool ambiguous) =>
        ([Entry(51001, 0, "WindowsTerminal"), Entry(51002, 51001, "OpenConsole.exe"),
          Entry(51003, 51002, "codex"), Entry(51004, 51003, "CONHOST.EXE"),
          Entry(52001), Entry(52002), Entry(52003), .. (ambiguous ? new[] { Entry(52999) } : Array.Empty<object>())],
         [Receipt(52001, "b"), Receipt(52001, "a"), Receipt(52001, "a"), Receipt(52002), Receipt(52003),
          Receipt(51002, "harness-must-win")]);

    private (int ExitCode, string Output) Run(object[] snapshot, object[] receipts,
        string receiptMode = "valid", string? rawReceipts = null, string? rawSnapshot = null, bool json = false)
    {
        var paths = WriteFixture(snapshot, receipts, rawSnapshot, rawReceipts);
        var arguments = new List<string> { "console-ownership", "--snapshot", paths.Snapshot, "--harness-pid", "51003" };
        if (receiptMode != "omitted")
            arguments.AddRange(["--receipts", receiptMode == "missing" ? Path.Combine(_root, "missing.json") : paths.Receipts]);
        if (json) arguments.Add("--json");
        return Invoke(arguments, () => throw new InvalidOperationException("Explicit snapshot must not capture inventory."));
    }

    private (string Snapshot, string Receipts) WriteFixture(object[] snapshot, object[] receipts,
        string? rawSnapshot = null, string? rawReceipts = null)
    {
        Directory.CreateDirectory(_root);
        var snapshotPath = Path.Combine(_root, "snapshot.json");
        var receiptPath = Path.Combine(_root, "receipts.json");
        File.WriteAllText(snapshotPath, rawSnapshot ?? JsonSerializer.Serialize(snapshot));
        File.WriteAllText(receiptPath, rawReceipts ?? JsonSerializer.Serialize(receipts));
        return (snapshotPath, receiptPath);
    }

    private static (int ExitCode, string Output) Invoke(IReadOnlyList<string> arguments,
        Func<ProcessCommandLineSnapshot> inventory)
    {
        using var output = new StringWriter();
        using var error = new StringWriter();
        var exit = ConsoleOwnershipCliCommand.Run(arguments, output, error, inventory);
        Assert.Equal(string.Empty, error.ToString());
        return (exit, output.ToString());
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
