@echo off
setlocal
cd /d "%~dp0"
python build_msi_hardware_visualizer.py
if errorlevel 1 goto failed
echo.
echo MSI build step complete.
pause
exit /b 0
:failed
echo.
echo MSI build failed. Check the messages above.
pause
exit /b 1
