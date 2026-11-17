<#
.SYNOPSIS
  Run a PowerShell script that lives inside this repository.

.DESCRIPTION
  Codex command allowlists work best with stable command prefixes. Ad-hoc PowerShell
  one-liners and direct `powershell -File <path>` calls often trigger repeated
  permission prompts because the meaningful operation is hidden behind a broad shell.

  This wrapper is intentionally narrow: it resolves the target path against the
  repository root, requires a `.ps1` file, and refuses to run anything outside the
  checkout. Approve this wrapper prefix once to run checked-in repo scripts without
  approving arbitrary PowerShell.

.EXAMPLE
  .\scripts\Invoke-RepoScript.ps1 scripts\Find-OrchestratorLocks.ps1

.EXAMPLE
  .\scripts\Invoke-RepoScript.ps1 scripts\Invoke-TestSummary.ps1 -Target .\Mcg.AgentOrchestrator.sln
#>
$ErrorActionPreference = 'Stop'
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
    ScriptPath = 'Value'; ScriptArguments = 'Arguments'
} -PositionalControl 'ScriptPath'
$ScriptPath = [string]$wrapperInput.Controls['ScriptPath']
$ScriptArguments = @($wrapperInput.Arguments)
if ([string]::IsNullOrWhiteSpace($ScriptPath)) {
    throw "Usage: .\scripts\Invoke-RepoScript.ps1 <repo-relative-script.ps1> [script arguments...]"
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

$repoRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$candidate = if ([System.IO.Path]::IsPathRooted($ScriptPath)) {
    $ScriptPath
} else {
    Join-Path $repoRoot $ScriptPath
}

$resolved = [System.IO.Path]::GetFullPath($candidate)
$rootWithSeparator = $repoRoot.TrimEnd([System.IO.Path]::DirectorySeparatorChar, [System.IO.Path]::AltDirectorySeparatorChar) + [System.IO.Path]::DirectorySeparatorChar

if (-not ($resolved.StartsWith($rootWithSeparator, [System.StringComparison]::OrdinalIgnoreCase) -or
          [string]::Equals($resolved, $repoRoot, [System.StringComparison]::OrdinalIgnoreCase))) {
    throw "Refusing to run script outside repository root: $resolved"
}

if ([System.IO.Path]::GetExtension($resolved) -ne '.ps1') {
    throw "Refusing to run non-PowerShell script: $resolved"
}

if (-not (Test-Path -LiteralPath $resolved -PathType Leaf)) {
    throw "Script not found: $resolved"
}

if ([string]::Equals([System.IO.Path]::GetFileName($resolved), 'Invoke-Git.ps1', [System.StringComparison]::OrdinalIgnoreCase)) {
    $processArguments = [Environment]::GetCommandLineArgs()
    $currentScript = [System.IO.Path]::GetFullPath($PSCommandPath)
    $scriptArgumentOffset = -1
    for ($i = 0; $i -lt $processArguments.Count; $i++) {
        try {
            $candidateScript = if ([System.IO.Path]::IsPathRooted($processArguments[$i])) {
                [System.IO.Path]::GetFullPath($processArguments[$i])
            } else {
                [System.IO.Path]::GetFullPath((Join-Path (Get-Location) $processArguments[$i]))
            }

            if ([string]::Equals($candidateScript, $currentScript, [System.StringComparison]::OrdinalIgnoreCase)) {
                $scriptArgumentOffset = $i + 2
                break
            }
        } catch {
        }
    }

    $gitArguments = if ($scriptArgumentOffset -ge 0 -and $scriptArgumentOffset -lt $processArguments.Count) {
        @($processArguments[$scriptArgumentOffset..($processArguments.Count - 1)])
    } else {
        $parseErrors = $null
        $lineTokens = @([System.Management.Automation.PSParser]::Tokenize($MyInvocation.Line, [ref]$parseErrors) |
            Where-Object { $_.Type -in 'Command', 'CommandArgument', 'CommandParameter', 'String' })
        $lineScriptOffset = -1
        for ($i = 0; $i -lt $lineTokens.Count; $i++) {
            try {
                $candidateScript = if ([System.IO.Path]::IsPathRooted($lineTokens[$i].Content)) {
                    [System.IO.Path]::GetFullPath($lineTokens[$i].Content)
                } else {
                    [System.IO.Path]::GetFullPath((Join-Path (Get-Location) $lineTokens[$i].Content))
                }

                if ([string]::Equals($candidateScript, $currentScript, [System.StringComparison]::OrdinalIgnoreCase)) {
                    $lineScriptOffset = $i + 2
                    break
                }
            } catch {
            }
        }

        if ($lineScriptOffset -ge 0 -and $lineScriptOffset -lt $lineTokens.Count) {
            @($lineTokens[$lineScriptOffset..($lineTokens.Count - 1)] | ForEach-Object { $_.Content })
        } else {
            @($ScriptArguments)
        }
    }

    & git -C $repoRoot @gitArguments
    exit $LASTEXITCODE
}

$powerShellPath = (Get-Process -Id $PID).Path
$PSNativeCommandArgumentPassing = 'Legacy'
$nativeArguments = @(@('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', $resolved) + $ScriptArguments |
    ForEach-Object { ConvertTo-CommandLineArgument $_ })
& $powerShellPath @nativeArguments
if ($LASTEXITCODE -is [int]) {
    exit $LASTEXITCODE
}
