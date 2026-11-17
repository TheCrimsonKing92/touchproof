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
Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

# Keep this raw-token boundary identical in all three command wrappers. A script
# param block would let PowerShell consume child flags before this code can run.
function Read-WrapperArguments {
    param(
        [object[]]$Tokens,
        [hashtable]$Controls,
        [string]$PositionalControl = ''
    )

    $values = @{}
    $child = [System.Collections.Generic.List[string]]::new()
    for ($index = 0; $index -lt $Tokens.Count; $index++) {
        $token = [string]$Tokens[$index]
        if ($token.StartsWith('--', [System.StringComparison]::Ordinal)) {
            $child.Add($token)
            if (-not $token.Contains('=') -and $index + 1 -lt $Tokens.Count -and
                -not ([string]$Tokens[$index + 1]).StartsWith('--', [System.StringComparison]::Ordinal)) {
                # Even a single-dash control spelling can be a child flag's value.
                $child.Add([string]$Tokens[++$index])
            }
            continue
        }
        # Once the repository script is selected, its single-dash controls belong
        # to that script too. Only the splatted remaining-arguments array is unwrapped.
        if ($PositionalControl -and $values.ContainsKey($PositionalControl) -and
            -not ($token -match '^-([^-:][^:]*):$' -and
                $Controls.ContainsKey($Matches[1]) -and $Controls[$Matches[1]] -eq 'Arguments')) {
            $child.Add($token)
            continue
        }

        $control = $null
        $attached = $null
        if ($token -match '^-([^-:][^:]*)(?::(.*))?$') {
            $spelling = $Matches[1]
            if ($Matches.ContainsKey(2)) { $attached = $Matches[2] }
            if ($Controls.ContainsKey($spelling)) {
                $control = $spelling
            } else {
                $candidates = @($Controls.Keys | Where-Object {
                    $_.StartsWith($spelling, [System.StringComparison]::OrdinalIgnoreCase)
                })
                if ($candidates.Count -gt 1) { throw "Ambiguous wrapper control: $token" }
                if ($candidates.Count -eq 1) { $control = $candidates[0] }
            }
        }

        if ($null -ne $control) {
            $kind = $Controls[$control]
            if ($kind -eq 'Switch') {
                $value = $true
                if (-not [string]::IsNullOrEmpty($attached)) {
                    $value = [System.Convert]::ToBoolean($attached.TrimStart('$'))
                } elseif ($null -ne $attached -and $index + 1 -lt $Tokens.Count -and
                    ($Tokens[$index + 1] -is [bool] -or
                     $Tokens[$index + 1] -is [System.Management.Automation.SwitchParameter])) {
                    $value = [bool]$Tokens[++$index]
                }
            } else {
                if (-not [string]::IsNullOrEmpty($attached)) {
                    $value = $attached
                } else {
                    if ($index + 1 -ge $Tokens.Count -or
                        ([string]$Tokens[$index + 1]).StartsWith('--', [System.StringComparison]::Ordinal)) {
                        throw "Missing value for wrapper control: $token"
                    }
                    $value = $Tokens[++$index]
                }
            }
            if ($kind -eq 'Arguments') {
                foreach ($item in @($value)) { $child.Add([string]$item) }
            } else {
                $values[$control] = $value
            }
            continue
        }

        if ($PositionalControl -and -not $values.ContainsKey($PositionalControl)) {
            $values[$PositionalControl] = $token
        } else {
            $child.Add($token)
        }
    }
    return [pscustomobject]@{ Controls = $values; Arguments = $child.ToArray() }
}

$wrapperInput = Read-WrapperArguments -Tokens $args -Controls @{
    AppDll = 'Value'; RepositoryRoot = 'Value'; ExpectedStateDirectory = 'Value'
    DisposableFixture = 'Switch'; Arguments = 'Arguments'
}
$wrapperControls = $wrapperInput.Controls
$AppDll = [string]$wrapperControls['AppDll']
$RepositoryRoot = [string]$wrapperControls['RepositoryRoot']
$ExpectedStateDirectory = [string]$wrapperControls['ExpectedStateDirectory']
$DisposableFixture = [bool]$wrapperControls['DisposableFixture']
$Arguments = @($wrapperInput.Arguments)
if ($Arguments.Count -eq 0) {
    throw "Usage: .\scripts\Invoke-RepoScript.ps1 scripts\Invoke-OrchestratorCommand.ps1 <orchestrator-args...>"
}

function ConvertTo-CommandLineArgument {
    param([string]$Value)

    if ($null -eq $Value) {
        return '""'
    }

    if ($Value.Length -gt 0 -and $Value.IndexOfAny([char[]]@(' ', "`t", "`n", "`r", '"')) -lt 0) {
        return $Value
    }

    $quoted = [System.Text.StringBuilder]::new()
    [void]$quoted.Append('"')
    $backslashes = 0
    foreach ($character in $Value.ToCharArray()) {
        if ($character -eq '\') {
            $backslashes++
            continue
        }

        if ($character -eq '"') {
            [void]$quoted.Append('\', ($backslashes * 2) + 1)
            [void]$quoted.Append('"')
            $backslashes = 0
            continue
        }

        if ($backslashes -gt 0) {
            [void]$quoted.Append('\', $backslashes)
            $backslashes = 0
        }

        [void]$quoted.Append($character)
    }

    if ($backslashes -gt 0) {
        [void]$quoted.Append('\', $backslashes * 2)
    }

    [void]$quoted.Append('"')
    return $quoted.ToString()
}

$repoRoot = Split-Path -Parent $PSScriptRoot
$hasBinding = $wrapperControls.ContainsKey('RepositoryRoot') -or $wrapperControls.ContainsKey('ExpectedStateDirectory') -or $DisposableFixture -or
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
# Use the same Windows argv quoting as the detached launcher. Legacy native
# passing otherwise removes embedded quotes (and empty tokens) on Windows.
$PSNativeCommandArgumentPassing = 'Legacy'
$nativeArguments = @(@($resolvedAppDll) + $Arguments | ForEach-Object { ConvertTo-CommandLineArgument $_ })
& $dotnetPath @nativeArguments
if ($LASTEXITCODE -is [int] -and $LASTEXITCODE -ne 0) {
    exit $LASTEXITCODE
}
