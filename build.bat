@echo off
setlocal
cd /d "%~dp0"
dotnet restore HardwareVisualizer.csproj --configfile NuGet.Config
if errorlevel 1 goto failed
dotnet build HardwareVisualizer.csproj -c Release -p:Platform=x64 --no-restore
if errorlevel 1 goto failed
echo.
echo Build complete.
echo Run launch_hardware_visualizer.bat to start the app.
pause
exit /b 0
:failed
echo.
echo Build failed. Check the messages above.
pause
exit /b 1
