@echo off
rem Purpose: copy the dotnet build output RoR2LatencyMod.dll (and pdb if present)
rem          into BepInEx\plugins\RoR2LatencyMod\ of the Default profile.
rem          Only plugins\RoR2LatencyMod\ is written; nothing else in the
rem          Default profile, the test profiles or the game directory is
rem          touched, and no existing file is modified or deleted. Missing
rem          directories are created.
rem Usage:   install-to-default.bat [--dry-run]
rem          no argument = copy; --dry-run = print planned copies only.
rem Prereq:  run "dotnet build" in F:\projects\ror2-latency-mod first.
setlocal
set "SRC=%~dp0..\src\RoR2LatencyMod\bin\Debug\netstandard2.1\RoR2LatencyMod.dll"
set "SRC_PDB=%~dp0..\src\RoR2LatencyMod\bin\Debug\netstandard2.1\RoR2LatencyMod.pdb"
set "ROOT=%APPDATA%\r2modmanPlus-local\RiskOfRain2\profiles"
set "PROFILE=Default"
set "DEST=%ROOT%\%PROFILE%\BepInEx\plugins\RoR2LatencyMod"
set "DRY="
if /i "%~1"=="--dry-run" set "DRY=1"

if not exist "%SRC%" (
  echo [ERROR] source dll not found: %SRC%
  echo         run: cd /d F:\projects\ror2-latency-mod ^&^& dotnet build
  exit /b 1
)
if not exist "%ROOT%\%PROFILE%" (
  echo [ERROR] profile not found: %ROOT%\%PROFILE%
  exit /b 1
)
if defined DRY (
  echo [dry-run] copy "%SRC%" -^> "%DEST%\RoR2LatencyMod.dll"
  if exist "%SRC_PDB%" echo [dry-run] copy "%SRC_PDB%" -^> "%DEST%\RoR2LatencyMod.pdb"
  exit /b 0
)
if not exist "%DEST%" mkdir "%DEST%"
if errorlevel 1 (
  echo [ERROR] cannot create directory: %DEST%
  exit /b 1
)
copy /Y "%SRC%" "%DEST%\RoR2LatencyMod.dll" >nul
if errorlevel 1 (
  echo [ERROR] copy failed: %SRC% -^> %DEST%
  exit /b 1
)
if exist "%SRC_PDB%" copy /Y "%SRC_PDB%" "%DEST%\RoR2LatencyMod.pdb" >nul
echo [OK] installed to %PROFILE% ^(%DEST%^)
exit /b 0
