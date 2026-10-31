@echo off
setlocal EnableExtensions
cd /d "%~dp0"
where dotnet >nul 2>nul
if errorlevel 1 (
  echo ERROR: .NET 10 SDK not found. Run Install-Dependencies.cmd.
  exit /b 1
)
dotnet restore Factory.slnx
if errorlevel 1 exit /b 1
dotnet build Factory.slnx -c Release --no-restore
if errorlevel 1 exit /b 1
dotnet run --project tests\Factory.Tests\Factory.Tests.csproj -c Release --no-restore
if errorlevel 1 exit /b 1
dotnet publish src\Factory\Factory.csproj -c Release -r win-x64 --self-contained false -o dist
if errorlevel 1 exit /b 1
if not exist dist\skills mkdir dist\skills
xcopy /E /I /Y skills dist\skills >nul
if errorlevel 1 exit /b 1
if not exist dist\agents mkdir dist\agents
xcopy /E /I /Y agents dist\agents >nul
if errorlevel 1 exit /b 1
echo.
echo Build and tests completed. Start: dist\Factory.exe doctor
exit /b 0
