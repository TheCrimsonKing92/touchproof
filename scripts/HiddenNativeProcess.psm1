Set-StrictMode -Version Latest

# Windows PowerShell 5.1 has no ProcessStartInfo.ArgumentList. Quote for the
# Windows native argv parser, doubling backslashes before quotes and the end.
function ConvertTo-ProcessArgument([string]$Value) {
    '"' + [regex]::Replace($Value, '(\\*)("|$)', {
        param($match)
        $slashes = $match.Groups[1].Value
        if ($match.Groups[2].Value -eq '"') { $slashes + $slashes + '\"' }
        else { $slashes + $slashes }
    }) + '"'
}

function Write-LaunchDiagnostic([string]$Token, [string]$FilePath, [string]$Message) {
    $line = ('{0} path="{1}" error="{2}"' -f $Token, $FilePath, $Message) -replace '[\r\n\u0085\u2028\u2029]', ' '
    if ($line.Length -gt 1024) { $line = $line.Substring(0, 1024) }
    while ([System.Text.Encoding]::UTF8.GetByteCount($line) -gt 1024 -or
        ($line.Length -gt 0 -and [char]::IsHighSurrogate($line[$line.Length - 1]))) {
        $line = $line.Substring(0, $line.Length - 1)
    }
    $bytes = [System.Text.Encoding]::UTF8.GetBytes($line + [Environment]::NewLine)
    $stderr = [Console]::OpenStandardError()
    $stderr.Write($bytes, 0, $bytes.Length)
    $stderr.Flush()
}

function Invoke-HiddenNativeProcess {
    [CmdletBinding()]
    param(
        [AllowEmptyString()][string]$FilePath,
        [AllowEmptyCollection()][AllowEmptyString()][string[]]$ArgumentList = @()
    )

    $process = $null
    $started = $false
    try {
        $start = [System.Diagnostics.ProcessStartInfo]::new()
        $start.FileName = $FilePath
        $start.UseShellExecute = $false
        $start.CreateNoWindow = $true
        $start.RedirectStandardInput = $true
        $start.RedirectStandardOutput = $true
        $start.RedirectStandardError = $true
        $location = Get-Location
        if ($location.Provider.Name -ne 'FileSystem') {
            throw 'The current provider is not a filesystem working directory.'
        }
        $start.WorkingDirectory = $location.ProviderPath
        if ($null -ne $start.PSObject.Properties['ArgumentList']) {
            foreach ($argument in $ArgumentList) { $start.ArgumentList.Add([string]$argument) }
        } else {
            $start.Arguments = (($ArgumentList | ForEach-Object { ConvertTo-ProcessArgument ([string]$_) }) -join ' ')
        }
        $process = [System.Diagnostics.Process]::Start($start)
        $started = $true
        $process.StandardInput.Close()
        # Both bounded byte drains must run before waiting; decoding or routing
        # through PowerShell would alter binary bytes and decorate stderr.
        $stdout = [Console]::OpenStandardOutput()
        $stderr = [Console]::OpenStandardError()
        $outputCopy = $process.StandardOutput.BaseStream.CopyToAsync($stdout, 81920)
        $errorCopy = $process.StandardError.BaseStream.CopyToAsync($stderr, 81920)
        $process.WaitForExit()
        $null = $outputCopy.GetAwaiter().GetResult()
        $null = $errorCopy.GetAwaiter().GetResult()
        $stdout.Flush()
        $stderr.Flush()
        return [int]$process.ExitCode
    } catch {
        $token = if ($started) { 'hidden_native_process_forward_failed' } else { 'hidden_native_process_start_failed' }
        Write-LaunchDiagnostic $token $FilePath $_.Exception.Message
        return [int]1
    } finally {
        if ($null -ne $process) { $process.Dispose() }
    }
}

Export-ModuleMember -Function Invoke-HiddenNativeProcess
