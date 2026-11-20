# Dependency index measurement

The checkpoint is incomplete. On the measured App build, three baseline runs and three index runs alternated, and two floor runs completed. Completing the checkpoint requires two more runs per measured arm, three more floor runs, and the namespace census. Capture them with both implementations present in the same Windows worktree and use one build for the full comparison.

## Candidate and host

| Field | Value |
| --- | --- |
| Checkpoint commit | |
| App module version ID | `caed4c45-abd2-48bc-b25c-946cf6098a66` |
| Machine | SEVENTHSON |
| Windows version | Microsoft Windows 10.0.26200 |
| .NET runtime | .NET 10.0.12 |
| Gate build configuration | Debug |
| Receipt paths | `.scratch/dependency-index-checkpoint` |

## Measurement protocol

Build the Infrastructure test project once in the gate configuration. Use that build for every run. Each command launches a fresh managed test process with one selected probe. Run the probes on the same machine with no warm-up. The floor loads the App assembly without building a model. Each measured arm builds its model for the App assembly alone and evaluates the Application layer rule forbidding dependencies on the CLI and Rendering namespaces.

The commands below start a fresh series. To resume the series in this note, use its existing build, confirm the App module version ID, and run only the outstanding probes. If that build is unavailable, start a fresh series.

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

## Run receipts

All eight runs below used the App module version ID above and reported zero violations. The earlier `baseline-32956-20c277f7fe674f24b2e9994a5ff459de.json` receipt used App module version ID `2aa3632a-ecac-4c78-8345-bbfeb5daa461` and is excluded.

| Collected UTC | Arm | Peak private bytes | Wall time (ms) | Receipt |
| --- | --- | ---: | ---: | --- |
| 2026-11-20 04:07:26 | Baseline | 710,680,576 | 8,573.1887 | `baseline-34876-35ed69c4d8d5495e965fcd6fcb5f680a.json` |
| 2026-11-20 04:12:27 | Index | 150,822,912 | 518.2318 | `index-16844-1139456538e34823bd9d1250fa052f49.json` |
| 2026-11-20 04:17:21 | Floor | 131,289,088 | 0.0237 | `floor-29236-b68c52ae39f748e8bc837ce416dbfe76.json` |
| 2026-11-20 04:24:41 | Baseline | 713,572,352 | 9,133.6008 | `baseline-28808-1f687d9112a44bccb379e96724f53f9d.json` |
| 2026-11-20 04:30:04 | Index | 150,351,872 | 469.5183 | `index-49476-7bda977514f94042ab6a533ac1ec0e70.json` |
| 2026-11-20 04:34:43 | Floor | 131,166,208 | 0.0249 | `floor-35724-c76b2fc2bdc341b99c9cd86caca97e11.json` |
| 2026-11-20 04:39:39 | Baseline | 710,680,576 | 8,375.5662 | `baseline-43448-ce794abca755499e966356cbc367249c.json` |
| 2026-11-20 04:44:28 | Index | 150,867,968 | 471.7694 | `index-51400-1b04c4361b974b9bae75a3c5266a19c6.json` |

| Comparison | Value |
| --- | ---: |
| Median floor peak (bytes) | Pending full series |
| Median baseline peak (bytes) | Pending full series |
| Median index peak (bytes) | Pending full series |
| Median baseline wall time (ms) | Pending full series |
| Median index wall time (ms) | Pending full series |
| Baseline median peak minus median floor (bytes) | Pending full series |
| Index median peak minus median floor (bytes) | Pending full series |
| Index peak reduction (percent) | Pending full series |

Subtract the median floor from each median peak. The index passes the memory criterion if its adjusted median is at most 25% of the adjusted baseline.

## Namespace cycles

The census counts elementary directed cycles independently in each assembly. It uses references between distinct namespaces in the same assembly and counts each rotation of a path as the same cycle.

| Assembly | Count | Cycles |
| --- | ---: | --- |
| App | | |
| Core | | |
| Infrastructure | | |

List each cycle from the receipt and repeat its first namespace at the end to show the closing edge. Add the cycle-free guard only if all three counts are zero. After recording the results, remove the probes, legacy cache and its tests, and the package reference.
