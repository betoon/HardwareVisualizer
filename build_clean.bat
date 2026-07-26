@echo off
setlocal
cd /d "%~dp0"
if exist "bin" rmdir /s /q "bin"
if exist "obj" rmdir /s /q "obj"
dotnet clean HardwareVisualizer.csproj -c Release -p:Platform=x64
dotnet restore HardwareVisualizer.csproj --configfile NuGet.Config
if errorlevel 1 goto failed
dotnet build HardwareVisualizer.csproj -c Release -p:Platform=x64 --no-restore
if errorlevel 1 goto failed
echo.
echo Clean build complete.
pause
exit /b 0
:failed
echo.
echo Clean build failed. Check the messages above.
pause
exit /b 1
