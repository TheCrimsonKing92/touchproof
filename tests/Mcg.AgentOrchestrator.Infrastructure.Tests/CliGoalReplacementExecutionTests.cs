using System.Reflection;
using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: pure helper calls and read-only reflection; no hook mutation or external state.
public sealed class CliGoalReplacementExecutionTests
{
    [Fact]
    public void ObjectiveHash_NormalizesLineEndingsAndTrailingWhitespace()
    {
        var padded = CliGoalReplacementExecution.HashGoalReplacementObjective(
            "  Ship the slice  \r\nSecond line\t\r\n\r\n");
        var normalized = CliGoalReplacementExecution.HashGoalReplacementObjective(
            "Ship the slice\nSecond line");
        var changed = CliGoalReplacementExecution.HashGoalReplacementObjective(
            "Ship the slice\nSecond line.");

        Assert.Equal("17523d73e5062fa4644db8881fb92037a6760d1d6055f7f6442022eb3a440caa", padded);
        Assert.Equal(padded, normalized);
        Assert.Equal("94c6681d57506b3b200391028cbf0df2708141aa3947f6d4df6f1a9265e35970", changed);
        Assert.NotEqual(padded, changed);
    }

    [Fact]
    public void Fingerprint_KeepsItsMaterialDigest()
    {
        var automatic = CliGoalReplacementExecution.ComputeGoalReplacementFingerprint(
            predecessorGoalId: "goal-pred",
            backlogItemId: "item-1",
            dispositionMaterial: "ZeroWorkCorrection",
            reason: " Reason text \r\n",
            objectiveHash: "abc",
            coverage: SourceBacklogCoverage.Full,
            orderedRoles: "Planner,Developer",
            requestedPipeline: "auto",
            requestedAgentOverrides: string.Empty,
            assignedAgents: "unprepared");
        var fiveRole = CliGoalReplacementExecution.ComputeGoalReplacementFingerprint(
            predecessorGoalId: "goal-pred",
            backlogItemId: "item-1",
            dispositionMaterial: "ZeroWorkCorrection",
            reason: " Reason text \r\n",
            objectiveHash: "abc",
            coverage: SourceBacklogCoverage.Full,
            orderedRoles: "Planner,Developer",
            requestedPipeline: "five-role",
            requestedAgentOverrides: string.Empty,
            assignedAgents: "unprepared");

        Assert.Equal("2909b4df0e4c317673b97dfbf9f0f181f58b10f9e44a7e5e6fe07c738ce8e250", automatic);
        Assert.Equal("47cee8c9e704a340885e00c0a6d8bac20d10188927ca8efaa8ecafd28187c2ef", fiveRole);
        Assert.NotEqual(automatic, fiveRole);
    }

    [Fact]
    public void GoalReplacement_HasOneOwner()
    {
        const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic |
            BindingFlags.Static | BindingFlags.Instance | BindingFlags.DeclaredOnly;
        string[] methodNames =
        [
            "ExecuteGoalReplacementOutsideTransaction",
            "ThrowGoalReplacementTransferLeaseTimeout",
            "RenderGoalReplacementOutcomeCode",
            "BuildGoalReplacementFingerprint",
            "ComputeGoalReplacementFingerprint",
            "ComputeLegacyGoalReplacementFingerprint",
            "AcquireGoalReplacementTransferLease",
            "TryReplayCommittedGoalReplacement",
            "ThrowReplayedGoalReplacementFailure",
            "ComputeUnpreparedGoalReplacementFingerprint",
            "ResolveLegacyGoalReplacementReplayRoles",
            "TryRecordGoalReplacementFailure",
            "ResolveObservedClaimAfterReplacementFailure",
            "ResolveObservedClaim",
            "RecordLegacyOwnerAmbiguousFailure",
            "CreateUnavailableGoalReplacementFacts",
            "TryRecordGoalReplacementPreflightFailure",
            "TryGetGoalReplacementFlag",
            "GoalReplacementUnlinkedBacklogItemId",
            "NormalizeGoalReplacementPipelineRequest",
            "BuildGoalReplacementRequestedAgentOverrides",
            "TryParseGoalReplacementDisposition",
            "ExtractGoalReplacementFailureCode",
            "ResolveGoalReplacementPreflightFailureCode",
            "ResolveGoalReplacementPreflightRoles",
            "HashGoalReplacementObjective"
        ];
        string[] hookNames =
        [
            "AfterGoalReplacementLeaseAcquisitionFailed",
            "BeforeGoalReplacementFailureClaimObservation"
        ];
        string[] nestedTypeNames =
        [
            "GoalReplacementTransferLeaseAcquisitionKind",
            "GoalReplacementTransferLeaseAcquisition",
            "GoalReplacementValidationException"
        ];
        var movedNames = methodNames.Concat(hookNames).Concat(nestedTypeNames).ToHashSet(StringComparer.Ordinal);
        var runner = typeof(CliPersistentStateRunner);
        var execution = typeof(CliGoalReplacementExecution);

        Assert.DoesNotContain(runner.GetMembers(flags), member => movedNames.Contains(member.Name));
        Assert.True(execution.IsAbstract && execution.IsSealed);
        var methods = execution.GetMethods(flags);
        foreach (var name in methodNames)
        {
            Assert.Equal(name == "ComputeGoalReplacementFingerprint" ? 2 : 1,
                methods.Count(method => method.Name == name));
        }

        foreach (var name in hookNames)
        {
            var hook = Assert.Single(execution.GetProperties(flags), property => property.Name == name);
            Assert.All(hook.GetAccessors(nonPublic: true), accessor => Assert.True(accessor.IsStatic));
        }

        foreach (var name in nestedTypeNames)
        {
            Assert.Single(execution.GetNestedTypes(flags), type => type.Name == name);
        }

        Assert.True(Assert.Single(methods,
            method => method.Name == "ExecuteGoalReplacementOutsideTransaction").IsAssembly);
        var runnerMethods = runner.GetMethods(flags);
        Assert.Single(runnerMethods, method => method.Name == "ExecuteGoalCreateOutsideTransaction");
        Assert.Single(typeof(CliKeyedGoalCreationSubmission).GetMethods(flags),
            method => method.Name == nameof(CliKeyedGoalCreationSubmission.Submit));
        Assert.True(Assert.Single(runnerMethods, method => method.Name == "ResolveCurrentGoal").IsAssembly);
        Assert.True(Assert.Single(runnerMethods,
            method => method.Name == "DeliverGoalCreationSideEffects").IsAssembly);
    }

    [Fact]
    public void KeyedSubmissionHasNoKernelLoadOrGoalWriterAndWorkspaceApplyOwnsTheCommit()
    {
        const BindingFlags hidden = BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance;
        static IEnumerable<MethodBase> References(MethodBase method)
        {
            var stateMachine = method.GetCustomAttribute<System.Runtime.CompilerServices.AsyncStateMachineAttribute>();
            var moveNext = stateMachine?.StateMachineType.GetMethod("MoveNext",
                BindingFlags.NonPublic | BindingFlags.Instance);
            var references = CliWriterPersistenceBoundaryTests.References(method);
            return moveNext is null ? references : references.Concat(CliWriterPersistenceBoundaryTests.References(moveNext));
        }

        var runner = typeof(CliPersistentStateRunner);
        var entry = runner.GetMethod(nameof(CliPersistentStateRunner.ExecuteCommand))!;
        var submissionType = typeof(CliKeyedGoalCreationSubmission);
        var predicate = submissionType.GetMethod(nameof(CliKeyedGoalCreationSubmission.IsCommand), hidden)!;
        var submission = submissionType.GetMethod(nameof(CliKeyedGoalCreationSubmission.Submit), hidden)!;
        var legacy = runner.GetMethod("ExecuteGoalCreateOutsideTransaction", hidden)!;
        var dispatch = References(entry).ToArray();
        Assert.Contains(predicate, dispatch);
        Assert.Contains(submission, dispatch);
        Assert.Contains(legacy, dispatch);
        var legacyCalls = References(legacy).ToArray();
        var legacyFinalizers = legacyCalls.Where(method => method.DeclaringType?.Assembly == runner.Assembly &&
            method.Name.Contains(legacy.Name, StringComparison.Ordinal));
        Assert.Contains(legacyCalls.Concat(legacyFinalizers.SelectMany(References)),
            method => method.Name == "CommitGoalCreationAsync");
        foreach (var name in new[] { "PrepareStartupAsync", "ExecuteStartupCommand" })
        {
            var method = typeof(CliReadOnlyStartupHydration).GetMethod(name, hidden)!;
            Assert.Contains(predicate, References(method));
        }

        var seen = new HashSet<MethodBase>();
        var pending = new Queue<MethodBase>([submission]);
        var reached = new List<MethodBase>();
        while (pending.TryDequeue(out var method))
        {
            if (!seen.Add(method)) continue;
            foreach (var called in References(method))
            {
                reached.Add(called);
                if (called.DeclaringType?.Assembly == runner.Assembly)
                    pending.Enqueue(called);
            }
        }

        Assert.Contains(reached, method => method.Name == nameof(ITransactionalOrchestratorStateRepository.LoadGoalAsync));
        Assert.DoesNotContain(reached, method => method.Name is "LoadAsync" or "CommitGoalCreationAsync" or
            "ReplaceGoalWithSnapshot" or "TransactGoalCreationWithOutboxAsync" or "MarkCreated" ||
            (method.DeclaringType == typeof(AgentOrchestratorKernel) && method.Name == nameof(AgentOrchestratorKernel.FromSnapshot)) ||
            method == legacy || method.DeclaringType == typeof(GoalCreationOperatorIntentHandler));

        var workspaceDispatch = typeof(OperatorIntentCoordinator).GetMethod(nameof(OperatorIntentCoordinator.ExecuteWorkspacePending),
            hidden, [typeof(AgentOrchestratorKernel), typeof(bool).MakeByRefType()])!;
        var handler = typeof(GoalCreationOperatorIntentHandler);
        var apply = handler.GetMethod("Apply", hidden)!;
        var bind = handler.GetMethod("BuildAndBind", hidden)!;
        Assert.Contains(apply, CliWriterPersistenceBoundaryTests.References(workspaceDispatch));
        Assert.Contains(CliWriterPersistenceBoundaryTests.References(apply), method => method.Name == "TransactGoalCreationWithOutboxAsync");
        Assert.Contains(CliWriterPersistenceBoundaryTests.References(bind), method => method.Name == nameof(AgentOrchestratorKernel.ReplaceGoalWithSnapshot));
        Assert.Contains(CliWriterPersistenceBoundaryTests.References(bind), method => method.Name == nameof(GoalIntakeRequestStore.MarkCreated));
    }
}
