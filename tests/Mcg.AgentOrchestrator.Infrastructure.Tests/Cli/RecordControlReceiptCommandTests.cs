using System.Text.Json;
using Mcg.AgentOrchestrator.App.Orchestration;
using Xunit;

// Parallel-safe: the fixture owns the workspace, database, and child environment.
public sealed class RecordControlReceiptCommandTests
{
    private const string GoalSelector = "ABC20000";
    private const string Obligation = "criterion-v1-0";
    private const string Candidate = "abc123";

    [Theory]
    [InlineData("negative-control", true)]
    [InlineData("series", false)]
    [InlineData("shard", true)]
    public async Task RealCliRecordsAllFieldsAndFreshStoreReadsThemBack(string kind, bool includeTrx)
    {
        using var fixture = new CliAskGoalFixture();
        await fixture.SeedAsync();
        var command = includeTrx ? "  verify --filter \"Case A\"  " : "--help";
        string[] trxPaths = includeTrx ? [@"results\with spaces.trx", "./another.trx"] : [];
        var args = Arguments(kind, command).Concat(trxPaths.SelectMany(path => new[] { "--trx", path })).ToArray();

        var result = await fixture.RunAsync(args);

        Assert.True(result.ExitCode == 0, result.StandardError);
        var receiptId = result.StandardOutput.TrimEnd('\r', '\n');
        Assert.True(Guid.TryParseExact(receiptId, "N", out _), result.StandardOutput);
        Assert.Equal(receiptId + Environment.NewLine, result.StandardOutput);
        var directory = ReceiptDirectory(fixture);
        var path = Assert.Single(Directory.GetFiles(directory));
        Assert.Equal(Path.Combine(directory, receiptId + ".json"), path);

        using var document = JsonDocument.Parse(await File.ReadAllBytesAsync(path));
        var json = document.RootElement;
        Assert.Equal(11, json.EnumerateObject().Count());
        Assert.Equal(receiptId, json.GetProperty("receiptId").GetString());
        Assert.Equal(CliAskGoalFixture.OtherId.Value, json.GetProperty("goalId").GetString());
        Assert.Equal(Obligation, json.GetProperty("obligationId").GetString());
        Assert.Equal(kind, json.GetProperty("kind").GetString());
        Assert.Equal(command, json.GetProperty("command").GetString());
        Assert.Equal(-5, json.GetProperty("exitStatus").GetInt32());
        Assert.Equal(7, json.GetProperty("passedCount").GetInt32());
        Assert.Equal(2, json.GetProperty("failedCount").GetInt32());
        Assert.Equal(trxPaths, json.GetProperty("trxPaths").EnumerateArray().Select(item => item.GetString()));
        Assert.Equal(Candidate, json.GetProperty("candidateSha").GetString());
        var recordedAt = json.GetProperty("recordedAt").GetDateTimeOffset();
        Assert.NotEqual(default, recordedAt);
        Assert.Equal(TimeSpan.Zero, recordedAt.Offset);

        var store = new OperatorControlReceiptStore(fixture.Workspace.OrchestratorDirectory);
        var receipt = Assert.IsType<OperatorControlReceipt>(store.Read(receiptId)?.Receipt);
        Assert.Equal(receiptId, receipt.ReceiptId);
        Assert.Equal(CliAskGoalFixture.OtherId.Value, receipt.GoalId);
        Assert.Equal(Obligation, receipt.ObligationId);
        Assert.Equal(kind, receipt.Kind);
        Assert.Equal(command, receipt.Command);
        Assert.Equal(-5, receipt.ExitStatus);
        Assert.Equal(7, receipt.PassedCount);
        Assert.Equal(2, receipt.FailedCount);
        Assert.Equal(trxPaths, receipt.TrxPaths);
        Assert.Equal(Candidate, receipt.CandidateSha);
        Assert.True(recordedAt.EqualsExact(receipt.RecordedAt));
    }

    [Theory]
    [InlineData("--goal", "missing", "was not found")]
    [InlineData("--goal", "abc", "ambiguous")]
    [InlineData("--goal", "../abc20000", "unique prefix")]
    [InlineData("--obligation", "../criterion-v1-0", "safe obligation identifier")]
    [InlineData("--kind", "Series", "negative-control, series or shard")]
    [InlineData("--exit-status", "not-an-int", "signed integer")]
    [InlineData("--failed", "-1", "non-negative integer")]
    public async Task RealCliRejectsInvalidFieldsWithoutPublishing(string option, string value, string diagnostic)
    {
        using var fixture = new CliAskGoalFixture();
        await fixture.SeedAsync();
        var args = Arguments("series", "verify");
        args[Array.IndexOf(args, option) + 1] = value;

        var result = await fixture.RunAsync(args);

        Assert.NotEqual(0, result.ExitCode);
        Assert.Empty(result.StandardOutput);
        Assert.Contains(diagnostic, result.StandardError);
        Assert.False(Directory.Exists(ReceiptDirectory(fixture)));
    }

    [Fact]
    public async Task RealCliPrintsNoIdWhenReceiptDirectoryCannotBeCreated()
    {
        using var fixture = new CliAskGoalFixture();
        await fixture.SeedAsync();
        var directory = ReceiptDirectory(fixture);
        Directory.CreateDirectory(Path.GetDirectoryName(directory)!);
        await File.WriteAllTextAsync(directory, "publication blocked by a file");

        var result = await fixture.RunAsync(Arguments("series", "verify"));

        Assert.NotEqual(0, result.ExitCode);
        Assert.Empty(result.StandardOutput);
        Assert.Contains("control-receipts", result.StandardError);
        Assert.Equal("publication blocked by a file", await File.ReadAllTextAsync(directory));
    }

    private static string[] Arguments(string kind, string command) =>
        ["record-control-receipt", "--goal", GoalSelector, "--obligation", Obligation, "--kind", kind,
            "--command", command, "--exit-status", "-5", "--passed", "7", "--failed", "2", "--candidate-sha", Candidate, "--json"];

    private static string ReceiptDirectory(CliAskGoalFixture fixture) =>
        Path.Combine(fixture.Workspace.OrchestratorDirectory, "operator-evidence", "control-receipts");
}
