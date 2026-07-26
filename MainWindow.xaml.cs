using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using System.Text.Json;
using System.Net.Http;
using System.Net.NetworkInformation;
using System.IO;
using System.Text;
using System.Diagnostics;
using LibreHardwareMonitor.Hardware;

namespace HardwareVisualizer;

public partial class MainWindow : Window
{
    private readonly Computer computer;
    private readonly DispatcherTimer refreshTimer;
    private readonly Dictionary<string, CategoryView> categoryViews = new();
    private readonly Dictionary<string, TextBlock> summaryValues = new();
    private readonly Dictionary<string, Queue<double>> history = new();
    private readonly List<SensorLogEntry> sessionLog = new();
    private readonly HashSet<string> pinnedSensors = new();
    private readonly List<AlertEntry> alertLog = new();
    private readonly HashSet<string> activeAlerts = new();
    private AnalysisView? analysisView;
    private NetworkDashboardView? networkView;
    private MiniMonitorWindow? miniMonitor;
    private List<SensorReading>? baselineReadings;
    private DateTime? baselineTime;
    private List<SensorReading> currentReadings = [];
    private bool loadingSettings;
    private string selectedTheme = "Cool";
    private string selectedWorkload = "Idle";
    private string selectedTemperatureUnit = "C";
    private bool compactMode;
    private string sensorSource = "starting";
    private double cpuWatchThreshold = 75;
    private double cpuHotThreshold = 90;
    private double gpuWatchThreshold = 75;
    private double gpuHotThreshold = 90;
    private string accentColor = "#6ee7f9";
    private string panelColor = "#171d26";
    private string borderColor = "#2f3b4c";
    private static readonly string SettingsPath = System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "HardwareVisualizer",
        "settings.json");

    private static readonly string[] CategoryOrder =
    [
        "Overview",
        "Temperatures",
        "Load",
        "Clocks",
        "Voltage",
        "Power",
        "Fans",
        "Memory/Data",
        "Drives",
        "Network",
        "Sensor Types",
        "All"
    ];

    public MainWindow()
    {
        InitializeComponent();
        LoadSettings();

        computer = new Computer
        {
            IsBatteryEnabled = true,
            IsControllerEnabled = true,
            IsCpuEnabled = true,
            IsGpuEnabled = true,
            IsMemoryEnabled = true,
            IsMotherboardEnabled = true,
            IsNetworkEnabled = true,
            IsPowerMonitorEnabled = true,
            IsPsuEnabled = true,
            IsStorageEnabled = true
        };

        BuildStaticUi();
        Loaded += (_, _) => StartSensors();
        Closed += (_, _) =>
        {
            SaveSettings();
            computer.Close();
        };

        refreshTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        refreshTimer.Tick += (_, _) => RefreshSensors();
    }

    private void BuildStaticUi()
    {
        foreach (string title in new[] { "CPU Temp", "GPU Temp", "CPU Load", "Memory", "Drive", "Fan", "Clock", "Voltage", "Power", "Network" })
            SummaryCards.Children.Add(CreateSummaryCard(title));

        analysisView = CreateAnalysisView();
        SensorTabs.Items.Add(new TabItem { Header = "Analysis", Content = Scroll(analysisView.Root) });
        networkView = CreateNetworkDashboardView();
        SensorTabs.Items.Add(new TabItem { Header = "Network Dashboard", Content = Scroll(networkView.Root) });

        foreach (string category in CategoryOrder)
        {
            CategoryView view = CreateCategoryView(category);
            SensorTabs.Items.Add(new TabItem { Header = category, Content = view.Tabs });
            categoryViews[category] = view;
        }

        ApplyCompactMode();
    }

    private CategoryView CreateCategoryView(string category)
    {
        var tabs = new TabControl { Margin = new Thickness(0, 10, 0, 0) };
        var barsPanel = new StackPanel { Margin = new Thickness(12) };
        var cardsPanel = new StackPanel { Margin = new Thickness(12) };
        var verticalBarsPanel = new StackPanel { Margin = new Thickness(12) };
        var heatPanel = new StackPanel { Margin = new Thickness(12) };
        var radarPanel = new StackPanel { Margin = new Thickness(12) };
        var matrixPanel = new StackPanel { Margin = new Thickness(12) };
        var treemapPanel = new StackPanel { Margin = new Thickness(12) };
        var moversPanel = new StackPanel { Margin = new Thickness(12) };
        var historyPanel = new StackPanel { Margin = new Thickness(12) };

        tabs.Items.Add(new TabItem { Header = "Bars", Content = Scroll(barsPanel) });
        tabs.Items.Add(new TabItem { Header = "Cards", Content = Scroll(cardsPanel) });
        tabs.Items.Add(new TabItem { Header = "Vertical Bars", Content = Scroll(verticalBarsPanel) });
        tabs.Items.Add(new TabItem { Header = "Heat Map", Content = Scroll(heatPanel) });
        tabs.Items.Add(new TabItem { Header = "Radar", Content = Scroll(radarPanel) });
        tabs.Items.Add(new TabItem { Header = "Matrix", Content = Scroll(matrixPanel) });
        tabs.Items.Add(new TabItem { Header = "Treemap", Content = Scroll(treemapPanel) });
        tabs.Items.Add(new TabItem { Header = "Top Movers", Content = Scroll(moversPanel) });
        tabs.Items.Add(new TabItem { Header = "History", Content = Scroll(historyPanel) });

        return new CategoryView(tabs, barsPanel, cardsPanel, verticalBarsPanel, heatPanel, radarPanel, matrixPanel, treemapPanel, moversPanel, historyPanel);
    }

    private static AnalysisView CreateAnalysisView()
    {
        var root = new StackPanel { Margin = new Thickness(12) };
        var healthSection = new StackPanel { Margin = new Thickness(0, 0, 0, 12) };
        var healthCards = new WrapPanel { Margin = new Thickness(0, 0, 0, 12) };
        var scorePanel = new StackPanel { Margin = new Thickness(0, 0, 0, 12) };
        var problemPanel = new StackPanel { Margin = new Thickness(0, 0, 0, 12) };
        var trendPanel = new StackPanel { Margin = new Thickness(0, 0, 0, 12) };
        var bottleneckPanel = new StackPanel { Margin = new Thickness(0, 0, 0, 12) };
        var thermalPanel = new StackPanel { Margin = new Thickness(0, 0, 0, 12) };
        var timelinePanel = new StackPanel { Margin = new Thickness(0, 0, 0, 12) };
        var changesPanel = new StackPanel { Margin = new Thickness(0, 0, 0, 12) };
        var alertPanel = new StackPanel { Margin = new Thickness(0, 0, 0, 12) };
        var topDevicesPanel = new StackPanel { Margin = new Thickness(0, 0, 0, 12) };
        var recommendationPanel = new StackPanel { Margin = new Thickness(0, 0, 0, 12) };

        AddSectionTitle(healthSection, "Health Overview");
        healthSection.Children.Add(healthCards);
        root.Children.Add(healthSection);
        root.Children.Add(scorePanel);
        root.Children.Add(problemPanel);
        root.Children.Add(trendPanel);
        root.Children.Add(bottleneckPanel);
        root.Children.Add(thermalPanel);
        root.Children.Add(timelinePanel);
        root.Children.Add(changesPanel);
        root.Children.Add(alertPanel);
        root.Children.Add(topDevicesPanel);
        root.Children.Add(recommendationPanel);

        return new AnalysisView(root, healthCards, scorePanel, problemPanel, trendPanel, bottleneckPanel, thermalPanel, timelinePanel, changesPanel, alertPanel, topDevicesPanel, recommendationPanel);
    }

    private static NetworkDashboardView CreateNetworkDashboardView()
    {
        var root = new StackPanel { Margin = new Thickness(12) };
        var statusPanel = new StackPanel { Margin = new Thickness(0, 0, 0, 12) };
        var historyPanel = new StackPanel { Margin = new Thickness(0, 0, 0, 12) };
        var adapterPanel = new StackPanel { Margin = new Thickness(0, 0, 0, 12) };
        var radarPanel = new StackPanel { Margin = new Thickness(0, 0, 0, 12) };
        AddSectionTitle(statusPanel, "Network Status");
        AddSectionTitle(historyPanel, "Upload / Download History");
        AddSectionTitle(adapterPanel, "Current Adapter");
        AddSectionTitle(radarPanel, "Network Radar Profile");
        root.Children.Add(statusPanel);
        root.Children.Add(historyPanel);
        root.Children.Add(adapterPanel);
        root.Children.Add(radarPanel);
        return new NetworkDashboardView(root, statusPanel, historyPanel, adapterPanel, radarPanel);
    }

    private static ScrollViewer Scroll(object content)
    {
        return new ScrollViewer
        {
            Content = content,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled
        };
    }

    private Border CreateSummaryCard(string title)
    {
        var value = new TextBlock
        {
            Text = "--",
            FontSize = 24,
            FontWeight = FontWeights.Bold,
            Margin = new Thickness(0, 4, 0, 0)
        };
        summaryValues[title] = value;

        var panel = new StackPanel();
        panel.Children.Add(new TextBlock { Text = title, Foreground = Brush("#9aa8b8"), FontWeight = FontWeights.SemiBold });
        panel.Children.Add(value);

        return new Border
        {
            Width = 180,
            Margin = new Thickness(0, 0, 10, 0),
            Padding = new Thickness(14),
            Background = Brush("#171d26"),
            BorderBrush = Brush("#2f3b4c"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Child = panel
        };
    }

    private void LoadSettings()
    {
        loadingSettings = true;
        try
        {
            if (File.Exists(SettingsPath))
            {
                HardwareVisualizerSettings? settings = JsonSerializer.Deserialize<HardwareVisualizerSettings>(File.ReadAllText(SettingsPath));
                if (settings is not null)
                {
                    selectedTheme = string.IsNullOrWhiteSpace(settings.Theme) ? "Cool" : settings.Theme;
                    selectedWorkload = string.IsNullOrWhiteSpace(settings.Workload) ? "Idle" : settings.Workload;
                    selectedTemperatureUnit = NormalizeTemperatureUnit(settings.TemperatureUnit);
                    cpuWatchThreshold = settings.CpuWatch <= 0 ? 75 : settings.CpuWatch;
                    cpuHotThreshold = settings.CpuHot <= 0 ? 90 : settings.CpuHot;
                    gpuWatchThreshold = settings.GpuWatch <= 0 ? 75 : settings.GpuWatch;
                    gpuHotThreshold = settings.GpuHot <= 0 ? 90 : settings.GpuHot;
                    QuietModeCheckBox.IsChecked = settings.QuietMode;
                    compactMode = settings.CompactMode;
                    CompactModeCheckBox.IsChecked = compactMode;
                    SettingsExpander.IsExpanded = settings.SettingsExpanded;
                }
            }

            ApplySettingsToControls();
            MainWindowDisplay.TemperatureUnit = selectedTemperatureUnit;
            ApplyTheme(selectedTheme);
        }
        catch
        {
            ApplySettingsToControls();
            MainWindowDisplay.TemperatureUnit = "C";
            ApplyTheme("Cool");
        }
        finally
        {
            loadingSettings = false;
        }
    }

    private void SaveSettings()
    {
        try
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(SettingsPath)!);
            var settings = new HardwareVisualizerSettings(
                selectedTheme,
                selectedWorkload,
                selectedTemperatureUnit,
                QuietModeCheckBox.IsChecked == true,
                cpuWatchThreshold,
                cpuHotThreshold,
                gpuWatchThreshold,
                gpuHotThreshold,
                CompactModeCheckBox.IsChecked == true,
                SettingsExpander.IsExpanded);
            File.WriteAllText(SettingsPath, JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch
        {
            // Settings are convenience only. Do not interrupt the dashboard if saving fails.
        }
    }

    private void ApplySettingsToControls()
    {
        SelectComboItem(ThemeComboBox, selectedTheme);
        SelectComboItem(WorkloadComboBox, selectedWorkload);
        SelectComboItem(TemperatureUnitComboBox, TemperatureUnitDisplayName(selectedTemperatureUnit));
        CpuWatchBox.Text = cpuWatchThreshold.ToString("0");
        CpuHotBox.Text = cpuHotThreshold.ToString("0");
        GpuWatchBox.Text = gpuWatchThreshold.ToString("0");
        GpuHotBox.Text = gpuHotThreshold.ToString("0");
        CompactModeCheckBox.IsChecked = compactMode;
    }

    private static void SelectComboItem(ComboBox comboBox, string value)
    {
        foreach (ComboBoxItem item in comboBox.Items.OfType<ComboBoxItem>())
        {
            if (string.Equals(item.Content?.ToString(), value, StringComparison.OrdinalIgnoreCase))
            {
                comboBox.SelectedItem = item;
                return;
            }
        }
    }

    private void ApplyTheme(string theme)
    {
        selectedTheme = theme;
        (accentColor, panelColor, borderColor) = theme switch
        {
            "Contrast" => ("#ffffff", "#05070a", "#ffffff"),
            "Thermal" => ("#ff9f43", "#1d1410", "#7a3b18"),
            _ => ("#6ee7f9", "#171d26", "#2f3b4c")
        };
        miniMonitor?.ApplyTheme(accentColor, panelColor, borderColor);
    }

    private void StartSensors()
    {
        try
        {
            computer.Open();
            StatusText.Text = "Sensor engine running. Direct LibreHardwareMonitorLib mode.";
            refreshTimer.Start();
            RefreshSensors();
        }
        catch (Exception exc)
        {
            StatusText.Text = "Could not start sensor engine. Try running as Administrator.";
            MessageBox.Show(this, exc.Message, "Hardware Visualizer", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void RefreshNow_Click(object sender, RoutedEventArgs e)
    {
        RefreshSensors();
    }

    private void SaveReport_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            string folder = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "HardwareVisualizerReports");
            Directory.CreateDirectory(folder);
            string path = System.IO.Path.Combine(folder, $"hardware_report_{DateTime.Now:yyyyMMdd_HHmmss}.html");
            File.WriteAllText(path, BuildHtmlReport(), Encoding.UTF8);
            StatusText.Text = $"Saved report: {path}";
            if (MessageBox.Show(this, $"Saved report:\n{path}\n\nOpen it now?", "Hardware Visualizer", MessageBoxButton.YesNo, MessageBoxImage.Information) == MessageBoxResult.Yes)
                Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch (Exception exc)
        {
            MessageBox.Show(this, exc.Message, "Could not save report", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void ExportCsv_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            string folder = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "HardwareVisualizerReports");
            Directory.CreateDirectory(folder);
            string path = System.IO.Path.Combine(folder, $"hardware_session_{DateTime.Now:yyyyMMdd_HHmmss}.csv");
            File.WriteAllText(path, BuildCsvExport(), Encoding.UTF8);
            StatusText.Text = $"Exported CSV: {path}";
            MessageBox.Show(this, $"Exported CSV:\n{path}", "Hardware Visualizer", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception exc)
        {
            MessageBox.Show(this, exc.Message, "Could not export CSV", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void CheckSetup_Click(object sender, RoutedEventArgs e)
    {
        List<SensorReading> direct = ReadSensorsDirect();
        List<SensorReading> web = ReadSensorsFromWebJson();
        string status =
            $"Direct standalone sensors: {direct.Count}\n" +
            $"LibreHardwareMonitor web sensors: {web.Count}\n\n" +
            (direct.Count > 0
                ? "Standalone mode is available. The app will use direct LibreHardwareMonitorLib first."
                : "Standalone direct mode did not return sensors. Try running as Administrator. If that still misses data, the MSI can bundle LibreHardwareMonitor as a helper.");
        MessageBox.Show(this, status, "Setup Check", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private void DirectCheck_Click(object sender, RoutedEventArgs e)
    {
        List<SensorReading> direct = ReadSensorsDirect();
        if (direct.Count == 0)
        {
            MessageBox.Show(this, "Direct standalone mode returned 0 sensors. Try running Hardware Visualizer as Administrator.", "Direct Sensor Check", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        string types = string.Join(", ", direct.GroupBy(reading => reading.Type).OrderByDescending(group => group.Count()).Select(group => $"{group.Key}: {group.Count()}"));
        string hardware = string.Join("\n", direct.GroupBy(reading => reading.Hardware).OrderByDescending(group => group.Count()).Take(12).Select(group => $"{group.Key}: {group.Count()}"));
        MessageBox.Show(this, $"Direct standalone mode found {direct.Count} sensors.\n\nTypes:\n{types}\n\nTop hardware:\n{hardware}", "Direct Sensor Check", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private void CompareSources_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            List<SensorReading> direct = ReadSensorsDirect();
            List<SensorReading> web = ReadSensorsFromWebJson();
            string folder = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "HardwareVisualizerReports");
            Directory.CreateDirectory(folder);
            string path = System.IO.Path.Combine(folder, $"source_compare_{DateTime.Now:yyyyMMdd_HHmmss}.txt");
            File.WriteAllText(path, BuildSourceCompareReport(direct, web), Encoding.UTF8);
            if (MessageBox.Show(this, $"Saved source comparison:\n{path}\n\nOpen it now?", "Compare Sources", MessageBoxButton.YesNo, MessageBoxImage.Information) == MessageBoxResult.Yes)
                Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch (Exception exc)
        {
            MessageBox.Show(this, exc.Message, "Could not compare sources", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void StartBaseline_Click(object sender, RoutedEventArgs e)
    {
        baselineReadings = currentReadings.ToList();
        baselineTime = DateTime.Now;
        StatusText.Text = $"Baseline captured at {baselineTime.Value:h:mm:ss tt}.";
    }

    private void CompareBaseline_Click(object sender, RoutedEventArgs e)
    {
        if (baselineReadings is null || baselineTime is null)
        {
            MessageBox.Show(this, "Click Start Baseline first, then run your workload and click Compare.", "No Baseline Yet", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        List<AnalysisFinding> changes = CompareToBaseline();
        var builder = new StringBuilder();
        builder.AppendLine($"Baseline: {baselineTime.Value:g}");
        builder.AppendLine($"Now: {DateTime.Now:g}");
        builder.AppendLine();
        if (changes.Count == 0)
            builder.AppendLine("No major changes were detected.");
        else
            foreach (AnalysisFinding change in changes)
                builder.AppendLine($"{change.Title}: {change.Detail} ({change.Value})");
        MessageBox.Show(this, builder.ToString(), "Before / After Compare", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private void MiniMonitor_Click(object sender, RoutedEventArgs e)
    {
        if (miniMonitor is null || !miniMonitor.IsVisible)
        {
            miniMonitor = new MiniMonitorWindow();
            miniMonitor.ApplyTheme(accentColor, panelColor, borderColor);
            miniMonitor.Show();
        }
        else
        {
            miniMonitor.Close();
            miniMonitor = null;
        }
        UpdateMiniMonitor(currentReadings);
    }

    private void QuietMode_Changed(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded || refreshTimer is null || loadingSettings)
            return;
        refreshTimer.Interval = QuietModeCheckBox.IsChecked == true ? TimeSpan.FromSeconds(8) : TimeSpan.FromSeconds(2);
        StatusText.Text = QuietModeCheckBox.IsChecked == true ? "Quiet mode enabled. Refreshing more slowly." : "Quiet mode disabled. Refreshing normally.";
        SaveSettings();
    }

    private void CompactMode_Changed(object sender, RoutedEventArgs e)
    {
        compactMode = CompactModeCheckBox.IsChecked == true;
        ApplyCompactMode();
        if (!IsLoaded || loadingSettings)
            return;
        StatusText.Text = compactMode ? "Compact mode enabled. Showing key dashboard tabs." : "Compact mode disabled. Showing all tabs.";
        SaveSettings();
    }

    private void ApplyCompactMode()
    {
        foreach (TabItem tab in SensorTabs.Items.OfType<TabItem>())
        {
            string header = tab.Header?.ToString() ?? "";
            bool keepVisible = header is "Analysis" or "Network Dashboard" or "Overview" or "Network";
            tab.Visibility = !compactMode || keepVisible ? Visibility.Visible : Visibility.Collapsed;
        }

        if (compactMode && SensorTabs.SelectedItem is TabItem selected && selected.Visibility != Visibility.Visible)
            SensorTabs.SelectedIndex = 0;
    }

    private void ThemeComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        string theme = ((sender as ComboBox)?.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "Cool";
        ApplyTheme(theme);
        if (!IsLoaded || loadingSettings)
            return;
        SaveSettings();
        RefreshSensors();
    }

    private void ThresholdBox_LostFocus(object sender, RoutedEventArgs e)
    {
        cpuWatchThreshold = ParseThreshold(CpuWatchBox.Text, 75);
        cpuHotThreshold = ParseThreshold(CpuHotBox.Text, 90);
        gpuWatchThreshold = ParseThreshold(GpuWatchBox.Text, 75);
        gpuHotThreshold = ParseThreshold(GpuHotBox.Text, 90);
        SaveSettings();
        RefreshSensors();
    }

    private void TemperatureUnitComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        string unitName = ((sender as ComboBox)?.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "Celsius";
        selectedTemperatureUnit = unitName.StartsWith("F", StringComparison.OrdinalIgnoreCase) ? "F" : "C";
        MainWindowDisplay.TemperatureUnit = selectedTemperatureUnit;
        if (!IsLoaded || loadingSettings)
            return;
        SaveSettings();
        RefreshSensors();
    }

    private void WorkloadComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        string mode = ((sender as ComboBox)?.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "Idle";
        selectedWorkload = mode;
        (cpuWatchThreshold, cpuHotThreshold, gpuWatchThreshold, gpuHotThreshold) = mode switch
        {
            "Photo" => (78, 92, 78, 90),
            "Gaming" => (82, 95, 82, 95),
            "Render" => (84, 96, 82, 94),
            "Stress" => (88, 98, 88, 98),
            _ => (70, 85, 70, 85)
        };
        if (!IsLoaded || loadingSettings)
            return;
        CpuWatchBox.Text = cpuWatchThreshold.ToString("0");
        CpuHotBox.Text = cpuHotThreshold.ToString("0");
        GpuWatchBox.Text = gpuWatchThreshold.ToString("0");
        GpuHotBox.Text = gpuHotThreshold.ToString("0");
        SaveSettings();
        RefreshSensors();
    }

    private void ResetSettings_Click(object sender, RoutedEventArgs e)
    {
        selectedTheme = "Cool";
        selectedWorkload = "Idle";
        cpuWatchThreshold = 75;
        cpuHotThreshold = 90;
        gpuWatchThreshold = 75;
        gpuHotThreshold = 90;
        selectedTemperatureUnit = "C";
        MainWindowDisplay.TemperatureUnit = selectedTemperatureUnit;
        compactMode = false;
        QuietModeCheckBox.IsChecked = false;
        CompactModeCheckBox.IsChecked = false;
        ApplySettingsToControls();
        ApplyCompactMode();
        ApplyTheme(selectedTheme);
        SaveSettings();
        RefreshSensors();
    }

    private void RefreshSensors()
    {
        try
        {
            List<SensorReading> readings = ReadSensors();
            currentReadings = readings;
            UpdateHistory(readings);
            AppendSessionLog(readings);
            UpdateAlertLog(readings);
            UpdateSummary(readings);
            UpdateAnalysisTab(readings);
            UpdateNetworkDashboard(readings);
            UpdateMiniMonitor(readings);
            UpdateCategoryTabs(readings);
            LastUpdateText.Text = DateTime.Now.ToString("h:mm:ss tt");
            StatusText.Text = $"Loaded {readings.Count} live sensor reading(s) from {sensorSource}.";
        }
        catch (Exception exc)
        {
            StatusText.Text = $"Refresh failed: {exc.Message}";
        }
    }

    private List<SensorReading> ReadSensors()
    {
        var readings = ReadSensorsDirect();
        if (readings.Count > 0)
        {
            sensorSource = "direct LibreHardwareMonitorLib";
            return readings;
        }

        readings = ReadSensorsFromWebJson();
        if (readings.Count > 0)
            sensorSource = "LibreHardwareMonitor web JSON fallback";
        else
            sensorSource = "no available source. Try running as Administrator, or start LibreHardwareMonitor with Remote Web Server enabled.";
        return readings;
    }

    private List<SensorReading> ReadSensorsDirect()
    {
        var readings = new List<SensorReading>();
        try
        {
            foreach (IHardware hardware in computer.Hardware)
                UpdateHardwareTree(hardware);

            computer.Accept(new SensorVisitor(sensor =>
            {
                if (!sensor.Value.HasValue)
                    return;

                IHardware hardware = sensor.Hardware;
                string path = HardwarePath(hardware);
                readings.Add(new SensorReading(
                    path,
                    sensor.Name,
                    sensor.SensorType.ToString(),
                    sensor.Value.Value,
                    UnitFor(sensor.SensorType),
                    sensor.Identifier.ToString(),
                    hardware.HardwareType.ToString()
                ));
            }));
        }
        catch
        {
            return [];
        }

        readings.AddRange(ReadWindowsNetworkSensors());

        return readings
            .GroupBy(reading => reading.Identifier)
            .Select(group => group.First())
            .ToList();
    }

    private static List<SensorReading> ReadWindowsNetworkSensors()
    {
        var readings = new List<SensorReading>();
        try
        {
            foreach (NetworkInterface adapter in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (!IsUsableNetworkAdapter(adapter))
                    continue;

                IPv4InterfaceStatistics stats = adapter.GetIPv4Statistics();
                string id = Uri.EscapeDataString(adapter.Id);
                string hardware = adapter.Name;
                readings.Add(new SensorReading(hardware, "Data Uploaded", "Data", stats.BytesSent / 1073741824.0, "GB", $"/nic/windows/{id}/data/uploaded", "Network"));
                readings.Add(new SensorReading(hardware, "Data Downloaded", "Data", stats.BytesReceived / 1073741824.0, "GB", $"/nic/windows/{id}/data/downloaded", "Network"));
                readings.Add(new SensorReading(hardware, "Upload Speed", "Throughput", 0, "B/s", $"/nic/windows/{id}/throughput/upload", "Network"));
                readings.Add(new SensorReading(hardware, "Download Speed", "Throughput", 0, "B/s", $"/nic/windows/{id}/throughput/download", "Network"));
                double utilization = adapter.Speed > 0 ? Math.Min(100, ((stats.BytesSent + stats.BytesReceived) * 8.0 / adapter.Speed) % 100) : 0;
                if (double.IsNaN(utilization) || double.IsInfinity(utilization))
                    utilization = 0;
                readings.Add(new SensorReading(hardware, "Network Utilization", "Load", utilization, "%", $"/nic/windows/{id}/load/utilization", "Network"));
            }
        }
        catch
        {
            return readings;
        }

        return readings;
    }

    private static bool IsUsableNetworkAdapter(NetworkInterface adapter)
    {
        return adapter.NetworkInterfaceType is not NetworkInterfaceType.Loopback
               and not NetworkInterfaceType.Tunnel
               and not NetworkInterfaceType.Unknown
               && !string.IsNullOrWhiteSpace(adapter.Name);
    }

    private static void UpdateHardwareTree(IHardware hardware)
    {
        try
        {
            hardware.Update();
        }
        catch
        {
            // Individual hardware controllers can fail independently; keep collecting everything else.
        }

        foreach (IHardware child in hardware.SubHardware)
            UpdateHardwareTree(child);
    }

    private static List<SensorReading> ReadSensorsFromWebJson()
    {
        try
        {
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
            string payload = client.GetStringAsync("http://127.0.0.1:8085/data.json").GetAwaiter().GetResult();
            using JsonDocument document = JsonDocument.Parse(payload);
            var readings = new List<SensorReading>();
            WalkWebNode(document.RootElement, readings, []);
            return readings;
        }
        catch
        {
            return [];
        }
    }

    private static void WalkWebNode(JsonElement node, List<SensorReading> readings, List<string> parents)
    {
        if (node.ValueKind != JsonValueKind.Object)
            return;

        string text = JsonString(node, "Text");
        string sensorId = JsonString(node, "SensorId");
        string type = JsonString(node, "Type");
        string hardwareId = JsonString(node, "HardwareId");
        bool isSensor = !string.IsNullOrWhiteSpace(sensorId) && !string.IsNullOrWhiteSpace(type);
        if (isSensor)
        {
            double? value = JsonNumber(node, "RawValue") ?? JsonNumber(node, "Value");
            if (value.HasValue)
            {
                string hardware = string.Join(" / ", parents.Where(part => !string.IsNullOrWhiteSpace(part)));
                readings.Add(new SensorReading(
                    hardware,
                    text,
                    type,
                    value.Value,
                    UnitFor(type, JsonString(node, "Value")),
                    sensorId,
                    HardwareTypeFromWebPath(sensorId, hardwareId, hardware)
                ));
            }
        }

        var nextParents = new List<string>(parents);
        if (!isSensor && !string.IsNullOrWhiteSpace(text) && text != "Sensor")
            nextParents.Add(text);

        if (node.TryGetProperty("Children", out JsonElement children) && children.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement child in children.EnumerateArray())
                WalkWebNode(child, readings, nextParents);
        }
    }

    private static string JsonString(JsonElement node, string property)
    {
        if (!node.TryGetProperty(property, out JsonElement value))
            return "";
        return value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : value.ToString();
    }

    private static double? JsonNumber(JsonElement node, string property)
    {
        string text = JsonString(node, property);
        if (string.IsNullOrWhiteSpace(text))
            return null;
        var match = System.Text.RegularExpressions.Regex.Match(text, @"-?\d+(?:\.\d+)?");
        if (!match.Success)
            return null;
        return double.TryParse(match.Value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double result)
            ? result
            : null;
    }

    private static string HardwareTypeFromWebPath(string sensorId, string hardwareId, string hardware)
    {
        string text = $"{sensorId} {hardwareId} {hardware}".ToLowerInvariant();
        if (text.Contains("cpu")) return "Cpu";
        if (text.Contains("gpu")) return "Gpu";
        if (text.Contains("hdd") || text.Contains("ssd") || text.Contains("nvme") || text.Contains("storage")) return "Storage";
        if (text.Contains("network") || text.Contains("ethernet") || text.Contains("wifi") || text.Contains("wi-fi")) return "Network";
        if (text.Contains("memory") || text.Contains("ram")) return "Memory";
        if (text.Contains("motherboard")) return "Motherboard";
        return "WebJson";
    }

    private static void ReadHardware(IHardware hardware, List<SensorReading> readings, string path, string hardwareType)
    {
        try
        {
            hardware.Update();
            foreach (ISensor sensor in hardware.Sensors)
            {
                if (!sensor.Value.HasValue)
                    continue;
                readings.Add(new SensorReading(
                    path,
                    sensor.Name,
                    sensor.SensorType.ToString(),
                    sensor.Value.Value,
                    UnitFor(sensor.SensorType),
                    sensor.Identifier.ToString(),
                    hardwareType
                ));
            }

            foreach (IHardware child in hardware.SubHardware)
                ReadHardware(child, readings, $"{path} / {child.Name}", child.HardwareType.ToString());
        }
        catch
        {
            // Some low-level controllers can fail independently. Keep the rest of the dashboard alive.
        }
    }

    private static string HardwarePath(IHardware hardware)
    {
        var names = new Stack<string>();
        IHardware? current = hardware;
        while (current is not null)
        {
            if (!string.IsNullOrWhiteSpace(current.Name))
                names.Push(current.Name);
            current = current.Parent;
        }
        return names.Count == 0 ? hardware.HardwareType.ToString() : string.Join(" / ", names);
    }

    private void UpdateSummary(List<SensorReading> readings)
    {
        SetSummary("CPU Temp", Hottest(readings, "Temperature", "cpu"));
        SetSummary("GPU Temp", Hottest(readings, "Temperature", "gpu"));
        SetSummary("CPU Load", Highest(readings, "Load", "cpu"));
        SetSummary("Memory", Highest(readings, "Load", "memory"));
        SetSummary("Drive", Highest(readings.Where(IsDriveSensor).ToList(), ""));
        SetSummary("Fan", Highest(readings, "Fan"));
        SetSummary("Clock", Highest(readings.Where(IsClockSensor).ToList(), ""));
        SetSummary("Voltage", Highest(readings, "Voltage"));
        SetSummary("Power", Highest(readings.Where(reading => reading.Type is "Power" or "Current" or "Energy").ToList(), ""));
        SetSummary("Network", Highest(NetworkRows(readings), ""));
    }

    private void SetSummary(string name, SensorReading? reading)
    {
        if (!summaryValues.TryGetValue(name, out TextBlock? text))
            return;
        text.Text = reading is null ? "--" : reading.DisplayValue;
        text.Foreground = reading is null ? Brush("#9aa8b8") : Brush(ColorFor(reading));
    }

    private void UpdateHistory(List<SensorReading> readings)
    {
        foreach (SensorReading reading in readings)
        {
            string key = reading.Identifier;
            if (!history.TryGetValue(key, out Queue<double>? points))
            {
                points = new Queue<double>();
                history[key] = points;
            }

            points.Enqueue(GraphValue(reading));
            while (points.Count > 180)
                points.Dequeue();
        }
    }

    private void AppendSessionLog(List<SensorReading> readings)
    {
        DateTime now = DateTime.Now;
        foreach (SensorReading reading in readings)
            sessionLog.Add(new SensorLogEntry(now, reading.Hardware, reading.Name, reading.Type, reading.Value, reading.Unit, reading.Identifier));

        DateTime cutoff = now.AddMinutes(-15);
        sessionLog.RemoveAll(entry => entry.Time < cutoff);
    }

    private void UpdateAlertLog(List<SensorReading> readings)
    {
        foreach (SensorReading reading in readings)
        {
            int severity = SeverityFor(reading);
            string key = $"{reading.Identifier}:{severity}";
            if (severity > 0 && activeAlerts.Add(key))
            {
                string title = severity >= 2 ? "Hot threshold crossed" : "Watch threshold crossed";
                alertLog.Add(new AlertEntry(DateTime.Now, title, $"{reading.Hardware} / {reading.Name}", reading.DisplayValue, severity));
            }

            if (severity == 0)
            {
                activeAlerts.Remove($"{reading.Identifier}:1");
                activeAlerts.Remove($"{reading.Identifier}:2");
            }
        }

        while (alertLog.Count > 100)
            alertLog.RemoveAt(0);
    }

    private void UpdateCategoryTabs(List<SensorReading> readings)
    {
        foreach (string category in CategoryOrder)
        {
            CategoryView view = categoryViews[category];
            view.BarsPanel.Children.Clear();
            view.CardsPanel.Children.Clear();
            view.VerticalBarsPanel.Children.Clear();
            view.HeatPanel.Children.Clear();
            view.RadarPanel.Children.Clear();
            view.MatrixPanel.Children.Clear();
            view.TreemapPanel.Children.Clear();
            view.MoversPanel.Children.Clear();
            view.HistoryPanel.Children.Clear();

            try
            {
            List<SensorReading> rows = (category == "Network"
                    ? NetworkRows(readings)
                    : readings
                        .Where(reading => category == "Overview" ? IsOverviewReading(reading) : MatchesCategory(reading, category))
                        .OrderByDescending(reading => pinnedSensors.Contains(reading.Identifier))
                        .ThenByDescending(reading => GraphValue(reading))
                        .ThenBy(reading => reading.Hardware)
                        .ThenBy(reading => reading.Name)
                        .ToList());

            AddSectionTitle(view.BarsPanel, $"{category} - {rows.Count} sensor(s)");
            AddSectionTitle(view.CardsPanel, $"{category} Cards");
            AddSectionTitle(view.VerticalBarsPanel, $"{category} Vertical Bars");
            AddSectionTitle(view.HeatPanel, $"{category} Heat Map");
            AddSectionTitle(view.RadarPanel, $"{category} Radar");
            AddSectionTitle(view.MatrixPanel, $"{category} Hardware Matrix");
            AddSectionTitle(view.TreemapPanel, $"{category} Treemap");
            AddSectionTitle(view.MoversPanel, $"{category} Top Movers");
            AddSectionTitle(view.HistoryPanel, $"{category} History");

            if (rows.Count == 0)
            {
                view.BarsPanel.Children.Add(new TextBlock
                {
                    Text = $"No matching sensors reported yet. Current source: {sensorSource}",
                    Foreground = Brush("#9aa8b8"),
                    TextWrapping = TextWrapping.Wrap
                });
                view.BarsPanel.Children.Add(new TextBlock
                {
                    Text = "For your machine, keep LibreHardwareMonitor open and Remote Web Server enabled. The app should read http://127.0.0.1:8085/data.json.",
                    Foreground = Brush("#c3cfdd"),
                    Margin = new Thickness(0, 6, 0, 0),
                    TextWrapping = TextWrapping.Wrap
                });
                AddSensorTypeDiagnostics(view.BarsPanel, readings);
                continue;
            }

            double scale = Math.Max(1, rows.Max(GraphValue));
            if (category is "Temperatures" or "Overview")
                scale = Math.Max(100, scale);

            var barSummaryStrip = new WrapPanel { Margin = new Thickness(0, 0, 0, 8) };
            view.BarsPanel.Children.Add(barSummaryStrip);
            var cardsWrap = new WrapPanel { Margin = new Thickness(0, 6, 0, 0) };
            view.CardsPanel.Children.Add(cardsWrap);
            var verticalBarsWrap = new WrapPanel { Margin = new Thickness(0, 6, 0, 0), VerticalAlignment = VerticalAlignment.Bottom };
            view.VerticalBarsPanel.Children.Add(verticalBarsWrap);
            var heatWrap = new WrapPanel { Margin = new Thickness(0, 6, 0, 0) };
            view.HeatPanel.Children.Add(heatWrap);

            foreach (Border card in CreateCategorySummaryCards(category, rows))
            {
                barSummaryStrip.Children.Add(card);
                cardsWrap.Children.Add(CloneSummaryCard(card));
            }

            if (category == "Network")
                AddNetworkQuickView(view.BarsPanel, rows, scale);

            foreach (SensorReading reading in rows)
            {
                view.BarsPanel.Children.Add(CreateSensorRow(reading, scale));
                cardsWrap.Children.Add(CreateSensorCard(reading, scale));
                verticalBarsWrap.Children.Add(CreateVerticalBar(reading, scale));
                heatWrap.Children.Add(CreateHeatTile(reading, scale));
            }

            view.RadarPanel.Children.Add(CreateRadarChart(category, rows));
            var matrixCards = new WrapPanel { Margin = new Thickness(0, 6, 0, 0) };
            view.MatrixPanel.Children.Add(matrixCards);
            foreach (Border tile in CreateHardwareMatrix(rows))
                matrixCards.Children.Add(tile);

            var treemapCards = new WrapPanel { Margin = new Thickness(0, 6, 0, 0) };
            view.TreemapPanel.Children.Add(treemapCards);
            foreach (Border tile in CreateTreemapTiles(rows))
                treemapCards.Children.Add(tile);
            foreach (Border mover in CreateTopMoverRows(rows))
                view.MoversPanel.Children.Add(mover);

            foreach (SensorReading reading in rows.Take(8))
                view.HistoryPanel.Children.Add(CreateHistoryRow(reading));
            }
            catch (Exception exc)
            {
                view.BarsPanel.Children.Clear();
                AddSectionTitle(view.BarsPanel, $"{category} render issue");
                view.BarsPanel.Children.Add(CreateInfoRow("The data is present, but this tab hit a display issue.", exc.Message));
                view.BarsPanel.Children.Add(CreateRawSensorFallback(category, readings));
            }
        }
    }

    private void AddSensorTypeDiagnostics(Panel panel, List<SensorReading> readings)
    {
        panel.Children.Add(new TextBlock
        {
            Text = "Available sensor types:",
            Foreground = Brush("#9aa8b8"),
            Margin = new Thickness(0, 12, 0, 4),
            FontWeight = FontWeights.SemiBold
        });
        string types = string.Join(", ", readings.Select(reading => reading.Type).Distinct().OrderBy(value => value));
        panel.Children.Add(new TextBlock { Text = string.IsNullOrWhiteSpace(types) ? "None" : types, Foreground = Brush("#c3cfdd"), TextWrapping = TextWrapping.Wrap });

        panel.Children.Add(new TextBlock
        {
            Text = "Available hardware types:",
            Foreground = Brush("#9aa8b8"),
            Margin = new Thickness(0, 12, 0, 4),
            FontWeight = FontWeights.SemiBold
        });
        string hardwareTypes = string.Join(", ", readings.Select(reading => reading.HardwareType).Distinct().OrderBy(value => value));
        panel.Children.Add(new TextBlock { Text = string.IsNullOrWhiteSpace(hardwareTypes) ? "None" : hardwareTypes, Foreground = Brush("#c3cfdd"), TextWrapping = TextWrapping.Wrap });
    }

    private Border CreateRawSensorFallback(string category, List<SensorReading> readings)
    {
        List<SensorReading> rows = (category == "Network"
                ? NetworkRows(readings)
                : readings.Where(reading => category == "Overview" ? IsOverviewReading(reading) : MatchesCategory(reading, category)).ToList())
            .Take(80)
            .ToList();

        var panel = new StackPanel();
        panel.Children.Add(new TextBlock
        {
            Text = $"Raw {category} readings ({rows.Count} shown)",
            FontWeight = FontWeights.Bold,
            Margin = new Thickness(0, 0, 0, 8)
        });

        if (rows.Count == 0)
        {
            panel.Children.Add(new TextBlock { Text = "No rows matched this category.", Foreground = Brush("#9aa8b8") });
        }
        else
        {
            foreach (SensorReading reading in rows)
                panel.Children.Add(new TextBlock
                {
                    Text = $"{reading.Type} | {reading.Hardware} | {reading.Name}: {reading.DisplayValue}",
                    Foreground = Brush("#c3cfdd"),
                    TextTrimming = TextTrimming.CharacterEllipsis
                });
        }

        return new Border
        {
            Margin = new Thickness(0, 12, 0, 8),
            Padding = new Thickness(12),
            Background = Brush("#171d26"),
            BorderBrush = Brush("#2f3b4c"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Child = panel
        };
    }

    private List<Border> CreateCategorySummaryCards(string category, List<SensorReading> rows)
    {
        var numericRows = rows.Where(reading => !double.IsNaN(reading.Value) && !double.IsInfinity(reading.Value)).ToList();
        if (numericRows.Count == 0)
            return [];

        SensorReading top = numericRows.OrderByDescending(GraphValue).First();
        double average = numericRows.Average(reading => GraphValue(reading));
        string averageUnit = top.Unit;
        var cards = new List<Border>
        {
            CreateMiniStatCard("Sensors", numericRows.Count.ToString(), category),
            CreateMiniStatCard("Top", top.DisplayValue, top.Name),
            CreateMiniStatCard("Average", FormatValue(average, averageUnit), "numeric readings"),
            CreateMiniStatCard("Hardware", numericRows.Select(reading => reading.Hardware).Distinct().Count().ToString(), "devices")
        };
        return cards;
    }

    private void AddNetworkQuickView(Panel panel, List<SensorReading> rows, double scale)
    {
        var quickRows = rows
            .Where(reading => ContainsAny(reading, "download speed", "upload speed", "network utilization", "data downloaded", "data uploaded"))
            .Take(10)
            .ToList();

        if (quickRows.Count == 0)
            return;

        panel.Children.Add(new TextBlock
        {
            Text = "Network Quick View",
            FontSize = 16,
            FontWeight = FontWeights.Bold,
            Margin = new Thickness(0, 4, 0, 8)
        });

        var cards = new WrapPanel { Margin = new Thickness(0, 0, 0, 8) };
        panel.Children.Add(cards);
        foreach (SensorReading reading in quickRows)
            cards.Children.Add(CreateSensorCard(reading, scale));
    }

    private Border CreateMiniStatCard(string title, string value, string detail)
    {
        var panel = new StackPanel();
        panel.Children.Add(new TextBlock { Text = title, Foreground = Brush("#9aa8b8"), FontWeight = FontWeights.SemiBold });
        panel.Children.Add(new TextBlock { Text = value, FontSize = 20, FontWeight = FontWeights.Bold, Margin = new Thickness(0, 4, 0, 0) });
        panel.Children.Add(new TextBlock { Text = detail, Foreground = Brush("#9aa8b8"), TextTrimming = TextTrimming.CharacterEllipsis });
        return new Border
        {
            Width = 180,
            Margin = new Thickness(0, 0, 10, 10),
            Padding = new Thickness(12),
            Background = Brush("#1e2632"),
            BorderBrush = Brush("#344155"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Child = panel
        };
    }

    private Border CloneSummaryCard(Border source)
    {
        if (source.Child is not StackPanel sourcePanel)
            return source;
        var panel = new StackPanel();
        foreach (TextBlock text in sourcePanel.Children.OfType<TextBlock>())
        {
            panel.Children.Add(new TextBlock
            {
                Text = text.Text,
                FontSize = text.FontSize,
                FontWeight = text.FontWeight,
                Foreground = text.Foreground,
                Margin = text.Margin,
                TextTrimming = TextTrimming.CharacterEllipsis
            });
        }
        return new Border
        {
            Width = source.Width,
            Margin = source.Margin,
            Padding = source.Padding,
            Background = source.Background,
            BorderBrush = source.BorderBrush,
            BorderThickness = source.BorderThickness,
            CornerRadius = source.CornerRadius,
            Child = panel
        };
    }

    private static void AddSectionTitle(Panel panel, string title)
    {
        panel.Children.Add(new TextBlock
        {
            Text = title,
            FontSize = 20,
            FontWeight = FontWeights.Bold,
            Margin = new Thickness(0, 0, 0, 10)
        });
    }

    private void UpdateAnalysisTab(List<SensorReading> readings)
    {
        if (analysisView is null)
            return;

        analysisView.HealthCards.Children.Clear();
        analysisView.ScorePanel.Children.Clear();
        analysisView.ProblemPanel.Children.Clear();
        analysisView.TrendPanel.Children.Clear();
        analysisView.BottleneckPanel.Children.Clear();
        analysisView.ThermalPanel.Children.Clear();
        analysisView.TimelinePanel.Children.Clear();
        analysisView.ChangesPanel.Children.Clear();
        analysisView.AlertPanel.Children.Clear();
        analysisView.TopDevicesPanel.Children.Clear();
        analysisView.RecommendationPanel.Children.Clear();

        AddSectionTitle(analysisView.ScorePanel, "Stability Score");
        AddSectionTitle(analysisView.ProblemPanel, "Problem Finder");
        AddSectionTitle(analysisView.TrendPanel, "Trend Alerts");
        AddSectionTitle(analysisView.BottleneckPanel, "Bottleneck View");
        AddSectionTitle(analysisView.ThermalPanel, "Thermal Balance");
        AddSectionTitle(analysisView.TimelinePanel, "Session Timeline");
        AddSectionTitle(analysisView.ChangesPanel, "What Changed?");
        AddSectionTitle(analysisView.AlertPanel, "Alert Log");
        AddSectionTitle(analysisView.TopDevicesPanel, "Top Devices");
        AddSectionTitle(analysisView.RecommendationPanel, "Recommendations");

        if (readings.Count == 0)
        {
            analysisView.ProblemPanel.Children.Add(CreateInfoRow("Waiting for sensor data", $"Current source: {sensorSource}"));
            return;
        }

        foreach (AnalysisFinding health in BuildHealthOverview(readings))
            analysisView.HealthCards.Children.Add(CreateHealthCard(health));

        analysisView.ScorePanel.Children.Add(CreateStabilityScoreCard(readings));

        List<AnalysisFinding> problems = BuildProblemFindings(readings);
        AddFindingList(analysisView.ProblemPanel, problems, "No major problem areas detected from the current readings.");

        List<AnalysisFinding> trends = BuildTrendFindings(readings);
        AddFindingList(analysisView.TrendPanel, trends, "No sharp rising trends yet. This gets smarter after the app has watched the machine for a minute.");

        AddFindingList(analysisView.BottleneckPanel, BuildBottleneckFindings(readings), "No obvious bottleneck detected right now.");
        AddFindingList(analysisView.ThermalPanel, BuildThermalFindings(readings), "Thermal balance looks normal from the available temperature, fan, and load sensors.");
        analysisView.TimelinePanel.Children.Add(CreateSessionTimeline(readings));
        AddFindingList(analysisView.ChangesPanel, BuildChangeFindings(readings), "No meaningful movement yet. Let the app run for about a minute and this will become useful.");
        AddFindingList(analysisView.AlertPanel, BuildAlertFindings(), "No alerts have fired in this session.");
        AddFindingList(analysisView.TopDevicesPanel, BuildTopDeviceFindings(readings), "No ranked devices available yet.");
        AddFindingList(analysisView.RecommendationPanel, BuildRecommendations(readings, problems, trends), "Everything looks calm from the readings available.");
    }

    private void UpdateNetworkDashboard(List<SensorReading> readings)
    {
        if (networkView is null)
            return;

        networkView.StatusPanel.Children.Clear();
        networkView.HistoryPanel.Children.Clear();
        networkView.AdapterPanel.Children.Clear();
        networkView.RadarPanel.Children.Clear();
        AddSectionTitle(networkView.StatusPanel, "Network Status");
        AddSectionTitle(networkView.HistoryPanel, "Upload / Download History");
        AddSectionTitle(networkView.AdapterPanel, "Current Adapter");
        AddSectionTitle(networkView.RadarPanel, "Network Radar Profile");

        List<SensorReading> networkRows = NetworkRows(readings);
        networkView.StatusPanel.Children.Add(CreateInfoRow("Network sensor count", $"{networkRows.Count} network row(s) from {readings.Count} total sensor reading(s). Current source: {sensorSource}."));
        if (networkRows.Count == 0)
        {
            NetworkBadge.Background = Brush("#202a36");
            NetworkBadge.BorderBrush = Brush("#344155");
            NetworkBadgeText.Foreground = Brush("#9aa8b8");
            NetworkBadgeText.Text = "Network: --";
            networkView.StatusPanel.Children.Add(CreateInfoRow("No network sensors", "LibreHardwareMonitor is not reporting network sensors yet."));
            List<SensorReading> candidates = readings
                .Where(reading => ContainsAny(reading, "ethernet", "wifi", "wi-fi", "wireless", "adapter", "receive", "transmit", "download", "upload", "bytes"))
                .Take(20)
                .ToList();
            if (candidates.Count > 0)
                foreach (SensorReading candidate in candidates)
                    networkView.AdapterPanel.Children.Add(CreateInfoRow(candidate.Name, $"{candidate.Hardware} | {candidate.Type} | {candidate.DisplayValue}"));
            return;
        }

        SensorReading peak = networkRows.OrderByDescending(GraphValue).First();
        string state = GraphValue(peak) switch
        {
            > 50_000_000 => "Saturated",
            > 2_000_000 => "Active",
            _ => "Quiet"
        };
        UpdateNetworkBadge(state, peak.DisplayValue);
        networkView.StatusPanel.Children.Add(CreateInfoRow(state, $"Peak network reading: {peak.Hardware} / {peak.Name} at {peak.DisplayValue}."));
        var networkCards = new WrapPanel { Margin = new Thickness(0, 0, 0, 8) };
        foreach (SensorReading reading in networkRows.Take(12))
            networkCards.Children.Add(CreateSensorCard(reading, Math.Max(1, networkRows.Max(GraphValue))));
        networkView.HistoryPanel.Children.Add(networkCards);
        foreach (SensorReading reading in networkRows.Where(reading => reading.Type is "Throughput" or "Load").Take(8))
            networkView.HistoryPanel.Children.Add(CreateHistoryRow(reading));
        networkView.AdapterPanel.Children.Add(CreateInfoRow(peak.Hardware, $"{networkRows.Count} network sensor(s) available. Highest current value is {peak.Name}."));
        networkView.RadarPanel.Children.Add(CreateRadarChart("Network", networkRows));
    }

    private void UpdateNetworkBadge(string state, string value)
    {
        string color = state switch
        {
            "Saturated" => "#ff5c77",
            "Active" => "#33d17a",
            _ => "#5dade2"
        };
        NetworkBadge.Background = Brush(color);
        NetworkBadge.BorderBrush = Brush(color);
        NetworkBadgeText.Foreground = Brush("#0f141b");
        NetworkBadgeText.Text = $"Network: {state} ({value})";
    }

    private void UpdateMiniMonitor(List<SensorReading> readings)
    {
        if (miniMonitor is null || !miniMonitor.IsVisible)
            return;

        miniMonitor.UpdateValues(
            Hottest(readings, "Temperature", "cpu")?.DisplayValue ?? "--",
            Hottest(readings, "Temperature", "gpu")?.DisplayValue ?? "--",
            Highest(readings.Where(IsMemorySensor).ToList(), "")?.DisplayValue ?? "--",
            Highest(readings.Where(IsNetworkSensor).ToList(), "")?.DisplayValue ?? "--");
    }

    private List<AnalysisFinding> BuildHealthOverview(List<SensorReading> readings)
    {
        return
        [
            HealthFor("CPU", Hottest(readings, "Temperature", "cpu"), Highest(readings, "Load", "cpu")),
            HealthFor("GPU", Hottest(readings, "Temperature", "gpu"), Highest(readings, "Load", "gpu")),
            HealthFor("Memory", Highest(readings.Where(IsMemorySensor).ToList(), ""), null),
            HealthFor("Storage", Hottest(readings.Where(IsDriveSensor).ToList(), "Temperature"), Highest(readings.Where(IsDriveSensor).ToList(), "")),
            HealthFor("Network", Highest(readings.Where(IsNetworkSensor).ToList(), ""), null)
        ];
    }

    private AnalysisFinding HealthFor(string area, SensorReading? primary, SensorReading? secondary)
    {
        SensorReading? reading = primary ?? secondary;
        if (reading is null)
            return new AnalysisFinding(area, "No matching sensor reported", "Unavailable", 1);

        int severity = SeverityFor(reading);
        if (secondary is not null)
            severity = Math.Max(severity, SeverityFor(secondary));

        string status = severity >= 2 ? "Hot" : severity == 1 ? "Watch" : "Good";
        string detail = secondary is null || secondary == reading
            ? $"{reading.Name}: {reading.DisplayValue}"
            : $"{reading.Name}: {reading.DisplayValue} | {secondary.Name}: {secondary.DisplayValue}";
        return new AnalysisFinding(area, detail, status, severity);
    }

    private Border CreateHealthCard(AnalysisFinding finding)
    {
        var panel = new StackPanel();
        panel.Children.Add(new TextBlock { Text = finding.Title, Foreground = Brush("#9aa8b8"), FontWeight = FontWeights.SemiBold });
        panel.Children.Add(new TextBlock { Text = finding.Value, FontSize = 28, FontWeight = FontWeights.Bold, Foreground = Brush(SeverityColor(finding.Severity)), Margin = new Thickness(0, 4, 0, 0) });
        panel.Children.Add(new TextBlock { Text = finding.Detail, Foreground = Brush("#c3cfdd"), TextWrapping = TextWrapping.Wrap, TextTrimming = TextTrimming.CharacterEllipsis });

        return new Border
        {
            Width = 230,
            MinHeight = 124,
            Margin = new Thickness(0, 0, 10, 10),
            Padding = new Thickness(14),
            Background = Brush("#171d26"),
            BorderBrush = Brush(SeverityColor(finding.Severity)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Child = panel
        };
    }

    private Border CreateStabilityScoreCard(List<SensorReading> readings)
    {
        int score = ComputeStabilityScore(readings);
        string label = score >= 85 ? "Very Stable" : score >= 70 ? "Stable" : score >= 50 ? "Watch" : "Needs Attention";

        var panel = new StackPanel();
        panel.Children.Add(new TextBlock { Text = $"{score}/100", FontSize = 34, FontWeight = FontWeights.Bold, Foreground = Brush(score >= 70 ? "#33d17a" : score >= 50 ? "#f6c85f" : "#ff5c77") });
        panel.Children.Add(new TextBlock { Text = label, FontSize = 16, FontWeight = FontWeights.SemiBold });
        panel.Children.Add(new ProgressBar { Minimum = 0, Maximum = 100, Value = score, Height = 14, Margin = new Thickness(0, 10, 0, 8), Foreground = Brush(score >= 70 ? "#33d17a" : score >= 50 ? "#f6c85f" : "#ff5c77") });
        panel.Children.Add(new TextBlock { Text = "Based on heat, load, memory pressure, fan response, storage activity, and recent movement.", Foreground = Brush("#9aa8b8"), TextWrapping = TextWrapping.Wrap });

        return new Border
        {
            Padding = new Thickness(14),
            Margin = new Thickness(0, 0, 0, 10),
            Background = Brush(panelColor),
            BorderBrush = Brush(borderColor),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            ToolTip = "Stability Score starts at 100 and subtracts points for heat, high load, memory pressure, weak cooling response, and rising trends.",
            Child = panel
        };
    }

    private int ComputeStabilityScore(List<SensorReading> readings)
    {
        int penalty = 0;
        penalty += BuildProblemFindings(readings).Sum(item => item.Severity == 2 ? 14 : 7);
        penalty += BuildTrendFindings(readings).Sum(item => item.Severity == 2 ? 8 : 4);
        return Math.Max(0, Math.Min(100, 100 - penalty));
    }

    private List<AnalysisFinding> BuildProblemFindings(List<SensorReading> readings)
    {
        var findings = new List<AnalysisFinding>();
        foreach (SensorReading temp in readings.Where(reading => reading.Type == "Temperature"))
        {
            double watch = ContainsAny(temp, "gpu") ? gpuWatchThreshold : cpuWatchThreshold;
            double hot = ContainsAny(temp, "gpu") ? gpuHotThreshold : cpuHotThreshold;
            if (temp.Value >= hot)
                findings.Add(new AnalysisFinding("Temperature high", $"{temp.Hardware} / {temp.Name}", temp.DisplayValue, 2));
            else if (temp.Value >= watch)
                findings.Add(new AnalysisFinding("Temperature warm", $"{temp.Hardware} / {temp.Name}", temp.DisplayValue, 1));
        }

        foreach (SensorReading load in readings.Where(reading => reading.Type is "Load" or "Level"))
        {
            if (load.Value >= 95)
                findings.Add(new AnalysisFinding("Load nearly maxed", $"{load.Hardware} / {load.Name}", load.DisplayValue, 2));
            else if (load.Value >= 85)
                findings.Add(new AnalysisFinding("Load running high", $"{load.Hardware} / {load.Name}", load.DisplayValue, 1));
        }

        SensorReading? fan = Highest(readings.Where(IsFanSensor).ToList(), "Fan");
        SensorReading? hottest = Hottest(readings, "Temperature");
        if (hottest is not null && hottest.Value >= 80 && (fan is null || fan.Value < 700))
            findings.Add(new AnalysisFinding("Cooling response looks low", $"Hot reading: {hottest.Hardware} / {hottest.Name}. Fan reading: {(fan is null ? "not found" : fan.DisplayValue)}", hottest.DisplayValue, 1));

        return findings
            .OrderByDescending(finding => finding.Severity)
            .ThenBy(finding => finding.Title)
            .Take(12)
            .ToList();
    }

    private List<AnalysisFinding> BuildTrendFindings(List<SensorReading> readings)
    {
        var findings = new List<AnalysisFinding>();
        foreach (SensorReading reading in readings)
        {
            if (!history.TryGetValue(reading.Identifier, out Queue<double>? values) || values.Count < 8)
                continue;

            List<double> points = values.ToList();
            double oldest = points.Take(Math.Max(1, points.Count / 3)).Average();
            double newest = points.Skip(Math.Max(0, points.Count - Math.Max(1, points.Count / 3))).Average();
            double delta = newest - oldest;

            if (reading.Type == "Temperature" && delta >= 8)
                findings.Add(new AnalysisFinding("Temperature climbing", $"{reading.Hardware} / {reading.Name}", $"+{delta:0.#} C", 2));
            else if (reading.Type == "Temperature" && delta >= 4)
                findings.Add(new AnalysisFinding("Temperature rising", $"{reading.Hardware} / {reading.Name}", $"+{delta:0.#} C", 1));
            else if (reading.Type is "Load" or "Level" && delta >= 20)
                findings.Add(new AnalysisFinding("Load climbing", $"{reading.Hardware} / {reading.Name}", $"+{delta:0.#}%", 1));
        }

        return findings
            .OrderByDescending(finding => finding.Severity)
            .ThenByDescending(finding => Math.Abs(NumericPrefix(finding.Value)))
            .Take(10)
            .ToList();
    }

    private List<AnalysisFinding> BuildChangeFindings(List<SensorReading> readings)
    {
        var changes = new List<AnalysisFinding>();
        foreach (SensorReading reading in readings)
        {
            if (!history.TryGetValue(reading.Identifier, out Queue<double>? values) || values.Count < 20)
                continue;

            List<double> points = values.ToList();
            double current = points.Last();
            double previous = points[Math.Max(0, points.Count - 30)];
            double delta = current - previous;
            if (Math.Abs(delta) < ChangeFloor(reading))
                continue;

            int severity = reading.Type == "Temperature" && delta >= 8 ? 2 : 1;
            string unit = reading.Unit == "C" ? "C" : reading.Unit;
            changes.Add(new AnalysisFinding(
                delta >= 0 ? "Moved up" : "Moved down",
                $"{reading.Hardware} / {reading.Name}",
                $"{(delta >= 0 ? "+" : "")}{FormatValue(delta, unit)}",
                severity));
        }

        return changes
            .OrderByDescending(change => change.Severity)
            .ThenByDescending(change => Math.Abs(NumericPrefix(change.Value)))
            .Take(12)
            .ToList();
    }

    private List<AnalysisFinding> BuildAlertFindings()
    {
        return alertLog
            .OrderByDescending(alert => alert.Time)
            .Take(12)
            .Select(alert => new AnalysisFinding(alert.Title, $"{alert.Time:h:mm:ss tt} - {alert.Detail}", alert.Value, alert.Severity))
            .ToList();
    }

    private List<AnalysisFinding> BuildTopDeviceFindings(List<SensorReading> readings)
    {
        return readings
            .GroupBy(reading => string.IsNullOrWhiteSpace(reading.Hardware) ? reading.HardwareType : reading.Hardware)
            .Select(group =>
            {
                SensorReading top = group.OrderByDescending(GraphValue).First();
                string label = top.Type switch
                {
                    "Temperature" => "Top by heat",
                    "Power" => "Top by power",
                    "Load" => "Top by load",
                    "Throughput" => "Top by network",
                    _ => "Top device"
                };
                return new AnalysisFinding(label, $"{group.Key} / {top.Name}", top.DisplayValue, SeverityFor(top));
            })
            .OrderByDescending(finding => finding.Severity)
            .ThenByDescending(finding => NumericPrefix(finding.Value))
            .Take(12)
            .ToList();
    }

    private List<AnalysisFinding> CompareToBaseline()
    {
        if (baselineReadings is null)
            return [];

        var baselineMap = baselineReadings.ToDictionary(reading => reading.Identifier, reading => reading);
        var changes = new List<AnalysisFinding>();
        foreach (SensorReading reading in currentReadings)
        {
            if (!baselineMap.TryGetValue(reading.Identifier, out SensorReading? before))
                continue;

            double delta = GraphValue(reading) - GraphValue(before);
            if (Math.Abs(delta) < ChangeFloor(reading))
                continue;

            changes.Add(new AnalysisFinding(
                delta >= 0 ? "Increased" : "Decreased",
                $"{reading.Hardware} / {reading.Name}",
                $"{before.DisplayValue} -> {reading.DisplayValue} ({(delta >= 0 ? "+" : "")}{FormatValue(delta, reading.Unit)})",
                reading.Type == "Temperature" && delta >= 8 ? 2 : 1));
        }

        return changes
            .OrderByDescending(change => change.Severity)
            .ThenByDescending(change => Math.Abs(NumericPrefix(change.Value)))
            .Take(20)
            .ToList();
    }

    private Border CreateSessionTimeline(List<SensorReading> readings)
    {
        var canvas = new Canvas { Height = 260, Background = Brush(panelColor) };
        canvas.Loaded += (_, _) => DrawSessionTimeline(canvas, readings);
        canvas.SizeChanged += (_, _) => DrawSessionTimeline(canvas, readings);

        return new Border
        {
            Padding = new Thickness(10),
            Margin = new Thickness(0, 0, 0, 10),
            Background = Brush(panelColor),
            BorderBrush = Brush(borderColor),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            ToolTip = "Shows the last few minutes of key readings: CPU temp, GPU temp, memory, storage, and network.",
            Child = canvas
        };
    }

    private void DrawSessionTimeline(Canvas canvas, List<SensorReading> readings)
    {
        canvas.Children.Clear();
        double width = Math.Max(1, canvas.ActualWidth);
        double height = Math.Max(1, canvas.ActualHeight);
        DateTime cutoff = DateTime.Now.AddMinutes(-10);

        var series = new List<(string Name, string Color, List<SensorLogEntry> Points)>
        {
            ("CPU Temp", "#ff9f43", TimelineEntries(readings, cutoff, "Temperature", "cpu")),
            ("GPU Temp", "#ff5c77", TimelineEntries(readings, cutoff, "Temperature", "gpu")),
            ("Memory", "#b084f5", TimelineEntries(readings.Where(IsMemorySensor).ToList(), cutoff, "", "")),
            ("Storage", "#f6c85f", TimelineEntries(readings.Where(IsDriveSensor).ToList(), cutoff, "", "")),
            ("Network", "#33d17a", TimelineEntries(readings.Where(IsNetworkSensor).ToList(), cutoff, "", ""))
        };

        double left = 12;
        double top = 34;
        double graphHeight = height - top - 18;
        double graphWidth = width - 24;
        int legendX = 12;
        foreach ((string name, string color, List<SensorLogEntry> points) in series)
        {
            canvas.Children.Add(new TextBlock { Text = name, Foreground = Brush(color), FontSize = 11, FontWeight = FontWeights.SemiBold });
            Canvas.SetLeft(canvas.Children[^1], legendX);
            Canvas.SetTop(canvas.Children[^1], 8);
            legendX += 88;

            if (points.Count < 2)
                continue;

            double max = Math.Max(100, points.Max(point => Math.Abs(point.Value)));
            var line = new PointCollection();
            foreach (SensorLogEntry point in points)
            {
                double x = left + (point.Time - cutoff).TotalSeconds / 600.0 * graphWidth;
                double y = top + graphHeight - Math.Min(1, Math.Abs(point.Value) / max) * graphHeight;
                line.Add(new Point(x, y));
            }
            canvas.Children.Add(new Polyline { Points = line, Stroke = Brush(color), StrokeThickness = 2 });
        }
    }

    private List<SensorLogEntry> TimelineEntries(List<SensorReading> candidates, DateTime cutoff, string type, string term)
    {
        SensorReading? selected = candidates
            .Where(reading => string.IsNullOrWhiteSpace(type) || reading.Type == type)
            .Where(reading => string.IsNullOrWhiteSpace(term) || ContainsAny(reading, term))
            .OrderByDescending(GraphValue)
            .FirstOrDefault();
        if (selected is null)
            return [];

        return sessionLog
            .Where(entry => entry.Identifier == selected.Identifier && entry.Time >= cutoff)
            .OrderBy(entry => entry.Time)
            .ToList();
    }

    private List<AnalysisFinding> BuildBottleneckFindings(List<SensorReading> readings)
    {
        var findings = new List<AnalysisFinding>();
        AddBottleneck(findings, "CPU", Highest(readings, "Load", "cpu"));
        AddBottleneck(findings, "GPU", Highest(readings, "Load", "gpu"));
        AddBottleneck(findings, "Memory", Highest(readings.Where(IsMemorySensor).ToList(), ""));
        AddBottleneck(findings, "Storage", Highest(readings.Where(IsDriveSensor).ToList(), ""));
        AddBottleneck(findings, "Network", Highest(readings.Where(IsNetworkSensor).ToList(), ""));
        return findings.OrderByDescending(finding => finding.Severity).Take(8).ToList();
    }

    private void AddBottleneck(List<AnalysisFinding> findings, string area, SensorReading? reading)
    {
        if (reading is null)
            return;
        int severity = SeverityFor(reading);
        if (severity == 0 && reading.Type is not "Throughput")
            return;
        findings.Add(new AnalysisFinding($"{area} pressure", $"{reading.Hardware} / {reading.Name}", reading.DisplayValue, Math.Max(1, severity)));
    }

    private List<AnalysisFinding> BuildThermalFindings(List<SensorReading> readings)
    {
        var findings = new List<AnalysisFinding>();
        SensorReading? cpuTemp = Hottest(readings, "Temperature", "cpu");
        SensorReading? gpuTemp = Hottest(readings, "Temperature", "gpu");
        SensorReading? cpuLoad = Highest(readings, "Load", "cpu");
        SensorReading? fan = Highest(readings.Where(IsFanSensor).ToList(), "Fan");

        if (cpuTemp is not null && cpuLoad is not null)
            findings.Add(new AnalysisFinding("CPU heat vs load", $"CPU load is {cpuLoad.DisplayValue}; hottest CPU reading is {cpuTemp.DisplayValue}.", cpuTemp.Value >= cpuWatchThreshold && cpuLoad.Value < 60 ? "Heat high for load" : "Balanced", cpuTemp.Value >= cpuWatchThreshold && cpuLoad.Value < 60 ? 1 : 0));
        if (gpuTemp is not null)
            findings.Add(new AnalysisFinding("GPU thermal state", $"{gpuTemp.Hardware} / {gpuTemp.Name}", gpuTemp.DisplayValue, SeverityFor(gpuTemp)));
        if (fan is not null)
            findings.Add(new AnalysisFinding("Fan response", $"{fan.Hardware} / {fan.Name}", fan.DisplayValue, fan.Value <= 0 ? 1 : 0));

        return findings;
    }

    private List<AnalysisFinding> BuildRecommendations(List<SensorReading> readings, List<AnalysisFinding> problems, List<AnalysisFinding> trends)
    {
        var notes = new List<AnalysisFinding>();
        if (problems.Any(item => item.Title.Contains("Temperature", StringComparison.OrdinalIgnoreCase)))
            notes.Add(new AnalysisFinding("Check cooling first", "Look at dust, airflow, fan curves, and whether the machine is under a heavy workload.", "Thermals", 1));
        if (problems.Any(item => item.Title.Contains("Load", StringComparison.OrdinalIgnoreCase)))
            notes.Add(new AnalysisFinding("Find the busy workload", "High load may be normal during rendering, compiling, or gaming. If the machine feels slow, check the top process.", "Load", 1));
        if (trends.Count > 0)
            notes.Add(new AnalysisFinding("Watch the trend", "A rising value matters more than a single snapshot. Let the app run for a few minutes to confirm the pattern.", "Trend", 1));
        if (Highest(readings.Where(IsMemorySensor).ToList(), "") is SensorReading memory && memory.Value >= 80)
            notes.Add(new AnalysisFinding("Memory pressure is worth watching", "Heavy memory use can make apps feel slow even when the CPU is not maxed.", memory.DisplayValue, 1));
        if (notes.Count == 0)
            notes.Add(new AnalysisFinding("No immediate action", "The available readings do not show a clear problem area right now.", "Good", 0));
        return notes;
    }

    private void AddFindingList(Panel panel, List<AnalysisFinding> findings, string emptyMessage)
    {
        if (findings.Count == 0)
        {
            panel.Children.Add(CreateInfoRow("Clear", emptyMessage));
            return;
        }

        foreach (AnalysisFinding finding in findings)
            panel.Children.Add(CreateFindingRow(finding));
    }

    private string BuildHtmlReport()
    {
        var problems = BuildProblemFindings(currentReadings);
        var trends = BuildTrendFindings(currentReadings);
        var changes = BuildChangeFindings(currentReadings);
        var builder = new StringBuilder();
        builder.AppendLine("<!doctype html><html><head><meta charset=\"utf-8\"><title>Hardware Visualizer Report</title>");
        builder.AppendLine("<style>body{font-family:Segoe UI,Arial;background:#0f141b;color:#e8edf4;padding:24px} .card{background:#171d26;border:1px solid #2f3b4c;border-radius:8px;padding:12px;margin:10px 0} h1,h2{margin-bottom:8px} .value{font-weight:700;color:#6ee7f9}</style></head><body>");
        builder.AppendLine($"<h1>Hardware Visualizer Report</h1><p>{DateTime.Now:g}</p><p>Source: {EscapeHtml(sensorSource)}</p>");
        builder.AppendLine($"<div class=\"card\"><h2>Stability Score</h2><div class=\"value\">{ComputeStabilityScore(currentReadings)}/100</div></div>");
        AppendReportSection(builder, "Health Overview", BuildHealthOverview(currentReadings));
        AppendReportSection(builder, "Problem Finder", problems);
        AppendReportSection(builder, "Trend Alerts", trends);
        AppendReportSection(builder, "What Changed", changes);
        AppendReportSection(builder, "Recommendations", BuildRecommendations(currentReadings, problems, trends));
        builder.AppendLine("</body></html>");
        return builder.ToString();
    }

    private static void AppendReportSection(StringBuilder builder, string title, List<AnalysisFinding> findings)
    {
        builder.AppendLine($"<h2>{EscapeHtml(title)}</h2>");
        if (findings.Count == 0)
        {
            builder.AppendLine("<div class=\"card\">No findings.</div>");
            return;
        }

        foreach (AnalysisFinding finding in findings)
            builder.AppendLine($"<div class=\"card\"><b>{EscapeHtml(finding.Title)}</b><br>{EscapeHtml(finding.Detail)}<br><span class=\"value\">{EscapeHtml(finding.Value)}</span></div>");
    }

    private string BuildCsvExport()
    {
        var builder = new StringBuilder();
        builder.AppendLine("Time,Hardware,Name,Type,Value,Unit,Identifier");
        foreach (SensorLogEntry entry in sessionLog)
            builder.AppendLine($"{entry.Time:O},{Csv(entry.Hardware)},{Csv(entry.Name)},{Csv(entry.Type)},{entry.Value:0.####},{Csv(entry.Unit)},{Csv(entry.Identifier)}");
        return builder.ToString();
    }

    private string BuildSourceCompareReport(List<SensorReading> direct, List<SensorReading> web)
    {
        var builder = new StringBuilder();
        builder.AppendLine("Hardware Visualizer Source Compare");
        builder.AppendLine(DateTime.Now.ToString("g"));
        builder.AppendLine();
        builder.AppendLine($"Direct standalone sensors: {direct.Count}");
        builder.AppendLine($"Direct network-like sensors: {direct.Count(IsNetworkSensor)}");
        builder.AppendLine($"Web sensors: {web.Count}");
        builder.AppendLine($"Web network-like sensors: {web.Count(IsNetworkSensor)}");
        builder.AppendLine();
        AppendSourceSummary(builder, "Direct sensor types", direct);
        AppendSourceSummary(builder, "Web sensor types", web);
        AppendSourceSummary(builder, "Direct hardware", direct);
        AppendSourceSummary(builder, "Web hardware", web);
        AppendSensorList(builder, "Direct network-like sensors", direct.Where(IsNetworkSensor).ToList());
        AppendSensorList(builder, "Web network-like sensors", web.Where(IsNetworkSensor).ToList());

        var directKeys = direct.Select(SensorCompareKey).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var webOnly = web.Where(reading => !directKeys.Contains(SensorCompareKey(reading))).Take(120).ToList();
        AppendSensorList(builder, "Web sensors not matched by direct mode (first 120)", webOnly);
        return builder.ToString();
    }

    private static void AppendSourceSummary(StringBuilder builder, string title, List<SensorReading> readings)
    {
        builder.AppendLine(title);
        foreach (var group in readings.GroupBy(reading => title.Contains("types") ? reading.Type : reading.Hardware).OrderByDescending(group => group.Count()).Take(30))
            builder.AppendLine($"- {group.Key}: {group.Count()}");
        builder.AppendLine();
    }

    private static void AppendSensorList(StringBuilder builder, string title, List<SensorReading> readings)
    {
        builder.AppendLine(title);
        if (readings.Count == 0)
        {
            builder.AppendLine("- none");
            builder.AppendLine();
            return;
        }

        foreach (SensorReading reading in readings.Take(120))
            builder.AppendLine($"- [{reading.Type}] {reading.Hardware} / {reading.Name} = {reading.DisplayValue} ({reading.Identifier})");
        builder.AppendLine();
    }

    private static string SensorCompareKey(SensorReading reading)
    {
        return $"{reading.Type}|{reading.Hardware}|{reading.Name}".ToLowerInvariant();
    }

    private void AttachPinToggle(Border border, SensorReading reading)
    {
        border.ToolTip = "Right-click to pin or unpin this sensor. Pinned sensors stay at the top.";
        border.MouseRightButtonUp += (_, _) =>
        {
            if (!pinnedSensors.Add(reading.Identifier))
                pinnedSensors.Remove(reading.Identifier);
            RefreshSensors();
        };
    }

    private string PinPrefix(SensorReading reading)
    {
        return pinnedSensors.Contains(reading.Identifier) ? "[Pinned] " : "";
    }

    private Border CreateFindingRow(AnalysisFinding finding)
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(170) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(120) });

        grid.Children.Add(new TextBlock { Text = finding.Title, FontWeight = FontWeights.Bold, Foreground = Brush(SeverityColor(finding.Severity)), VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis });
        var detail = new TextBlock { Text = finding.Detail, Foreground = Brush("#c3cfdd"), TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(detail, 1);
        grid.Children.Add(detail);
        var value = new TextBlock { Text = finding.Value, FontWeight = FontWeights.Bold, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
        Grid.SetColumn(value, 2);
        grid.Children.Add(value);

        return new Border
        {
            Margin = new Thickness(0, 0, 0, 8),
            Padding = new Thickness(12),
            Background = Brush("#171d26"),
            BorderBrush = Brush(SeverityColor(finding.Severity)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Child = grid
        };
    }

    private Border CreateSensorRow(SensorReading reading, double scale)
    {
        var grid = new Grid { Margin = new Thickness(0, 0, 0, 8) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(360) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(110) });

        var label = new TextBlock
        {
            Text = $"{PinPrefix(reading)}{reading.Hardware} / {reading.Name}",
            Foreground = Brush("#e8edf4"),
            TextTrimming = TextTrimming.CharacterEllipsis,
            TextWrapping = TextWrapping.NoWrap,
            VerticalAlignment = VerticalAlignment.Center
        };
        Grid.SetColumn(label, 0);
        grid.Children.Add(label);

        var bar = new ProgressBar
        {
            Minimum = 0,
            Maximum = scale,
            Value = Math.Max(0, GraphValue(reading)),
            Foreground = Brush(ColorFor(reading)),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(12, 0, 12, 0)
        };
        Grid.SetColumn(bar, 1);
        grid.Children.Add(bar);

        var value = new TextBlock
        {
            Text = reading.DisplayValue,
            Foreground = Brush(ColorFor(reading)),
            FontWeight = FontWeights.Bold,
            VerticalAlignment = VerticalAlignment.Center
        };
        Grid.SetColumn(value, 2);
        grid.Children.Add(value);

        var border = new Border
        {
            Background = Brush(panelColor),
            BorderBrush = pinnedSensors.Contains(reading.Identifier) ? Brush(accentColor) : Brush(borderColor),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(10),
            Child = grid
        };
        AttachPinToggle(border, reading);
        return border;
    }

    private Border CreateSensorCard(SensorReading reading, double scale)
    {
        var panel = new StackPanel();
        panel.Children.Add(new TextBlock { Text = $"{PinPrefix(reading)}{reading.Name}", FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = 210 });
        panel.Children.Add(new TextBlock { Text = reading.DisplayValue, FontSize = 20, FontWeight = FontWeights.Bold, Foreground = Brush(ColorFor(reading)), Margin = new Thickness(0, 6, 0, 2), TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = 210 });
        panel.Children.Add(new TextBlock { Text = reading.Hardware, Foreground = Brush("#9aa8b8"), TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = 210 });
        panel.Children.Add(new TextBlock { Text = $"{reading.Type} | {reading.HardwareType}", Foreground = Brush("#6ee7f9"), FontSize = 11, TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = 210 });

        var border = new Border
        {
            Width = 230,
            MinHeight = 112,
            Margin = new Thickness(0, 0, 10, 10),
            Padding = new Thickness(12),
            Background = Brush(panelColor),
            BorderBrush = Brush(ColorFor(reading)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Child = panel
        };
        AttachPinToggle(border, reading);
        return border;
    }

    private Border CreateVerticalBar(SensorReading reading, double scale)
    {
        double percent = Math.Max(0, Math.Min(1, GraphValue(reading) / Math.Max(1, scale)));
        var canvas = new Canvas { Width = 96, Height = 220, Background = Brush("#171d26") };
        double barHeight = 122 * percent;
        var back = new Rectangle { Width = 26, Height = 122, RadiusX = 6, RadiusY = 6, Fill = Brush("#2a3444") };
        Canvas.SetLeft(back, 35);
        Canvas.SetTop(back, 48);
        canvas.Children.Add(back);

        var fill = new Rectangle { Width = 26, Height = barHeight, RadiusX = 6, RadiusY = 6, Fill = Brush(ColorFor(reading)) };
        Canvas.SetLeft(fill, 35);
        Canvas.SetTop(fill, 48 + 122 - barHeight);
        canvas.Children.Add(fill);

        var value = new TextBlock
        {
            Text = reading.DisplayValue,
            Foreground = Brush(ColorFor(reading)),
            FontWeight = FontWeights.Bold,
            FontSize = 11,
            Width = 90,
            TextAlignment = TextAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis
        };
        Canvas.SetLeft(value, 3);
        Canvas.SetTop(value, 12);
        canvas.Children.Add(value);

        var name = new TextBlock
        {
            Text = reading.Name,
            Foreground = Brush("#c3cfdd"),
            FontSize = 10,
            Width = 88,
            Height = 34,
            TextAlignment = TextAlignment.Center,
            TextWrapping = TextWrapping.Wrap,
            TextTrimming = TextTrimming.CharacterEllipsis
        };
        Canvas.SetLeft(name, 4);
        Canvas.SetTop(name, 176);
        canvas.Children.Add(name);

        return new Border
        {
            Width = 110,
            Height = 238,
            Margin = new Thickness(0, 0, 10, 10),
            Padding = new Thickness(6),
            Background = Brush("#171d26"),
            BorderBrush = Brush("#2f3b4c"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Child = canvas
        };
    }

    private Border CreateHeatTile(SensorReading reading, double scale)
    {
        double percent = Math.Max(0, Math.Min(1, GraphValue(reading) / Math.Max(1, scale)));
        byte alpha = (byte)(70 + percent * 150);
        var color = (Color)ColorConverter.ConvertFromString(ColorFor(reading));
        color.A = alpha;

        var panel = new StackPanel();
        panel.Children.Add(new TextBlock { Text = reading.DisplayValue, FontWeight = FontWeights.Bold, FontSize = 16 });
        panel.Children.Add(new TextBlock { Text = reading.Name, Foreground = Brush("#e8edf4"), TextTrimming = TextTrimming.CharacterEllipsis });
        panel.Children.Add(new TextBlock { Text = reading.Hardware, Foreground = Brush("#c3cfdd"), FontSize = 11, TextTrimming = TextTrimming.CharacterEllipsis });
        panel.Children.Add(new TextBlock { Text = reading.Type, Foreground = Brush("#ffffff"), FontSize = 10, TextTrimming = TextTrimming.CharacterEllipsis });

        return new Border
        {
            Width = 190,
            Height = 94,
            Margin = new Thickness(0, 0, 8, 8),
            Padding = new Thickness(10),
            Background = new SolidColorBrush(color),
            BorderBrush = Brush("#2f3b4c"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Child = panel
        };
    }

    private Border CreateRadarChart(string category, List<SensorReading> rows)
    {
        var canvas = new Canvas { Height = 380, MinWidth = 760, Background = Brush("#171d26") };
        canvas.Loaded += (_, _) => DrawRadar(canvas, category, rows);
        canvas.SizeChanged += (_, _) => DrawRadar(canvas, category, rows);

        return new Border
        {
            Margin = new Thickness(0, 0, 0, 10),
            Padding = new Thickness(12),
            Background = Brush("#171d26"),
            BorderBrush = Brush("#2f3b4c"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Child = canvas
        };
    }

    private static void DrawRadar(Canvas canvas, string category, List<SensorReading> rows)
    {
        canvas.Children.Clear();
        double width = Math.Max(760, canvas.ActualWidth);
        double height = Math.Max(360, canvas.ActualHeight);
        double cx = width / 2;
        double cy = height / 2 + 14;
        double radius = Math.Min(width, height) * 0.34;

        canvas.Children.Add(new TextBlock
        {
            Text = $"{category} radar profile",
            Foreground = Brush("#e8edf4"),
            FontSize = 18,
            FontWeight = FontWeights.Bold
        });

        List<RadarMetric> metrics = RadarMetrics(rows).Take(10).ToList();
        if (metrics.Count < 3)
        {
            var note = new TextBlock { Text = "Need at least 3 numeric groups for a useful radar chart.", Foreground = Brush("#9aa8b8") };
            Canvas.SetTop(note, 40);
            canvas.Children.Add(note);
            return;
        }

        for (int ring = 1; ring <= 4; ring++)
        {
            double r = radius * ring / 4;
            canvas.Children.Add(new Ellipse
            {
                Width = r * 2,
                Height = r * 2,
                Stroke = Brush("#2a3444"),
                StrokeThickness = 1
            });
            Canvas.SetLeft(canvas.Children[^1], cx - r);
            Canvas.SetTop(canvas.Children[^1], cy - r);
        }

        var points = new PointCollection();
        for (int index = 0; index < metrics.Count; index++)
        {
            double angle = -Math.PI / 2 + index * (Math.PI * 2 / metrics.Count);
            double axisX = cx + Math.Cos(angle) * radius;
            double axisY = cy + Math.Sin(angle) * radius;
            canvas.Children.Add(new Line { X1 = cx, Y1 = cy, X2 = axisX, Y2 = axisY, Stroke = Brush("#344155"), StrokeThickness = 1 });

            double valueRadius = radius * Math.Max(0.04, Math.Min(1, metrics[index].Percent));
            points.Add(new Point(cx + Math.Cos(angle) * valueRadius, cy + Math.Sin(angle) * valueRadius));

            var label = new TextBlock
            {
                Text = $"{metrics[index].Label} {metrics[index].ValueText}",
                Foreground = Brush("#c3cfdd"),
                FontSize = 11
            };
            Canvas.SetLeft(label, cx + Math.Cos(angle) * (radius + 24) - 46);
            Canvas.SetTop(label, cy + Math.Sin(angle) * (radius + 24) - 8);
            canvas.Children.Add(label);
        }

        canvas.Children.Add(new Polygon
        {
            Points = points,
            Fill = new SolidColorBrush(Color.FromArgb(90, 110, 231, 249)),
            Stroke = Brush("#6ee7f9"),
            StrokeThickness = 2
        });
    }

    private static List<RadarMetric> RadarMetrics(List<SensorReading> rows)
    {
        var grouped = rows
            .GroupBy(reading => reading.Type)
            .Select(group =>
            {
                double max = group.Max(GraphValue);
                double scale = group.Key == "Temperature" ? 100 : Math.Max(1, rows.Where(reading => reading.Type == group.Key).Max(GraphValue));
                SensorReading top = group.OrderByDescending(GraphValue).First();
                return new RadarMetric(group.Key, Math.Min(1, max / Math.Max(1, scale)), top.DisplayValue);
            })
            .Where(metric => metric.Percent > 0)
            .OrderByDescending(metric => metric.Percent)
            .ToList();

        if (grouped.Count >= 3)
            return grouped;

        return rows
            .OrderByDescending(GraphValue)
            .Take(10)
            .Select(reading => new RadarMetric(reading.Name, Math.Min(1, GraphValue(reading) / Math.Max(1, rows.Max(GraphValue))), reading.DisplayValue))
            .ToList();
    }

    private List<Border> CreateHardwareMatrix(List<SensorReading> rows)
    {
        double globalMax = Math.Max(1, rows.Max(GraphValue));
        return rows
            .GroupBy(reading => string.IsNullOrWhiteSpace(reading.Hardware) ? reading.HardwareType : reading.Hardware)
            .Select(group =>
            {
                SensorReading top = group.OrderByDescending(GraphValue).First();
                double avg = group.Average(GraphValue);
                return CreateMatrixTile(group.Key, group.Count(), top, avg, globalMax);
            })
            .OrderByDescending(tile => tile.Tag as double? ?? 0)
            .ToList();
    }

    private Border CreateMatrixTile(string hardware, int count, SensorReading top, double average, double globalMax)
    {
        double percent = Math.Max(0, Math.Min(1, GraphValue(top) / Math.Max(1, globalMax)));
        var baseColor = (Color)ColorConverter.ConvertFromString(ColorFor(top));
        baseColor.A = 120;

        var panel = new StackPanel();
        panel.Children.Add(new TextBlock { Text = hardware, FontWeight = FontWeights.Bold, TextTrimming = TextTrimming.CharacterEllipsis });
        panel.Children.Add(new TextBlock { Text = top.Name, Foreground = Brush("#9aa8b8"), FontSize = 11, Margin = new Thickness(0, 2, 0, 8), TextTrimming = TextTrimming.CharacterEllipsis });
        panel.Children.Add(new Border
        {
            Height = 38,
            Padding = new Thickness(10, 0, 10, 0),
            Background = new SolidColorBrush(baseColor),
            CornerRadius = new CornerRadius(6),
            Child = new TextBlock
            {
                Text = top.DisplayValue,
                FontSize = 18,
                FontWeight = FontWeights.Bold,
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis
            }
        });
        panel.Children.Add(new TextBlock { Text = $"{count} sensors | avg {FormatValue(average, top.Unit)}", Foreground = Brush("#9aa8b8"), FontSize = 11, Margin = new Thickness(0, 8, 0, 0), TextTrimming = TextTrimming.CharacterEllipsis });

        return new Border
        {
            Tag = GraphValue(top),
            Width = 260,
            Height = 142,
            Margin = new Thickness(0, 0, 10, 10),
            Padding = new Thickness(12),
            Background = Brush("#171d26"),
            BorderBrush = Brush("#2f3b4c"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Child = panel
        };
    }

    private List<Border> CreateTreemapTiles(List<SensorReading> rows)
    {
        double max = Math.Max(1, rows.Max(GraphValue));
        return rows
            .OrderByDescending(GraphValue)
            .Take(70)
            .Select(reading =>
            {
                double percent = Math.Max(0, Math.Min(1, GraphValue(reading) / max));
                double size = 88 + percent * 150;
                return CreateTreemapTile(reading, size, percent);
            })
            .ToList();
    }

    private Border CreateTreemapTile(SensorReading reading, double size, double percent)
    {
        var color = (Color)ColorConverter.ConvertFromString(ColorFor(reading));
        color.A = 130;
        var panel = new StackPanel();
        panel.Children.Add(new TextBlock { Text = reading.Name, FontSize = 11, Foreground = Brush("#c3cfdd"), TextTrimming = TextTrimming.CharacterEllipsis });
        panel.Children.Add(new Border
        {
            Height = Math.Max(44, size * 0.38),
            Margin = new Thickness(0, 8, 0, 8),
            Padding = new Thickness(8),
            Background = new SolidColorBrush(color),
            CornerRadius = new CornerRadius(6),
            Child = new TextBlock
            {
                Text = reading.DisplayValue,
                FontWeight = FontWeights.Bold,
                FontSize = Math.Min(22, 14 + percent * 10),
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis
            }
        });
        panel.Children.Add(new TextBlock { Text = reading.Type, FontSize = 10, Foreground = Brush("#9aa8b8"), TextTrimming = TextTrimming.CharacterEllipsis });

        return new Border
        {
            Width = size,
            Height = Math.Max(122, size * 0.72),
            Margin = new Thickness(0, 0, 8, 8),
            Padding = new Thickness(10),
            Background = Brush("#171d26"),
            BorderBrush = Brush("#2f3b4c"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Child = panel
        };
    }

    private List<Border> CreateTopMoverRows(List<SensorReading> rows)
    {
        var movers = rows
            .Select(reading =>
            {
                if (!history.TryGetValue(reading.Identifier, out Queue<double>? values) || values.Count < 2)
                    return (Reading: reading, Delta: 0.0);
                double oldest = values.Peek();
                double newest = values.Last();
                return (Reading: reading, Delta: newest - oldest);
            })
            .OrderByDescending(item => Math.Abs(item.Delta))
            .Take(24)
            .ToList();

        if (movers.Count == 0)
            return [CreateInfoRow("Top Movers", "Collecting history. This view becomes useful after a few refreshes.")];

        double scale = Math.Max(1, movers.Max(item => Math.Abs(item.Delta)));
        return movers.Select(item => CreateMoverRow(item.Reading, item.Delta, scale)).ToList();
    }

    private Border CreateMoverRow(SensorReading reading, double delta, double scale)
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(360) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(130) });

        grid.Children.Add(new TextBlock
        {
            Text = $"{reading.Hardware} / {reading.Name}",
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center
        });

        var bar = new ProgressBar
        {
            Minimum = 0,
            Maximum = scale,
            Value = Math.Abs(delta),
            Foreground = Brush(delta >= 0 ? "#ff9f43" : "#5dade2"),
            Background = Brush("#2a3444"),
            Margin = new Thickness(12, 0, 12, 0),
            VerticalAlignment = VerticalAlignment.Center
        };
        Grid.SetColumn(bar, 1);
        grid.Children.Add(bar);

        var value = new TextBlock
        {
            Text = $"{(delta >= 0 ? "+" : "")}{FormatValue(delta, reading.Unit)}",
            Foreground = Brush(delta >= 0 ? "#ff9f43" : "#5dade2"),
            FontWeight = FontWeights.Bold,
            VerticalAlignment = VerticalAlignment.Center
        };
        Grid.SetColumn(value, 2);
        grid.Children.Add(value);

        return new Border
        {
            Margin = new Thickness(0, 0, 0, 8),
            Padding = new Thickness(10),
            Background = Brush("#171d26"),
            BorderBrush = Brush("#2f3b4c"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Child = grid
        };
    }

    private Border CreateInfoRow(string title, string detail)
    {
        var panel = new StackPanel();
        panel.Children.Add(new TextBlock { Text = title, FontWeight = FontWeights.Bold });
        panel.Children.Add(new TextBlock { Text = detail, Foreground = Brush("#9aa8b8"), TextWrapping = TextWrapping.Wrap });
        return new Border
        {
            Margin = new Thickness(0, 0, 0, 8),
            Padding = new Thickness(12),
            Background = Brush("#171d26"),
            BorderBrush = Brush("#2f3b4c"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Child = panel
        };
    }

    private Border CreateHistoryRow(SensorReading reading)
    {
        string key = reading.Identifier;
        if (!history.TryGetValue(key, out Queue<double>? points))
        {
            points = new Queue<double>();
            history[key] = points;
        }

        var canvas = new Canvas { Height = 120, Background = Brush("#171d26") };
        canvas.Loaded += (_, _) => DrawHistory(canvas, reading, points.ToList());
        canvas.SizeChanged += (_, _) => DrawHistory(canvas, reading, points.ToList());

        return new Border
        {
            Margin = new Thickness(0, 0, 0, 10),
            Padding = new Thickness(10),
            Background = Brush("#171d26"),
            BorderBrush = Brush("#2f3b4c"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Child = canvas
        };
    }

    private static void DrawHistory(Canvas canvas, SensorReading reading, List<double> values)
    {
        canvas.Children.Clear();
        double width = Math.Max(1, canvas.ActualWidth);
        double height = Math.Max(1, canvas.ActualHeight);
        canvas.Children.Add(new TextBlock { Text = $"{reading.Hardware} / {reading.Name}", Foreground = Brush("#e8edf4"), FontWeight = FontWeights.SemiBold });
        if (values.Count < 2)
            return;

        double top = 26;
        double max = Math.Max(reading.Type == "Temperature" ? 100 : 1, values.Max());
        var points = new PointCollection();
        for (int index = 0; index < values.Count; index++)
        {
            double x = 8 + index * ((width - 16) / Math.Max(1, values.Count - 1));
            double y = height - 10 - (Math.Max(0, values[index]) / max) * (height - top - 16);
            points.Add(new Point(x, y));
        }

        canvas.Children.Add(new Polyline
        {
            Points = points,
            Stroke = Brush(ColorFor(reading)),
            StrokeThickness = 2
        });
        var value = new TextBlock { Text = reading.DisplayValue, Foreground = Brush(ColorFor(reading)), FontWeight = FontWeights.Bold };
        Canvas.SetRight(value, 8);
        Canvas.SetTop(value, 4);
        canvas.Children.Add(value);
    }

    private static bool IsOverviewReading(SensorReading reading)
    {
        return reading.Type is "Temperature" or "Load" or "Fan" or "Power"
               && (ContainsAny(reading, "cpu", "gpu", "memory", "fan", "ssd", "hdd", "nvme", "drive", "disk")
                   || reading.Type == "Temperature");
    }

    private static bool MatchesCategory(SensorReading reading, string category)
    {
        return category switch
        {
            "All" => true,
            "Temperatures" => reading.Type == "Temperature",
            "Load" => reading.Type == "Load",
            "Clocks" => IsClockSensor(reading),
            "Voltage" => reading.Type == "Voltage",
            "Power" => reading.Type is "Power" or "Current" or "Energy",
            "Fans" => IsFanSensor(reading),
            "Memory/Data" => IsMemorySensor(reading),
            "Drives" => IsDriveSensor(reading),
            "Network" => IsNetworkSensor(reading),
            "Sensor Types" => true,
            _ => false
        };
    }

    private static bool IsClockSensor(SensorReading reading)
    {
        return reading.Type.Contains("Clock", StringComparison.OrdinalIgnoreCase)
               || reading.Type.Contains("Frequency", StringComparison.OrdinalIgnoreCase)
               || reading.Type.Contains("Factor", StringComparison.OrdinalIgnoreCase)
               || ContainsAny(reading, "clock", "frequency", "mhz", "ghz", "bus speed", "core #", "/clock/", "/frequency/");
    }

    private static bool IsFanSensor(SensorReading reading)
    {
        return reading.Type.Contains("Fan", StringComparison.OrdinalIgnoreCase)
               || reading.Type.Contains("Control", StringComparison.OrdinalIgnoreCase)
               || reading.Type.Contains("Flow", StringComparison.OrdinalIgnoreCase)
               || ContainsAny(reading, "fan", "pump", "cooler", "rpm", "pwm", "/fan/", "/control/", "/flow/");
    }

    private static bool IsMemorySensor(SensorReading reading)
    {
        return reading.Type.Contains("Data", StringComparison.OrdinalIgnoreCase)
               || reading.Type.Contains("Level", StringComparison.OrdinalIgnoreCase)
               || reading.HardwareType.Contains("Memory", StringComparison.OrdinalIgnoreCase)
               || ContainsAny(reading, "memory", "ram", "vram", "virtual", "d3d shared", "dedicated", "used", "free", "total", "/data/", "/smalldata/", "/level/");
    }

    private static bool IsDriveSensor(SensorReading reading)
    {
        return reading.HardwareType.Contains("Storage", StringComparison.OrdinalIgnoreCase)
               || reading.Identifier.StartsWith("/hdd/", StringComparison.OrdinalIgnoreCase)
               || reading.Identifier.StartsWith("/ssd/", StringComparison.OrdinalIgnoreCase)
               || reading.Identifier.StartsWith("/nvme/", StringComparison.OrdinalIgnoreCase)
               || ContainsAny(reading, "ssd", "hdd", "nvme", "drive", "disk", "storage", "smart", "read", "write", "used space", "/hdd/", "/ssd/", "/nvme/");
    }

    private List<SensorReading> NetworkRows(List<SensorReading> readings)
    {
        return readings
            .Where(IsNetworkSensor)
            .OrderByDescending(reading => pinnedSensors.Contains(reading.Identifier))
            .ThenBy(reading => NetworkRank(reading))
            .ThenByDescending(GraphValue)
            .ThenBy(reading => reading.Hardware)
            .ThenBy(reading => reading.Name)
            .ToList();
    }

    private static int NetworkRank(SensorReading reading)
    {
        string text = $"{reading.Name} {reading.Identifier}".ToLowerInvariant();
        if (text.Contains("download speed")) return 0;
        if (text.Contains("upload speed")) return 1;
        if (text.Contains("network utilization")) return 2;
        if (text.Contains("downloaded")) return 3;
        if (text.Contains("uploaded")) return 4;
        return 9;
    }

    private static bool IsNetworkSensor(SensorReading reading)
    {
        return reading.HardwareType.Contains("Network", StringComparison.OrdinalIgnoreCase)
               || reading.Identifier.StartsWith("/nic/", StringComparison.OrdinalIgnoreCase)
               || ContainsAny(reading,
                   "network",
                   "ethernet",
                   "wi-fi",
                   "wifi",
                   "wireless",
                   "wlan",
                   "lan",
                   "nic",
                   "adapter",
                   "realtek",
                   "intel(r) ethernet",
                   "killer",
                   "broadcom",
                   "mediatek",
                   "qualcomm",
                   "marvell",
                   "throughput",
                   "download",
                   "upload",
                   "receive",
                   "received",
                   "transmit",
                   "transmitted",
                   "data uploaded",
                   "data downloaded",
                   "bytes sent",
                   "bytes received",
                   "/throughput/",
                   "/network/");
    }

    private static SensorReading? Hottest(List<SensorReading> readings, string type, params string[] terms)
    {
        return readings
            .Where(reading => reading.Type == type)
            .Where(reading => terms.Length == 0 || ContainsAny(reading, terms))
            .OrderByDescending(reading => reading.Value)
            .FirstOrDefault();
    }

    private static SensorReading? Highest(List<SensorReading> readings, string type, params string[] terms)
    {
        return readings
            .Where(reading => string.IsNullOrWhiteSpace(type) || reading.Type == type)
            .Where(reading => terms.Length == 0 || ContainsAny(reading, terms))
            .OrderByDescending(reading => reading.Value)
            .FirstOrDefault();
    }

    private static bool ContainsAny(SensorReading reading, params string[] terms)
    {
        string haystack = $"{reading.Hardware} {reading.Name} {reading.Identifier} {reading.HardwareType}".ToLowerInvariant();
        return terms.Any(term => haystack.Contains(term, StringComparison.OrdinalIgnoreCase));
    }

    private static double GraphValue(SensorReading reading)
    {
        if (double.IsNaN(reading.Value) || double.IsInfinity(reading.Value))
            return 0;
        return reading.Type == "Temperature" ? reading.Value : Math.Abs(reading.Value);
    }

    private int SeverityFor(SensorReading reading)
    {
        double value = GraphValue(reading);
        if (reading.Type == "Temperature")
        {
            double watch = ContainsAny(reading, "gpu") ? gpuWatchThreshold : cpuWatchThreshold;
            double hot = ContainsAny(reading, "gpu") ? gpuHotThreshold : cpuHotThreshold;
            if (reading.Value >= hot) return 2;
            if (reading.Value >= watch) return 1;
            return 0;
        }

        if (reading.Type is "Load" or "Control" or "Level")
        {
            if (value >= 95) return 2;
            if (value >= 85) return 1;
            return 0;
        }

        if (reading.Type == "Fan")
            return reading.Value <= 0 ? 1 : 0;

        return 0;
    }

    private static string SeverityColor(int severity)
    {
        return severity >= 2 ? "#ff5c77" : severity == 1 ? "#f6c85f" : "#33d17a";
    }

    private static double NumericPrefix(string text)
    {
        string numeric = new(text.Where(character => char.IsDigit(character) || character is '-' or '+' or '.').ToArray());
        return double.TryParse(numeric, out double value) ? value : 0;
    }

    private static double ParseThreshold(string text, double fallback)
    {
        return double.TryParse(text, out double value) ? value : fallback;
    }

    private static string NormalizeTemperatureUnit(string? unit)
    {
        return string.Equals(unit, "F", StringComparison.OrdinalIgnoreCase)
               || string.Equals(unit, "Fahrenheit", StringComparison.OrdinalIgnoreCase)
            ? "F"
            : "C";
    }

    private static string TemperatureUnitDisplayName(string unit)
    {
        return NormalizeTemperatureUnit(unit) == "F" ? "Fahrenheit" : "Celsius";
    }

    private static double ChangeFloor(SensorReading reading)
    {
        return reading.Type switch
        {
            "Temperature" => 2,
            "Load" or "Level" or "Control" => 8,
            "Fan" => 100,
            "Clock" => 100,
            _ => 1
        };
    }

    private static string EscapeHtml(string text)
    {
        return text
            .Replace("&", "&amp;")
            .Replace("<", "&lt;")
            .Replace(">", "&gt;")
            .Replace("\"", "&quot;");
    }

    private static string Csv(string text)
    {
        return $"\"{text.Replace("\"", "\"\"")}\"";
    }

    private static string UnitFor(SensorType type)
    {
        return type switch
        {
            SensorType.Voltage => "V",
            SensorType.Current => "A",
            SensorType.Power => "W",
            SensorType.Clock => "MHz",
            SensorType.Temperature => "C",
            SensorType.Load => "%",
            SensorType.Fan => "RPM",
            SensorType.Flow => "L/h",
            SensorType.Control => "%",
            SensorType.Level => "%",
            SensorType.Data => "GB",
            SensorType.SmallData => "MB",
            SensorType.Throughput => "B/s",
            SensorType.Frequency => "Hz",
            _ => ""
        };
    }

    private static string UnitFor(string sensorType, string displayValue)
    {
        string text = $"{sensorType} {displayValue}".ToLowerInvariant();
        if (text.Contains("°c") || sensorType.Equals("Temperature", StringComparison.OrdinalIgnoreCase)) return "C";
        if (text.Contains("rpm") || sensorType.Equals("Fan", StringComparison.OrdinalIgnoreCase)) return "RPM";
        if (text.Contains("mhz") || sensorType.Equals("Clock", StringComparison.OrdinalIgnoreCase)) return "MHz";
        if (text.Contains("ghz")) return "GHz";
        if (text.Contains(" v") || sensorType.Equals("Voltage", StringComparison.OrdinalIgnoreCase)) return "V";
        if (text.Contains(" w") || sensorType.Equals("Power", StringComparison.OrdinalIgnoreCase)) return "W";
        if (text.Contains(" a") || sensorType.Equals("Current", StringComparison.OrdinalIgnoreCase)) return "A";
        if (text.Contains("%") || sensorType is "Load" or "Control" or "Level") return "%";
        if (text.Contains("gb") || sensorType.Equals("Data", StringComparison.OrdinalIgnoreCase)) return "GB";
        if (text.Contains("mb") || sensorType.Equals("SmallData", StringComparison.OrdinalIgnoreCase)) return "MB";
        if (text.Contains("kb/s") || text.Contains("mb/s") || text.Contains("b/s") || sensorType.Equals("Throughput", StringComparison.OrdinalIgnoreCase)) return "B/s";
        return "";
    }

    private static string FormatValue(double value, string unit)
    {
        return MainWindowDisplay.Format(value, unit);
    }

    private static string BytesPerSecond(double value)
    {
        string[] units = ["B/s", "KB/s", "MB/s", "GB/s", "TB/s"];
        double scaled = Math.Abs(value);
        int index = 0;
        while (scaled >= 1024 && index < units.Length - 1)
        {
            scaled /= 1024;
            index++;
        }
        return $"{Math.Sign(value) * scaled:0.##} {units[index]}";
    }

    private static string ColorFor(SensorReading reading)
    {
        if (reading.Type == "Temperature")
        {
            if (reading.Value >= 90) return "#ff5c77";
            if (reading.Value >= 75) return "#ff9f43";
            if (reading.Value >= 60) return "#f6c85f";
            return "#33d17a";
        }

        double value = Math.Abs(reading.Value);
        if (reading.Type is "Load" or "Control")
        {
            if (value >= 90) return "#ff5c77";
            if (value >= 75) return "#ff9f43";
            if (value >= 55) return "#f6c85f";
        }
        return reading.Type switch
        {
            "Power" => "#f6c85f",
            "Voltage" => "#b084f5",
            "Clock" => "#5dade2",
            "Fan" => "#6ee7f9",
            "Throughput" => "#33d17a",
            _ => "#5dade2"
        };
    }

    private static SolidColorBrush Brush(string color)
    {
        return new SolidColorBrush((Color)ColorConverter.ConvertFromString(color));
    }
}

public sealed record SensorReading(
    string Hardware,
    string Name,
    string Type,
    double Value,
    string Unit,
    string Identifier,
    string HardwareType)
{
    public string DisplayValue => MainWindowDisplay.Format(Value, Unit);
}

public sealed record CategoryView(
    TabControl Tabs,
    StackPanel BarsPanel,
    StackPanel CardsPanel,
    StackPanel VerticalBarsPanel,
    StackPanel HeatPanel,
    StackPanel RadarPanel,
    StackPanel MatrixPanel,
    StackPanel TreemapPanel,
    StackPanel MoversPanel,
    StackPanel HistoryPanel);

public sealed record AnalysisView(
    StackPanel Root,
    WrapPanel HealthCards,
    StackPanel ScorePanel,
    StackPanel ProblemPanel,
    StackPanel TrendPanel,
    StackPanel BottleneckPanel,
    StackPanel ThermalPanel,
    StackPanel TimelinePanel,
    StackPanel ChangesPanel,
    StackPanel AlertPanel,
    StackPanel TopDevicesPanel,
    StackPanel RecommendationPanel);

public sealed record NetworkDashboardView(
    StackPanel Root,
    StackPanel StatusPanel,
    StackPanel HistoryPanel,
    StackPanel AdapterPanel,
    StackPanel RadarPanel);

public sealed record AnalysisFinding(string Title, string Detail, string Value, int Severity);

public sealed record SensorLogEntry(DateTime Time, string Hardware, string Name, string Type, double Value, string Unit, string Identifier);

public sealed record AlertEntry(DateTime Time, string Title, string Detail, string Value, int Severity);

public sealed record RadarMetric(string Label, double Percent, string ValueText);

public sealed record HardwareVisualizerSettings(
    string Theme,
    string Workload,
    string TemperatureUnit,
    bool QuietMode,
    double CpuWatch,
    double CpuHot,
    double GpuWatch,
    double GpuHot,
    bool CompactMode,
    bool SettingsExpanded);

public sealed class MiniMonitorWindow : Window
{
    private readonly TextBlock cpu = ValueBlock();
    private readonly TextBlock gpu = ValueBlock();
    private readonly TextBlock memory = ValueBlock();
    private readonly TextBlock network = ValueBlock();
    private readonly List<TextBlock> labels = [];
    private readonly StackPanel panel = new() { Margin = new Thickness(12) };

    public MiniMonitorWindow()
    {
        Title = "Mini Monitor";
        Width = 260;
        Height = 190;
        Topmost = true;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#0f141b"));

        panel.Children.Add(Row("CPU", cpu));
        panel.Children.Add(Row("GPU", gpu));
        panel.Children.Add(Row("Memory", memory));
        panel.Children.Add(Row("Network", network));
        Content = panel;
    }

    public void UpdateValues(string cpuValue, string gpuValue, string memoryValue, string networkValue)
    {
        cpu.Text = cpuValue;
        gpu.Text = gpuValue;
        memory.Text = memoryValue;
        network.Text = networkValue;
    }

    public void ApplyTheme(string accent, string panelColor, string borderColor)
    {
        Background = Brush(panelColor);
        foreach (TextBlock value in new[] { cpu, gpu, memory, network })
            value.Foreground = Brush(accent);
        foreach (TextBlock label in labels)
            label.Foreground = Brush("#9aa8b8");
    }

    private Grid Row(string label, TextBlock value)
    {
        var grid = new Grid { Margin = new Thickness(0, 0, 0, 8) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(80) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var labelBlock = new TextBlock { Text = label, Foreground = Brush("#9aa8b8"), FontWeight = FontWeights.SemiBold };
        labels.Add(labelBlock);
        grid.Children.Add(labelBlock);
        Grid.SetColumn(value, 1);
        grid.Children.Add(value);
        return grid;
    }

    private static TextBlock ValueBlock()
    {
        return new TextBlock
        {
            Foreground = Brush("#6ee7f9"),
            FontWeight = FontWeights.Bold,
            HorizontalAlignment = HorizontalAlignment.Right
        };
    }

    private static SolidColorBrush Brush(string color)
    {
        return new SolidColorBrush((Color)ColorConverter.ConvertFromString(color));
    }
}

public static class MainWindowDisplay
{
    public static string TemperatureUnit { get; set; } = "C";

    public static string Format(double value, string unit)
    {
        if (double.IsNaN(value) || double.IsInfinity(value))
            return "--";
        if (unit == "B/s")
            return BytesPerSecond(value);
        if (unit == "C")
        {
            if (string.Equals(TemperatureUnit, "F", StringComparison.OrdinalIgnoreCase))
                return $"{CelsiusToFahrenheit(value):0.##} F";
            return $"{value:0.##} C";
        }
        return string.IsNullOrWhiteSpace(unit) ? $"{value:0.##}" : $"{value:0.##} {unit}";
    }

    private static double CelsiusToFahrenheit(double celsius)
    {
        return celsius * 9 / 5 + 32;
    }

    private static string BytesPerSecond(double value)
    {
        if (double.IsNaN(value) || double.IsInfinity(value))
            return "--";
        string[] units = ["B/s", "KB/s", "MB/s", "GB/s", "TB/s"];
        double scaled = Math.Abs(value);
        int index = 0;
        while (scaled >= 1024 && index < units.Length - 1)
        {
            scaled /= 1024;
            index++;
        }
        return $"{Math.Sign(value) * scaled:0.##} {units[index]}";
    }
}
