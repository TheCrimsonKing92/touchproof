[CmdletBinding()]
param(
    [string]$Target
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"
$ProgressPreference = 'SilentlyContinue'
Import-Module (Join-Path $PSScriptRoot 'HiddenNativeProcess.psm1') -Force

$repoRoot = Split-Path -Parent $PSScriptRoot
if ([string]::IsNullOrWhiteSpace($Target)) {
    $Target = Join-Path $repoRoot "Mcg.AgentOrchestrator.sln"
}

$dotnetHost = if ([string]::IsNullOrWhiteSpace($env:MCG_ORCHESTRATOR_DOTNET_PATH)) {
    "dotnet"
}
else {
    $env:MCG_ORCHESTRATOR_DOTNET_PATH
}

# HiddenNativeProcess owns UseShellExecute = $false and CreateNoWindow = $true.
$exitCode = Invoke-HiddenNativeProcess -FilePath $dotnetHost -ArgumentList @(
    'restore', $Target, '--force-evaluate', '--nologo', '--verbosity', 'minimal', '-p:AuditPipeline=true'
)
if ($exitCode -ne 0) {
    exit $exitCode
}
