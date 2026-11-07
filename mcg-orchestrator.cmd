@echo off
setlocal DisableDelayedExpansion
set "MCG_CLI_LAUNCHER_STARTED_UTC="
set "MCG_CLI_LAUNCHER_PATH=fresh"
set "ROOT=%~dp0"
set "MCG_ORCHESTRATOR_REPOSITORY_ROOT=%ROOT%"
set "LOCK_DIR=%ROOT%.build-lock"
set "LOCK_STALE_SECONDS=60"
set "APP_PROJECT=%ROOT%src\Mcg.AgentOrchestrator.App\Mcg.AgentOrchestrator.App.csproj"
set "APP_DLL=%ROOT%src\Mcg.AgentOrchestrator.App\bin\Debug\net10.0\Mcg.AgentOrchestrator.App.dll"
set "APP_HEAD=%ROOT%src\Mcg.AgentOrchestrator.App\bin\Debug\net10.0\Mcg.AgentOrchestrator.App.dll.git-head"
set "APP_ASSETS=%ROOT%src\Mcg.AgentOrchestrator.App\obj\project.assets.json"
set "CORE_ASSETS=%ROOT%src\Mcg.AgentOrchestrator.Core\obj\project.assets.json"
set "INFRASTRUCTURE_ASSETS=%ROOT%src\Mcg.AgentOrchestrator.Infrastructure\obj\project.assets.json"
set "PROVIDERS_ASSETS=%ROOT%src\Mcg.AgentOrchestrator.Infrastructure.Providers\obj\project.assets.json"
set "DOTNET_HOST=dotnet"
if defined MCG_ORCHESTRATOR_DOTNET_PATH set "DOTNET_HOST=%MCG_ORCHESTRATOR_DOTNET_PATH%"

:: Intentionally inherited by the entire launcher process tree (CLI, builds, gates, and their children) to
:: limit aggregate memory pressure on many-core hosts. The CLI's Workstation/non-concurrent GC mode is scoped
:: to its runtimeconfig by Mcg.AgentOrchestrator.App.csproj, and MSBuild node reuse is disabled repo-wide by
:: Directory.Build.rsp; do not export those settings here because unrelated descendants own their runtimes.
set "DOTNET_GCConserveMemory=7"

:: Launcher options are a prefix only. Keep the omitted-option fast path and its
:: original %* forwarding untouched. Explicit selection always verifies freshness.
set "RUNTIME_CACHE_ROOT="
if "%~1"=="--runtime-cache-root" goto selected_cache_path
:: The home publisher projects its adopted record into two batch-readable lines.
if "%MCG_ORCHESTRATOR_LAUNCHER_INTREE%"=="1" goto intree_path
set "ADOPTION_POINTER=%ROOT%.orchestrator\continuity\home-build-adoption.cmd.txt"
if not exist "%ADOPTION_POINTER%" goto intree_path
set "ADOPTED_COMMIT="
set "ADOPTED_DLL="
(
    set /p "ADOPTED_COMMIT="
    set /p "ADOPTED_DLL="
) < "%ADOPTION_POINTER%"
if not defined ADOPTED_COMMIT goto intree_path
if not defined ADOPTED_DLL goto intree_path
if not exist "%ADOPTED_DLL%" goto intree_path
set "CURRENT_HEAD="
for /f "usebackq delims=" %%H in (`git -C "%ROOT%." rev-parse HEAD 2^>nul ^|^| echo HEAD_UNAVAILABLE`) do set "CURRENT_HEAD=%%H"
if not defined CURRENT_HEAD goto intree_path
if "%CURRENT_HEAD%"=="HEAD_UNAVAILABLE" goto intree_path
if not "%ADOPTED_COMMIT%"=="%CURRENT_HEAD%" goto intree_path
git -C "%ROOT%." diff --quiet HEAD -- src Directory.Build.props Directory.Build.rsp global.json >nul 2>&1
if not "%ERRORLEVEL%"=="0" goto intree_path
set "MCG_CLI_LAUNCHER_PATH=fast"
"%DOTNET_HOST%" "%ADOPTED_DLL%" %*
exit /b %ERRORLEVEL%

:selected_cache_path
if "%~2"=="" goto invalid_cache_option
set "RUNTIME_CACHE_ROOT=%~2"
powershell -NoProfile -ExecutionPolicy Bypass -File "%ROOT%scripts\resolve-run-dir.ps1" "%APP_DLL%" -CacheRoot "%RUNTIME_CACHE_ROOT%" -ValidateCacheRootOnly
if errorlevel 1 exit /b 1
shift
shift
if "%~1"=="--runtime-cache-root" goto invalid_cache_option
:: Batch parameters split unquoted = , and ;, so never rebuild the native tail
:: from %1/SHIFT. Slice only the two launcher tokens from the original %* text.
:: Capture with delayed expansion disabled to preserve literal exclamation marks.
set NATIVE_ARGS=%*
setlocal EnableDelayedExpansion
set "PREFIX_TOKENS=0"
set "PREFIX_IN_TOKEN="
set "PREFIX_QUOTED="
:strip_launcher_prefix
if not defined NATIVE_ARGS goto launcher_prefix_stripped
set "PREFIX_CHAR=!NATIVE_ARGS:~0,1!"
set "NATIVE_ARGS=!NATIVE_ARGS:~1!"
set "PREFIX_NONQUOTE=!PREFIX_CHAR:"=!"
if not defined PREFIX_NONQUOTE goto launcher_prefix_quote
if defined PREFIX_QUOTED goto strip_launcher_prefix
if "!PREFIX_CHAR!"==" " goto launcher_prefix_separator
if "!PREFIX_CHAR!"=="	" goto launcher_prefix_separator
set "PREFIX_IN_TOKEN=1"
goto strip_launcher_prefix
:launcher_prefix_quote
set "PREFIX_IN_TOKEN=1"
if defined PREFIX_QUOTED (set "PREFIX_QUOTED=") else set "PREFIX_QUOTED=1"
goto strip_launcher_prefix
:launcher_prefix_separator
if not defined PREFIX_IN_TOKEN goto strip_launcher_prefix
set "PREFIX_IN_TOKEN="
set /a PREFIX_TOKENS+=1
if !PREFIX_TOKENS! lss 2 goto strip_launcher_prefix
:launcher_prefix_stripped
:: All subsequent launcher work and native execution retain their existing
:: expansion rules. The nested SETLOCAL scopes unwind when the launcher exits.
setlocal DisableDelayedExpansion
goto intree_path

:invalid_cache_option
echo ERROR: --runtime-cache-root requires one absolute directory path and may appear only once before the native command. >&2
exit /b 1

:intree_path
for /f "delims=" %%T in ('powershell -NoProfile -Command "[DateTimeOffset]::UtcNow.ToString('o')" 2^>nul') do set "MCG_CLI_LAUNCHER_STARTED_UTC=%%T"
:: Reclaim a dead-owner or age-stale lock left by a prior crashed invocation.
:: A lock older than LOCK_STALE_SECONDS is stale even if owner.pid is present.
:: A younger lock is also reclaimed when owner.pid is missing after a short recheck or its owner is gone.
if exist "%LOCK_DIR%" powershell -NoProfile -Command "$ld='%LOCK_DIR%';$threshold=%LOCK_STALE_SECONDS%;$re=$false;if(Test-Path -LiteralPath $ld){try{$age=((Get-Date)-(Get-Item -LiteralPath $ld -ErrorAction Stop).LastWriteTime).TotalSeconds;if($age -gt $threshold){$re=$true}}catch{$re=$true};if(-not $re){$pf=Join-Path $ld 'owner.pid';if(-not (Test-Path -LiteralPath $pf)){Start-Sleep -Milliseconds 500};if(Test-Path -LiteralPath $pf){try{$op=[int]((Get-Content -LiteralPath $pf -Raw -ErrorAction Stop).Trim());if(-not (Get-Process -Id $op -ErrorAction SilentlyContinue)){$re=$true}}catch{$re=$true}}else{$re=$true}}};if($re){Remove-Item -LiteralPath $ld -Recurse -Force -ErrorAction SilentlyContinue}"

:: Up-to-date check -- generated bin/obj files are excluded so restore output cannot make a
:: current App.dll look stale. The shared checker still fails closed for a missing/mismatched
:: git marker and for newer checked-in source files.
powershell -NoProfile -ExecutionPolicy Bypass -File "%ROOT%scripts\Test-OrchestratorArtifactFreshness.ps1" -RepositoryRoot "%ROOT%." -ArtifactPath "%APP_DLL%" -MarkerPath "%APP_HEAD%" "%ROOT%src" "%ROOT%Directory.Build.props" "%ROOT%Directory.Build.rsp" "%ROOT%global.json"
set "FRESH_EXIT=%ERRORLEVEL%"
if "%FRESH_EXIT%"=="0" goto run_app

:: Acquire build lock -- mkdir is atomic on NTFS; spin/retry up to 30 s
set "LOCK_TRIES=0"
:try_lock
mkdir "%LOCK_DIR%" 2>nul
if not errorlevel 1 goto locked
set /a LOCK_TRIES+=1
if %LOCK_TRIES% geq 30 (
    echo ERROR: could not acquire build lock after 30 s >&2
    exit /b 1
)
ping -n 2 -w 500 127.0.0.1 >nul 2>&1
goto try_lock

:locked
:: Record our PID so concurrent invocations can test our liveness before reclaiming.
for /f "delims=" %%a in ('powershell -NoProfile -Command "(Get-Process -Id $PID).Parent.Id"') do set "MYPID=%%a"
if defined MYPID echo %MYPID%>"%LOCK_DIR%\owner.pid"

if not exist "%APP_ASSETS%" goto missing_build_assets
if not exist "%CORE_ASSETS%" goto missing_build_assets
if not exist "%INFRASTRUCTURE_ASSETS%" goto missing_build_assets
if not exist "%PROVIDERS_ASSETS%" goto missing_build_assets

set "BUILD_LOG=%TEMP%\mcg-build-%RANDOM%.log"
call "%DOTNET_HOST%" build "%APP_PROJECT%" --no-restore --nologo -v quiet -clp:ErrorsOnly -p:UseSharedCompilation=false -p:McgIsolatedArtifactsPath= >"%BUILD_LOG%" 2>&1
set BUILD_EXIT=%ERRORLEVEL%
rmdir /s /q "%LOCK_DIR%" 2>nul

if %BUILD_EXIT% neq 0 goto build_failed
powershell -NoProfile -ExecutionPolicy Bypass -File "%ROOT%scripts\Update-AppDllGitHeadMarker.ps1" "%ROOT%." "%APP_HEAD%"
del "%BUILD_LOG%" 2>nul
set "MCG_CLI_LAUNCHER_PATH=built"

:run_app
:: Run from a verified complete closure under the selected cache base/v2/<digest>.
:: Omitted cache selection retains %TEMP%\mcg-run.
:: The resolver reuses or repairs the current digest and removes other digest directories only when
:: no process holds their App.dll open. A live run keeps its directory intact while the in-tree output
:: can be rebuilt. Native SQLite assets are required; fail if the build output cannot supply them.
set "RUNDIR="
if defined RUNTIME_CACHE_ROOT goto resolve_selected_cache
for /f "usebackq delims=" %%R in (`powershell -NoProfile -ExecutionPolicy Bypass -File "%ROOT%scripts\resolve-run-dir.ps1" "%APP_DLL%"`) do set "RUNDIR=%%R"
goto resolved_cache
:resolve_selected_cache
for /f "usebackq delims=" %%R in (`powershell -NoProfile -ExecutionPolicy Bypass -File "%ROOT%scripts\resolve-run-dir.ps1" "%APP_DLL%" -CacheRoot "%RUNTIME_CACHE_ROOT%"`) do set "RUNDIR=%%R"
:resolved_cache
if not defined RUNDIR (
    echo ERROR: could not prepare isolated orchestrator run directory; see resolver error above and retry after repair >&2
    exit /b 1
)
if defined RUNTIME_CACHE_ROOT goto run_selected_cache
"%DOTNET_HOST%" "%RUNDIR%\Mcg.AgentOrchestrator.App.dll" %*
exit /b %ERRORLEVEL%
:run_selected_cache
"%DOTNET_HOST%" "%RUNDIR%\Mcg.AgentOrchestrator.App.dll" %NATIVE_ARGS%
exit /b %ERRORLEVEL%

:build_failed
if not exist "%APP_DLL%" goto show_build_error
powershell -NoProfile -Command "try{$f=[IO.File]::Open('%APP_DLL%',[IO.FileMode]::Open,[IO.FileAccess]::Write);$f.Close();exit 1}catch{exit 0}"
if not errorlevel 1 (
    echo ERROR: build failed: App.dll is locked by a running orchestrator instance; stop it and retry >&2
    del "%BUILD_LOG%" 2>nul
    exit /b %BUILD_EXIT%
)
:show_build_error
type "%BUILD_LOG%" >&2
echo. >&2
echo ERROR: dotnet build failed >&2
del "%BUILD_LOG%" 2>nul
exit /b %BUILD_EXIT%

:missing_build_assets
rmdir /s /q "%LOCK_DIR%" 2>nul
echo ERROR: App artifact is missing or stale and no-restored build assets are unavailable; run .\scripts\Invoke-RepoScript.ps1 scripts\Invoke-PackageBootstrap.ps1, then retry. >&2
exit /b 1
