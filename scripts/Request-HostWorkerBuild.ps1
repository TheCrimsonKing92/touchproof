param(
    [Parameter(Mandatory = $true)][string[]]$Projects,
    [string]$Configuration = 'Debug',
    [string]$RequestKey = ([Guid]::NewGuid().ToString('N')),
    [ValidateRange(1, 2147483)][int]$BusyTimeoutSeconds = 120
)

# Windows PowerShell serializes module-loading progress to stderr under
# -EncodedCommand. Keep helper progress out of the verb's output contract.
$ProgressPreference = 'SilentlyContinue'
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

# Windows PowerShell 5.1 has no ProcessStartInfo.ArgumentList.
function ConvertTo-ProcessArgument([string]$Value) {
    '"' + [regex]::Replace($Value, '(\\*)("|$)', {
        param($match)
        $slashes = $match.Groups[1].Value
        if ($match.Groups[2].Value -eq '"') { $slashes + $slashes + '\"' }
        else { $slashes + $slashes }
    }) + '"'
}

$appDll = ''
$process = $null
try {
    $repoRoot = Split-Path -Parent $PSScriptRoot
    $appOutput = if ([string]::IsNullOrWhiteSpace($env:MCG_ORCHESTRATOR_APP_OUTPUT)) {
        Join-Path $repoRoot 'src\Mcg.AgentOrchestrator.App\bin\Debug\net10.0'
    } else {
        [System.IO.Path]::GetFullPath($env:MCG_ORCHESTRATOR_APP_OUTPUT)
    }
    $appDll = Join-Path $appOutput 'Mcg.AgentOrchestrator.App.dll'
    if ([string]::IsNullOrWhiteSpace($env:MCG_ORCHESTRATOR_APP_OUTPUT) -and
        -not (Test-Path -LiteralPath $appDll -PathType Leaf) -and
        -not [string]::IsNullOrWhiteSpace($env:MCG_ORCHESTRATOR_HOME)) {
        $homeOutput = Join-Path ([System.IO.Path]::GetFullPath($env:MCG_ORCHESTRATOR_HOME.Trim())) 'src\Mcg.AgentOrchestrator.App\bin\Debug\net10.0'
        $appDll = Join-Path $homeOutput 'Mcg.AgentOrchestrator.App.dll'
    }
    if (-not (Test-Path -LiteralPath $appDll -PathType Leaf)) { [Console]::Error.WriteLine('worker_build_client_missing app="' + $appDll + '"'); exit 1 }
    $arguments = @($appDll, 'worker-build-request') + @($Projects) + @('--configuration', $Configuration, '--request-key', $RequestKey, '--busy-timeout-seconds', $BusyTimeoutSeconds.ToString([System.Globalization.CultureInfo]::InvariantCulture))
    $start = [System.Diagnostics.ProcessStartInfo]::new()
    $start.FileName = 'dotnet'
    $start.UseShellExecute = $false
    $start.CreateNoWindow = $true
    $start.RedirectStandardInput = $true
    $start.RedirectStandardOutput = $true
    $start.RedirectStandardError = $true
    $start.WorkingDirectory = (Get-Location).ProviderPath
    if ($null -ne $start.PSObject.Properties['ArgumentList']) {
        foreach ($argument in $arguments) { $start.ArgumentList.Add([string]$argument) }
    } else {
        $start.Arguments = (($arguments | ForEach-Object { ConvertTo-ProcessArgument ([string]$_) }) -join ' ')
    }
    $process = [System.Diagnostics.Process]::Start($start)
    $process.StandardInput.Close()
    # Start both drains before waiting. Copy bytes so PowerShell cannot decorate
    # stderr or change the child's encoding, and redirected parents keep output.
    $outputCopy = $process.StandardOutput.BaseStream.CopyToAsync([Console]::OpenStandardOutput())
    $errorCopy = $process.StandardError.BaseStream.CopyToAsync([Console]::OpenStandardError())
    $process.WaitForExit()
    $null = $outputCopy.GetAwaiter().GetResult()
    $null = $errorCopy.GetAwaiter().GetResult()
    $exitCode = $process.ExitCode
} catch {
    [Console]::Error.WriteLine('worker_build_channel_unavailable app="' + $appDll + '"')
    $exitCode = 1
} finally {
    if ($null -ne $process) { $process.Dispose() }
}
exit $exitCode
