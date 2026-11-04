<#
.SYNOPSIS
  Run the repository's built orchestrator CLI from a stable, repo-bounded script prefix.

.DESCRIPTION
  Use this through scripts\Invoke-RepoScript.ps1 when Codex needs to run foreground
  orchestrator commands without ad-hoc dotnet invocations or repeated permission
  prompts. Arguments are forwarded exactly as PowerShell receives them.

.EXAMPLE
  .\scripts\Invoke-RepoScript.ps1 scripts\Invoke-OrchestratorCommand.ps1 status ddae01e6

.EXAMPLE
  .\scripts\Invoke-RepoScript.ps1 scripts\Invoke-OrchestratorCommand.ps1 attention answer ddae01e6 099efaf9 Model fit evidence is a concise per-role note.
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
    $launcher = Join-Path $repoRoot "mcg-orchestrator.cmd"
    if (-not (Test-Path -LiteralPath $launcher -PathType Leaf)) {
        throw "Orchestrator launcher not found: $launcher"
    }

    # Resolve existing ancestors through filesystem handles before appending the
    # cold-cache suffix. GetFullPath alone cannot detect an escaping junction.
    if ([Environment]::OSVersion.Platform -eq [PlatformID]::Win32NT -and
        -not ('ForegroundRuntimeCachePathAuthority' -as [type])) {
        Add-Type -TypeDefinition @'
using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;
public static class ForegroundRuntimeCachePathAuthority {
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFile(string path, uint access, uint share,
        IntPtr security, uint creation, uint flags, IntPtr template);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetFinalPathNameByHandle(SafeFileHandle handle,
        StringBuilder path, uint size, uint flags);
    public static string Resolve(string path) {
        using (var handle = CreateFile(path, 0, 7, IntPtr.Zero, 3, 0x02000000, IntPtr.Zero)) {
            if (handle.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error());
            var buffer = new StringBuilder(32768);
            uint length = GetFinalPathNameByHandle(handle, buffer, (uint)buffer.Capacity, 0);
            if (length == 0) throw new Win32Exception(Marshal.GetLastWin32Error());
            if (length >= buffer.Capacity) throw new InvalidOperationException("Canonical path is too long.");
            var result = buffer.ToString();
            if (result.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase))
                return @"\\" + result.Substring(8);
            return result.StartsWith(@"\\?\", StringComparison.Ordinal) ? result.Substring(4) : result;
        }
    }
}
'@
    }

    function Resolve-CacheDirectoryPath {
        param([string]$Path)
        $fullPath = [System.IO.Path]::GetFullPath($Path)
        try {
            $item = Get-Item -LiteralPath $fullPath -Force -ErrorAction Stop
        }
        catch [System.Management.Automation.ItemNotFoundException] {
            $parent = Split-Path -Parent $fullPath
            if ([string]::IsNullOrEmpty($parent) -or $parent -eq $fullPath) { throw }
            return Join-Path (Resolve-CacheDirectoryPath $parent) (Split-Path -Leaf $fullPath)
        }
        if (-not $item.PSIsContainer) { throw "Cache ancestor is not a directory: $fullPath" }
        if ([Environment]::OSVersion.Platform -eq [PlatformID]::Win32NT) {
            return [ForegroundRuntimeCachePathAuthority]::Resolve($fullPath)
        }
        $target = $item.ResolveLinkTarget($true)
        if ($null -ne $target) { return $target.FullName }
        $parent = Split-Path -Parent $fullPath
        if ([string]::IsNullOrEmpty($parent) -or $parent -eq $fullPath) { return $fullPath }
        return Join-Path (Resolve-CacheDirectoryPath $parent) $item.Name
    }

    $cacheRoot = Join-Path $repoRoot '.orchestrator/operator-run-cache'
    try {
        $canonicalRepository = Resolve-CacheDirectoryPath $repoRoot
        $canonicalCache = Resolve-CacheDirectoryPath $cacheRoot
        $repositoryPrefix = $canonicalRepository.TrimEnd(
            [System.IO.Path]::DirectorySeparatorChar,
            [System.IO.Path]::AltDirectorySeparatorChar) + [System.IO.Path]::DirectorySeparatorChar
        if (-not $canonicalCache.StartsWith($repositoryPrefix, [System.StringComparison]::OrdinalIgnoreCase)) {
            throw "Canonical cache path escapes repository '$canonicalRepository': $canonicalCache"
        }
    }
    catch {
        throw "Cannot use foreground runtime cache '$cacheRoot'. $($_.Exception.Message)"
    }

    & $launcher --runtime-cache-root $cacheRoot @Arguments
    if ($LASTEXITCODE -is [int] -and $LASTEXITCODE -ne 0) {
        exit $LASTEXITCODE
    }

    return
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
