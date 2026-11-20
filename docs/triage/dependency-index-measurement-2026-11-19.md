# Dependency index measurement

On the matched App build, the dependency index reduced median peak private bytes above the process floor by **96.62%**, exceeding the 75% threshold. Median wall time for construction and rule evaluation was 8,573.1887 ms with ArchUnitNET and 471.7694 ms with the index.

On 2026-11-19 at 22:50 local time, the owner ended the checkpoint early. The recorded series contains four baseline runs, three index runs, and two floor runs. Three of the baseline runs match the App build used by the index and floor runs.

## Candidate and host

| Field | Value |
| --- | --- |
| Probe source revision | `ee12fc77225cc2c15a17e1d5e34bbc2045c7e3f8` |
| App module version ID | `caed4c45-abd2-48bc-b25c-946cf6098a66` |
| Machine | SEVENTHSON |
| Windows version | Microsoft Windows 10.0.26200 |
| .NET runtime | .NET 10.0.12 |
| Gate build configuration | Debug |
| Receipt paths | `.scratch/dependency-index-checkpoint` |

## Measurement protocol

The operator ran one probe per fresh managed test process with `Invoke-TestSummary.ps1` and `-TestInput MCG_TEST_INPUT_DEPENDENCY_INDEX_CHECKPOINT=1`, with no warm-up. The floor probe loaded the App assembly without building a model. The two measured arms each built their model for App alone and evaluated the rule forbidding types in the Application namespace from depending on types in the CLI and Rendering namespaces.

The probes read Windows `PeakPagefileUsage` after evaluation. The stopwatch covered model construction and rule evaluation, excluding test process startup. The comparison uses medians from runs with the App module version ID above.

## Run receipts

The eight matched runs all recorded zero violations. The earlier baseline is included for provenance. Its App module version ID was `2aa3632a-ecac-4c78-8345-bbfeb5daa461`, so it is excluded from the medians.

| Collected UTC | Arm | Peak private bytes | Wall time (ms) | Receipt |
| --- | --- | ---: | ---: | --- |
| 2026-11-20 03:29:25 | Baseline (earlier build) | 715,071,488 | 10,111.5393 | `baseline-32956-20c277f7fe674f24b2e9994a5ff459de.json` |
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
| Median floor peak (bytes) | 131,227,648 |
| Median baseline peak (bytes) | 710,680,576 |
| Median index peak (bytes) | 150,822,912 |
| Median baseline wall time (ms) | 8,573.1887 |
| Median index wall time (ms) | 471.7694 |
| Baseline median peak minus median floor (bytes) | 579,452,928 |
| Index median peak minus median floor (bytes) | 19,595,264 |
| Index peak reduction (percent) | 96.62% |

The floor-adjusted index median was 3.38% of the floor-adjusted baseline median. The memory criterion allows up to 25%.

## Namespace cycles

Namespace cycle counts for App, Core, and Infrastructure are unmeasured by owner decision on 2026-11-19 and left for a follow-up.
