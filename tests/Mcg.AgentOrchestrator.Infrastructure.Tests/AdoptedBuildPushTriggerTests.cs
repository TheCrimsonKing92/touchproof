using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core.Conductor;

// Parallel-safe: private policy state, registries and sinks; no ambient setting is changed.
public sealed class AdoptedBuildPushTriggerTests
{
    [Theory]
    [InlineData(true, "", "", "", 1)]
    [InlineData(false, "", "", "", 0)]
    [InlineData(false, "", "failed-project", "", 0)]
    [InlineData(true, "pending-project", "", "", 1)]
    [InlineData(true, "", "", "stopped-project", 1)]
    [InlineData(true, "pending-project", "", "stopped-project", 1)]
    [InlineData(true, "", "failed-project", "", 0)]
    [InlineData(true, "pending-project", "failed-project", "stopped-project", 0)]
    public void FinalizedFleet_PushesWhenEnabledWithoutReportedFailure(
        bool enabled, string pending, string failed, string stopped, int count)
    {
        var pushed = new List<string>();
        var events = new List<string>();
        var attention = new List<string>();
        var commit = new string('c', 40);
        var trigger = new AdoptedBuildPushTrigger(
            () => ConductorAutonomyPolicy.Conservative with { AdoptedBuildPushEnabled = enabled },
            (value, sink) => { pushed.Add(value); sink($"ADOPTED_BUILD_PUSHED commit={value}"); }, events.Add, attention.Add);
        trigger.OnFinalized(commit, pending, failed, stopped);
        Assert.Equal(count, pushed.Count);
        if (enabled && failed.Length != 0)
        {
            Assert.Equal($"ADOPTED_BUILD_PUSH_HELD commit={commit} reason=fleet-adoption-failed", Assert.Single(events));
            Assert.Contains("fleet-adoption-failed", Assert.Single(attention));
        }
        else
        {
            Assert.Empty(attention);
            if (count == 0) Assert.Empty(events);
            else
            {
                Assert.Equal(commit, Assert.Single(pushed));
                Assert.Equal($"ADOPTED_BUILD_PUSHED commit={commit} stopped={stopped} pending={pending}", Assert.Single(events));
            }
        }
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
