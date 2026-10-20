@echo off
cd /d "%~dp0"
taskkill /im S3Explorer.exe /f >nul 2>&1
dotnet build -c Release > build.log 2>&1
echo EXITCODE=%ERRORLEVEL% >> build.log
if exist bin\Release\net8.0-windows\S3Explorer.exe start "" bin\Release\net8.0-windows\S3Explorer.exe
