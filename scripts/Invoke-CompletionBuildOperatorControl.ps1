param(
    [Parameter(Mandatory = $true)][string]$ProbeDll,
    [Parameter(Mandatory = $true)][string]$SourceRepository,
    [Parameter(Mandatory = $true)][string]$OutputRoot,
    [ValidateSet('all', 'held', 'stop', 'renewal', 'restart', 'ownership-loss', 'replacement', 'stale-candidate', 'stale-dispatch')]
    [string]$Control = 'all'
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
if (-not [System.OperatingSystem]::IsWindows()) { throw 'This control requires Windows and PowerShell 7.' }
$probePath = (Resolve-Path -LiteralPath $ProbeDll).Path
$sourcePath = (Resolve-Path -LiteralPath $SourceRepository).Path
$evidenceRoot = [System.IO.Path]::GetFullPath($OutputRoot)
if ([System.IO.Path]::GetFileName($probePath) -ne 'Mcg.AgentOrchestrator.SupervisorLaunchProbe.dll') {
    throw 'Pass the exact-candidate SupervisorLaunchProbe DLL from its build receipt.'
}
if (Test-Path -LiteralPath $evidenceRoot) { throw 'OutputRoot must be a new directory; existing evidence is never overwritten.' }
if (-not (Test-Path -LiteralPath (Join-Path $sourcePath 'Mcg.AgentOrchestrator.sln') -PathType Leaf)) {
    throw 'SourceRepository must identify the explicit candidate checkout.'
}
New-Item -ItemType Directory -Path $evidenceRoot | Out-Null
$controls = if ($Control -eq 'all') {
    @('held', 'stop', 'renewal', 'restart', 'ownership-loss', 'replacement', 'stale-candidate', 'stale-dispatch')
} else { @($Control) }
foreach ($case in $controls) {
    $caseRoot = Join-Path $evidenceRoot $case
    # Foreground dotnet host, shell-plain argument array, no paid worker or deployed conductor.
    & dotnet $probePath completion-build $caseRoot $sourcePath $case
    if ($LASTEXITCODE -ne 0) {
        throw "Control '$case' exited $LASTEXITCODE; inspect $caseRoot\receipts.jsonl and host-*.err.log. Evidence retained."
    }
}
Write-Output "PASS operator controls=$($controls.Count) evidence=$evidenceRoot"
