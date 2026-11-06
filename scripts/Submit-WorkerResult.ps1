param(
    [Parameter(Mandatory = $true)][string]$Worktree,
    [Parameter(Mandatory = $true)][string]$Task,
    [Parameter(Mandatory = $true)][string]$Path,
    [Parameter(Mandatory = $true)][string]$InputFile
)

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
    if (-not (Test-Path -LiteralPath $appDll -PathType Leaf)) { throw 'App assembly missing' }
    $arguments = @($appDll, 'submit-worker-result', '--worktree', $Worktree, '--task', $Task, '--path', $Path, '--input', $InputFile)
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
    [Console]::Error.WriteLine('REJECTED: submit tool unavailable')
    $exitCode = 3
} finally {
    if ($null -ne $process) { $process.Dispose() }
}
exit $exitCode
