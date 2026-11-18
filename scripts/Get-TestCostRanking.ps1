<#
Metadata eligibility uses LastWriteTimeUtc relative to the newest TRX mtime in the
input corpus, before opening files. Fully valid candidates then retain the ranking
window relative to the latest embedded finish. Copied/retimestamped historical files
may therefore be opened and subsequently excluded. Missing finishes are never inferred.
Receipt counts and exclusions go to stderr; stdout contains only ranking CSV.
#>
[CmdletBinding()]
param(
    [string]$InputRoot = (Join-Path (Split-Path $PSScriptRoot -Parent) '.orchestrator\acceptance-gate-attempts'),
    [ValidateRange(0.001, 87600)]
    [double]$WindowHours = 24,
    [ValidateRange(0, [int]::MaxValue)]
    [int]$Top = 0
)

$ErrorActionPreference = 'Stop'
$invariant = [System.Globalization.CultureInfo]::InvariantCulture
$dateStyles = [System.Globalization.DateTimeStyles]::AssumeUniversal -bor
    [System.Globalization.DateTimeStyles]::AdjustToUniversal
$failureOutcomes = [System.Collections.Generic.HashSet[string]]::new(
    [string[]]@('Failed', 'Error', 'Timeout', 'Aborted'),
    [System.StringComparer]::OrdinalIgnoreCase)
$excludedRateOutcomes = [System.Collections.Generic.HashSet[string]]::new(
    [string[]]@('NotExecuted', 'Inconclusive'),
    [System.StringComparer]::OrdinalIgnoreCase)
$knownOutcomes = [System.Collections.Generic.HashSet[string]]::new(
    [string[]]@('Passed', 'Failed', 'Error', 'Timeout', 'Aborted', 'NotExecuted', 'Inconclusive'),
    [System.StringComparer]::OrdinalIgnoreCase)

if (-not (Test-Path -LiteralPath $InputRoot -PathType Container)) {
    throw "TRX input root does not exist: $InputRoot"
}

$exclusions = [ordered]@{
    metadataOutOfWindow = 0
    finishOutOfWindow = 0
    malformedXml = 0
    missingFinish = 0
    unparseableTimestamps = 0
    missingResults = 0
    missingTestName = 0
    unknownOutcome = 0
    invalidDuration = 0
}

function Read-Receipt {
    param([System.IO.FileInfo]$File)

    # Access failures are corpus failures, not malformed receipt exclusions.
    $stream = [System.IO.File]::OpenRead($File.FullName)
    $document = [System.Xml.XmlDocument]::new()
    $document.PreserveWhitespace = $false
    try {
        $document.Load($stream)
    }
    catch [System.Xml.XmlException] {
        return [pscustomobject]@{ Reason = 'malformedXml' }
    }
    finally {
        $stream.Dispose()
    }
    $testRun = $document.SelectSingleNode("/*[local-name()='TestRun']")
    $times = $document.SelectSingleNode("/*[local-name()='TestRun']/*[local-name()='Times']")
    if ($null -eq $testRun -or $null -eq $times -or [string]::IsNullOrWhiteSpace($times.GetAttribute('finish'))) {
        return [pscustomobject]@{ Reason = 'missingFinish' }
    }

    $finish = [DateTimeOffset]::MinValue
    if (-not [DateTimeOffset]::TryParse(
        $times.GetAttribute('finish'),
        $invariant,
        $dateStyles,
        [ref]$finish)) {
        return [pscustomobject]@{ Reason = 'unparseableTimestamps' }
    }

    $results = @($document.SelectNodes("/*[local-name()='TestRun']/*[local-name()='Results']/*[local-name()='UnitTestResult']"))
    if ($results.Count -eq 0) {
        return [pscustomobject]@{ Reason = 'missingResults' }
    }

    $validatedResults = [System.Collections.Generic.List[object]]::new()
    foreach ($result in $results) {
        $name = $result.GetAttribute('testName')
        $outcome = $result.GetAttribute('outcome')
        $durationText = $result.GetAttribute('duration')
        if ([string]::IsNullOrWhiteSpace($name)) {
            return [pscustomobject]@{ Reason = 'missingTestName' }
        }
        if (-not $knownOutcomes.Contains($outcome)) {
            return [pscustomobject]@{ Reason = 'unknownOutcome' }
        }

        $duration = [TimeSpan]::Zero
        if ([string]::IsNullOrWhiteSpace($durationText) -or
            -not [TimeSpan]::TryParse($durationText, $invariant, [ref]$duration) -or
            $duration -lt [TimeSpan]::Zero) {
            return [pscustomobject]@{ Reason = 'invalidDuration' }
        }
        $validatedResults.Add([pscustomobject]@{ Name = $name; Outcome = $outcome; Seconds = $duration.TotalSeconds })
    }
    return [pscustomobject]@{ Reason = $null; Finish = $finish; Results = $validatedResults }
}

$receipts = [System.Collections.Generic.List[object]]::new()
$files = @(Get-ChildItem -LiteralPath $InputRoot -Filter '*.trx' -File -Recurse)
if ($files.Count -gt 0) {
    $latestWrite = ($files | ForEach-Object LastWriteTimeUtc | Measure-Object -Maximum).Maximum
    $metadataStart = $latestWrite.Subtract([TimeSpan]::FromHours($WindowHours))
    foreach ($file in $files) {
        if ($file.LastWriteTimeUtc -lt $metadataStart) {
            $exclusions.metadataOutOfWindow++
            continue
        }
        $receipt = Read-Receipt $file
        if ($null -ne $receipt.Reason) {
            $exclusions[$receipt.Reason]++
            continue
        }
        $receipts.Add($receipt)
    }
}

$qualified = 0
$aggregates = [System.Collections.Generic.Dictionary[string, object]]::new([System.StringComparer]::Ordinal)
if ($receipts.Count -gt 0) {
    $latestFinish = ($receipts | ForEach-Object Finish | Measure-Object -Maximum).Maximum
    $windowStart = $latestFinish.Subtract([TimeSpan]::FromHours($WindowHours))
    foreach ($receipt in $receipts) {
        if ($receipt.Finish -lt $windowStart) {
            $exclusions.finishOutOfWindow++
            continue
        }
        $qualified++
        foreach ($result in $receipt.Results) {
            $name = $result.Name
            $outcome = $result.Outcome

            if (-not $aggregates.ContainsKey($name)) {
                $aggregates[$name] = [pscustomobject]@{
                    TestName = $name
                    ExecutionCount = 0
                    FailureCount = 0
                    RateDenominator = 0
                    TotalSeconds = 0.0
                    MaximumSeconds = 0.0
                }
            }

            $aggregate = $aggregates[$name]
            $aggregate.ExecutionCount++
            $seconds = $result.Seconds
            $aggregate.TotalSeconds += $seconds
            $aggregate.MaximumSeconds = [Math]::Max($aggregate.MaximumSeconds, $seconds)
            if (-not $excludedRateOutcomes.Contains($outcome)) {
                $aggregate.RateDenominator++
                if ($failureOutcomes.Contains($outcome)) {
                    $aggregate.FailureCount++
                }
            }
        }
    }
}

$outOfWindow = $exclusions.metadataOutOfWindow + $exclusions.finishOutOfWindow
$malformed = $files.Count - $outOfWindow - $qualified
[Console]::Error.WriteLine("Receipts: examined=$($files.Count) out-of-window=$outOfWindow malformed=$malformed qualified=$qualified")
$reasonText = @($exclusions.GetEnumerator() | ForEach-Object { "$($_.Key)=$($_.Value)" }) -join ' '
[Console]::Error.WriteLine("Exclusions: $reasonText")
if ($qualified -eq 0) {
    [Console]::Error.WriteLine('No qualified TRX receipts.')
    return
}

$cost = [System.Collections.Generic.List[object]]::new()
$failure = [System.Collections.Generic.List[object]]::new()
foreach ($aggregate in $aggregates.Values) {
    $failureRate = if ($aggregate.RateDenominator -eq 0) {
        0.0
    }
    else {
        $aggregate.FailureCount / $aggregate.RateDenominator
    }
    $aggregate | Add-Member -NotePropertyName FailureRate -NotePropertyValue $failureRate
    $cost.Add($aggregate)
    if ($aggregate.RateDenominator -gt 0) {
        $failure.Add($aggregate)
    }
}

$cost.Sort([System.Comparison[object]]{
    param($left, $right)
    $comparison = ([double]$right.TotalSeconds).CompareTo([double]$left.TotalSeconds)
    if ($comparison -ne 0) { return $comparison }
    return [System.StringComparer]::Ordinal.Compare($left.TestName, $right.TestName)
})
$failure.Sort([System.Comparison[object]]{
    param($left, $right)
    $comparison = ([double]$right.FailureRate).CompareTo([double]$left.FailureRate)
    if ($comparison -ne 0) { return $comparison }
    $comparison = ([int]$right.FailureCount).CompareTo([int]$left.FailureCount)
    if ($comparison -ne 0) { return $comparison }
    return [System.StringComparer]::Ordinal.Compare($left.TestName, $right.TestName)
})

$rows = [System.Collections.Generic.List[object]]::new()
foreach ($ranking in @(@{ Kind = 'cost'; Values = $cost }, @{ Kind = 'failure'; Values = $failure })) {
    $limit = if ($Top -eq 0) { $ranking.Values.Count } else { [Math]::Min($Top, $ranking.Values.Count) }
    for ($index = 0; $index -lt $limit; $index++) {
        $value = $ranking.Values[$index]
        $rows.Add([pscustomobject][ordered]@{
            ranking_kind = $ranking.Kind
            rank = $index + 1
            test_name = $value.TestName
            execution_count = $value.ExecutionCount
            failure_count = $value.FailureCount
            failure_rate = $value.FailureRate.ToString('0.000000', $invariant)
            total_seconds = $value.TotalSeconds.ToString('0.000000', $invariant)
            maximum_seconds = $value.MaximumSeconds.ToString('0.000000', $invariant)
        })
    }
}

$rows | ConvertTo-Csv
