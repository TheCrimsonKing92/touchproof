# Semantic test-impact spike

This console loads `Mcg.AgentOrchestrator.sln` with Roslyn 5.6.0 in Debug. It does
not register with the solution, acceptance manifest or shipped selection code.
There is no persistent semantic index. The committed corpus pins the `src` and
`tests` Git trees; later tree drift exits 3 and the known-answer test skips.
Fixtures remain independent of that baseline.

Build with the repository worker adapter:

```powershell
.\scripts\Invoke-WorkerBuildCheck.ps1 tools/Mcg.SemanticImpactSpike/src/Mcg.SemanticImpactSpike.csproj tools/Mcg.SemanticImpactSpike/tests/Mcg.SemanticImpactSpike.Tests.csproj
```

Use an **absolute, short** artifacts path. The experiment's package cache is next
to `ArtifactsPath`; without that property it uses this directory's ignored
`artifacts/packages`. Long worktree paths can exceed the native MSBuild copy
limit for Roslyn's build-host content. The console build explicitly copies that
child process closure. `Microsoft.Build.Framework` is excluded from runtime
assets so MSBuild Locator can resolve the installed SDK.

Before analysis, restore the repository solution and fixture solution into the
same analysis artifacts path. Restoring the spike projects alone does not
restore the repository under analysis. Pass the same properties to restore and
to the console's repeated `--msbuild-property KEY=VALUE` arguments. For example,
with `$analysis`, `$packages`, and `$dll` set to absolute paths:

```powershell
dotnet restore Mcg.AgentOrchestrator.sln -p:UseArtifactsOutput=true "-p:ArtifactsPath=$analysis" "-p:RestorePackagesPath=$packages" "-p:NuGetPackageRoot=$packages/"
dotnet restore tools/Mcg.SemanticImpactSpike/tests/Fixtures/ImpactFixture/ImpactFixture.sln -p:UseArtifactsOutput=true "-p:ArtifactsPath=$analysis" "-p:RestorePackagesPath=$packages" "-p:NuGetPackageRoot=$packages/"
dotnet $dll verify --repo (Get-Location).Path --data tools/Mcg.SemanticImpactSpike/data/known-answer-queries.json --receipt tools/Mcg.SemanticImpactSpike/data/verify-receipt.json --msbuild-property UseArtifactsOutput=true --msbuild-property "ArtifactsPath=$analysis" --msbuild-property "RestorePackagesPath=$packages" --msbuild-property "NuGetPackageRoot=$packages/"
```

`measure` runs the same repository queries without the fixture prelude. `query`
accepts `--id QUERY_ID` to select one query. Every command requires `--receipt`.
Exit 0 means scored checks passed (the cost verdict can still be stop); exit 1
means a check or workspace diagnostic failed; exit 2 means an exception; exit 3
means baseline drift. Raw results, differences, source identities, properties,
timings, project settings and diagnostics are machine-written JSON. The
required/excluded queries list unconstrained results separately; those are not
proven false positives.

Receipt serialization replaces repository roots with `<repo>` and owner-home
roots with `<user-home>` in every string value, including errors and diagnostics.
Suffixes remain available for review; substitute local roots before replaying
recorded arguments. To redact a historical receipt without rerunning analysis:

```powershell
dotnet $dll redact --repo (Get-Location).Path --receipt tools/Mcg.SemanticImpactSpike/data/initial-run.json
```

The historical receipt retains its original measurements and results; the report
discloses this deterministic redaction. Fresh `verify` also runs the shared
receipt-path assertions used by `SemanticImpactSpikeReceiptPathTests`.

`data/Establish-KnownAnswers.ps1` records the independent source-reading procedure.
Initial expectations were recorded before any repository query. Two namespace
identity mistakes were subsequently corrected by re-reading their declarations;
the initial mismatches remain in `data/initial-run.json`. Do not regenerate
reachability expectations from tool output. Queries without established ground
truth stay unscored.
`callers` identities use the containing member's source declaration identifier
line; call and construction sites use the expression's line. Symbol inputs use
assembly name plus Roslyn documentation-comment ID.

The shared `SpikeChecks` routines serve both the console and committed xUnit
tests. Fixture check names bind to fixed expected identity sets, including an
explicit interface implementation, an unrelated same-named method, a delegate
method group, partial declarations and an inherited fact. Abstract test bases
are not returned; inherited public facts are returned for concrete derived
classes. `nameof` and documentation references do not participate in traversal.
Dispatch through source interfaces and source overrides is conservative;
framework dispatch is a separate measured variant. Test assemblies must have
the target project in their project-reference closure.

## Operator check after landing

Workers do not launch test hosts. These projects are outside the gate's test
inventory and must not be added to its manifest. The operator should restore
`Mcg.AgentOrchestrator.sln` with its normal output paths first, build the spike
projects with the adapter, then launch the **managed** spike test assembly:

```powershell
dotnet <absolute-path-to-Mcg.SemanticImpactSpike.Tests.dll> --report-trx
```

The known-answer test uses normal repository assets by default. A direct project
build can set `SpikeAnalysisArtifactsPath` to explicitly select separately
restored repository assets. The fixture loader uses the build's recorded
`ArtifactsPath`. The operator records pass, fail or baseline-drift skip; that
post-landing run does not hold landing. The four public `SemanticImpactSpike*Tests`
classes are the executable checks in this test assembly. The `SpikeFixture*`
classes are semantic-analysis inputs in a separate project and are not launched
by this command.
