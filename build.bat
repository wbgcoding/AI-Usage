@echo off
setlocal enabledelayedexpansion

rem Single authoritative build. Builds, tests, publishes
rem both architectures, builds the installer and refreshes dist\ - every run fills dist\ fresh.
rem
rem dist\ holds ONLY final artifacts. Publishing writes to build\publish\ first, so a leftover or
rem running file from a previous publish can never masquerade as a fresh artifact.

set ROOT=%~dp0
set DIST=%ROOT%dist
set STAGE=%ROOT%build\publish
set CONFIG=Release

rem An older build kept its settings and history next to the exe, so starting the portable copy out
rem of dist\ left a data folder there. dist\ is cleared further down anyway, but only after a long
rem build and test - clearing this one up front keeps a stale folder out of the way from the start.
if exist "%DIST%\data" rmdir /s /q "%DIST%\data"

rem A test run that was interrupted (window closed, run cancelled) leaves its own testhost.exe
rem behind, and that process keeps the test project's output files open - the next build then fails
rem while copying its own assemblies, with an error that names a process nobody started on purpose.
rem testhost.exe belongs to the test runner and to nothing else, so ending a leftover is safe, but
rem only one that was started from this checkout: a test run of another project is left alone.
rem When there is none, nothing happens and the build carries on.
powershell -NoProfile -Command "Get-Process testhost -ErrorAction SilentlyContinue | Where-Object { $_.Path -and $_.Path.StartsWith('%ROOT%', [StringComparison]::OrdinalIgnoreCase) } | Stop-Process -Force" >nul 2>nul

rem The signing key must never be committable: refuse to build when the default key path is not
rem ignored by git (skipped when git is not installed or this is not a checkout).
where git >nul 2>nul
if not errorlevel 1 if exist "%ROOT%.git" (
    git -C "%ROOT%." check-ignore -q ".signing/ai-usage-update.pem"
    if errorlevel 1 (
        echo ERROR: .signing/ is not in .gitignore, the signing key could be committed.
        goto :error
    )
)

echo === build ===
dotnet build "%ROOT%AiUsage.slnx" -c %CONFIG%
if errorlevel 1 goto :error

echo.
echo === test ===
dotnet test "%ROOT%tests\AiUsage.Tests\AiUsage.Tests.csproj" -c %CONFIG% --no-build
if errorlevel 1 goto :error

rem A copy running OUT OF dist\ holds that one file open, and only that one file: an installed copy
rem elsewhere never blocks anything here. Windows allows renaming a running executable, so the old
rem file is moved aside instead of ending the build - the leftover is deleted on the next run, once
rem nothing holds it any more. Everything else in dist\ is rebuilt below regardless.
del /q "%DIST%\AI-Usage.exe.old-*" >nul 2>nul
if exist "%DIST%\AI-Usage.exe" (
    del /q "%DIST%\AI-Usage.exe" >nul 2>nul
    if exist "%DIST%\AI-Usage.exe" (
        move /y "%DIST%\AI-Usage.exe" "%DIST%\AI-Usage.exe.old-%RANDOM%" >nul
        if errorlevel 1 (
            echo ERROR: the portable copy in dist\ is running and could not be moved aside.
            echo        Close it and run this again.
            goto :error
        )
        echo Note: the running portable copy was moved aside; it keeps running until you close it.
    )
)

rem Version comes from the csproj alone, so it is read back rather than
rem repeated. token 3 is the value: "  <Version>" | "Version" | "1.0.0" | "/Version"
set VERSION=
for /f "tokens=3 delims=<>" %%v in ('findstr /r "<Version>" "%ROOT%src\AiUsage\AiUsage.csproj"') do set VERSION=%%v
if "%VERSION%"=="" (
    echo ERROR: could not read ^<Version^> from the csproj.
    goto :error
)
echo Version: %VERSION%

rem Inlined rather than a CALL'd subroutine: CALL to a label defined later in the file is unreliable
rem after a "for /f" command-substitution loop like the one above it (a known cmd.exe quirk) -
rem GOTO :error, used everywhere else in this file, is unaffected and keeps working normally.
if exist "%STAGE%" rmdir /s /q "%STAGE%"
if exist "%STAGE%" (
    echo ERROR: could not clear %STAGE%
    echo        Something is still using it - close any running app or Explorer window there.
    goto :error
)
mkdir "%STAGE%"
if errorlevel 1 (
    echo ERROR: could not create %STAGE%
    goto :error
)

rem dist\ is emptied entry by entry rather than removed wholesale: a still-running portable copy that
rem was renamed aside above cannot be deleted until it exits, and that one leftover must not end the
rem build. Every artifact this run produces is checked for below, so a stale one can never pass as new.
if exist "%DIST%" (
    del /q "%DIST%\*" >nul 2>nul
    for /d %%s in ("%DIST%\*") do rmdir /s /q "%%s"
) else (
    mkdir "%DIST%"
)
if not exist "%DIST%" (
    echo ERROR: could not create %DIST%
    goto :error
)
for %%f in ("%DIST%\AI-Usage.exe" "%DIST%\SHA256SUMS.txt") do (
    if exist %%f (
        echo ERROR: could not clear %%f
        echo        Something is still using it - close any running app or Explorer window there.
        goto :error
    )
)

for %%a in (x64 arm64) do (
    echo.
    echo === publish win-%%a ===
    rem Self-contained single file: no runtime prerequisite on the target machine.
    rem PublishTrimmed is deliberately NOT used - WPF does not support it.
    rem IncludeNativeLibrariesForSelfExtract is REQUIRED: without it WPF's native DLLs stay next to
    rem the exe and the portable exe dies with DllNotFoundException as soon as it is moved alone.
    rem
    rem EnableCompressionInSingleFile is deliberately NOT used: it deflate-compresses the whole
    rem payload into the exe, which leaves a file with no readable strings and the entropy profile
    rem of a packer - exactly what generic machine-learning malware detections are built to catch,
    rem and the single most reported cause of false positives on self-contained .NET apps.
    dotnet publish "%ROOT%src\AiUsage\AiUsage.csproj" ^
        -c %CONFIG% -r win-%%a --self-contained ^
        -p:PublishSingleFile=true ^
        -p:IncludeNativeLibrariesForSelfExtract=true ^
        -p:DebugType=pdbonly ^
        -o "%STAGE%\win-%%a"
    if errorlevel 1 goto :error

    if not exist "%STAGE%\win-%%a\AI-Usage.exe" (
        echo ERROR: publish produced no executable for win-%%a.
        goto :error
    )

    rem dist\ ships ONE portable exe, and it is x64: Windows 11 is 64-bit only and ARM64 users take
    rem the installer, which bundles both. The arm64 publish still happens above so the installer
    rem can carry it, but it is sourced straight from build\publish\ and never lands in dist\.
    if /i "%%a"=="x64" (
        copy /y "%STAGE%\win-%%a\AI-Usage.exe" "%DIST%\AI-Usage.exe" >nul
        if errorlevel 1 (
            echo ERROR: could not copy the win-%%a executable into dist.
            goto :error
        )
    )

    rem The .pdb deliberately stays in build\publish\ and out of dist\. Symbols embed absolute
    rem source paths from this machine, and dist\ is the folder people receive.
    rem
    rem A second, framework-dependent build used to ship here too, as a smaller download for a
    rem machine that already has the .NET Desktop Runtime. Dropped deliberately: one exe is
    rem simpler to support, and the self-contained size stays as it is on purpose.
)

echo.
echo === installer ===
rem winget installs Inno Setup per USER by default, which puts it nowhere near PATH. Looking only
rem at PATH meant a release could quietly ship without its installer on a machine that has the tool.
set "PF86=%ProgramFiles(x86)%"
set ISCC=
for /f "delims=" %%i in ('where iscc 2^>nul') do set "ISCC=%%i"
if not defined ISCC if exist "%LOCALAPPDATA%\Programs\Inno Setup 6\ISCC.exe" set "ISCC=%LOCALAPPDATA%\Programs\Inno Setup 6\ISCC.exe"
if not defined ISCC if exist "%PF86%\Inno Setup 6\ISCC.exe" set "ISCC=%PF86%\Inno Setup 6\ISCC.exe"
if not defined ISCC if exist "%ProgramFiles%\Inno Setup 6\ISCC.exe" set "ISCC=%ProgramFiles%\Inno Setup 6\ISCC.exe"

if not defined ISCC (
    echo WARNING: Inno Setup ^(ISCC.exe^) not found - skipping the installer.
) else (
    echo Using %ISCC%
    "%ISCC%" /DAppVersion=%VERSION% "%ROOT%installer\AiUsage.iss"
    if errorlevel 1 goto :error
)

echo.
echo === file versions ===
rem The in-app update compares the version resource of the downloaded exe or setup with the running
rem version, so the built files have to carry the csproj version.
powershell -NoProfile -ExecutionPolicy Bypass -File "%ROOT%tools\CheckFileVersions.ps1" -Version "%VERSION%" -Dist "%DIST%"
if errorlevel 1 goto :error

echo.
echo === sign ===
rem The in-app update only installs a file whose detached signature matches the public key built into
rem the program, so every shipped exe gets an <name>.sig next to it. The private key lives in the
rem gitignored .signing folder (or wherever AIUSAGE_SIGNING_KEY points) and is only ever read by the
rem UpdateSigner tool. Without a key the build still succeeds, but the in-app update refuses it.
set "SIGNKEY=%AIUSAGE_SIGNING_KEY%"
if not defined SIGNKEY set "SIGNKEY=%ROOT%.signing\ai-usage-update.pem"
if not exist "%SIGNKEY%" (
    echo WARNING: Unsigned build: in-app update will refuse this release.
) else (
    rem Built once here, then run per file; "dotnet run" would build it again for every exe.
    dotnet build "%ROOT%tools\UpdateSigner\UpdateSigner.csproj" -c %CONFIG%
    if errorlevel 1 goto :error
    for %%f in ("%DIST%\*.exe") do (
        dotnet "%ROOT%tools\UpdateSigner\bin\%CONFIG%\net10.0\UpdateSigner.dll" sign --key "%SIGNKEY%" "%%f"
        if errorlevel 1 goto :error
        if not exist "%%f.sig" (
            echo ERROR: no signature was produced for %%~nxf.
            goto :error
        )
    )
)

echo.
echo === checksums ===
rem One SHA256SUMS.txt over every shipped artifact. Get-FileHash is used rather than certutil,
rem whose output is localized and would break parsing.
powershell -NoProfile -Command ^
    "Get-ChildItem -Path '%DIST%\*.exe' | ForEach-Object { '{0}  {1}' -f (Get-FileHash $_.FullName -Algorithm SHA256).Hash.ToLower(), $_.Name } | Set-Content -Path '%DIST%\SHA256SUMS.txt' -Encoding ascii"
if errorlevel 1 goto :error
if not exist "%DIST%\SHA256SUMS.txt" (
    echo ERROR: no checksum file was produced.
    goto :error
)

echo.
echo Artifacts in %DIST%
type "%DIST%\SHA256SUMS.txt"

rem The fresh copy is started once as a smoke test - it has to come up and still be running a few
rem seconds later, which is the one thing a green test suite cannot prove about the published
rem single-file executable. --second-instance skips the single-instance handover and gives this copy
rem its own data folder and its own browser sign-ins, so the everyday copy's settings, history and
rem sessions are never written from here.
rem
rem The copy is closed again before the build ends: it is a check, not a window to leave behind.
rem The widget hides to the tray on a close request instead of exiting, so CloseMainWindow is only
rem the polite first try and the process is ended outright when it is still there afterwards.
rem AIUSAGE_NOSTART=1 skips the whole check for an automated run.
if not "%AIUSAGE_NOSTART%"=="1" (
    echo.
    echo === start check ===
    powershell -NoProfile -Command ^
        "$p = Start-Process -FilePath '%DIST%\AI-Usage.exe' -ArgumentList '--second-instance' -PassThru;" ^
        "Start-Sleep -Seconds 8;" ^
        "if ($p.HasExited) { Write-Host ('ERROR: the fresh copy exited on its own, code ' + $p.ExitCode + '.'); exit 1 };" ^
        "$null = $p.CloseMainWindow();" ^
        "if (-not $p.WaitForExit(5000)) { $p.Kill(); $null = $p.WaitForExit(5000) };" ^
        "exit 0"
    if errorlevel 1 goto :error
    echo The fresh copy started, stayed up for 8 seconds and was closed again.
)

echo.
echo BUILD OK - artifacts are in dist\.
endlocal
exit /b 0

:error
echo.
echo BUILD FAILED
rem Keep the window open on failure so the error stays readable, but ONLY when build.bat was
rem launched by a double-click in Explorer (which runs it as cmd /c "..."), never in an automated
rem run: an automated caller sets AIUSAGE_NOPAUSE=1, and a run started from an existing console
rem keeps its own window open anyway. CMDCMDLINE carries the /c switch only in the double-click
rem case, and findstr looks for that literal switch.
if not "%AIUSAGE_NOPAUSE%"=="1" (
    echo %CMDCMDLINE% | findstr /i /c:"/c" >nul && pause
)
endlocal
exit /b 1
