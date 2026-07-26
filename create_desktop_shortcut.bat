@echo off
setlocal
set "APP_DIR=%~dp0"
set "SHORTCUT=%USERPROFILE%\Desktop\Hardware Visualizer.lnk"
powershell -NoProfile -ExecutionPolicy Bypass -Command "$ws=New-Object -ComObject WScript.Shell; $s=$ws.CreateShortcut('%SHORTCUT%'); $s.TargetPath='%APP_DIR%launch_hardware_visualizer.bat'; $s.WorkingDirectory='%APP_DIR%'; $s.IconLocation='%SystemRoot%\System32\shell32.dll,13'; $s.Save()"
if errorlevel 1 (
    echo Could not create desktop shortcut.
    pause
    exit /b 1
)
echo Created desktop shortcut:
echo %SHORTCUT%
pause
