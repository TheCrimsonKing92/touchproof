# Scope-tool outputs are immutable after publication. Only the publishing phase holds
# the per-entry file lock; source hashing and compilation never serialize helpers.
function Get-WorkerBuildScopeToolCacheRoot {
    if (-not [string]::IsNullOrWhiteSpace($env:MCG_WORKER_BUILD_SCOPE_TOOL_CACHE)) {
        $override = $env:MCG_WORKER_BUILD_SCOPE_TOOL_CACHE
        if (-not [IO.Path]::IsPathRooted($override)) { $override = Join-Path (Get-Location).Path $override }
        return [IO.Path]::GetFullPath($override)
    }
    $local = $env:LOCALAPPDATA
    if ([string]::IsNullOrWhiteSpace($local)) { $local = [Environment]::GetFolderPath('LocalApplicationData') }
    if ([string]::IsNullOrWhiteSpace($local)) { throw 'Scope tool cache requires local application data.' }
    return [IO.Path]::GetFullPath((Join-Path $local 'Mcg.AgentOrchestrator\tool-cache\worker-build-scope'))
}

function Invoke-WorkerBuildScopeGit {
    param([string]$Root, [string[]]$Arguments, [string]$InputText = '')
    $info = [Diagnostics.ProcessStartInfo]::new()
    $info.FileName = 'git'
    $info.WorkingDirectory = $Root
    $info.UseShellExecute = $false
    $info.CreateNoWindow = $true
    $info.RedirectStandardInput = $true
    $info.RedirectStandardOutput = $true
    $info.RedirectStandardError = $true
    $info.StandardInputEncoding = [Text.UTF8Encoding]::new($false)
    $info.StandardOutputEncoding = [Text.UTF8Encoding]::new($false)
    $info.StandardErrorEncoding = [Text.UTF8Encoding]::new($false)
    # Quote using the Windows argv convention, including trailing backslashes.
    $info.Arguments = (@('-c', 'core.fsmonitor=false', '-c', 'gc.auto=0') + $Arguments | ForEach-Object {
        '"' + [regex]::Replace([regex]::Replace($_, '(\\*)"', '$1$1\"'), '(\\+)$', '$1$1') + '"'
    }) -join ' '
    foreach ($name in @('GIT_DIR', 'GIT_WORK_TREE', 'GIT_COMMON_DIR', 'GIT_INDEX_FILE', 'GIT_OBJECT_DIRECTORY', 'GIT_ALTERNATE_OBJECT_DIRECTORIES')) {
        $info.EnvironmentVariables.Remove($name)
    }
    $info.EnvironmentVariables['GIT_OPTIONAL_LOCKS'] = '0'
    $process = [Diagnostics.Process]::new()
    $process.StartInfo = $info
    try {
        if (-not $process.Start()) { throw 'Could not start scope tool Git process.' }
        $stdout = $process.StandardOutput.ReadToEndAsync()
        $stderr = $process.StandardError.ReadToEndAsync()
        $process.StandardInput.Write($InputText)
        $process.StandardInput.Close()
        if (-not $process.WaitForExit(60000)) {
            $process.Kill()
            $process.WaitForExit()
            throw 'Scope tool Git process did not exit within the hang guard.'
        }
        $output = $stdout.GetAwaiter().GetResult()
        $errorText = $stderr.GetAwaiter().GetResult()
        if ($process.ExitCode -ne 0) { throw "Scope tool Git command failed: $($Arguments[0]) exit=$($process.ExitCode) $errorText" }
        return $output
    }
    finally { $process.Dispose() }
}

function Get-WorkerBuildScopeToolDigest {
    param([string]$ToolProject, [string]$WorkingDirectory)
    $project = [IO.Path]::GetFullPath($ToolProject)
    $root = (Invoke-WorkerBuildScopeGit -Root ([IO.Path]::GetDirectoryName($project)) -Arguments @('rev-parse', '--show-toplevel')).Trim()
    $root = [IO.Path]::GetFullPath($root).TrimEnd('\', '/')
    $prefix = $root + [IO.Path]::DirectorySeparatorChar
    $projects = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    $paths = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    $pending = [Collections.Generic.Stack[string]]::new()
    $pending.Push($project)
    while ($pending.Count -gt 0) {
        $current = $pending.Pop()
        if (-not $current.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase)) { throw "Scope tool project is outside home repository: $current" }
        if (-not $projects.Add($current)) { continue }
        $directory = [IO.Path]::GetDirectoryName($current)
        [void]$paths.Add($(if ($directory -eq $root) { '.' } else { $directory.Substring($prefix.Length).Replace('\', '/') }))
        [xml]$xml = [IO.File]::ReadAllText($current)
        foreach ($reference in $xml.SelectNodes('//*[local-name()="ProjectReference"]')) {
            $include = $reference.GetAttribute('Include')
            if ([string]::IsNullOrWhiteSpace($include) -or $include -match '[$@*?;]') { throw "Unsupported scope tool ProjectReference: $include" }
            $pending.Push([IO.Path]::GetFullPath((Join-Path $directory $include)))
        }
        while ($directory.Length -ge $root.Length) {
            foreach ($leaf in @('Directory.Build.props', 'Directory.Build.targets', 'Directory.Packages.props', 'Directory.Build.rsp', 'global.json')) {
                $relative = if ($directory -eq $root) { $leaf } else { $directory.Substring($prefix.Length).Replace('\', '/') + '/' + $leaf }
                [void]$paths.Add($relative)
            }
            if ($directory -eq $root) { break }
            $directory = [IO.Path]::GetDirectoryName($directory)
        }
    }
    $specs = @($paths | ForEach-Object { ':(literal)' + $_ })
    $files = [Collections.Generic.Dictionary[string,string]]::new([StringComparer]::Ordinal)
    $listing = Invoke-WorkerBuildScopeGit -Root $root -Arguments (@('ls-files', '--stage', '-z', '--') + $specs)
    foreach ($record in $listing.Split([char]0)) {
        if ($record.Length -eq 0) { continue }
        if ($record -notmatch '^(\d+) ([a-f0-9]+) 0\t(.+)$') { throw 'Unmerged or invalid scope tool index entry.' }
        $files[$Matches[3]] = $Matches[1] + "`t" + $Matches[2]
    }
    $status = Invoke-WorkerBuildScopeGit -Root $root -Arguments (@('status', '--porcelain=v2', '-z', '--untracked-files=all', '--') + $specs)
    $records = $status.Split([char]0)
    $changed = [Collections.Generic.Dictionary[string,string]]::new([StringComparer]::Ordinal)
    for ($i = 0; $i -lt $records.Length; $i++) {
        $record = $records[$i]
        if ($record.Length -eq 0) { continue }
        if ($record.StartsWith('? ')) { $changed[$record.Substring(2)] = '100644'; continue }
        if ($record.StartsWith('1 ') -or $record.StartsWith('2 ')) {
            $rename = $record.StartsWith('2 ')
            $parts = $record -split ' ', $(if ($rename) { 10 } else { 9 })
            $path = $parts[$parts.Length - 1]
            if ($parts[5] -eq '000000') { [void]$files.Remove($path) }
            else { $changed[$path] = $parts[5] }
            if ($rename) { $i++ } # original path is a separate NUL-delimited record
        }
        else { throw 'Unsupported scope tool working-tree status.' }
    }
    if ($changed.Count -gt 0) {
        $names = [string[]]@($changed.Keys)
        [Array]::Sort($names, [StringComparer]::Ordinal)
        # Git's stdin-paths accepts C-quoted paths, so whitespace and non-ASCII
        # filenames cannot split the input or depend on the shell code page.
        $inputLines = @($names | ForEach-Object { '"' + $_.Replace('\', '\\').Replace('"', '\"').Replace("`n", '\n').Replace("`r", '\r').Replace("`t", '\t') + '"' })
        $hashes = (Invoke-WorkerBuildScopeGit -Root $root -Arguments @('hash-object', '--stdin-paths') -InputText (($inputLines -join "`n") + "`n")).TrimEnd("`r", "`n") -split '\r?\n'
        if ($hashes.Count -ne $names.Length) { throw 'Scope tool content identity count mismatch.' }
        for ($i = 0; $i -lt $names.Length; $i++) {
            if ($hashes[$i] -notmatch '^[a-f0-9]{40}([a-f0-9]{24})?$') { throw 'Invalid scope tool content identity.' }
            $files[$names[$i]] = $changed[$names[$i]] + "`t" + $hashes[$i]
        }
    }
    Push-Location -LiteralPath $WorkingDirectory
    try {
        $sdk = @(& dotnet --version 2>&1)
        if ($LASTEXITCODE -ne 0 -or $sdk.Count -ne 1 -or [string]::IsNullOrWhiteSpace([string]$sdk[0])) { throw 'Scope tool SDK version unavailable.' }
    }
    finally { Pop-Location }
    $builder = [Text.StringBuilder]::new("wbst-digest-v1`nsdk`t$($sdk[0])`n")
    $ordered = [string[]]@($files.Keys)
    [Array]::Sort($ordered, [StringComparer]::Ordinal)
    foreach ($path in $ordered) {
        # Length-prefix paths to avoid ambiguity even when filenames contain newlines.
        [void]$builder.Append("f`t$($files[$path])`t$($path.Length):$path`n")
    }
    $sha = [Security.Cryptography.SHA256]::Create()
    try { return ([BitConverter]::ToString($sha.ComputeHash([Text.Encoding]::UTF8.GetBytes($builder.ToString())))).Replace('-', '').ToLowerInvariant() }
    finally { $sha.Dispose() }
}

function Test-WorkerBuildScopeToolCacheEntry {
    param([string]$Entry, [string]$Digest)
    try {
        $manifest = Get-Content -LiteralPath (Join-Path $Entry 'manifest.json') -Raw -ErrorAction Stop | ConvertFrom-Json -ErrorAction Stop
        $dll = Join-Path $Entry 'WorkerBuildScopeTool.dll'
        return ($manifest.sourceDigest -is [string] -and $manifest.dllSha256 -is [string] -and
            $manifest.sourceDigest -ceq $Digest -and
            $manifest.dllSha256 -cmatch '^[a-f0-9]{64}$' -and
            (Test-Path -LiteralPath (Join-Path $Entry 'WorkerBuildScopeTool.runtimeconfig.json') -PathType Leaf) -and
            (Test-Path -LiteralPath (Join-Path $Entry 'WorkerBuildScopeTool.deps.json') -PathType Leaf) -and
            (Get-FileHash -LiteralPath $dll -Algorithm SHA256 -ErrorAction Stop).Hash.ToLowerInvariant() -ceq $manifest.dllSha256)
    }
    catch { return $false }
}

function Resolve-WorkerBuildScopeTool {
    param([string]$ToolProject, [string]$WorkingDirectory, [string]$BuildRoot, [string]$BuildBin, [string]$BootstrapLog)
    $cacheRoot = Get-WorkerBuildScopeToolCacheRoot
    [void][IO.Directory]::CreateDirectory($cacheRoot)
    $elapsed = 0L
    for ($attempt = 0; $attempt -lt 2; $attempt++) {
        $digest = Get-WorkerBuildScopeToolDigest -ToolProject $ToolProject -WorkingDirectory $WorkingDirectory
        $entry = Join-Path $cacheRoot $digest
        $line = "scope-tool: cache=hit digest=$($digest.Substring(0, 12))"
        if ($attempt -eq 0 -and (Test-WorkerBuildScopeToolCacheEntry -Entry $entry -Digest $digest)) {
            return [pscustomobject]@{ Dll = Join-Path $entry 'WorkerBuildScopeTool.dll'; Line = $line; Failure = $null }
        }
        $watch = [Diagnostics.Stopwatch]::StartNew()
        & dotnet build $ToolProject --nologo --configuration Debug --verbosity minimal --artifacts-path $BuildRoot --output $BuildBin -maxcpucount:1 -p:BuildInParallel=false -tl:off *> $BootstrapLog
        $buildExit = $LASTEXITCODE
        $watch.Stop()
        $elapsed += $watch.ElapsedMilliseconds
        $line = "scope-tool: cache=miss digest=$($digest.Substring(0, 12)) build_ms=$elapsed"
        if ($buildExit -ne 0) {
            return [pscustomobject]@{ Dll = $null; Line = $line; Failure = "worker build scope tool bootstrap failed; complete-log: $BootstrapLog" }
        }
        if ((Get-WorkerBuildScopeToolDigest -ToolProject $ToolProject -WorkingDirectory $WorkingDirectory) -cne $digest) {
            if ($attempt -eq 0) {
                # A fresh attempt cannot inherit stale outputs. Both generations
                # remain under process temp and its existing final cleanup.
                $BuildRoot = Join-Path $BuildRoot 'retry'
                $BuildBin = Join-Path $BuildRoot 'bin'
                continue
            }
            return [pscustomobject]@{ Dll = $null; Line = $line; Failure = 'scope tool sources changed during bootstrap' }
        }
        $dll = Join-Path $BuildBin 'WorkerBuildScopeTool.dll'
        if (-not (Test-Path -LiteralPath $dll -PathType Leaf)) {
            return [pscustomobject]@{ Dll = $dll; Line = $line; Failure = $null }
        }
        $token = "$PID-$([Guid]::NewGuid().ToString('N'))"
        $staging = Join-Path $cacheRoot ".stage-$token"
        $retired = Join-Path $cacheRoot ".retired-$token"
        $publishLock = $null
        try {
            [void][IO.Directory]::CreateDirectory($staging)
            Get-ChildItem -LiteralPath $BuildBin -Force | Copy-Item -Destination $staging -Recurse -Force
            @{ sourceDigest = $digest; dllSha256 = (Get-FileHash -LiteralPath $dll -Algorithm SHA256).Hash.ToLowerInvariant() } |
                ConvertTo-Json -Compress | Set-Content -LiteralPath (Join-Path $staging 'manifest.json') -Encoding UTF8
            if (-not (Test-WorkerBuildScopeToolCacheEntry -Entry $staging -Digest $digest)) { throw 'Scope tool staging output failed validation.' }
            try {
                # Lock files persist; deleting one after release would allow a second
                # publisher to lock a different file identity for the same digest.
                $publishLock = [IO.File]::Open((Join-Path $cacheRoot ".publish-$digest.lock"), 'OpenOrCreate', 'ReadWrite', 'None')
                if (-not (Test-WorkerBuildScopeToolCacheEntry -Entry $entry -Digest $digest)) {
                    if (Test-Path -LiteralPath $entry) { [IO.Directory]::Move($entry, $retired) }
                    [IO.Directory]::Move($staging, $entry)
                }
            }
            catch [IO.IOException] { } # Another publisher or publication-only failure: use validated winner or local output.
            catch [UnauthorizedAccessException] { }
            if (Test-WorkerBuildScopeToolCacheEntry -Entry $entry -Digest $digest) { $dll = Join-Path $entry 'WorkerBuildScopeTool.dll' }
        }
        finally {
            if ($null -ne $publishLock) { $publishLock.Dispose() }
            # Both paths are unique children of the explicit cache root, owned here.
            foreach ($temporary in @($staging, $retired)) {
                if (-not [IO.Path]::GetFullPath($temporary).StartsWith($cacheRoot.TrimEnd('\', '/') + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
                    throw 'Scope tool cleanup path escaped the cache root.'
                }
                if (Test-Path -LiteralPath $temporary) { Remove-Item -LiteralPath $temporary -Recurse -Force }
            }
        }
        return [pscustomobject]@{ Dll = $dll; Line = $line; Failure = $null }
    }
}
