@echo off
setlocal EnableExtensions
cd /d "%~dp0"
echo Factory - native dependency verification
where winget >nul 2>nul
if errorlevel 1 (
  echo ERROR: winget not found. Install Microsoft App Installer or install prerequisites manually.
  exit /b 1
)
where dotnet >nul 2>nul
if errorlevel 1 goto missing_sdk
dotnet --list-sdks | findstr /B /C:"10." >nul
if errorlevel 1 goto missing_sdk
echo [OK] .NET 10 SDK
goto check_git
:missing_sdk
echo [MISSING] .NET 10 SDK
call :confirm "Install .NET 10 SDK?"
if errorlevel 1 exit /b 2
winget install --id Microsoft.DotNet.SDK.10 --exact --source winget --accept-package-agreements --accept-source-agreements
if errorlevel 1 exit /b 3
:check_git
where git >nul 2>nul
if errorlevel 1 (
  echo [MISSING] Git
  call :confirm "Install Git?"
  if errorlevel 1 exit /b 2
  winget install --id Git.Git --exact --source winget --accept-package-agreements --accept-source-agreements
  if errorlevel 1 exit /b 3
) else (echo [OK] Git)
where codex.exe >nul 2>nul
if not errorlevel 1 goto check_gh
if defined FACTORY_AGENT_EXECUTABLE if exist "%FACTORY_AGENT_EXECUTABLE%" goto check_gh
if exist "%LOCALAPPDATA%\Programs\OpenAI\Codex\bin\codex.exe" goto check_gh
if exist "%USERPROFILE%\.codex\packages\standalone\current\bin\codex.exe" goto check_gh
if exist "%USERPROFILE%\.codex\packages\standalone\current\codex.exe" goto check_gh
echo [MISSING] Native agent CLI (codex.exe)
call :confirm "Install native agent CLI using the official Windows installer?"
if errorlevel 1 exit /b 2
rem Official standalone installer, no Node.js or npm. Execute only after confirmation.
powershell.exe -NoProfile -Command "$ErrorActionPreference='Stop'; $url='https://chatgpt.com/codex/install.ps1'; $script=(Invoke-WebRequest -UseBasicParsing -Uri $url).Content; & ([scriptblock]::Create($script))"
if errorlevel 1 (
  echo ERROR: Native agent CLI installation failed. See official releases for manual installation.
  exit /b 3
)
:check_gh
where gh >nul 2>nul
if errorlevel 1 (
  echo [OPTIONAL] GitHub CLI not found
  call :confirm "Install GitHub CLI?"
  if not errorlevel 1 (
    winget install --id GitHub.cli --exact --source winget --accept-package-agreements --accept-source-agreements
    if errorlevel 1 exit /b 3
  )
) else (echo [OK] GitHub CLI)
echo.
echo Checks complete. Restart PowerShell if PATH was updated.
echo Sign in manually to the agent CLI and to GitHub CLI when needed.
exit /b 0
:confirm
choice /C YN /N /M "%~1 [Y/N] "
if errorlevel 2 exit /b 1
exit /b 0
