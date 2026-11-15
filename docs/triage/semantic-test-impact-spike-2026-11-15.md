# Semantic test-impact spike — 2026-11-15

Verdict: **stop**. Cold load, compilation, index and all 12 queries cost **186.763 s**; warm cost **373.506 s**, against the operator-supplied **1,120 s gate**. Both exceed the 120 s stop limit. The final corpus has **9/9 scored answers correct**, with 3 queries explicitly unscored. All decision queries are correct in both runs. Peak spike working set is **2769477632 bytes** (2.769 GB).

## Evidence and method

Baseline revision: 79361c3d29b3b070acf74fe012bcecf995001ce4. Source tree: edf5da743483d0539507eff2d7ddfa2aa9d38bf5. Tests tree: 1c533df5fb971d4e831c67c92a316296e98dae45. Normalized corpus SHA-256: A982A8168DCEE97ECE8909BFFEF64BB9BCD64CA8690C43F1D7931650C04682C3.

The standalone project, fixtures, tests, data and receipts live in tools/Mcg.SemanticImpactSpike. There is no shipped reference or selection integration. The solution was opened through Roslyn MSBuildWorkspace in Debug, without a solution filter. The solution project-reference closure produced 20 cold and 20 warm compilations. Every resolved project setting, target framework and define is recorded in both receipts. Distinct warm settings: .NETCoreApp,Version=v10.0, CSharp14, nullable Enable, Debug. Directory.Build.props and targets apply. Directory.Build.rsp is a CLI response file and does not apply to this workspace API.

Host: 24 logical processors, 51385864192 bytes available memory reported by the GC; runtime .NET 10.0.12; OS Microsoft Windows 10.0.26200. Report-generation SDK: 10.0.103 (same host and SDK selection as analysis); CPU model was not captured by these receipts. Measurements were sequential, with no simultaneous worker build. Other machine activity was not controlled.

Cold means a fresh spike process and a newly restored design-time output directory with no earlier workspace evaluation. Package and operating-system file caches were already warm from builds, restore and an exploratory run; no reboot or OS cache eviction was attempted. Warm is the following fresh-process verification using the same design-time outputs and corpus. It performs the small fixture verification before the measured repository interval; that can warm JIT/compiler code, and is disclosed here. Neither process reuses a semantic index.

Commands and properties are stored in cold-run.json and verify-receipt.json. Repository paths use the `<repo>` marker and owner-home paths use `<user-home>`, including nested diagnostics and error text. These markers preserve suffixes and do not resolve as execution paths; substitute the local roots to replay. Both runs use UseArtifactsOutput=true, `ArtifactsPath=<user-home>\AppData\Local\..\LocalLow\mcg-dotnet-isolated\goals\bf649943\path-refresh`, and an explicitly supplied package root. Repository and fixture assets were restored there first. NuGetAudit=false applies to these analysis restore/workspace invocations; this is no vulnerability-audit evidence. The worker build retains its normal policy.

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
| 1 | kernel-partial-call-sites | unscored | 224 | n/a | 62.422 / 85.235 |
| 2 | acceptance-implementers | correct | 33 | 0 / 0 | 0.551 / 0.618 |
| 3 | four-interface-dispatch-sites | correct | 20 | 0 / 0 | 174.174 / 217.568 |
| 4 | crosswalk-delegate-seam | unscored | 19 | n/a | 11.081 / 13.948 |
| 5 | qualified-standing-rules-binding | correct | 1 | 0 / 0 | 126.497 / 150.383 |
| 6 | selector-callers-without-linq | correct | 41 | 0 / 0 | 174.642 / 225.064 |
| 7 | timeline-method-callers | correct | 12 | 0 / 0 | 112.254 / 169.974 |
| 8 | standing-rule-regression | correct | 718 | 0 / 0 | 8.310 / 8.681 |
| 9 | timeline-file-affected | correct | 6 | 0 / 0 | 0.105 / 0.100 |
| 10 | pretester-file-affected | correct | 734 | 0 / 0 | 9.891 / 8.966 |
| 11 | inherited-repository-tests | unscored | 48 | n/a | 0.612 / 0.420 |
| 12 | record-construction-sites | correct | 11 | 0 / 0 | 88.451 / 110.913 |

Queries 1, 4 and 11 have no independently established exact ground truth. Query 1 returns 224 call-site lines across kernel partial callers. Query 4 returns 19 identities (supplier test classes and the shipping CrosswalkStackReader type); helper reuse prevented establishing its exact supplier closure by reading alone. Query 11 returns the picked class's declared test methods, but no repository example with inherited test methods was established. Those rows are excluded from the score; the fixture proves the inherited-fact shape.

The standing-rule query targets DeveloperRules and RulesForRole, not the file-name constants. The source-grounded path is WorkerDispatchTestsSubscriptionPreflight.cs:44 → WorkerProfileDispatcher.PrepareSubscriptionTask:557 → PrepareTask:224 → kernel BuildTaskBriefSource → TaskBriefs.cs:440 RenderBriefSection → WorkerStandingRules.cs:73 RulesForRole → DeveloperRules. Standing-rule score: correct, 718 of 4135 discovered classes. PreTesterGuardSelections score: correct, 734 returned classes. These hub queries constrain required/excluded identities only. TimelineMessageBound file score: correct, 6 returned identities against its exact expected set. Counts describe coverage; test-time savings were not measured.

The four interface call sites are compared against source-grounded site/body identities, including the default interface body; per-query differences are in the receipts. The selector query has 41 containing methods and excludes LINQ Select by bound symbol. TimelineMessageBound.Bound has 12 containing callers. Callers are identified by declaration identifier source line; call/construction sites use expression line. Multiple expressions on the same line are collapsed by that identity form, a limitation particularly relevant to unscored query 1.

Shared fixture checks (console results; xUnit hosts were not executed by the Developer):

- interface-first: True; returned SpikeFixtureInterfaceTests
- interface-second-explicit: True; returned SpikeFixtureInterfaceTests
- interface-unrelated: True; returned
- static-bound: True; returned SpikeFixtureStaticTests
- static-unrelated: True; returned
- delegate-method-group: True; returned SpikeFixtureDelegateTests
- partial-member: True; returned SpikeFixturePartialTests
- inherited-fact: True; returned SpikeFixtureInheritedTests
- receipt-path-redaction: True; returned <repo>, <repo>

The committed interface/static tests directly assert the expected fixture class and empty unrelated result. Both tests and verify use SpikeChecks and the same semantic index. Test discovery follows the xUnit Fact symbol (including Theory and attribute subclasses), merges partial types, and includes public inherited facts for concrete derived classes; abstract bases and helpers without tests are not returned. Full xUnit runner parity beyond those modeled cases remains unmeasured.

## Uncached cost

| Phase | Cold seconds | Warm seconds |
| --- | ---: | ---: |
| Workspace opening | 5.049 | 28.003 |
| Compilations and forced compiler diagnostics | 36.558 | 75.757 |
| Semantic reverse-index construction | 144.091 | 268.747 |
| Twelve queries | 0.769 | 0.992 |
| Full measured interval, including accounting overhead | 186.763 | 373.506 |
| Load/index plus only gate-shaped queries 8–10 | 185.716 | 372.525 |
| Peak spike working set, bytes | 2769477632 | 2752724992 |

Uncached analysis creates 20 compilations, forces binding diagnostics, walks every syntax tree for declarations, bound identifier/member references and invocation/construction sites, then builds interface/override relationships and the test catalog. The final graph has 142417 nodes and 1445554 reverse edges. The single-threaded index walk follows GetDiagnostics body binding, so this implementation's uncached cost is an upper bound rather than an optimized Roslyn limit. Cold/warm opening plus compilation alone cost 41.607 / 103.760 s. Individual binding, identity-generation and allocation costs were not profiled separately. A smaller reference walk is an unmeasured optimization.

The peak is the spike process's peak working set over its entire lifetime, including fixture verification and the framework-dispatch variant where applicable. The MSBuild child peak was not captured; the 4 GB decision check is applied to the observed spike process only, as the plan permits when child memory is unavailable. It is not a measured aggregate process-tree peak.

Workspace/compilation diagnostics in final measurements:

None in either final run. Cold/warm project compilations: 20/20, without compiler errors.

Framework-declared dispatch is excluded from scored traversal; source-declared interface and override dispatch is included. The separate warm variant enables the stored framework edges and measures these hub queries:

| Query | Returned classes | Variant query ms |
| --- | ---: | ---: |
| standing-rule-regression | 718 | 12.075 |
| timeline-file-affected | 6 | 0.092 |
| pretester-file-affected | 734 | 9.659 |

Variant and scored identities are listed in the receipts. Traversal is transitive with cycle detection and requires each returned test assembly's project-reference closure to contain all target assemblies; linked files in several projects can therefore miss tests reaching only one copy. It does not traverse nameof or documentation references. Reflection, dynamic calls, runtime-built delegates, process-spawning-only reachability, indexer access, user-defined operators/conversions and implicit foreach/using/await pattern calls are unsupported or unmeasured. Dependencies reached only through xUnit constructors, disposal, IAsyncLifetime or IClassFixture setup are not linked to the test class. The baseline check pins committed src/tests trees, not dirty working-tree contents. These are limitations for any future integration. Statically identifiable method groups and lambda bodies contribute dependency edges, without receiver points-to or delegate-value-flow precision.

## Reuse finding and decision

No persistent index cache was implemented or benchmarked. Changed-file keys alone cannot safely reuse this index across commits: an edit can change overload/interface binding in an unchanged file. A valid reuse design needs compilation-input fingerprints, including source contents, project/reference graphs, metadata identities/content, framework/SDK/compiler version, language/nullable/define options, imported props/targets, generators and additional files. Dependent compilations must be invalidated and references rebound. The storage size, reload cost and correct invalidation cost are unmeasured. Reusing only query answers cannot remove the expensive compilation and index construction. Warm filesystem/design-time outputs alone did not meet the limit.

The operator supplies a 1,120-second median passing gate (23 runs) and 41–43 seconds of existing selection planning. Small means both observed cold and warm totals at most 45 seconds; 45–120 seconds is conditional and yields stop as measured, with the reuse requirement stated; above 120 seconds or above the memory limit is stop. Proceed additionally requires queries 3, 5, 6, 7, 8, 9 and 10 correct and both runs' checks passing. This report's totals are 186.763 and 373.506 seconds; required-query pass=True, cold status=passed, warm status=passed, peak=2769477632 bytes. Applying those conditions yields **stop**. Cold analysis consumes 16.675% of the supplied gate duration. No cache feasibility or future speedup is assumed in that verdict.

Any later integration must first provide additive evidence beside current selection in ImpactReplay before narrowing anything. The Developer authored four SemanticImpactSpike test classes (including receipt-path redaction) and the separate SpikeFixture analysis inputs, built the changed projects with the worker adapter, and ran console verification. The operator owns one direct managed test-project run after landing, recording pass, fail or baseline-drift skip; that check does not hold landing. No acceptance manifest or existing project was edited.
