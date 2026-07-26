Hardware Visualizer

This is a separate dashboard app that reads sensors directly through LibreHardwareMonitorLib.

How to use:
1. Run build.bat once.
2. Run launch_hardware_visualizer_as_admin.bat for best hardware sensor access.
3. Use launch_hardware_visualizer.bat if you only want normal user mode.
4. Optional: run create_desktop_shortcut.bat to add a Desktop shortcut.
5. Optional: run build_msi.bat to create a self-contained MSI installer.

MSI installer:
- Product name: HardwareVisualizer V1.0 Brian E. Toon 2026.
- The installer asks for the installation directory.
- The installer asks whether to create a Desktop shortcut and always creates a Start Menu shortcut.
- The MSI output is written to installer\output when WiX Toolset is installed.

Recommended sensor setup:
1. Start LibreHardwareMonitor.
2. Enable Options > Remote Web Server > Run.
3. Start Hardware Visualizer.

Useful buttons:
- Save Report writes an HTML report to Documents\HardwareVisualizerReports.
- Export CSV writes rolling session readings to Documents\HardwareVisualizerReports.
- Check Setup verifies LibreHardwareMonitor web data is reachable.
- Start Baseline and Compare let you capture a before/after workload report.
- Mini Monitor opens a tiny always-on-top CPU/GPU/memory/network window.
- Workload presets adjust Watch/Hot thresholds for Idle, Photo Editing, Gaming, Rendering, or Stress Test.

The app is intentionally separate from System Monitoring.
LibreHardwareMonitor source is used from:
..\github\LibreHardwareMonitor-master

LibreHardwareMonitor is MPL-2.0 licensed. Keep its license notices with redistributed builds.
