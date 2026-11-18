<#
.SYNOPSIS
  Print a bounded line window from a file inside this repository.

.DESCRIPTION
  Use this through scripts\Invoke-RepoScript.ps1 when Codex needs a narrow source
  slice without ad-hoc PowerShell pipelines such as Get-Content | Select-Object.
  The target path is resolved against the repository root and must stay inside it.

.EXAMPLE
  .\scripts\Invoke-RepoScript.ps1 scripts\Show-RepoFileSlice.ps1 src\Example.cs 40 25
.EXAMPLE
  .\scripts\Invoke-RepoScript.ps1 scripts\Show-RepoFileSlice.ps1 src\Example.cs -SymbolName ExampleMethod -Count 25
#>
param(
    [Parameter(Mandatory = $true, Position = 0)]
    [string]$Path,

    [Parameter(Position = 1)]
    [ValidateRange(1, 2147483647)]
    [int]$Start = 1,

    [Parameter(Position = 2)]
    [ValidateRange(1, 500)]
    [int]$Count = 80,

    [Parameter()]
    [string]$SymbolName
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repoRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$candidate = if ([System.IO.Path]::IsPathRooted($Path)) {
    $Path
} else {
    Join-Path $repoRoot $Path
}

$resolved = [System.IO.Path]::GetFullPath($candidate)
$rootWithSeparator = $repoRoot.TrimEnd(
    [System.IO.Path]::DirectorySeparatorChar,
    [System.IO.Path]::AltDirectorySeparatorChar) + [System.IO.Path]::DirectorySeparatorChar

if (-not ($resolved.StartsWith($rootWithSeparator, [System.StringComparison]::OrdinalIgnoreCase) -or
          [string]::Equals($resolved, $repoRoot, [System.StringComparison]::OrdinalIgnoreCase))) {
    throw "Refusing to read file outside repository root: $resolved"
}

if (-not (Test-Path -LiteralPath $resolved -PathType Leaf)) {
    throw "File not found: $resolved"
}

$symbolFound = -not $PSBoundParameters.ContainsKey('SymbolName')
if (-not $symbolFound -and [string]::IsNullOrWhiteSpace($SymbolName)) {
    throw 'SymbolName must not be empty or whitespace.'
}

$end = $Start + $Count - 1
$lineNumber = 0
$lastLineShown = 0
foreach ($line in [System.IO.File]::ReadLines($resolved)) {
    $lineNumber++
    if (-not $symbolFound) {
        if ($line.IndexOf($SymbolName, [System.StringComparison]::Ordinal) -lt 0) {
            continue
        }

        $symbolFound = $true
        $Start = $lineNumber
        $end = $Start + $Count - 1
    }

    if ($lineNumber -ge $Start -and $lineNumber -le $end) {
        "{0,5}: {1}" -f $lineNumber, $line
        $lastLineShown = $lineNumber
    }
}

if (-not $symbolFound) {
    throw "Symbol not found: $SymbolName in $resolved"
}

if ($lastLineShown -gt 0 -and $lineNumber -gt $lastLineShown) {
    "Truncated: last line shown {0}; total lines {1}." -f $lastLineShown, $lineNumber
}
