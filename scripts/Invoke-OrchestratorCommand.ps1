<#
.SYNOPSIS
  Run an explicit prebuilt orchestrator DLL through a repo-bounded script prefix.

.DESCRIPTION
  Select the prebuilt runtime with -AppDll. Arguments are forwarded exactly as
  PowerShell receives them.

.EXAMPLE
  .\scripts\Invoke-RepoScript.ps1 scripts\Invoke-OrchestratorCommand.ps1 -AppDll .\runtime\Mcg.AgentOrchestrator.App.dll status ddae01e6

.EXAMPLE
  .\scripts\Invoke-RepoScript.ps1 scripts\Invoke-OrchestratorCommand.ps1 -AppDll .\runtime\Mcg.AgentOrchestrator.App.dll --help
#>
[CmdletBinding(PositionalBinding = $false)]
param(
    [string]$AppDll,
    [string]$Pipeline,
    [string]$RepositoryRoot,
    [string]$ExpectedStateDirectory,
    [switch]$DisposableFixture,

    [Parameter(ValueFromRemainingArguments = $true)]
    [string[]]$Arguments
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$Arguments = @($Arguments)
if ($PSBoundParameters.ContainsKey('Pipeline')) {
    # PowerShell otherwise abbreviates --pipeline to the common
    # -PipelineVariable parameter and silently removes it from the CLI argv.
    $Arguments += @('--pipeline', $Pipeline)
}
if ($Arguments.Count -eq 0) {
    throw "Usage: .\scripts\Invoke-RepoScript.ps1 scripts\Invoke-OrchestratorCommand.ps1 <orchestrator-args...>"
}

$repoRoot = Split-Path -Parent $PSScriptRoot
$hasBinding = $PSBoundParameters.ContainsKey('RepositoryRoot') -or $PSBoundParameters.ContainsKey('ExpectedStateDirectory') -or $DisposableFixture -or
    @($Arguments | Where-Object { $_ -match '^--(repository-root|expected-state-directory|disposable-fixture)(=|$)' }).Count -gt 0
if ($hasBinding) {
    if (-not [string]::IsNullOrWhiteSpace($RepositoryRoot)) { $Arguments += @('--repository-root', $RepositoryRoot) }
    if (-not [string]::IsNullOrWhiteSpace($ExpectedStateDirectory)) { $Arguments += @('--expected-state-directory', $ExpectedStateDirectory) }
    if ($DisposableFixture) { $Arguments += '--disposable-fixture' }
    # A bound invocation must select its prepared runtime explicitly, before
    # launcher build/cache writes or root exports can precede binding validation.
    if ([string]::IsNullOrWhiteSpace($AppDll)) {
        throw 'Workspace binding requires an explicit prebuilt -AppDll; build and select the compatible runtime before retrying.'
    }
}
if ([string]::IsNullOrWhiteSpace($AppDll)) {
    [Console]::Error.WriteLine('Use mcg-orchestrator.cmd for foreground orchestrator commands; Invoke-OrchestratorCommand.ps1 requires an explicit -AppDll.')
    exit 1
}

$resolvedAppDll = [System.IO.Path]::GetFullPath($AppDll)

$rootWithSeparator = $repoRoot.TrimEnd(
    [System.IO.Path]::DirectorySeparatorChar,
    [System.IO.Path]::AltDirectorySeparatorChar) + [System.IO.Path]::DirectorySeparatorChar

if (-not ($resolvedAppDll.StartsWith($rootWithSeparator, [System.StringComparison]::OrdinalIgnoreCase) -or
          [string]::Equals($resolvedAppDll, $repoRoot, [System.StringComparison]::OrdinalIgnoreCase))) {
    throw "Refusing to run orchestrator DLL outside repository root: $resolvedAppDll"
}

if (-not (Test-Path -LiteralPath $resolvedAppDll -PathType Leaf)) {
    throw "App DLL not found. Build the app first: $resolvedAppDll"
}

$dotnetPath = [Environment]::GetEnvironmentVariable("MCG_ORCHESTRATOR_DOTNET_PATH", "Process")
if ([string]::IsNullOrWhiteSpace($dotnetPath)) { $dotnetPath = "dotnet" }
& $dotnetPath $resolvedAppDll @Arguments
if ($LASTEXITCODE -is [int] -and $LASTEXITCODE -ne 0) {
    exit $LASTEXITCODE
}
