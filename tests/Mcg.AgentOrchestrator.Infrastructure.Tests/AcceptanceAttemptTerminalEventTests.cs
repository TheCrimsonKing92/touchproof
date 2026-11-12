using System.Text.Json;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: artifacts have a unique root; clock, processes and holder jobs are injected.
public sealed class AcceptanceAttemptTerminalEventTests
{
    [Theory]
    [InlineData("settled", "ProcessDied", "holder-tree-settled:Cancelled", "true", "Cancelled")]
    [InlineData("legacy", "ProcessDied", "legacy-holder-settled", "none", "none")]
    [InlineData("stale", "ProcessDied", "owner process was not alive, heartbeat was stale, and no terminal result artifact existed", "none", "none")]
    [InlineData("detach", "Cancelled", "holder-tree-cancelled-before-detach", "none", "Cancelled")]
    public void NoResultTransitionWritesOneEvent(string site, string outcome, string detail,
        string settled, string phase)
    {
        using var fixture = new Fixture(site);
        Assert.Equal(ConductorParallelAcceptanceAttemptOutcome.Running, fixture.Read().Outcome);
        Assert.False(File.Exists(fixture.Attempt.ResultPath));
        Assert.Empty(fixture.Events());

        fixture.Observe();
        var terminal = fixture.Read();
        Assert.Equal(outcome, terminal.Outcome.ToString());
        Assert.Equal(detail, terminal.Detail);
        AssertLine(Assert.Single(fixture.Events()), terminal, settled, phase);

        fixture.Observe();
        AssertLine(Assert.Single(fixture.Events()), terminal, settled, phase);
    }

    [Theory]
    [InlineData("settled")]
    [InlineData("legacy")]
    [InlineData("stale")]
    [InlineData("detach")]
    public void ResultPresentSuppressesTerminalEvent(string site)
    {
        using var fixture = new Fixture(site);
        // Even an unusable result belongs to the result mapper, not a no-result transition.
        File.WriteAllText(fixture.Attempt.ResultPath, "null");
        Assert.True(File.Exists(fixture.Attempt.ResultPath));

        fixture.Observe();
        fixture.Observe();
        Assert.Empty(fixture.Events());
        if (site is "settled" or "legacy")
        {
            Assert.Equal(ConductorParallelAcceptanceAttemptOutcome.Running, fixture.Read().Outcome);
            // Same holder without the result must take the emitting branch.
            File.Delete(fixture.Attempt.ResultPath);
            fixture.Observe();
            Assert.Single(fixture.Events());
        }
        if (site == "detach")
            Assert.Equal(ConductorParallelAcceptanceAttemptOutcome.Cancelled, fixture.Read().Outcome);
    }

    [Theory]
    [InlineData("settled")]
    [InlineData("legacy")]
    [InlineData("stale")]
    [InlineData("detach")]
    public void AlreadyTerminalDoesNotBackfillEvent(string site)
    {
        using var fixture = new Fixture(site);
        fixture.Save(fixture.Attempt with
        {
            Outcome = ConductorParallelAcceptanceAttemptOutcome.ProcessDied,
            CompletedAt = fixture.Now, Detail = "previous termination"
        });

        fixture.Observe();
        fixture.Observe();
        Assert.Empty(fixture.Events());
    }

    [Theory]
    [InlineData("settled")]
    [InlineData("legacy")]
    [InlineData("stale")]
    [InlineData("detach")]
    public void AppendFailurePreservesTerminalPublicationAndReturn(string site)
    {
        using var fixture = new Fixture(site);
        fixture.FailNextAppend = true;

        var error = Record.Exception(fixture.Observe);

        Assert.Null(error);
        Assert.Equal(1, fixture.FailedAppends);
        Assert.Equal(site == "detach" ? ConductorParallelAcceptanceAttemptOutcome.Cancelled :
            ConductorParallelAcceptanceAttemptOutcome.ProcessDied, fixture.Read().Outcome);
        Assert.Empty(fixture.Events());
        Assert.Contains("acceptance-attempt append failed:", File.ReadAllText(fixture.Attempt.StderrPath));
        fixture.Observe();
        Assert.Empty(fixture.Events());
    }

    [Fact]
    public void FormatterEscapesFieldsAndReadsBothHolderValues()
    {
        using var fixture = new Fixture("stale");
        var terminal = fixture.Attempt with
        {
            Outcome = ConductorParallelAcceptanceAttemptOutcome.ProcessDied,
            Detail = "reason \"quoted\" \\ path\r\nnext\tfield\u0001"
        };
        File.WriteAllText(terminal.MetadataPath + ".holder-settled.json", "{\"Settled\":false}");
        File.WriteAllText(terminal.MetadataPath + ".holder.json", "{\"Phase\":\"Released\"}");

        AssertLine(AcceptanceAttemptTerminalEvent.Format(terminal), terminal, "false", "Released");
    }

    [Theory]
    [InlineData("{", "{\"Phase\":\"Released\"}", "unreadable", "Released")]
    [InlineData("{\"Settled\":true}", "{\"Phase\":42}", "true", "unreadable")]
    [InlineData(null, null, "none", "none")]
    public void FormatterPreservesAvailableDiagnostics(string? settledJson, string? phaseJson,
        string settled, string phase)
    {
        using var fixture = new Fixture("stale");
        if (settledJson is not null)
            File.WriteAllText(fixture.Attempt.MetadataPath + ".holder-settled.json", settledJson);
        if (phaseJson is not null)
            File.WriteAllText(fixture.Attempt.MetadataPath + ".holder.json", phaseJson);

        AssertLine(AcceptanceAttemptTerminalEvent.Format(fixture.Attempt), fixture.Attempt, settled, phase);
    }

    [Fact]
    public void FormatterContainsUnexpectedReaderFailure()
    {
        using var fixture = new Fixture("stale");
        var line = AcceptanceAttemptTerminalEvent.Format(fixture.Attempt,
            _ => throw new InvalidOperationException("diagnostic reader failed"));

        AssertLine(line, fixture.Attempt, "unreadable", "unreadable");
    }

    private static void AssertLine(string line, ConductorParallelAcceptanceAttempt terminal,
        string settled, string phase)
    {
        Assert.StartsWith("ACCEPTANCE_ATTEMPT_TERMINAL ", line);
        Assert.DoesNotContain("\r", line);
        Assert.DoesNotContain("\n", line);
        Assert.Contains($"goal={JsonSerializer.Serialize(terminal.GoalId)} ", line);
        Assert.Contains($"attempt-id={JsonSerializer.Serialize(terminal.AttemptId)} ", line);
        Assert.Contains($"slot=\"slot-{terminal.SlotIndex}\" ", line);
        Assert.Contains($"outcome=\"{terminal.Outcome}\" ", line);
        Assert.Contains($"detail={JsonSerializer.Serialize(terminal.Detail ?? "")} ", line);
        Assert.Contains($"holder-settled=\"{settled}\" ", line);
        Assert.EndsWith($"holder-phase=\"{phase}\"", line);
    }

    private sealed class Fixture : IDisposable
    {
        private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
        private readonly string _root = Path.Combine(Path.GetTempPath(), $"attempt-terminal-{Guid.NewGuid():N}");
        private readonly string _site;
        private readonly string _log;
        private readonly ConductorParallelAcceptanceCandidate _candidate;
        private readonly ConductorParallelAcceptanceAttemptCoordinator _coordinator;
        private readonly SpawnProcessIdentity _generation = new(111, DateTimeOffset.UnixEpoch, "conductor.exe");
        private readonly SpawnProcessIdentity _holder = new(222, DateTimeOffset.UnixEpoch, "dotnet.exe");
        private readonly AcceptanceHolderLease _lease;
        private bool _holderAlive = true;
        private int _activeShards;
        internal bool FailNextAppend;
        internal int FailedAppends;
        internal DateTimeOffset Now = DateTimeOffset.UnixEpoch.AddDays(1);
        internal ConductorParallelAcceptanceAttempt Attempt { get; }

        internal Fixture(string site)
        {
            _site = site;
            Directory.CreateDirectory(_root);
            _log = Path.Combine(_root, "conduct-events.log");
            _lease = new AcceptanceHolderLease(utcNow: () => Now,
                classify: identity => identity == _holder && !_holderAlive
                    ? SpawnTrackedProcessStatus.DeadOrRecycled : SpawnTrackedProcessStatus.LiveMatch,
                attach: _ => new EmptyDisposable(),
                job: (_, _, status, cancel) =>
                {
                    if (cancel) { _holderAlive = false; _activeShards = 0; }
                    return new(status == SpawnTrackedProcessStatus.LiveMatch,
                        !_holderAlive && _activeShards == 0, $"active-shards:{_activeShards}");
                });
            var writer = new ConductEventLogWriter(_log, utcNow: () => Now,
                beforeAppendCommit: () =>
                {
                    if (!FailNextAppend) return;
                    FailNextAppend = false;
                    FailedAppends++;
                    throw new IOException("injected conduct append failure");
                });
            var goal = new AgentOrchestratorKernel().CreateGoal("Terminal attempt event");
            _candidate = ConductorParallelAcceptanceCandidate.Create(goal, 0, [], "branch", "main");
            _coordinator = new ConductorParallelAcceptanceAttemptCoordinator(Path.Combine(_root, "attempts"), "integration",
                utcNow: () => Now, isProcessAlive: _ => _holderAlive,
                holderLease: site == "stale" ? null : _lease, generationIdentity: _generation,
                conductEventLogWriter: writer, recentHeartbeatGrace: TimeSpan.FromMinutes(5),
                launchOwnedProcess: launch =>
                {
                    if (site != "stale")
                    {
                        using var claim = _lease.Claim(AcceptanceHolderBinding.For(launch.Attempt), _holder);
                    }
                    return new(_holder.ProcessId, _holder.StartedAt, _holder.ImagePath);
                });
            if (site == "legacy")
            {
                var directory = Path.Combine(_root, "attempts", goal.Id.Value);
                Directory.CreateDirectory(directory);
                Attempt = new("legacy", goal.Id.Value, goal.Id.Value, 0, "branch", "main", Now, Now,
                    _holder.ProcessId, ConductorParallelAcceptanceAttemptOutcome.Running,
                    Path.Combine(directory, "out"), Path.Combine(directory, "err"),
                    Path.Combine(directory, "exit"), Path.Combine(directory, "heartbeat"),
                    Path.Combine(directory, "result"), Path.Combine(directory, "legacy.attempt.json"))
                { OwnerProcessStartedAt = _holder.StartedAt, OwnerExecutablePath = _holder.ImagePath };
                Save(Attempt);
                _holderAlive = false;
                return;
            }

            var started = _coordinator.Evaluate(_candidate, ConductorAutonomyPolicy.Conservative,
                (_, _, _, _, _) => throw new InvalidOperationException("Unexpected verifier execution."));
            Assert.Equal(ConductorParallelAcceptanceAttemptDecisionKind.Started, started.Kind);
            Attempt = started.Attempt!;
            var binding = AcceptanceHolderBinding.For(Attempt);
            if (site == "settled")
            {
                Assert.True(_lease.Cancel(binding, _generation, "test cancellation"));
                File.WriteAllText(Attempt.MetadataPath + ".holder-settled.json", "{\"Settled\":true}");
            }
            else if (site == "detach")
            {
                _holderAlive = false;
                _activeShards = 1;
                // Deduplicate the pre-existing hold append before arming the failure hook.
                Assert.Single(_coordinator.GetAllCapacityReservingAttempts());
                Assert.Equal("Claimed", _lease.Read(binding).Phase);
            }
            else
            {
                Assert.Equal(ConductorParallelAcceptanceAttemptDecisionKind.Running,
                    _coordinator.Evaluate(_candidate, ConductorAutonomyPolicy.Conservative,
                        (_, _, _, _, _) => throw new InvalidOperationException("Unexpected verifier execution.")).Kind);
                _holderAlive = false;
                Now = Now.AddMinutes(6);
            }
        }

        internal void Observe()
        {
            if (_site == "detach") _coordinator.PrepareHolderDetach();
            else if (_site == "stale")
                _coordinator.ObserveExistingAttempt(Attempt, _candidate);
            else _coordinator.GetAllCapacityReservingAttempts();
        }

        internal void Save(ConductorParallelAcceptanceAttempt attempt) =>
            File.WriteAllText(attempt.MetadataPath, JsonSerializer.Serialize(attempt, JsonOptions));

        internal ConductorParallelAcceptanceAttempt Read() =>
            JsonSerializer.Deserialize<ConductorParallelAcceptanceAttempt>(File.ReadAllText(Attempt.MetadataPath), JsonOptions)!;

        internal string[] Events() => !File.Exists(_log) ? [] : File.ReadAllLines(_log).Select(line =>
        {
            using var document = JsonDocument.Parse(line);
            return document.RootElement.GetProperty("eventKind").GetString() == "acceptance-attempt"
                ? document.RootElement.GetProperty("detail").GetString() : null;
        }).OfType<string>().ToArray();

        public void Dispose() => Directory.Delete(_root, recursive: true);
        private sealed class EmptyDisposable : IDisposable { public void Dispose() { } }
    }
}
