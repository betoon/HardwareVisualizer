@echo off
setlocal
cd /d "%~dp0"
set "APP_EXE=%~dp0bin\x64\Release\net10.0-windows\HardwareVisualizer.exe"
if exist "%APP_EXE%" (
    "%APP_EXE%"
    exit /b %errorlevel%
)
echo Built app was not found. Building and launching from source...
dotnet run --project HardwareVisualizer.csproj -c Release -p:Platform=x64
if errorlevel 1 pause
