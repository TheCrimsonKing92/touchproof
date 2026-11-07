using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core.Conductor;

// Parallel-safe: private policy state, registries and sinks; no ambient setting is changed.
public sealed class AdoptedBuildPushTriggerTests
{
    [Theory]
    [InlineData(true, "", "", 1)]
    [InlineData(false, "", "", 0)]
    [InlineData(true, "pending-project", "", 0)]
    [InlineData(true, "", "failed-project", 0)]
    public void FinalizedFleet_PushesOnlyWhenEnabledAndAllAdopted(
        bool enabled, string pending, string failed, int count)
    {
        var pushed = new List<string>();
        var events = new List<string>();
        var attention = new List<string>();
        var commit = new string('c', 40);
        var trigger = new AdoptedBuildPushTrigger(
            () => ConductorAutonomyPolicy.Conservative with { AdoptedBuildPushEnabled = enabled },
            value => { pushed.Add(value); events.Add("ADOPTED_BUILD_PUSHED"); }, events.Add, attention.Add);
        trigger.OnFinalized(commit, pending, failed);
        Assert.Equal(count, pushed.Count);
        Assert.Equal(count, events.Count);
        if (count != 0) Assert.Equal(commit, Assert.Single(pushed));
        Assert.Empty(attention);
    }

    [Fact]
    public void PolicyFileChanges_AreReadAtEachFinalizedAdoption()
    {
        var root = Directory.CreateTempSubdirectory("push-policy-").FullName;
        try
        {
            var pushed = new List<string>();
            var events = new List<string>();
            var trigger = new AdoptedBuildPushTrigger(
                () => ConductorAutonomyPolicy.LoadFromOrchestratorDirectory(new DirectoryInfo(root)), pushed.Add, events.Add,
                _ => Assert.Fail("No attention expected for a valid policy."));
            void Write(bool enabled) => File.WriteAllText(Path.Combine(root, "conductor-policy.json"),
                (ConductorAutonomyPolicy.Conservative with { AdoptedBuildPushEnabled = enabled }).ToJson());
            Write(false);
            trigger.OnFinalized("off", "", "");
            Write(true);
            trigger.OnFinalized("on", "", "");
            Write(false);
            trigger.OnFinalized("off-again", "", "");
            Assert.Equal(new[] { "on" }, pushed);
            Assert.Empty(events);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void MalformedPolicy_HoldsWithAttention()
    {
        var events = new List<string>();
        var attention = new List<string>();
        var trigger = new AdoptedBuildPushTrigger(() => throw new FormatException("private diagnostic"),
            _ => Assert.Fail("Malformed policy cannot authorize a push."), events.Add, attention.Add);
        trigger.OnFinalized(new string('c', 40), "", "");
        Assert.Contains("ADOPTED_BUILD_PUSH_HELD", Assert.Single(events));
        Assert.Contains("reason=policy-unreadable", events[0]);
        Assert.Contains("push-adopted", Assert.Single(attention));
        Assert.DoesNotContain("private diagnostic", attention[0]);
    }

    [Theory]
    [InlineData(true, true, false)]
    [InlineData(true, false, true)]
    [InlineData(true, true, true)]
    [InlineData(false, true, false)]
    [InlineData(false, false, true)]
    [InlineData(false, true, true)]
    public void HoldSinkFailures_DoNotEscapeOrSkipOtherSink(bool policyFails, bool eventFails, bool attentionFails)
    {
        var events = new List<string>();
        var attention = new List<string>();
        var pushes = 0;
        var trigger = new AdoptedBuildPushTrigger(
            () => policyFails ? throw new FormatException("private diagnostic") :
                ConductorAutonomyPolicy.Conservative with { AdoptedBuildPushEnabled = true },
            _ => { pushes++; throw new InvalidOperationException("private diagnostic"); },
            line => { events.Add(line); if (eventFails) throw new IOException("private diagnostic"); },
            body => { attention.Add(body); if (attentionFails) throw new Microsoft.Data.Sqlite.SqliteException("private diagnostic", 1); });

        var error = Record.Exception(() => trigger.OnFinalized(new string('c', 40), "", ""));

        Assert.Null(error);
        Assert.Equal(policyFails ? 0 : 1, pushes);
        Assert.Contains("reason=" + (policyFails ? "policy-unreadable" : "service-failed"), Assert.Single(events));
        Assert.Contains("push-adopted", Assert.Single(attention));
        Assert.DoesNotContain("private diagnostic", string.Join("\n", events.Concat(attention)));
    }
}
