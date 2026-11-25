using System.Text.Json;
using Mcg.AgentOrchestrator.App.Cli;

public sealed class ShadowStartReclamationTests
{
    private static readonly string[] Disposable = ["--scratch-retention=disposable"];

    [Fact]
    public void ConsecutiveDisposableRuns_DoNotAccumulateCopies()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var fixture = new ShadowStartCommandTests.Fixture();
        fixture.CreateDatabase("state.db");
        fixture.CreateDatabase("cohort-acceptance.db");

        var success = fixture.Run("normal", "success", extra: Disposable);
        Assert.True(success.Exit == 0, success.Output + success.Error);
        Assert.Contains("cleanup=reclaimed", success.Output);
        AssertReclaimed(fixture, "success", expectedRuns: 1);
        Assert.Single(Directory.EnumerateFiles(fixture.Registry, "*.settlement.json"));
        var path = Assert.Single(Directory.EnumerateFiles(fixture.Registry, "*.receipt.json"));
        using (var receipt = JsonDocument.Parse(File.ReadAllText(path)))
        {
            var root = receipt.RootElement;
            Assert.True(root.GetProperty("wall_ms").TryGetInt64(out _));
            Assert.True(root.GetProperty("first_tick_ms").TryGetInt64(out _));
            Assert.Contains($"run_id={root.GetProperty("run_id").GetString()}", success.Output);
            Assert.Equal("reclaimed", root.GetProperty("cleanup").GetString());
            Assert.Contains("startup-first-tick", root.GetProperty("journal_tail").GetProperty("conduct-events.log").GetString());
        }

        File.WriteAllBytes(Path.Combine(fixture.Source, "state.db-journal"), []);
        File.WriteAllBytes(Path.Combine(fixture.Source, "cohort-acceptance.db-journal"), []);
        var copies = 0;
        var partial = fixture.Hooks with
        {
            BeforeBackup = store =>
            {
                Assert.True(File.Exists(store.Destination));
                Assert.True(File.Exists(store.Staged));
                if (++copies == 2)
                {
                    Assert.True(File.Exists(store.Staged + "-journal"));
                    throw new IOException("Injected failure after one completed backup.");
                }
            }
        };
        var failure = fixture.Run("normal", "partial", extra: Disposable, hooks: partial);
        Assert.Equal(2, copies);
        Assert.Equal(1, failure.Exit);
        Assert.Contains("assertion=database-backup", failure.Error);
        Assert.Contains("cleanup=reclaimed", failure.Error);
        AssertReclaimed(fixture, "partial", expectedRuns: 2);

        var marker = fixture.Scratch("timeout-started");
        var timeout = fixture.Run("no-first-tick", "timeout", startupSeconds: "8", extra: Disposable, marker: marker);
        Assert.Equal("started", File.ReadAllText(marker));
        Assert.Equal(1, timeout.Exit);
        Assert.Contains("assertion=startup-ceiling", timeout.Error);
        Assert.Contains("cleanup=reclaimed", timeout.Error);
        AssertReclaimed(fixture, "timeout", expectedRuns: 3);

        var interrupted = ShadowStartCrashRecoveryTests.Seed(fixture, "interrupted");
        Assert.True(File.Exists(Path.Combine(interrupted.State, "state.db-wal")));
        var again = fixture.Run("normal", "after-interruption", extra: Disposable);
        Assert.True(again.Exit == 0, again.Output + again.Error);
        AssertReclaimed(fixture, "interrupted", expectedRuns: 5);
        AssertReclaimed(fixture, "after-interruption", expectedRuns: 5);
    }

    [Fact]
    public async Task CancellationAfterAdmissionReclaimsCopies()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var fixture = new ShadowStartCommandTests.Fixture();
        fixture.CreateDatabase("state.db");
        using var cancel = new CancellationTokenSource();
        using var ready = new FileSystemWatcher(Path.GetDirectoryName(fixture.Scratch("ready"))!, "ready");
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        ready.Created += (_, _) => started.TrySetResult();
        ready.EnableRaisingEvents = true;
        var run = Task.Run(() => fixture.Run("no-first-tick", "cancelled", extra: Disposable,
            marker: fixture.Scratch("ready"), cancellationToken: cancel.Token));
        try { await started.Task.WaitAsync(TimeSpan.FromSeconds(15)); }
        finally
        {
            cancel.Cancel();
            await run;
        }

        var result = await run;
        Assert.Equal(1, result.Exit);
        Assert.Contains("assertion=cancelled", result.Error);
        Assert.Contains("cleanup=reclaimed", result.Error);
        AssertReclaimed(fixture, "cancelled", expectedRuns: 1);
    }

    private static void AssertReclaimed(ShadowStartCommandTests.Fixture fixture, string scratch, int expectedRuns)
    {
        var state = Path.Combine(fixture.Scratch(scratch), ".orchestrator");
        Assert.Empty(Directory.EnumerateFiles(state));
        Assert.Empty(Directory.EnumerateFileSystemEntries(fixture.Staging));
        Assert.Equal(expectedRuns, Directory.EnumerateFiles(fixture.Registry, "*.receipt.json").Count());
        foreach (var path in Directory.EnumerateFiles(fixture.Registry, "*.run.json"))
            Assert.Equal(ShadowStartPhase.Reclaimed, JsonSerializer.Deserialize<ShadowStartRun>(File.ReadAllText(path))!.Phase);
    }
}
