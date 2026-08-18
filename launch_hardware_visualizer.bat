@echo off
setlocal
cd /d "%~dp0"
set "APP_EXE=%~dp0bin\x64\Release\net10.0-windows\HardwareVisualizer.exe"
dotnet build HardwareVisualizer.csproj -c Release -p:Platform=x64
if errorlevel 1 goto failed
if not exist "%APP_EXE%" goto missing
"%APP_EXE%"
exit /b %errorlevel%

:missing
echo Built app was not found at:
echo %APP_EXE%
pause
exit /b 1

:failed
echo.
echo Build failed. The app was not launched.
pause
exit /b 1
