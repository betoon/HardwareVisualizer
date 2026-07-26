@echo off
set "APP_DIR=%~dp0"
set "APP_EXE=%APP_DIR%bin\x64\Release\net10.0-windows\HardwareVisualizer.exe"
if exist "%APP_EXE%" (
    powershell.exe -NoProfile -ExecutionPolicy Bypass -Command "Start-Process -FilePath '%APP_EXE%' -Verb RunAs"
    exit /b
)
powershell.exe -NoProfile -ExecutionPolicy Bypass -Command "Start-Process -FilePath 'cmd.exe' -ArgumentList '/c cd /d ""%APP_DIR%"" && dotnet run --project HardwareVisualizer.csproj -c Release -p:Platform=x64' -Verb RunAs"
