param(
    [Parameter(Mandatory = $true)][string[]]$Projects,
    [string]$Configuration = 'Debug',
    [string]$RequestKey = ([Guid]::NewGuid().ToString('N')),
    [ValidateRange(1, 2147483)][int]$BusyTimeoutSeconds = 120
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$descriptorPath = Join-Path (Get-Location).Path '.orchestrator-context\worker-build-channel.json'
$descriptorStream = [System.IO.FileStream]::new($descriptorPath, [System.IO.FileMode]::Open,
    [System.IO.FileAccess]::Read, ([System.IO.FileShare]::ReadWrite -bor [System.IO.FileShare]::Delete))
try {
    $descriptorReader = [System.IO.StreamReader]::new($descriptorStream)
    try { $descriptor = $descriptorReader.ReadToEnd() | ConvertFrom-Json }
    finally { $descriptorReader.Dispose() }
}
finally { $descriptorStream.Dispose() }
if ($descriptor.version -ne 1 -or $descriptor.pipe -notmatch '^mcg-worker-build-[a-f0-9]{32}$') {
    throw 'Unsupported managed worker build endpoint descriptor.'
}
# Request bytes and the descriptor are transport only. The host verifies this connection's OS peer,
# canonical dispatch, scope and candidate; only its canonical terminal record is build evidence.
if ($null -eq ('Mcg.WorkerBuildPipeClientV2' -as [type])) {
    Add-Type -TypeDefinition @'
using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
namespace Mcg {
    public static class WorkerBuildPipeClientV2 {
        [DllImport("kernel32.dll", CharSet=CharSet.Unicode, SetLastError=true)]
        private static extern SafePipeHandle CreateFileW(string name, uint access, uint share,
            IntPtr attributes, uint creation, uint flags, IntPtr template);
        [DllImport("kernel32.dll", CharSet=CharSet.Unicode, SetLastError=true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool WaitNamedPipeW(string name, uint timeoutMilliseconds);
        public static NamedPipeClientStream Open(string name, int busyTimeoutSeconds, out double waitedSeconds) {
            waitedSeconds = 0;
            var budgetMilliseconds = busyTimeoutSeconds * 1000;
            var busy = false;
            var elapsed = Stopwatch.StartNew();
            while (true) {
                // A wait signals availability, not ownership; competing clients share this budget.
                if (busy && elapsed.ElapsedMilliseconds >= budgetMilliseconds) {
                    waitedSeconds = elapsed.Elapsed.TotalSeconds;
                    return null;
                }
                // Do not request FILE_CREATE_PIPE_INSTANCE, which GenericWrite would include.
                var handle = CreateFileW(@"\\.\pipe\" + name, 0x0012019b, 0, IntPtr.Zero, 3, 0, IntPtr.Zero);
                if (!handle.IsInvalid) return new NamedPipeClientStream(PipeDirection.InOut, false, true, handle);
                var error = Marshal.GetLastWin32Error();
                handle.Dispose();
                if (error != 231) throw new Win32Exception(error);
                if (!busy) {
                    busy = true;
                    Console.Error.WriteLine("worker_build_channel_wait pipe=\"\\\\.\\pipe\\" + name +
                        "\" busy_timeout_seconds=" + busyTimeoutSeconds);
                }
                var remaining = budgetMilliseconds - elapsed.ElapsedMilliseconds;
                if (remaining <= 0) {
                    waitedSeconds = elapsed.Elapsed.TotalSeconds;
                    return null;
                }
                if (!WaitNamedPipeW(@"\\.\pipe\" + name, (uint)remaining)) {
                    error = Marshal.GetLastWin32Error();
                    if (error != 121 && error != 231) throw new Win32Exception(error);
                }
            }
        }
    }
}
'@
}
[double]$waitedSeconds = 0
$pipe = [Mcg.WorkerBuildPipeClientV2]::Open([string]$descriptor.pipe, $BusyTimeoutSeconds, [ref]$waitedSeconds)
if ($null -eq $pipe) {
    $waitedText = $waitedSeconds.ToString('0.###', [System.Globalization.CultureInfo]::InvariantCulture)
    [Console]::Error.WriteLine('worker_build_channel_busy pipe="\\.\pipe\' + $descriptor.pipe + '" waited_seconds=' + $waitedText)
    exit 1
}
try {
    $encoding = [System.Text.UTF8Encoding]::new($false)
    $writer = [System.IO.StreamWriter]::new($pipe, $encoding, 4096, $true)
    $reader = [System.IO.StreamReader]::new($pipe, $encoding, $false, 4096, $true)
    try {
        $request = @{ Version = 1; RequestKey = $RequestKey; Projects = @($Projects); Configuration = $Configuration }
        $writer.WriteLine(($request | ConvertTo-Json -Compress -Depth 3))
        $writer.Flush()
        $responseText = $reader.ReadLine()
        if ([string]::IsNullOrWhiteSpace($responseText)) { throw 'Managed worker build host closed without a terminal response.' }
        $response = $responseText | ConvertFrom-Json
        Write-Output "worker_build_invocation=$($response.InvocationId); outcome=$($response.Outcome); $($response.Diagnostic)"
        $structuralStatus = 'Unavailable'
        $structuralDiagnostic = 'Legacy host response has no structural evidence.'
        $compositeExit = [int]$response.ExitCode
        $compilerExit = 'unavailable'
        if ($response.PSObject.Properties['CompilerExitCode']) { $compilerExit = $response.CompilerExitCode }
        if ($response.PSObject.Properties['StructuralCheck'] -and $null -ne $response.StructuralCheck) {
            $structuralDiagnostic = 'Malformed structural evidence in host response.'
            $check = $response.StructuralCheck
            $valid = $response.PSObject.Properties['StructuralStatus'] -and
                $response.PSObject.Properties['StructuralDiagnostic']
            foreach ($name in @('InvocationId', 'DispatchId', 'TreeDigest', 'ScopeIdentity', 'PolicyDigest', 'Diagnostic', 'Status')) {
                if (-not $check.PSObject.Properties[$name] -or [string]::IsNullOrWhiteSpace([string]$check.$name)) { $valid = $false }
            }
            if ($valid -and $check.InvocationId -eq $response.InvocationId -and
                $response.StructuralStatus -in @('Passed', 'Violation', 'Unavailable', 'NotApplicable')) {
                $structuralStatus = $response.StructuralStatus
                $structuralDiagnostic = $response.StructuralDiagnostic
            }
            if ($structuralStatus -notin @('Passed', 'NotApplicable')) { $compositeExit = 1 }
        }
        Write-Output "compiler_exit=$compilerExit; structural_status=$structuralStatus; verification_exit=$compositeExit; $structuralDiagnostic"
        exit $compositeExit
    }
    finally { $writer.Dispose(); $reader.Dispose() }
}
finally { $pipe.Dispose() }
