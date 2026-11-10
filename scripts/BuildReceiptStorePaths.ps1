# A fallback ledger belongs to Git metadata, not to candidate source content.
# Resolve from the explicit checkout rather than the process's Git environment.
function Get-BuildReceiptStateDirectory {
    param([string]$Root)
    $entry = Join-Path $Root '.git'
    if ([IO.Directory]::Exists($entry)) { $git = $entry }
    elseif ([IO.File]::Exists($entry)) {
        $pointer = [IO.File]::ReadAllText($entry).Trim()
        if (-not $pointer.StartsWith('gitdir: ', [StringComparison]::Ordinal)) { throw 'Invalid Git metadata pointer.' }
        $git = [IO.Path]::GetFullPath($pointer.Substring(8), $Root)
    }
    else { return Join-Path $Root '.orchestrator' } # Non-repository fake build callers.
    $commonFile = Join-Path $git 'commondir'
    if ([IO.File]::Exists($commonFile)) {
        $git = [IO.Path]::GetFullPath([IO.File]::ReadAllText($commonFile).Trim(), $git)
    }
    if (-not [IO.Directory]::Exists((Join-Path $git 'objects')) -or
        -not [IO.Directory]::Exists((Join-Path $git 'refs'))) { throw 'Cannot resolve Git custody for the build receipt ledger.' }
    return Join-Path $git '.orchestrator'
}
