using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using PacketDotNet;
using SharpPcap;
using SharpPcap.LibPcap;

namespace HardwareVisualizer;

internal sealed class NetworkToolsView : IDisposable
{
    private static readonly Brush Panel = Brush("#171d26");
    private static readonly Brush Border = Brush("#2f3b4c");
    private static readonly Brush Text = Brush("#dbe7f5");
    private static readonly Brush Muted = Brush("#9aa8b8");
    private static readonly Brush Accent = Brush("#6ee7f9");

    private readonly ObservableCollection<ConnectionRow> connections = [];
    private readonly ObservableCollection<PortRow> ports = [];
    private readonly ObservableCollection<DeviceRow> devices = [];
    private readonly ObservableCollection<PacketRow> packets = [];
    private readonly ObservableCollection<ProtocolRow> protocols = [];
    private readonly ConcurrentDictionary<string, (long Packets, long Bytes)> protocolCounts = new(StringComparer.OrdinalIgnoreCase);
    private readonly ComboBox captureAdapter = InputCombo();
    private readonly TextBox captureFilter = Input("ip or arp");
    private readonly TextBlock captureStatus = Status();
    private CancellationTokenSource? scanCancellation;
    private ICaptureDevice? activeCapture;
    private long packetSequence;

    public NetworkToolsView()
    {
        Root = new TabControl { Margin = new Thickness(8), Background = Brush("#0f141c"), Foreground = Text };
        Root.Items.Add(Tab("Diagnostics", CreateDiagnostics()));
        Root.Items.Add(Tab("Connections", CreateConnections()));
        Root.Items.Add(Tab("Port Scanner", CreatePortScanner()));
        Root.Items.Add(Tab("Device Discovery", CreateDiscovery()));
        Root.Items.Add(Tab("Packet Capture", CreateCapture()));
    }

    public TabControl Root { get; }

    public void Dispose()
    {
        scanCancellation?.Cancel();
        StopCapture();
    }

    private UIElement CreateDiagnostics()
    {
        var target = Input("1.1.1.1 or hostname");
        var output = Output();
        var buttons = new WrapPanel();
        buttons.Children.Add(Action("Ping", async () => await RunPing(target.Text, output)));
        buttons.Children.Add(Action("Traceroute", async () => await RunTrace(target.Text, output)));
        buttons.Children.Add(Action("DNS Lookup", async () => await RunDns(target.Text, output)));
        return Page("Network Diagnostics", "Ping, trace a route, or resolve forward and reverse DNS. These operations run only when requested.", Row(target, buttons), output);
    }

    private UIElement CreateConnections()
    {
        var grid = GridFor(connections);
        AddColumn(grid, "Protocol", "Protocol", 75);
        AddColumn(grid, "Local endpoint", "Local", 220);
        AddColumn(grid, "Remote endpoint", "Remote", 220);
        AddColumn(grid, "State", "State", 110);
        var status = Status();
        var refresh = Action("Refresh Connections", () => RefreshConnections(status));
        RefreshConnections(status);
        return Page("Socket / Connection Viewer", "Current Windows TCP connections, TCP listeners, and UDP listeners.", Row(refresh, status), grid);
    }

    private UIElement CreatePortScanner()
    {
        var target = Input("192.168.1.1");
        var portList = Input("22,53,80,443,445,3389,8080");
        var status = Status();
        var grid = GridFor(ports);
        AddColumn(grid, "Port", "Port", 75);
        AddColumn(grid, "Service", "Service", 130);
        AddColumn(grid, "Status", "Status", 100);
        AddColumn(grid, "Latency", "Latency", 100);
        var start = Action("Scan Selected Ports", async () => await ScanPorts(target.Text, portList.Text, status));
        return Page("Selected Port Scanner", "Checks only the ports entered below. Private/local targets are allowed by default; public targets are rejected.", Row(Labeled("Target", target), Labeled("Ports", portList), start), status, grid);
    }

    private UIElement CreateDiscovery()
    {
        var subnet = Input(DefaultSubnet());
        var status = Status();
        var grid = GridFor(devices);
        AddColumn(grid, "IP address", "Address", 140);
        AddColumn(grid, "Hostname", "Hostname", 220);
        AddColumn(grid, "MAC address", "Mac", 160);
        AddColumn(grid, "Latency", "Latency", 90);
        AddColumn(grid, "Services", "Services", 260);
        var start = Action("Discover Devices", async () => await Discover(subnet.Text, status));
        var cancel = Action("Cancel", () => scanCancellation?.Cancel());
        return Page("Private Network Device Discovery", "Scans one private IPv4 /24 subnet using ping and a short list of common service ports. Nothing runs automatically.", Row(Labeled("Subnet", subnet), start, cancel), status, grid);
    }

    private UIElement CreateCapture()
    {
        var packetGrid = GridFor(packets);
        AddColumn(packetGrid, "#", "Number", 65);
        AddColumn(packetGrid, "Time", "Time", 105);
        AddColumn(packetGrid, "Protocol", "Protocol", 85);
        AddColumn(packetGrid, "Source", "Source", 210);
        AddColumn(packetGrid, "Destination", "Destination", 210);
        AddColumn(packetGrid, "Length", "Length", 80);
        AddColumn(packetGrid, "Details", "Details", 340);

        var protocolGrid = GridFor(protocols, 180);
        AddColumn(protocolGrid, "Protocol", "Protocol", 110);
        AddColumn(protocolGrid, "Packets", "Packets", 90);
        AddColumn(protocolGrid, "Bytes", "Bytes", 110);

        var refresh = Action("Refresh Adapters", LoadCaptureAdapters);
        var start = Action("Start Capture", StartCapture);
        var stop = Action("Stop", StopCapture);
        var clear = Action("Clear", () => { packets.Clear(); protocols.Clear(); protocolCounts.Clear(); packetSequence = 0; });
        LoadCaptureAdapters();
        var split = new Grid();
        split.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        split.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(300) });
        Grid.SetColumn(packetGrid, 0);
        Grid.SetColumn(protocolGrid, 1);
        split.Children.Add(packetGrid);
        split.Children.Add(protocolGrid);
        return Page("Packet Capture and Protocol Inspection", "Optional Npcap/SharpPcap capture. Only packet headers and protocol metadata are displayed; payload contents are not shown.", Row(Labeled("Adapter", captureAdapter), Labeled("Capture filter", captureFilter), refresh, start, stop, clear), captureStatus, split);
    }

    private async Task RunPing(string target, TextBox output)
    {
        output.Text = "Pinging...";
        try
        {
            using var ping = new Ping();
            var builder = new StringBuilder();
            for (int i = 0; i < 4; i++)
            {
                PingReply reply = await ping.SendPingAsync(target.Trim(), 2000);
                builder.AppendLine(reply.Status == IPStatus.Success
                    ? $"Reply from {reply.Address}: {reply.RoundtripTime} ms, {reply.Buffer.Length} bytes"
                    : $"{reply.Status}");
            }
            output.Text = builder.ToString();
        }
        catch (Exception exception) { output.Text = exception.Message; }
    }

    private async Task RunTrace(string target, TextBox output)
    {
        output.Text = "Tracing route...";
        try
        {
            string host = target.Trim();
            var builder = new StringBuilder();
            using var ping = new Ping();
            byte[] buffer = new byte[32];
            for (int ttl = 1; ttl <= 30; ttl++)
            {
                PingReply reply = await ping.SendPingAsync(host, 2500, buffer, new PingOptions(ttl, true));
                string address = reply.Address?.ToString() ?? "*";
                builder.AppendLine($"{ttl,2}  {(reply.Status == IPStatus.TimedOut ? "*" : reply.RoundtripTime + " ms"),8}  {address}");
                output.Text = builder.ToString();
                if (reply.Status == IPStatus.Success) break;
            }
        }
        catch (Exception exception) { output.Text = exception.Message; }
    }

    private async Task RunDns(string target, TextBox output)
    {
        output.Text = "Resolving...";
        try
        {
            IPHostEntry entry = await Dns.GetHostEntryAsync(target.Trim());
            output.Text = $"Hostname: {entry.HostName}\nAliases: {string.Join(", ", entry.Aliases)}\nAddresses:\n{string.Join("\n", entry.AddressList.Select(address => "  " + address))}";
        }
        catch (Exception exception) { output.Text = exception.Message; }
    }

    private void RefreshConnections(TextBlock status)
    {
        connections.Clear();
        try
        {
            IPGlobalProperties properties = IPGlobalProperties.GetIPGlobalProperties();
            foreach (TcpConnectionInformation item in properties.GetActiveTcpConnections())
                connections.Add(new("TCP", item.LocalEndPoint.ToString(), item.RemoteEndPoint.ToString(), item.State.ToString()));
            foreach (IPEndPoint item in properties.GetActiveTcpListeners())
                connections.Add(new("TCP", item.ToString(), "—", "Listen"));
            foreach (IPEndPoint item in properties.GetActiveUdpListeners())
                connections.Add(new("UDP", item.ToString(), "—", "Listen"));
            status.Text = $"{connections.Count} connection/listener rows refreshed at {DateTime.Now:T}.";
        }
        catch (Exception exception) { status.Text = exception.Message; }
    }

    private async Task ScanPorts(string targetText, string portText, TextBlock status)
    {
        ports.Clear();
        try
        {
            IPAddress address = await ResolvePrivateTarget(targetText);
            int[] selected = ParsePorts(portText).Take(256).ToArray();
            if (selected.Length == 0) throw new InvalidOperationException("Enter one or more ports.");
            status.Text = $"Scanning {selected.Length} selected port(s) on {address}...";
            var results = await Task.WhenAll(selected.Select(port => CheckPort(address, port, CancellationToken.None)));
            foreach (PortRow row in results.OrderBy(row => row.Port)) ports.Add(row);
            status.Text = $"Finished. {results.Count(row => row.Status == "Open")} open of {results.Length} checked.";
        }
        catch (Exception exception) { status.Text = exception.Message; }
    }

    private async Task Discover(string subnetText, TextBlock status)
    {
        devices.Clear();
        scanCancellation?.Cancel();
        scanCancellation = new CancellationTokenSource();
        CancellationToken token = scanCancellation.Token;
        try
        {
            string prefix = ValidatePrivate24(subnetText);
            status.Text = $"Discovering {prefix}1–254...";
            using var gate = new SemaphoreSlim(32);
            var found = new ConcurrentBag<(IPAddress Address, long Latency)>();
            var tasks = Enumerable.Range(1, 254).Select(async last =>
            {
                await gate.WaitAsync(token);
                try
                {
                    var address = IPAddress.Parse(prefix + last);
                    using var ping = new Ping();
                    PingReply reply = await ping.SendPingAsync(address, 450);
                    if (reply.Status == IPStatus.Success) found.Add((address, reply.RoundtripTime));
                }
                catch { }
                finally { gate.Release(); }
            });
            await Task.WhenAll(tasks);
            var arp = ReadArpTable();
            foreach (var item in found.OrderBy(item => item.Address.ToString(), StringComparer.OrdinalIgnoreCase))
            {
                token.ThrowIfCancellationRequested();
                string hostname = "";
                try { hostname = (await Dns.GetHostEntryAsync(item.Address)).HostName; } catch { }
                int[] common = [22, 53, 80, 139, 443, 445, 3389, 8080];
                PortRow[] checks = await Task.WhenAll(common.Select(port => CheckPort(item.Address, port, token, 220)));
                string services = string.Join(", ", checks.Where(row => row.Status == "Open").Select(row => row.Service));
                devices.Add(new(item.Address.ToString(), hostname, arp.GetValueOrDefault(item.Address.ToString(), ""), item.Latency + " ms", services));
                status.Text = $"Found {devices.Count} responding device(s)...";
            }
            status.Text = $"Discovery complete: {devices.Count} responding device(s).";
        }
        catch (OperationCanceledException) { status.Text = $"Discovery cancelled; {devices.Count} device(s) retained."; }
        catch (Exception exception) { status.Text = exception.Message; }
    }

    private void LoadCaptureAdapters()
    {
        captureAdapter.Items.Clear();
        try
        {
            foreach (LibPcapLiveDevice device in LibPcapLiveDeviceList.Instance)
                captureAdapter.Items.Add(new ComboBoxItem { Content = string.IsNullOrWhiteSpace(device.Description) ? device.Name : device.Description, Tag = device });
            captureAdapter.SelectedIndex = captureAdapter.Items.Count > 0 ? 0 : -1;
            captureStatus.Text = captureAdapter.Items.Count > 0
                ? $"{captureAdapter.Items.Count} capture adapter(s) available. Capture is stopped."
                : "No capture adapters were found. Install Npcap, then refresh adapters.";
        }
        catch (Exception exception)
        {
            captureStatus.Text = $"Packet capture unavailable: {exception.Message}. Install Npcap from npcap.com and run as administrator if configured for admin-only capture.";
        }
    }

    private void StartCapture()
    {
        StopCapture();
        if (captureAdapter.SelectedItem is not ComboBoxItem { Tag: ICaptureDevice device })
        {
            captureStatus.Text = "Select a capture adapter first.";
            return;
        }
        try
        {
            device.OnPacketArrival += PacketArrived;
            device.Open(DeviceModes.Promiscuous, 1000);
            string filter = captureFilter.Text.Trim();
            if (!string.IsNullOrWhiteSpace(filter)) device.Filter = filter;
            device.StartCapture();
            activeCapture = device;
            captureStatus.Text = $"Capturing on {device.Description}.";
        }
        catch (Exception exception)
        {
            captureStatus.Text = $"Could not start capture: {exception.Message}";
            try { device.Close(); } catch { }
        }
    }

    private void StopCapture()
    {
        if (activeCapture is null) return;
        try { activeCapture.StopCapture(); } catch { }
        try { activeCapture.Close(); } catch { }
        activeCapture.OnPacketArrival -= PacketArrived;
        activeCapture = null;
        captureStatus.Text = $"Capture stopped. {packetSequence} packet(s) observed.";
    }

    private void PacketArrived(object sender, PacketCapture capture)
    {
        RawCapture raw = capture.GetPacket();
        Packet packet;
        try { packet = Packet.ParsePacket(raw.LinkLayerType, raw.Data); }
        catch { return; }
        string protocol = "Other", source = "", destination = "", details = "";
        var ip = packet.Extract<IPPacket>();
        if (ip is not null)
        {
            source = ip.SourceAddress.ToString();
            destination = ip.DestinationAddress.ToString();
            protocol = ip.Protocol.ToString();
            if (packet.Extract<TcpPacket>() is { } tcp)
            {
                protocol = tcp.DestinationPort == 443 || tcp.SourcePort == 443 ? "TLS/TCP" : "TCP";
                details = $"{tcp.SourcePort} → {tcp.DestinationPort} {tcp.Flags}";
            }
            else if (packet.Extract<UdpPacket>() is { } udp)
            {
                protocol = udp.DestinationPort == 53 || udp.SourcePort == 53 ? "DNS" : udp.DestinationPort is 67 or 68 || udp.SourcePort is 67 or 68 ? "DHCP" : "UDP";
                details = $"{udp.SourcePort} → {udp.DestinationPort}";
            }
        }
        else if (packet.Extract<ArpPacket>() is { } arp)
        {
            protocol = "ARP";
            source = arp.SenderProtocolAddress.ToString();
            destination = arp.TargetProtocolAddress.ToString();
            details = arp.Operation.ToString();
        }
        long number = Interlocked.Increment(ref packetSequence);
        protocolCounts.AddOrUpdate(protocol, (1, raw.Data.Length), (_, old) => (old.Packets + 1, old.Bytes + raw.Data.Length));
        Application.Current.Dispatcher.BeginInvoke(() =>
        {
            packets.Insert(0, new(number, raw.Timeval.Date.ToLocalTime().ToString("HH:mm:ss.fff"), protocol, source, destination, raw.Data.Length, details));
            while (packets.Count > 1000) packets.RemoveAt(packets.Count - 1);
            protocols.Clear();
            foreach (var item in protocolCounts.OrderByDescending(item => item.Value.Bytes))
                protocols.Add(new(item.Key, item.Value.Packets, item.Value.Bytes));
            captureStatus.Text = $"Capturing: {packetSequence} packet(s), {protocolCounts.Values.Sum(item => item.Bytes):N0} bytes.";
        });
    }

    private static async Task<IPAddress> ResolvePrivateTarget(string text)
    {
        IPAddress[] addresses = await Dns.GetHostAddressesAsync(text.Trim());
        IPAddress? address = addresses.FirstOrDefault(item => item.AddressFamily == AddressFamily.InterNetwork);
        if (address is null) throw new InvalidOperationException("No IPv4 address was found for that target.");
        if (!IsPrivate(address) && !IPAddress.IsLoopback(address))
            throw new InvalidOperationException("Public targets are disabled. Enter a private/local IPv4 address or hostname.");
        return address;
    }

    private static string ValidatePrivate24(string text)
    {
        Match match = Regex.Match(text.Trim(), @"^(\d{1,3})\.(\d{1,3})\.(\d{1,3})\.(?:0|\*)\s*(?:/24)?$");
        if (!match.Success) throw new InvalidOperationException("Enter a private /24 subnet such as 192.168.1.0/24.");
        var address = IPAddress.Parse($"{match.Groups[1]}.{match.Groups[2]}.{match.Groups[3]}.1");
        if (!IsPrivate(address)) throw new InvalidOperationException("Only private IPv4 subnets are allowed.");
        return $"{match.Groups[1]}.{match.Groups[2]}.{match.Groups[3]}.";
    }

    private static bool IsPrivate(IPAddress address)
    {
        byte[] b = address.GetAddressBytes();
        return b.Length == 4 && (b[0] == 10 || b[0] == 127 || b[0] == 192 && b[1] == 168 || b[0] == 172 && b[1] is >= 16 and <= 31 || b[0] == 169 && b[1] == 254);
    }

    private static IEnumerable<int> ParsePorts(string text)
    {
        var result = new SortedSet<int>();
        foreach (string token in text.Split([',', ' ', ';'], StringSplitOptions.RemoveEmptyEntries))
        {
            if (token.Contains('-'))
            {
                string[] ends = token.Split('-', 2);
                if (int.TryParse(ends[0], out int start) && int.TryParse(ends[1], out int end))
                    for (int port = Math.Max(1, start); port <= Math.Min(65535, end); port++) result.Add(port);
            }
            else if (int.TryParse(token, out int port) && port is >= 1 and <= 65535) result.Add(port);
        }
        return result;
    }

    private static async Task<PortRow> CheckPort(IPAddress address, int port, CancellationToken token, int timeout = 650)
    {
        var watch = Stopwatch.StartNew();
        try
        {
            using var client = new TcpClient(address.AddressFamily);
            await client.ConnectAsync(address, port, token).AsTask().WaitAsync(TimeSpan.FromMilliseconds(timeout), token);
            return new(port, ServiceName(port), "Open", watch.ElapsedMilliseconds + " ms");
        }
        catch { return new(port, ServiceName(port), "Closed", "—"); }
    }

    private static string ServiceName(int port) => port switch
    {
        20 or 21 => "FTP", 22 => "SSH", 23 => "Telnet", 25 => "SMTP", 53 => "DNS", 67 or 68 => "DHCP",
        80 => "HTTP", 110 => "POP3", 123 => "NTP", 139 => "NetBIOS", 143 => "IMAP", 443 => "HTTPS",
        445 => "SMB", 554 => "RTSP", 631 => "IPP", 3389 => "RDP", 5900 => "VNC", 8080 => "HTTP-Alt", _ => port.ToString()
    };

    private static Dictionary<string, string> ReadArpTable()
    {
        var result = new Dictionary<string, string>();
        try
        {
            using var process = Process.Start(new ProcessStartInfo("arp.exe", "-a") { RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true });
            string output = process?.StandardOutput.ReadToEnd() ?? "";
            process?.WaitForExit(2000);
            foreach (Match match in Regex.Matches(output, @"(?m)^\s*(\d+\.\d+\.\d+\.\d+)\s+([0-9a-fA-F-]{17})\s+")) result[match.Groups[1].Value] = match.Groups[2].Value.ToUpperInvariant();
        }
        catch { }
        return result;
    }

    private static string DefaultSubnet()
    {
        foreach (NetworkInterface nic in NetworkInterface.GetAllNetworkInterfaces().Where(item => item.OperationalStatus == OperationalStatus.Up))
            foreach (UnicastIPAddressInformation address in nic.GetIPProperties().UnicastAddresses.Where(item => item.Address.AddressFamily == AddressFamily.InterNetwork))
                if (IsPrivate(address.Address)) { byte[] b = address.Address.GetAddressBytes(); return $"{b[0]}.{b[1]}.{b[2]}.0/24"; }
        return "192.168.1.0/24";
    }

    private static TabItem Tab(string header, UIElement content) => new() { Header = header, Content = content };
    private static ScrollViewer Page(string title, string description, params UIElement[] children)
    {
        var panel = new StackPanel { Margin = new Thickness(12) };
        panel.Children.Add(new TextBlock { Text = title, FontSize = 20, FontWeight = FontWeights.Bold, Foreground = Text });
        panel.Children.Add(new TextBlock { Text = description, Foreground = Muted, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 12) });
        foreach (UIElement child in children) panel.Children.Add(child);
        return new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto };
    }

    private static WrapPanel Row(params UIElement[] children)
    {
        var row = new WrapPanel { Margin = new Thickness(0, 0, 0, 10) };
        foreach (UIElement child in children) row.Children.Add(child);
        return row;
    }

    private static FrameworkElement Labeled(string label, Control control)
    {
        var panel = new StackPanel { Margin = new Thickness(0, 0, 8, 4) };
        panel.Children.Add(new TextBlock { Text = label, Foreground = Muted, Margin = new Thickness(0, 0, 0, 3) });
        panel.Children.Add(control);
        return panel;
    }

    private static Button Action(string text, Action action)
    {
        var button = new Button { Content = text, Padding = new Thickness(12, 7, 12, 7), Margin = new Thickness(0, 17, 8, 4), Background = Panel, Foreground = Text, BorderBrush = Border };
        button.Click += (_, _) => action();
        return button;
    }

    private static Button Action(string text, Func<Task> action)
    {
        var button = new Button { Content = text, Padding = new Thickness(12, 7, 12, 7), Margin = new Thickness(0, 17, 8, 4), Background = Panel, Foreground = Text, BorderBrush = Border };
        button.Click += async (_, _) => { button.IsEnabled = false; try { await action(); } finally { button.IsEnabled = true; } };
        return button;
    }

    private static TextBox Input(string value) => new() { Text = value, Width = 260, Padding = new Thickness(7), Margin = new Thickness(0, 0, 8, 4), Background = Brush("#111823"), Foreground = Text, BorderBrush = Border };
    private static ComboBox InputCombo() => new() { Width = 340, Padding = new Thickness(6), Margin = new Thickness(0, 0, 8, 4), Background = Brush("#111823"), Foreground = Text, BorderBrush = Border };
    private static TextBox Output() => new() { MinHeight = 280, IsReadOnly = true, AcceptsReturn = true, FontFamily = new FontFamily("Consolas"), TextWrapping = TextWrapping.NoWrap, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, Background = Brush("#111823"), Foreground = Text, BorderBrush = Border, Padding = new Thickness(8) };
    private static TextBlock Status() => new() { Foreground = Accent, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 10) };

    private static DataGrid GridFor<T>(ObservableCollection<T> source, double minHeight = 350) => new()
    {
        ItemsSource = source, AutoGenerateColumns = false, IsReadOnly = true, CanUserAddRows = false,
        MinHeight = minHeight, Background = Brush("#111823"), Foreground = Text, BorderBrush = Border,
        GridLinesVisibility = DataGridGridLinesVisibility.Horizontal, AlternatingRowBackground = Brush("#151d28"),
        RowBackground = Brush("#111823"), HeadersVisibility = DataGridHeadersVisibility.Column
    };

    private static void AddColumn(DataGrid grid, string header, string path, double width) => grid.Columns.Add(new DataGridTextColumn { Header = header, Binding = new Binding(path), Width = width });
    private static Brush Brush(string value) => new SolidColorBrush((Color)ColorConverter.ConvertFromString(value));

    private sealed record ConnectionRow(string Protocol, string Local, string Remote, string State);
    private sealed record PortRow(int Port, string Service, string Status, string Latency);
    private sealed record DeviceRow(string Address, string Hostname, string Mac, string Latency, string Services);
    private sealed record PacketRow(long Number, string Time, string Protocol, string Source, string Destination, int Length, string Details);
    private sealed record ProtocolRow(string Protocol, long Packets, long Bytes);
}
