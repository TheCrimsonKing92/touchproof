param([Parameter(Mandatory=$true)][string]$RepositoryRoot)
$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath($RepositoryRoot)
$dataRoot = Join-Path $root 'tools/Mcg.SemanticImpactSpike/data'
$cold = Get-Content (Join-Path $dataRoot 'cold-run.json') -Raw | ConvertFrom-Json
$warm = Get-Content (Join-Path $dataRoot 'verify-receipt.json') -Raw | ConvertFrom-Json
$data = Get-Content (Join-Path $dataRoot 'known-answer-queries.json') -Raw | ConvertFrom-Json
if($cold.DataHash -ne $warm.DataHash){throw 'Cold/warm corpus hashes differ.'}
function F($number) { ([double]$number).ToString('F3',[Globalization.CultureInfo]::InvariantCulture) }
$scored = @($warm.Results | Where-Object Score -ne 'unscored')
$correct = @($scored | Where-Object Score -eq 'correct').Count
$unscored = @($warm.Results | Where-Object Score -eq 'unscored').Count
$required = @($data.Queries | Where-Object DecisionRequired)
$requiredPassed = $true
foreach($q in $required) {
    if(@($cold.Results | Where-Object {$_.Id -eq $q.Id -and $_.Score -eq 'correct'}).Count -ne 1 -or
       @($warm.Results | Where-Object {$_.Id -eq $q.Id -and $_.Score -eq 'correct'}).Count -ne 1){$requiredPassed=$false}
}
$peak = [math]::Max($cold.PeakWorkingSetBytes,$warm.PeakWorkingSetBytes)
$verdict = if($requiredPassed -and $cold.Status -eq 'passed' -and $warm.Status -eq 'passed' -and
    $cold.TotalSeconds -le 45 -and $warm.TotalSeconds -le 45 -and $peak -le 4GB){'proceed'}else{'stop'}
$rows = foreach($q in $data.Queries) {
    $c = $cold.Results | Where-Object Id -eq $q.Id
    $w = $warm.Results | Where-Object Id -eq $q.Id
    "| $($q.OperatorNumber) | $($q.Id) | $($w.Score) | $($w.Returned.Count) | $($w.Missing.Count) / $($w.Extra.Count) | $(F (1000*$c.Seconds)) / $(F (1000*$w.Seconds)) |"
}
$variant = foreach($r in $warm.FrameworkDispatchVariant) {
    "| $($r.Id) | $($r.Returned.Count) | $(F (1000*$r.Seconds)) |"
}
$fixtures = foreach($r in $warm.FixtureChecks) { ("- $($r.Name): $($r.Passed); returned $($r.Returned -join ', ')").TrimEnd() }
$diagnostics = @($cold.Diagnostics)+@($warm.Diagnostics)
$diagnosticText = if($diagnostics.Count -eq 0){"None in either final run. Cold/warm project compilations: $($cold.Projects.Count)/$($warm.Projects.Count), without compiler errors."}else{$diagnostics -join "`n"}
$costReason = if($cold.TotalSeconds -gt 120 -and $warm.TotalSeconds -gt 120){'Both exceed the 120 s stop limit.'}elseif($cold.TotalSeconds -gt 45 -or $warm.TotalSeconds -gt 45){'At least one run exceeds the 45 s small-cost limit.'}else{'Both meet the 45 s small-cost limit.'}
$requiredText = if($requiredPassed){'All decision queries are correct in both runs.'}else{'At least one decision query is missing or incorrect; see the per-query receipts.'}
# Result records use IDs; resolve operator numbers through the corpus.
function ResultFor($number) { $q = $data.Queries | Where-Object OperatorNumber -eq $number; $warm.Results | Where-Object Id -eq $q.Id }
$q1=ResultFor 1; $q4=ResultFor 4; $q6=ResultFor 6; $q7=ResultFor 7; $q8=ResultFor 8; $q9=ResultFor 9; $q10=ResultFor 10
$settings = @($warm.Projects | ForEach-Object { "$($_.Framework), $($_.LanguageVersion), nullable $($_.Nullable), $($_.Optimization)" } | Sort-Object -Unique) -join '; '
$sdk = dotnet --version
if($LASTEXITCODE -ne 0){throw 'Could not record the report-generation SDK version.'}
$date = ([DateTimeOffset]$warm.TimestampUtc).UtcDateTime.ToString('yyyy-MM-dd')
$report = @"
# Semantic test-impact spike — $date

Verdict: **$verdict**. Cold load, compilation, index and all $($data.Queries.Count) queries cost **$(F $cold.TotalSeconds) s**; warm cost **$(F $warm.TotalSeconds) s**, against the operator-supplied **1,120 s gate**. $costReason The final corpus has **$correct/$($scored.Count) scored answers correct**, with $unscored queries explicitly unscored. $requiredText Peak spike working set is **$peak bytes** ($(F ($peak/1e9)) GB).

## Evidence and method

Baseline revision: $($data.BaselineRevision). Source tree: $($data.SourceTree). Tests tree: $($data.TestsTree). Normalized corpus SHA-256: $($warm.DataHash).

The standalone project, fixtures, tests, data and receipts live in tools/Mcg.SemanticImpactSpike. There is no shipped reference or selection integration. The solution was opened through Roslyn MSBuildWorkspace in Debug, without a solution filter. The solution project-reference closure produced $($cold.Projects.Count) cold and $($warm.Projects.Count) warm compilations. Every resolved project setting, target framework and define is recorded in both receipts. Distinct warm settings: $settings. Directory.Build.props and targets apply. Directory.Build.rsp is a CLI response file and does not apply to this workspace API.

Host: $($warm.ProcessorCount) logical processors, $($warm.TotalAvailableMemoryBytes) bytes available memory reported by the GC; runtime $($warm.Runtime); OS $($warm.OS). Report-generation SDK: $sdk (same host and SDK selection as analysis); CPU model was not captured by these receipts. Measurements were sequential, with no simultaneous worker build. Other machine activity was not controlled.

Cold means a fresh spike process and a newly restored design-time output directory with no earlier workspace evaluation. Package and operating-system file caches were already warm from builds, restore and an exploratory run; no reboot or OS cache eviction was attempted. Warm is the following fresh-process verification using the same design-time outputs and corpus. It performs the small fixture verification before the measured repository interval; that can warm JIT/compiler code, and is disclosed here. Neither process reuses a semantic index.

Commands and properties are stored in cold-run.json and verify-receipt.json. Repository paths use the ``<repo>`` marker and owner-home paths use ``<user-home>``, including nested diagnostics and error text. These markers preserve suffixes and do not resolve as execution paths; substitute the local roots to replay. Both runs use UseArtifactsOutput=$($warm.Properties.UseArtifactsOutput), ``ArtifactsPath=$($warm.Properties.ArtifactsPath)``, and an explicitly supplied package root. Repository and fixture assets were restored there first. NuGetAudit=$($warm.Properties.NuGetAudit) applies to these analysis restore/workspace invocations; this is no vulnerability-audit evidence. The worker build retains its normal policy.

Receipts beside the corpus:

- [cold-run.json](../../tools/Mcg.SemanticImpactSpike/data/cold-run.json): final cold measurement, exit 0.
- [verify-receipt.json](../../tools/Mcg.SemanticImpactSpike/data/verify-receipt.json): warm measurement plus shared fixture assertions, exit 0.
- [initial-run.json](../../tools/Mcg.SemanticImpactSpike/data/initial-run.json): exploratory result retained, including the original identity mistakes. Its path strings were deterministically redacted with the console's redact command; measurements, timestamp, corpus hash and results were preserved. This is historical evidence, not a new run.

The first corpus preceded semantic execution. Independent re-reading corrected two namespace spellings after the exploratory run: GoalLifecycleEventWriterTests.cs:9 supplies the namespace on its nested PassingAcceptanceVerifier, and OrchestratorWorkspace.cs:1 places that parameter type in App.Orchestration. Initial query 2 had one missing and one extra identity; query 4 had an unresolved input. Those results remain in initial-run.json. All other expected sets were retained. Establish-KnownAnswers.ps1 records the source searches, declaration locations, required/excluded rationale and those corrections. No reachability answer was substituted from tool output.

This revision refresh followed a baseline-drift exit after main was merged into the goal. Before rerunning semantic analysis, the Developer inspected the thirteen changed src/tests paths and reran Establish-KnownAnswers.ps1 at 79361c3d29b3b070acf74fe012bcecf995001ce4. Comparing each complete query contract before/after showed zero differences; only BaselineRevision, SourceTree and TestsTree changed. The historical initial receipt retains its earlier baseline and corpus hash.

## Correctness

Exact comparisons use deduplicated identity sets. Queries 8 and 10 use the operator-adopted required/excluded contract. Their unconstrained returned identities are fully listed in the receipts and have no established false-positive verdict. For unscored rows, the difference column is n/a.

| Query | ID | Final score | Returned identities | Missing / extra violations | Cold / warm query ms |
| --- | --- | --- | ---: | ---: | ---: |
$($rows -join "`n")

Queries 1, 4 and 11 have no independently established exact ground truth. Query 1 returns $($q1.Returned.Count) call-site lines across kernel partial callers. Query 4 returns $($q4.Returned.Count) identities (supplier test classes and the shipping CrosswalkStackReader type); helper reuse prevented establishing its exact supplier closure by reading alone. Query 11 returns the picked class's declared test methods, but no repository example with inherited test methods was established. Those rows are excluded from the score; the fixture proves the inherited-fact shape.

The standing-rule query targets DeveloperRules and RulesForRole, not the file-name constants. The source-grounded path is WorkerDispatchTestsSubscriptionPreflight.cs:44 → WorkerProfileDispatcher.PrepareSubscriptionTask:557 → PrepareTask:224 → kernel BuildTaskBriefSource → TaskBriefs.cs:440 RenderBriefSection → WorkerStandingRules.cs:73 RulesForRole → DeveloperRules. Standing-rule score: $($q8.Score), $($q8.Returned.Count) of $($warm.TestClassCount) discovered classes. PreTesterGuardSelections score: $($q10.Score), $($q10.Returned.Count) returned classes. These hub queries constrain required/excluded identities only. TimelineMessageBound file score: $($q9.Score), $($q9.Returned.Count) returned identities against its exact expected set. Counts describe coverage; test-time savings were not measured.

The four interface call sites are compared against source-grounded site/body identities, including the default interface body; per-query differences are in the receipts. The selector query has $($q6.Returned.Count) containing methods and excludes LINQ Select by bound symbol. TimelineMessageBound.Bound has $($q7.Returned.Count) containing callers. Callers are identified by declaration identifier source line; call/construction sites use expression line. Multiple expressions on the same line are collapsed by that identity form, a limitation particularly relevant to unscored query 1.

Shared fixture checks (console results; xUnit hosts were not executed by the Developer):

$($fixtures -join "`n")

The committed interface/static tests directly assert the expected fixture class and empty unrelated result. Both tests and verify use SpikeChecks and the same semantic index. Test discovery follows the xUnit Fact symbol (including Theory and attribute subclasses), merges partial types, and includes public inherited facts for concrete derived classes; abstract bases and helpers without tests are not returned. Full xUnit runner parity beyond those modeled cases remains unmeasured.

## Uncached cost

| Phase | Cold seconds | Warm seconds |
| --- | ---: | ---: |
| Workspace opening | $(F $cold.OpenSeconds) | $(F $warm.OpenSeconds) |
| Compilations and forced compiler diagnostics | $(F $cold.CompilationSeconds) | $(F $warm.CompilationSeconds) |
| Semantic reverse-index construction | $(F $cold.BuildSeconds) | $(F $warm.BuildSeconds) |
| Twelve queries | $(F $cold.QuerySeconds) | $(F $warm.QuerySeconds) |
| Full measured interval, including accounting overhead | $(F $cold.TotalSeconds) | $(F $warm.TotalSeconds) |
| Load/index plus only gate-shaped queries 8–10 | $(F $cold.GateSubsetSeconds) | $(F $warm.GateSubsetSeconds) |
| Peak spike working set, bytes | $($cold.PeakWorkingSetBytes) | $($warm.PeakWorkingSetBytes) |

Uncached analysis creates $($warm.Projects.Count) compilations, forces binding diagnostics, walks every syntax tree for declarations, bound identifier/member references and invocation/construction sites, then builds interface/override relationships and the test catalog. The final graph has $($warm.NodeCount) nodes and $($warm.EdgeCount) reverse edges. The single-threaded index walk follows GetDiagnostics body binding, so this implementation's uncached cost is an upper bound rather than an optimized Roslyn limit. Cold/warm opening plus compilation alone cost $(F ($cold.OpenSeconds+$cold.CompilationSeconds)) / $(F ($warm.OpenSeconds+$warm.CompilationSeconds)) s. Individual binding, identity-generation and allocation costs were not profiled separately. A smaller reference walk is an unmeasured optimization.

The peak is the spike process's peak working set over its entire lifetime, including fixture verification and the framework-dispatch variant where applicable. The MSBuild child peak was not captured; the 4 GB decision check is applied to the observed spike process only, as the plan permits when child memory is unavailable. It is not a measured aggregate process-tree peak.

Workspace/compilation diagnostics in final measurements:

$diagnosticText

Framework-declared dispatch is excluded from scored traversal; source-declared interface and override dispatch is included. The separate warm variant enables the stored framework edges and measures these hub queries:

| Query | Returned classes | Variant query ms |
| --- | ---: | ---: |
$($variant -join "`n")

Variant and scored identities are listed in the receipts. Traversal is transitive with cycle detection and requires each returned test assembly's project-reference closure to contain all target assemblies; linked files in several projects can therefore miss tests reaching only one copy. It does not traverse nameof or documentation references. Reflection, dynamic calls, runtime-built delegates, process-spawning-only reachability, indexer access, user-defined operators/conversions and implicit foreach/using/await pattern calls are unsupported or unmeasured. Dependencies reached only through xUnit constructors, disposal, IAsyncLifetime or IClassFixture setup are not linked to the test class. The baseline check pins committed src/tests trees, not dirty working-tree contents. These are limitations for any future integration. Statically identifiable method groups and lambda bodies contribute dependency edges, without receiver points-to or delegate-value-flow precision.

## Reuse finding and decision

No persistent index cache was implemented or benchmarked. Changed-file keys alone cannot safely reuse this index across commits: an edit can change overload/interface binding in an unchanged file. A valid reuse design needs compilation-input fingerprints, including source contents, project/reference graphs, metadata identities/content, framework/SDK/compiler version, language/nullable/define options, imported props/targets, generators and additional files. Dependent compilations must be invalidated and references rebound. The storage size, reload cost and correct invalidation cost are unmeasured. Reusing only query answers cannot remove the expensive compilation and index construction. Warm filesystem/design-time outputs alone did not meet the limit.

The operator supplies a 1,120-second median passing gate (23 runs) and 41–43 seconds of existing selection planning. Small means both observed cold and warm totals at most 45 seconds; 45–120 seconds is conditional and yields stop as measured, with the reuse requirement stated; above 120 seconds or above the memory limit is stop. Proceed additionally requires queries 3, 5, 6, 7, 8, 9 and 10 correct and both runs' checks passing. This report's totals are $(F $cold.TotalSeconds) and $(F $warm.TotalSeconds) seconds; required-query pass=$requiredPassed, cold status=$($cold.Status), warm status=$($warm.Status), peak=$peak bytes. Applying those conditions yields **$verdict**. Cold analysis consumes $(F (100*$cold.TotalSeconds/1120))% of the supplied gate duration. No cache feasibility or future speedup is assumed in that verdict.

Any later integration must first provide additive evidence beside current selection in ImpactReplay before narrowing anything. The Developer authored four SemanticImpactSpike test classes (including receipt-path redaction) and the separate SpikeFixture analysis inputs, built the changed projects with the worker adapter, and ran console verification. The operator owns one direct managed test-project run after landing, recording pass, fail or baseline-drift skip; that check does not hold landing. No acceptance manifest or existing project was edited.
"@
# Unscored differences are undefined rather than false positives against an empty expected set.
foreach($q in $data.Queries | Where-Object Mode -eq 'no ground truth') {
    $report = $report.Replace("| $($q.Id) | unscored | " + ($warm.Results | Where-Object Id -eq $q.Id).Returned.Count + " | 0 / " + ($warm.Results | Where-Object Id -eq $q.Id).Returned.Count + " |",
        "| $($q.Id) | unscored | " + ($warm.Results | Where-Object Id -eq $q.Id).Returned.Count + " | n/a |")
}
$path = Join-Path $root "docs/triage/semantic-test-impact-spike-$date.md"
[IO.File]::WriteAllText($path,$report+[Environment]::NewLine)
"report=$path verdict=$verdict scored=$correct/$($scored.Count)"
