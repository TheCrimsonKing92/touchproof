# Dependency index measurement

Reviewer measurements are pending. Capture them with both implementations present in the same Windows worktree.

## Candidate and host

| Field | Value |
| --- | --- |
| Checkpoint commit | |
| App module version ID | |
| Machine | |
| Windows version | |
| .NET runtime | |
| Gate build configuration | |
| Receipt paths | |

## Measurement protocol

Build the Infrastructure test project once in the gate configuration. Use that build for every run. Each command launches a fresh managed test process with one selected probe. Run the probes on the same machine with no warm-up. The floor loads the App assembly without building a model. Each measured arm builds its model for the App assembly alone and evaluates the Application layer rule forbidding dependencies on the CLI and Rendering namespaces.

```powershell
$project = 'tests/Mcg.AgentOrchestrator.Infrastructure.Tests/Mcg.AgentOrchestrator.Infrastructure.Tests.csproj'
$configuration = 'Debug'
$runs = @('Floor', 'Baseline', 'Index')
.\scripts\Invoke-RepoScript.ps1 scripts\Invoke-WorkerBuildCheck.ps1 $project -Configuration $configuration
if ($LASTEXITCODE -ne 0) { throw "Checkpoint build failed ($LASTEXITCODE)." }

foreach ($round in 1..5) {
    foreach ($arm in $runs) {
        .\scripts\Invoke-RepoScript.ps1 scripts\Invoke-TestSummary.ps1 -Target $project -Configuration $configuration -NoBuild -Filter "FullyQualifiedName~DependencyIndexMeasure${arm}ProbeTests" -TestInput 'MCG_TEST_INPUT_DEPENDENCY_INDEX_CHECKPOINT=1'
        if ($LASTEXITCODE -ne 0) { throw "Checkpoint ${arm} round $round failed ($LASTEXITCODE)." }
    }
}

.\scripts\Invoke-RepoScript.ps1 scripts\Invoke-TestSummary.ps1 -Target $project -Configuration $configuration -NoBuild -Filter 'FullyQualifiedName~DependencyIndexCensusProbeTests' -TestInput 'MCG_TEST_INPUT_DEPENDENCY_INDEX_CHECKPOINT=1'
if ($LASTEXITCODE -ne 0) { throw "Namespace census failed ($LASTEXITCODE)." }
```

Use the new JSON receipts in `.scratch/dependency-index-checkpoint`. Check the run order, host, configuration, App module version ID, and rule violation counts. The probe reads Windows `PeakPagefileUsage` after evaluation. The stopwatch covers model construction and rule evaluation after the test process has started.

| Round | Floor peak bytes | Baseline peak bytes | Baseline time (ms) | Index peak bytes | Index time (ms) |
| ---: | ---: | ---: | ---: | ---: | ---: |
| 1 | | | | | |
| 2 | | | | | |
| 3 | | | | | |
| 4 | | | | | |
| 5 | | | | | |
| Median | | | | | |

| Comparison | Value |
| --- | ---: |
| Baseline median peak minus median floor (bytes) | |
| Index median peak minus median floor (bytes) | |
| Index peak reduction (percent) | |

Subtract the median floor from each median peak. The index passes the memory criterion if its adjusted median is at most 25% of the adjusted baseline.

## Namespace cycles

The census counts elementary directed cycles independently in each assembly. It uses references between distinct namespaces in the same assembly and counts each rotation of a path as the same cycle.

| Assembly | Count | Cycles |
| --- | ---: | --- |
| App | | |
| Core | | |
| Infrastructure | | |

List each cycle from the receipt and repeat its first namespace at the end to show the closing edge. Add the cycle-free guard only if all three counts are zero. After recording the results, remove the probes, legacy cache and its tests, and the package reference.
