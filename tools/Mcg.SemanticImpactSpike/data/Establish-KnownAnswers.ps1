param([Parameter(Mandatory=$true)][string]$RepositoryRoot)
$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath($RepositoryRoot)
Push-Location $root
try {
    # This is a source-reading aid, independent of the semantic spike. Run BEFORE querying.
    # Its finite text searches, reviewed exclusions and identity conventions are the evidence.
    $revision = @(git rev-parse HEAD HEAD:src HEAD:tests)
    function Source([string]$path) { [IO.File]::ReadAllText((Join-Path $root $path)) }
    function Hits([string]$pattern) {
        @(rg -n -F $pattern src tests -g '*.cs' -g '!**/bin/**' -g '!**/obj/**' -g '!**/.scratch/**' -g '!**/TestResults/**')
    }
    function CallerDeclarations([string]$pattern) {
        $found = foreach($hit in (Hits $pattern)) {
            if($hit -notmatch '^(.*?):(\d+):'){continue}
            $path=$Matches[1]; $line=[int]$Matches[2]
            $lines=[IO.File]::ReadAllLines((Join-Path $root $path))
            $prefix=$lines[0..($line-1)] -join "`n"
            $methods=[regex]::Matches($prefix,'(?m)^\s*(?:(?:public|private|internal|protected|static|async|sealed|override|virtual|new)\s+)+[\w<>,?\[\].]+\s+(?<name>\w+)\s*\(')
            if($methods.Count -eq 0){throw "Cannot source-locate caller at $hit"}
            $m=$methods[$methods.Count-1]
            $declarationLine=1+([regex]::Matches($prefix.Substring(0,$m.Groups['name'].Index),"`n")).Count
            $path.Replace('\','/')+':'+$declarationLine
        }
        @($found | Sort-Object -Unique)
    }
    function Entry($number,$id,$kind,$project,$targets,$expected,$evidence) {
        [ordered]@{ Id=$id; OperatorNumber=$number; Kind=$kind; Project=$project; Targets=@($targets);
            Expected=@($expected); Mode='exact'; Evidence=$evidence; DecisionRequired=($number -in @(3,5,6,7,8,9,10)) }
    }
    $core='Mcg.AgentOrchestrator.Core'; $infra='Mcg.AgentOrchestrator.Infrastructure'; $app='Mcg.AgentOrchestrator.App'
    $queries=[Collections.Generic.List[object]]::new()
    $buildId='M:Mcg.AgentOrchestrator.Core.AgentOrchestratorKernel.BuildTaskBrief(Mcg.AgentOrchestrator.Core.GoalId,Mcg.AgentOrchestrator.Core.TaskId,System.String,System.String,System.String,System.String,System.String,System.Collections.Generic.IReadOnlyList{System.String},System.String,System.Nullable{System.Int32},System.Nullable{System.Boolean},System.Collections.Generic.IReadOnlyList{System.String},System.Nullable{System.Int32},System.Collections.Generic.IReadOnlyList{Mcg.AgentOrchestrator.Core.ReviewFindingLocation},System.String,Mcg.AgentOrchestrator.Core.ReviewRetryCapReceipt,System.Boolean)'
    $q=Entry 1 'kernel-partial-call-sites' 'calls' $core @($buildId) @() 'rg -n -F BuildTaskBrief( src tests (source-only exclusions in this script). More than 200 hits include helpers and source strings. Exact whole-solution call-site ground truth was not established by reading; no ground truth, unscored. Partial declarations inspected at AgentOrchestratorKernel.TaskBriefProjection.cs:5 and TaskBriefs.cs:440.'
    $q.Mode='no ground truth'; $queries.Add($q)

    $impl = @(
        'AcceptanceCohortWorkflowTests.BlockingAcceptanceVerifier','AcceptanceCohortWorkflowTests.SequenceAcceptanceVerifier',
        'AcceptanceCohortWorkflowTests.ManifestThrowingAcceptanceVerifier','AcceptanceCohortWorkflowTestsAttributionStableSlotLease.LeaseObservingVerifier',
        'GateProgressReportingVerifier','AcceptanceCohortWorkflowTestsFocusedAttribution.FocusedVerifier',
        'AcceptanceCohortWorkflowTestsMainSuspectReleaseProbe.ProbeVerifier','CohortLaneAttributionScenario.RecordingVerifier',
        'AttributionOccupancyHeartbeatTests.ProgressVerifier','BackgroundGateStartHarness.ScriptedAcceptanceVerifier',
        'CliCommandTestBase.ProbeAcceptanceVerifier','CliCommandTestsPersistentRunnerCommandsGraphRepair.GatedVerifier',
        'CohortMemberReopenedGateAbandonmentScenarioTests.UncalledVerifier','ConductorDriverTests.FakeAcceptanceVerifier',
        'ConductorDriverTests.ThrowingAcceptanceVerifier','FocusedAttributionActivationScenarioTests.FocusedVerifier',
        'Mcg.AgentOrchestrator.Infrastructure.Tests.GoalLifecycleEventWriterTests.PassingAcceptanceVerifier','GoalWorktreeFastTests.FakeAcceptanceVerifier',
        'GoalWorktreeTestBase.FakeAcceptanceVerifier','GroupedGateAttemptLegacyRestoreTests.UnexpectedVerifier',
        'HostedVerifierQosFixture.Verifier','KeyedRepositoryEvidenceExecutionTests.RecordingVerifier',
        'LifecycleGoalEpicInheritanceTests.FakeAcceptanceVerifier','MergeTrainBisectSlotLossTests.BisectSlotVerifier',
        'MergeTrainDeferralSharedFixture.GateVerifier','MergeTrainGateCancelReadmissionTests.UncalledVerifier',
        'MergeTrainGateOnlySlotsBusyDeferralTests.GateVerifier','PostLandingCanaryTests.FakeAcceptanceVerifier',
        'PullRequestDeliveryAdmissionTests.UnusedVerifier','PullRequestTrunkDriftTests.NeverRunVerifier',
        'RealWorkerProcessGuardTests.PassingAcceptanceVerifier','SelectedExecutionBackstopTrainLandingIntakeTests.PublishingVerifier'
    ) | ForEach-Object {'T:'+$_}
    $impl += 'T:Mcg.AgentOrchestrator.Infrastructure.GoalAcceptanceVerifier'
    $interface='T:Mcg.AgentOrchestrator.Infrastructure.IGoalAcceptanceVerifier'
    $queries.Add((Entry 2 'acceptance-implementers' 'implementers' $infra @($interface) $impl 'rg -n IGoalAcceptanceVerifier src tests; read each class declaration (including multiline primary constructors) and containing class. GoalAcceptanceVerifier.cs:249 is shipping; 32 sealed test implementations. No derived implementation classes found. Identity correction after exploratory run: GoalLifecycleEventWriterTests.cs:9 explicitly declares namespace Mcg.AgentOrchestrator.Infrastructure.Tests; its nested verifier at line 588 is therefore namespace-qualified. Initial exploratory mismatch is retained in initial-run.json.'))

    $signature='.ComputeEffectivePlanIdentity(System.String,System.Collections.Generic.IReadOnlyList{System.String})'
    $dispatch = @(
        "$infra|M:Mcg.AgentOrchestrator.Infrastructure.IGoalAcceptanceVerifier$signature",
        "Mcg.AgentOrchestrator.Infrastructure.Tests|M:AcceptanceCohortWorkflowTests.ManifestThrowingAcceptanceVerifier$signature",
        "Mcg.AgentOrchestrator.Infrastructure.Tests|M:CohortLaneAttributionScenario.RecordingVerifier$signature",
        "Mcg.AgentOrchestrator.Infrastructure.Tests|M:GroupedGateAttemptLegacyRestoreTests.UnexpectedVerifier$signature",
        "Mcg.AgentOrchestrator.Infrastructure.Tests|M:MergeTrainBisectSlotLossTests.BisectSlotVerifier$signature"
    )
    $sites=@('src/Mcg.AgentOrchestrator.App/Orchestration/GroupedGateIdentityInspector.cs:29',
        'src/Mcg.AgentOrchestrator.App/Orchestration/GroupedGateIdentityInspector.cs:70',
        'src/Mcg.AgentOrchestrator.App/Orchestration/ConductorDriver.AcceptanceCohortExecution.cs:93',
        'src/Mcg.AgentOrchestrator.App/Orchestration/ConductorDriver.MergeTrains.cs:140')
    $expected=foreach($site in $sites){foreach($member in $dispatch){$site+' => '+$member}}
    $q=Entry 3 'four-interface-dispatch-sites' 'dispatch' $infra @('M:Mcg.AgentOrchestrator.Infrastructure.IGoalAcceptanceVerifier'+$signature) $expected 'Read the four named sites and receiver declarations as IGoalAcceptanceVerifier. rg -n ComputeEffectivePlanIdentity( src tests: four overrides at AcceptanceCohortWorkflowTests.cs:480, LaneScopedAttribution.cs:165, GroupedGateAttemptLegacyRestoreTests.cs:446, MergeTrainBisectSlotLossTests.cs:329. Other 29 implementations use default body at GoalAcceptanceVerifier.cs:219. Conservative possible implementations, no receiver points-to inference.'
    $q.Sites=$sites; $queries.Add($q)

    $executeId='M:Mcg.AgentOrchestrator.App.Cli.ProjectCliCommand.Execute(System.Collections.Generic.IReadOnlyList{System.String},Mcg.AgentOrchestrator.App.Orchestration.OrchestratorProjectRegistry,System.String,System.String,System.IO.TextWriter,System.Func{Mcg.AgentOrchestrator.Infrastructure.IUnitCommandMeasurer},System.Func{Mcg.AgentOrchestrator.App.Orchestration.DefaultStateMover},System.Func{Mcg.AgentOrchestrator.Infrastructure.IPullRequestRequirementProbe},System.IO.Stream,System.IO.TextWriter,System.String,Mcg.AgentOrchestrator.App.Orchestration.OrchestratorWorkspace,Mcg.AgentOrchestrator.App.Orchestration.WorkspaceInvocationBinding,System.Func{Mcg.AgentOrchestrator.Infrastructure.CrosswalkStackReader})'
    $q=Entry 4 'crosswalk-delegate-seam' 'delegate' $app @($executeId) @() 'rg -n crosswalkReaderFactory: tests/Mcg.AgentOrchestrator.Infrastructure.Tests -g *.cs; ProjectCliCommand.cs:27,67,121 traces the factory to CrosswalkStackReader. Factories in shared test helpers are reused through multiple layers; exact supplier test-class closure not established by independent source reading, no ground truth. Returned classes and reached shipping type remain visible unscored. Input identity correction after exploratory run: OrchestratorWorkspace.cs declares namespace Mcg.AgentOrchestrator.App.Orchestration, not Infrastructure. No reachability expectation was changed.'
    $q.Mode='no ground truth'; $queries.Add($q)
    $render='M:Mcg.AgentOrchestrator.Core.WorkerStandingRules.RenderBriefSection(Mcg.AgentOrchestrator.Core.AgentRole,System.String,System.Nullable{Mcg.AgentOrchestrator.Core.ProjectStack})'
    $q=Entry 5 'qualified-standing-rules-binding' 'binding' $core @($render) @("$core|$render") 'Read AgentOrchestratorKernel.TaskBriefs.cs:440 and WorkerStandingRules.cs:71. Explicit type is WorkerStandingRules; nearby EngineeringPracticeRegistry.RenderBriefSection at line 432 is a different symbol.'
    $q.Sites=@('src/Mcg.AgentOrchestrator.Core/Application/AgentOrchestratorKernel.TaskBriefs.cs:440');$queries.Add($q)
    $select='M:Mcg.AgentOrchestrator.Infrastructure.ProjectDiscoveryAdapterSelector.Select(System.String,Mcg.AgentOrchestrator.Infrastructure.CrosswalkStackReader)'
    $queries.Add((Entry 6 'selector-callers-without-linq' 'callers' $infra @($select) (CallerDeclarations 'ProjectDiscoveryAdapterSelector.Select') 'rg -n -F ProjectDiscoveryAdapterSelector.Select src tests: 43 expressions, 41 enclosing declarations at this revision. Source-reading aid records declaration identifier line, deduplicated. Read selector declaration Onboarding/ProjectDiscoveryAdapterSelector.cs:8. LINQ Select has a different receiver and is excluded.'))
    $bound='M:Mcg.AgentOrchestrator.Core.TimelineMessageBound.Bound(System.String,System.String,System.String)'
    $queries.Add((Entry 7 'timeline-method-callers' 'callers' $core @($bound) (CallerDeclarations 'TimelineMessageBound.Bound') 'rg -n -F TimelineMessageBound.Bound src tests: 14 expressions in 12 enclosing methods across five classes. Each method signature was read; declaration identifier lines are the method identity for this corpus. Nine expressions in TimelineMessageBoundTests, two in ConductorDriverTestsPreTesterDeferredEvidence, one in each other file.'))
    $rules='M:Mcg.AgentOrchestrator.Core.WorkerStandingRules.RulesForRole(Mcg.AgentOrchestrator.Core.AgentRole,System.Nullable{Mcg.AgentOrchestrator.Core.ProjectStack})'
    $q=Entry 8 'standing-rule-regression' 'affected' $core @('F:Mcg.AgentOrchestrator.Core.WorkerStandingRules.DeveloperRules',$rules) @('WorkerDispatchTestsSubscriptionPreflight') 'Required/excluded ruling: hub closure too large to enumerate from source. Read WorkerDispatchTestsSubscriptionPreflight.cs:44 -> WorkerProfileDispatcher.cs:557 PrepareSubscriptionTask -> PrepareTask:224 -> BuildTaskBriefSource -> TaskBriefs.cs:440 RenderBriefSection -> WorkerStandingRules.cs:73 RulesForRole -> DeveloperRules:39. Excluded ProjectDiscoveryAdapterSelectorSourceTests.cs:7 only reads files and calls VerifiedRepositoryRoot.Find; no semantic call to kernel or rules. File-name constants are not query targets.'
    $q.Mode='required/excluded';$q.Excluded=@('ProjectDiscoveryAdapterSelectorSourceTests');$queries.Add($q)
    $timelineClasses=@('TimelineMessageBoundTests','TimelineMessageBoundPersistenceTests','ConductorDriverTestsPreTesterDeferredEvidence','PreTesterLegacyMessageFallbackTests','PreTesterTypedEntryRoundTripTests','PreTesterTypedEntryNegativeControlTests')
    $q=Entry 9 'timeline-file-affected' 'affected' $core @() $timelineClasses 'rg -n TimelineMessageBound src tests and read every hit. Five classes call Bound (query 7); TimelineMessageBoundPersistenceTests.cs:21 reads CharacterLimit only. No shipping caller. All references occur in tests, hence exact six-class closure; empty answer would be wrong.'
    $q.File='src/Mcg.AgentOrchestrator.Core/Persistence/TimelineMessageBound.cs';$queries.Add($q)
    $q=Entry 10 'pretester-file-affected' 'affected' $app @() @('ConductorBatchLoopTestsWalkStepLedger','ConductorDriverTestsPreTesterAlwaysRunGuards','PreTesterCoupledClassSelectionsTests','PreTesterCliSurfaceWiringTests','PreTesterCliSurfaceSelectionTests','PreTesterGuardHelperAndScanTests','PreTesterReverseDependencySelectionTests','PreTesterStartedSelectionReconciliationTests') 'Required/excluded ruling: exact transitive closure too large to enumerate by reading. Direct references read in the eight required test classes (lines 154,420,67,97,136,215,85,24 respectively); production hub is ConductorDriver.PreTesterDeferredEvidence.cs:129. RoleRequirementStackGuidanceTests is excluded: whole source read, pure SdlcRolePromptRequirements/Core kernel calls, Core-only project reference and no App subprocess. Unconstrained returned classes are listed separately, never mislabeled proven false positives.'
    $q.File='src/Mcg.AgentOrchestrator.App/Orchestration/PreTesterGuardSelections.cs';$q.Mode='required/excluded';$q.Excluded=@('RoleRequirementStackGuidanceTests');$queries.Add($q)
    $q=Entry 11 'inherited-repository-tests' 'tests' 'Mcg.AgentOrchestrator.Infrastructure.Tests' @('T:WorkerDispatchTestsSubscriptionPreflight') @() 'Search abstract class and Fact/Theory in Core/Infrastructure test bases, including WorkerDispatchTestSupport and AcceptanceCohortWorkflowTests: matched facts occur in source strings or separate concrete classes. No repository class with inherited test methods established; no ground truth, unscored. Fixture has an abstract fact base and concrete derived test class.'
    $q.Mode='no ground truth';$queries.Add($q)
    $ctor='M:Mcg.AgentOrchestrator.App.Application.AgentDefinitionInput.#ctor(System.String,System.String,System.String,System.String,System.String,System.Nullable{System.Int32},System.String,System.String,System.String,System.String,System.String,System.String,System.Nullable{System.Int32},System.String)'
    $creationSites=foreach($hit in (Hits 'new AgentDefinitionInput(')){if($hit -match '^(.*?):(\d+):'){$Matches[1].Replace('\','/')+':'+$Matches[2]}}
    $queries.Add((Entry 12 'record-construction-sites' 'construction' $app @($ctor) @($creationSites | Sort-Object -Unique) 'rg -n AgentDefinitionInput src tests and read all matches: one record declaration, factory parameter, and eleven explicit new expressions. No target-typed constructions or alias uses. Shipping CliCommandHandlers.Goals.Agents.cs:56; tests ApplicationLayerHelperEquivalenceTests.cs:68, ModelReleaseDefaultsTests.cs:10,11,15,16,36,37, WorkerDispatchTestsModelSelection.cs:1405,1436,1470.'))
    $data=[ordered]@{ SchemaVersion=1;BaselineRevision=$revision[0];SourceTree=$revision[1];TestsTree=$revision[2];Queries=$queries.ToArray() }
    $path=Join-Path $root 'tools/Mcg.SemanticImpactSpike/data/known-answer-queries.json'
    [IO.File]::WriteAllText($path,($data | ConvertTo-Json -Depth 12)+[Environment]::NewLine)
    "source expectations written: $path (12 queries; source-only procedure)"
} finally { Pop-Location }
