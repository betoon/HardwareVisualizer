@echo off
setlocal
set "APP_DIR=%~dp0"
set "APP_EXE=%APP_DIR%bin\x64\Release\net10.0-windows\HardwareVisualizer.exe"
cd /d "%APP_DIR%"
if not exist "%APP_EXE%" goto build
powershell.exe -NoProfile -ExecutionPolicy Bypass -Command "Start-Process -FilePath '%APP_EXE%' -WorkingDirectory '%APP_DIR%' -Verb RunAs"
exit /b %errorlevel%

:build
echo HardwareVisualizer has not been built yet. Building Release version...
dotnet build HardwareVisualizer.csproj -c Release -p:Platform=x64
if errorlevel 1 goto failed
if not exist "%APP_EXE%" goto missing
powershell.exe -NoProfile -ExecutionPolicy Bypass -Command "Start-Process -FilePath '%APP_EXE%' -WorkingDirectory '%APP_DIR%' -Verb RunAs"
exit /b %errorlevel%

:failed
echo.
echo Build failed. The app was not launched.
pause
exit /b 1

:missing
echo Built app was not found at:
echo %APP_EXE%
pause
exit /b 1
