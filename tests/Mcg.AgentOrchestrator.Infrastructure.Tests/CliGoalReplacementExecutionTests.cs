using System.Reflection;
using System.Runtime.CompilerServices;
using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

// Parallel-safe: pure helper calls, source reads, and reflection.
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
        static IEnumerable<MethodBase> References(MethodBase method)
        {
            var references = CliWriterPersistenceBoundaryTests.References(method);
            var stateMachine = method.GetCustomAttribute<AsyncStateMachineAttribute>();
            if (stateMachine is null) return references;

            var map = stateMachine.StateMachineType.GetInterfaceMap(typeof(IAsyncStateMachine));
            var moveNextIndex = Array.FindIndex(map.InterfaceMethods,
                interfaceMethod => interfaceMethod.Name == nameof(IAsyncStateMachine.MoveNext));
            return references.Concat(CliWriterPersistenceBoundaryTests.References(map.TargetMethods[moveNextIndex]));
        }

        var runner = typeof(CliPersistentStateRunner);
        var entry = CliPersistentStateRunner.ExecuteCommand;
        var predicate = CliKeyedGoalCreationSubmission.IsCommand;
        var submission = CliKeyedGoalCreationSubmission.Submit;
        AssertKeyedRunnerDispatch();
        Assert.True(predicate(["goal", "Objective", "--request-key", "key"]));
        Assert.True(predicate(["simple-goal", "Objective", "--request-key", "key"]));
        var dispatch = References(entry.Method).ToArray();
        var validation = Assert.Single(dispatch, method => method.DeclaringType == typeof(CliCommandHelp) &&
            method.Name == nameof(CliCommandHelp.ValidateGoalCreationOptions));
        Assert.Contains(predicate.Method, dispatch);
        Assert.Contains(submission.Method, dispatch);
        var legacy = Assert.Single(dispatch, method =>
            method.DeclaringType == runner && method.Name == "ExecuteGoalCreateOutsideTransaction");
        var legacyCalls = References(legacy).ToArray();
        var legacyFinalizers = legacyCalls.Where(method => method.DeclaringType?.Assembly == runner.Assembly &&
            method.Name.Contains(legacy.Name, StringComparison.Ordinal));
        Assert.Contains(legacyCalls.Concat(legacyFinalizers.SelectMany(References)),
            method => method.Name == "CommitGoalCreationAsync");
        var prepare = CliReadOnlyStartupHydration.PrepareStartupAsync;
        var execute = CliReadOnlyStartupHydration.ExecuteStartupCommand;
        Assert.Contains(predicate.Method, References(prepare.Method));
        Assert.Contains(predicate.Method, References(execute.Method));

        var seen = new HashSet<MethodBase>();
        var pending = new Queue<MethodBase>([validation, predicate.Method, submission.Method]);
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

        var workspaceApply = (OperatorIntentCoordinator coordinator, AgentOrchestratorKernel kernel) =>
            coordinator.ExecuteWorkspacePending(kernel, out _);
        var workspaceDispatch = Assert.Single(References(workspaceApply.Method), method =>
            method.DeclaringType == typeof(OperatorIntentCoordinator) &&
            method.Name == nameof(OperatorIntentCoordinator.ExecuteWorkspacePending));
        var handler = typeof(GoalCreationOperatorIntentHandler);
        var apply = Assert.Single(References(workspaceDispatch), method =>
            method.DeclaringType == handler && method.Name == nameof(GoalCreationOperatorIntentHandler.Apply));
        var applyCalls = References(apply).ToArray();
        Assert.Contains(applyCalls, method => method.Name == "TransactGoalCreationWithOutboxAsync");
        var bind = Assert.Single(applyCalls.Where(method => method.DeclaringType?.DeclaringType == handler)
            .SelectMany(References), method => method.DeclaringType == handler && method.Name == "BuildAndBind");
        var bindCalls = References(bind).ToArray();
        Assert.Contains(bindCalls, method => method.Name == nameof(AgentOrchestratorKernel.ReplaceGoalWithSnapshot));
        Assert.Contains(bindCalls, method => method.Name == nameof(GoalIntakeRequestStore.MarkCreated));
    }

    private static void AssertKeyedRunnerDispatch([CallerFilePath] string testSourcePath = "")
    {
        // Inspect the worktree used to compile this test. The harness root can point to the publishing checkout.
        var path = Path.Combine(VerifiedRepositoryRoot.Resolve(testSourcePath, static _ => null),
            "src/Mcg.AgentOrchestrator.App/Cli/CliPersistentStateRunner.cs");
        var root = CSharpSyntaxTree.ParseText(File.ReadAllText(path)).GetRoot();
        var runner = Assert.Single(root.DescendantNodes().OfType<ClassDeclarationSyntax>(),
            type => type.Identifier.ValueText == nameof(CliPersistentStateRunner));
        var entry = Assert.Single(runner.Members.OfType<MethodDeclarationSyntax>(),
            method => method.Identifier.ValueText == nameof(CliPersistentStateRunner.ExecuteCommand));
        var body = Assert.IsType<BlockSyntax>(entry.Body);

        // The keyed route must return before the shared runner can reach any other dispatch or writer.
        var allowed = (BlockSyntax)SyntaxFactory.ParseStatement("""
            {
                CliCommandHelp.ValidateGoalCreationOptions(args);
                if (CliKeyedGoalCreationSubmission.IsCommand(args))
                    return CliKeyedGoalCreationSubmission.Submit(args, stateRepository, workspace, agents, ref currentGoal,
                        operatorIntentSubmissionSource, allowTerminalSourceClaimTakeover);
            }
            """);
        bool MatchesKeyedPrefix(BlockSyntax candidate) =>
            allowed.IsEquivalentTo(SyntaxFactory.Block(candidate.Statements.Take(allowed.Statements.Count)));

        Assert.True(MatchesKeyedPrefix(body),
            $"{path}: ExecuteCommand must validate options and immediately return the keyed submission before other dispatch.");

        var fullLoad = SyntaxFactory.ParseStatement("stateRepository.LoadAsync().GetAwaiter().GetResult();");
        Assert.False(MatchesKeyedPrefix(body.WithStatements(body.Statements.Insert(1, fullLoad))));
        var keyedBranch = Assert.IsType<IfStatementSyntax>(body.Statements[1]);
        var branchWithLoad = keyedBranch.WithStatement(SyntaxFactory.Block(fullLoad, keyedBranch.Statement));
        Assert.False(MatchesKeyedPrefix(body.WithStatements(body.Statements.Replace(keyedBranch, branchWithLoad))));
    }
}
