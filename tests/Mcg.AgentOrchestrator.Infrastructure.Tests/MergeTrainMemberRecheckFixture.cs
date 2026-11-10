using System.Text.Json;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;

// Parallel-safe: private TRX files, in-memory effects/cache, no git, builds or real gates.
internal sealed class MergeTrainMemberRecheckFixture : IDisposable
{
    internal const string Main = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    internal const string Tree = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
    private readonly string _root = InfrastructureTestSupport.CreateTempDirectory();
    internal MergeTrainReceipt Receipt { get; set; }
    internal MergeTrainMemberBinding[] Members { get; }
    internal MemoryCache Cache { get; } = new();
    internal List<MergeTrainMemberBinding> Calls { get; } = [];
    internal Dictionary<GoalId, string> Implicated { get; } = [];
    internal List<MergeTrainEjection> Ejections { get; } = [];
    internal List<(string Kind, string Detail)> Events { get; } = [];

    internal MergeTrainMemberRecheckFixture(int count = 3)
    {
        Members = MergeTrainPrefixBisectionTests.Members(count).Select(member =>
            member.WithRebasedHead(Tree) with { BriefVersion = 1 }).ToArray();
        Receipt = new("fixture-red", MergeTrainIdentity.Create(Members, Main, Tree, "manifest"),
            MergeTrainGateOutcome.Failed, DateTimeOffset.UnixEpoch, 0, ["train"], 1,
            [WriteTrx(("Ns.CulpritTests.One", "Failed"), ("Ns.CulpritTests.Two", "Failed"))])
            { GateAttemptId = "fixture-gate" };
    }

    internal string WriteTrx(params (string Name, string Outcome)[] tests) =>
        MergeTrainFocusedRecheckTests.WriteTrx(_root, Guid.NewGuid().ToString("N") + ".trx", tests);

    internal MergeTrainFocusedRecheckRunner.RunResult Execution(ConductorCohortFocusedSelection selection,
        bool fails, long? cpu = 10)
    {
        var paths = WriteTrx(selection.Identities.Select((name, index) =>
            (name + ".Case", fails && index == 0 ? "Failed" : "Passed")).ToArray());
        return new("fixture-run", Main, new(ConductorCohortFocusedPassKind.Executed,
            fails ? [selection.Identities[0] + ".Case"] : [], fails ? ["focused"] : [],
            selection.Identities.Count, !fails, [paths], Tree), CpuMilliseconds: cpu);
    }

    internal MergeTrainMemberRecheck.Outcome? Run(MergeTrainMemberRecheckRunner.Runner runner,
        MergeTrainMemberBinding? attributed = null, bool messageSubject = false, int attempt = 0,
        CancellationToken token = default) => MergeTrainMemberRecheck.Run(Receipt, attributed, messageSubject, attempt,
        (member, main, selection, cancellation) =>
        {
            Calls.Add(member);
            Assert.Equal(Main, main);
            return runner(member, main, selection, cancellation);
        }, Cache, (member, source) => Implicated[member.GoalId] = source, Ejections.Add,
        (kind, detail) => Events.Add((kind, detail)), token);

    public void Dispose() => Directory.Delete(_root, recursive: true);

    internal sealed class MemoryCache : MergeTrainMemberRecheck.IOutcomeCache
    {
        private readonly Dictionary<(string, string), string> _outcomes = [];
        internal Action? BeforeSave { get; set; }
        internal Func<MergeTrainMemberRecheck.Outcome, MergeTrainMemberRecheck.Outcome>? OnRead { get; set; }
        internal int Count => _outcomes.Count;
        public MergeTrainMemberRecheck.Outcome? Read(MergeTrainReceipt receipt)
        {
            if (!_outcomes.TryGetValue((receipt.ReceiptId, receipt.Identity.Value), out var json)) return null;
            var outcome = JsonSerializer.Deserialize<MergeTrainMemberRecheck.Outcome>(json)!;
            return OnRead?.Invoke(outcome) ?? outcome;
        }
        public bool TrySave(MergeTrainReceipt receipt, MergeTrainMemberRecheck.Outcome outcome)
        {
            BeforeSave?.Invoke();
            return _outcomes.TryAdd((receipt.ReceiptId, receipt.Identity.Value), JsonSerializer.Serialize(outcome));
        }
    }
}
