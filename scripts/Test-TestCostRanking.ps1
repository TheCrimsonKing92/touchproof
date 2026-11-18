[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$scriptPath = Join-Path $PSScriptRoot 'Get-TestCostRanking.ps1'
$fixtureRoot = Join-Path $PSScriptRoot 'fixtures\test-cost-ranking'

$first = (& $scriptPath -InputRoot $fixtureRoot) -join "`n"
$second = (& $scriptPath -InputRoot $fixtureRoot) -join "`n"
if (-not [string]::Equals($first, $second, [StringComparison]::Ordinal)) {
    throw 'Ranking output changed across identical runs.'
}

$expected = @'
"ranking_kind","rank","test_name","execution_count","failure_count","failure_rate","total_seconds","maximum_seconds"
"cost","1","Alpha","2","1","0.500000","5.000000","3.000000"
"cost","2","Beta","2","1","0.500000","3.000000","1.500000"
"cost","3","Tie-A","1","0","0.000000","1.000000","1.000000"
"cost","4","Tie-B","1","0","0.000000","1.000000","1.000000"
"cost","5","Skipped","1","0","0.000000","0.000000","0.000000"
"failure","1","Alpha","2","1","0.500000","5.000000","3.000000"
"failure","2","Beta","2","1","0.500000","3.000000","1.500000"
"failure","3","Tie-A","1","0","0.000000","1.000000","1.000000"
"failure","4","Tie-B","1","0","0.000000","1.000000","1.000000"
'@
$expected = $expected.Replace("`r`n", "`n").TrimEnd("`r", "`n")
if (-not [string]::Equals($expected, $first, [StringComparison]::Ordinal)) {
    throw "Unexpected ranking output.`nEXPECTED:`n$expected`nACTUAL:`n$first"
}

if ($first.Contains('TooOld', [StringComparison]::Ordinal) -or
    $first.Contains($fixtureRoot, [StringComparison]::OrdinalIgnoreCase)) {
    throw 'Ranking output leaked excluded input or an absolute fixture path.'
}

$invalidRoot = Join-Path $PSScriptRoot 'fixtures\test-cost-ranking-invalid'
$diagnostics = [System.IO.StringWriter]::new()
$originalError = [Console]::Error
try {
    [Console]::SetError($diagnostics)
    $invalidOutput = @(& $scriptPath -InputRoot $invalidRoot)
}
finally {
    [Console]::SetError($originalError)
}
# Missing finish is deliberately a counted exclusion, replacing the old throw contract.
$invalidDiagnostics = $diagnostics.ToString()
$diagnostics.Dispose()
if ($invalidOutput.Count -ne 0 -or
    -not $invalidDiagnostics.Contains('Receipts: examined=1 out-of-window=0 malformed=1 qualified=0', [StringComparison]::Ordinal) -or
    -not $invalidDiagnostics.Contains('missingFinish=1', [StringComparison]::Ordinal) -or
    -not $invalidDiagnostics.Contains('No qualified TRX receipts.', [StringComparison]::Ordinal)) {
    throw "Missing-finish receipt was not explicitly excluded: $invalidDiagnostics"
}

Write-Output 'PASS: recursive TRX cost/failure ranking is windowed and byte-stable.'
