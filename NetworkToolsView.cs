using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;
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
    private readonly ComboBox discoveryAdapter = InputCombo();
    private readonly ObservableCollection<PacketRow> packets = [];
    private readonly ObservableCollection<ProtocolRow> protocols = [];
    private readonly ObservableCollection<DeviceBandwidthRow> deviceBandwidth = [];
    private readonly ConcurrentDictionary<string, (long Packets, long Bytes)> protocolCounts = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, (long Download, long Upload)> deviceTraffic = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, (long Download, long Upload)> previousDeviceTraffic = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<RawCapture> capturedPackets = [];
    private readonly object captureSync = new();
    private readonly DispatcherTimer bandwidthTimer = new() { Interval = TimeSpan.FromSeconds(2) };
    private readonly DispatcherTimer routerTimer = new() { Interval = TimeSpan.FromSeconds(2) };
    private readonly HttpClient routerClient = new() { Timeout = TimeSpan.FromSeconds(2) };
    private readonly HashSet<string> localAddresses = GetLocalAddresses();
    private readonly ComboBox captureAdapter = InputCombo();
    private readonly TextBox captureFilter = Input("ip or arp");
    private readonly TextBlock captureStatus = Status();
    private CancellationTokenSource? scanCancellation;
    private ICaptureDevice? activeCapture;
    private long packetSequence;
    private TextBlock? routerBandwidthStatus;
    private TextBlock? routerDownload;
    private TextBlock? routerUpload;
    private ulong? previousRouterReceived;
    private ulong? previousRouterSent;
    private DateTime previousRouterSample;

    public NetworkToolsView()
    {
        Root = new TabControl { Margin = new Thickness(8), Background = Brush("#0f141c"), Foreground = Text };
        Root.Items.Add(Tab("Diagnostics", CreateDiagnostics()));
        Root.Items.Add(Tab("Connections", CreateConnections()));
        Root.Items.Add(Tab("Port Scanner", CreatePortScanner()));
        Root.Items.Add(Tab("Device Discovery", CreateDiscovery()));
        Root.Items.Add(Tab("Bandwidth by Device", CreateBandwidth()));
        Root.Items.Add(Tab("Packet Capture", CreateCapture()));
        bandwidthTimer.Tick += (_, _) => RefreshObservedBandwidth();
        routerTimer.Tick += async (_, _) => await RefreshRouterBandwidth();
        bandwidthTimer.Start();
    }

    public TabControl Root { get; }

    public void Dispose()
    {
        scanCancellation?.Cancel();
        bandwidthTimer.Stop();
        routerTimer.Stop();
        routerClient.Dispose();
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
        var filter = Input("");
        var status = Status();
        var grid = GridFor(devices);
        AddColumn(grid, "IP address", "Address", 140);
        AddColumn(grid, "Hostname", "Hostname", 220);
        AddColumn(grid, "MAC address", "Mac", 160);
        AddColumn(grid, "Vendor clue", "Vendor", 150);
        AddColumn(grid, "Likely device", "DeviceType", 150);
        AddColumn(grid, "Latency", "Latency", 90);
        AddColumn(grid, "Services", "Services", 260);
        var view = CollectionViewSource.GetDefaultView(devices);
        filter.TextChanged += (_, _) =>
        {
            string search = filter.Text.Trim();
            view.Filter = item => item is DeviceRow row && (search.Length == 0 || $"{row.Address} {row.Hostname} {row.Mac} {row.Vendor} {row.DeviceType} {row.Services}".Contains(search, StringComparison.OrdinalIgnoreCase));
        };
        LoadDiscoveryAdapters(subnet);
        discoveryAdapter.SelectionChanged += (_, _) =>
        {
            if (discoveryAdapter.SelectedItem is ComboBoxItem { Tag: NetworkScanTarget selected }) subnet.Text = selected.Range;
        };
        var start = Action("Discover Devices", async () => await Discover(subnet.Text, status));
        var cancel = Action("Cancel", () => scanCancellation?.Cancel());
        var export = Action("Export CSV", () => ExportDevices(status));
        return Page("Wireless / LAN Device Scanner", "Scans the selected Wi-Fi or Ethernet subnet using ping, the Windows ARP table, reverse DNS, and common service probes. Enter a private CIDR or range such as 10.0.0.0/24 or 10.0.0.1-10.0.0.100. Nothing runs automatically.", Row(Labeled("Network adapter", discoveryAdapter), Labeled("Private subnet or range", subnet), start, cancel, export), Row(Labeled("Filter results", filter)), status, grid);
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
        var save = Action("Save PCAP", SaveCapture);
        var clear = Action("Clear", () =>
        {
            packets.Clear();
            protocols.Clear();
            protocolCounts.Clear();
            lock (captureSync) capturedPackets.Clear();
            packetSequence = 0;
        });
        LoadCaptureAdapters();
        var split = new Grid();
        split.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        split.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(300) });
        Grid.SetColumn(packetGrid, 0);
        Grid.SetColumn(protocolGrid, 1);
        split.Children.Add(packetGrid);
        split.Children.Add(protocolGrid);
        return Page("Packet Capture and Protocol Inspection", "Optional Npcap/SharpPcap capture. Only packet headers and protocol metadata are displayed; payload contents are not shown. Saving creates a standard PCAP file containing captured packets.", Row(Labeled("Adapter", captureAdapter), Labeled("Capture filter", captureFilter), refresh, start, stop, save, clear), captureStatus, split);
    }

    private UIElement CreateBandwidth()
    {
        routerBandwidthStatus = Status();
        routerDownload = new TextBlock { Text = "--", Foreground = Accent, FontSize = 24, FontWeight = FontWeights.Bold, Margin = new Thickness(0, 2, 30, 2) };
        routerUpload = new TextBlock { Text = "--", Foreground = Accent, FontSize = 24, FontWeight = FontWeights.Bold, Margin = new Thickness(0, 2, 30, 2) };
        var totals = new WrapPanel();
        totals.Children.Add(Labeled("Router WAN download", routerDownload));
        totals.Children.Add(Labeled("Router WAN upload", routerUpload));

        var startRouter = Action("Start Router Monitor", () =>
        {
            previousRouterReceived = null;
            previousRouterSent = null;
            routerTimer.Start();
            _ = RefreshRouterBandwidth();
        });
        var stopRouter = Action("Stop Router Monitor", () =>
        {
            routerTimer.Stop();
            if (routerBandwidthStatus is not null) routerBandwidthStatus.Text = "Router monitoring stopped.";
        });

        var grid = GridFor(deviceBandwidth);
        AddColumn(grid, "Observed device", "Address", 180);
        AddColumn(grid, "Download", "DownloadRate", 130);
        AddColumn(grid, "Upload", "UploadRate", 130);
        AddColumn(grid, "Observed total", "Total", 140);
        AddColumn(grid, "Coverage", "Coverage", 340);

        return Page("Bandwidth by Device",
            "NETGEAR R7000 UPnP provides authoritative whole-Internet totals. Per-device rows contain only traffic visible to this computer's selected capture adapter; switched wired and encrypted wireless traffic between other clients and the router is normally not visible.",
            Row(startRouter, stopRouter), totals, routerBandwidthStatus, grid);
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
            IPAddress[] targets = ParsePrivateRange(subnetText);
            status.Text = $"Scanning {targets.Length:N0} private address(es)...";
            using var gate = new SemaphoreSlim(48);
            var found = new ConcurrentDictionary<string, long?>();
            var tasks = targets.Select(async address =>
            {
                await gate.WaitAsync(token);
                try
                {
                    using var ping = new Ping();
                    PingReply reply = await ping.SendPingAsync(address, 350);
                    if (reply.Status == IPStatus.Success)
                    {
                        found[address.ToString()] = reply.RoundtripTime;
                        return;
                    }

                    int[] discoveryPorts = [22, 80, 443, 445];
                    PortRow[] probes = await Task.WhenAll(discoveryPorts.Select(port => CheckPort(address, port, token, 160)));
                    if (probes.Any(row => row.Status == "Open")) found[address.ToString()] = null;
                }
                catch { }
                finally { gate.Release(); }
            });
            await Task.WhenAll(tasks);
            var arp = ReadArpTable();
            foreach (string address in arp.Keys.Where(ip => targets.Any(target => target.ToString() == ip))) found.TryAdd(address, null);
            foreach (var item in found.OrderBy(item => IpNumber(IPAddress.Parse(item.Key))))
            {
                token.ThrowIfCancellationRequested();
                IPAddress address = IPAddress.Parse(item.Key);
                string hostname = "";
                try { hostname = (await Dns.GetHostEntryAsync(address)).HostName; } catch { }
                int[] common = [22, 53, 80, 139, 443, 445, 554, 631, 3389, 5900, 8080, 9100];
                PortRow[] checks = await Task.WhenAll(common.Select(port => CheckPort(address, port, token, 220)));
                string services = string.Join(", ", checks.Where(row => row.Status == "Open").Select(row => row.Service));
                string mac = arp.GetValueOrDefault(item.Key, "");
                devices.Add(new(item.Key, hostname, mac, VendorClue(mac), InferDeviceType(address, hostname, checks), item.Value.HasValue ? item.Value + " ms" : "Detected", services));
                status.Text = $"Found {devices.Count} responding device(s)...";
            }
            status.Text = $"Scan complete: {devices.Count} device(s) detected across {targets.Length:N0} address(es). Devices may be wired or wireless; Windows cannot reliably identify that connection type for another client.";
        }
        catch (OperationCanceledException) { status.Text = $"Discovery cancelled; {devices.Count} device(s) retained."; }
        catch (Exception exception) { status.Text = exception.Message; }
    }

    private void LoadDiscoveryAdapters(TextBox range)
    {
        discoveryAdapter.Items.Clear();
        foreach (NetworkInterface nic in NetworkInterface.GetAllNetworkInterfaces().Where(item => item.OperationalStatus == OperationalStatus.Up))
        {
            foreach (UnicastIPAddressInformation address in nic.GetIPProperties().UnicastAddresses.Where(item => item.Address.AddressFamily == AddressFamily.InterNetwork && IsPrivate(item.Address)))
            {
                int prefix = address.PrefixLength is >= 20 and <= 30 ? address.PrefixLength : 24;
                string cidr = NetworkCidr(address.Address, prefix);
                string kind = nic.NetworkInterfaceType == NetworkInterfaceType.Wireless80211 ? "Wi-Fi" : nic.NetworkInterfaceType.ToString();
                discoveryAdapter.Items.Add(new ComboBoxItem { Content = $"{nic.Name} ({kind}) — {address.Address}", Tag = new NetworkScanTarget(cidr) });
            }
        }
        discoveryAdapter.SelectedIndex = discoveryAdapter.Items.Count > 0 ? 0 : -1;
        if (discoveryAdapter.SelectedItem is ComboBoxItem { Tag: NetworkScanTarget selected }) range.Text = selected.Range;
    }

    private void ExportDevices(TextBlock status)
    {
        if (devices.Count == 0) { status.Text = "Scan for devices before exporting."; return; }
        var dialog = new SaveFileDialog { Title = "Export network devices", Filter = "CSV files (*.csv)|*.csv", DefaultExt = ".csv", AddExtension = true, FileName = $"network-devices-{DateTime.Now:yyyyMMdd-HHmmss}.csv" };
        if (dialog.ShowDialog() != true) return;
        var lines = new List<string> { "IP Address,Hostname,MAC Address,Vendor Clue,Likely Device,Latency,Services" };
        lines.AddRange(devices.Select(row => string.Join(",", Csv(row.Address), Csv(row.Hostname), Csv(row.Mac), Csv(row.Vendor), Csv(row.DeviceType), Csv(row.Latency), Csv(row.Services))));
        File.WriteAllLines(dialog.FileName, lines, new UTF8Encoding(true));
        status.Text = $"Exported {devices.Count} device(s) to {dialog.FileName}.";
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
        lock (captureSync)
        {
            capturedPackets.Add(raw);
            if (capturedPackets.Count > 10000)
                capturedPackets.RemoveAt(0);
        }
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

            TrackDeviceTraffic(source, destination, raw.Data.Length);
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

    private void TrackDeviceTraffic(string source, string destination, int bytes)
    {
        if (localAddresses.Contains(source) && !localAddresses.Contains(destination))
            deviceTraffic.AddOrUpdate(destination, (0, bytes), (_, old) => (old.Download, old.Upload + bytes));
        else if (localAddresses.Contains(destination) && !localAddresses.Contains(source))
            deviceTraffic.AddOrUpdate(source, (bytes, 0), (_, old) => (old.Download + bytes, old.Upload));
    }

    private void RefreshObservedBandwidth()
    {
        foreach (var item in deviceTraffic.OrderByDescending(item => item.Value.Download + item.Value.Upload))
        {
            previousDeviceTraffic.TryGetValue(item.Key, out var previous);
            long downloadRate = Math.Max(0, item.Value.Download - previous.Download) / 2;
            long uploadRate = Math.Max(0, item.Value.Upload - previous.Upload) / 2;
            previousDeviceTraffic[item.Key] = item.Value;
            DeviceBandwidthRow? existing = deviceBandwidth.FirstOrDefault(row => row.Address == item.Key);
            var updated = new DeviceBandwidthRow(item.Key, FormatRate(downloadRate), FormatRate(uploadRate), FormatBytes(item.Value.Download + item.Value.Upload), "Observed by this computer; not authoritative for the whole LAN");
            if (existing is null) deviceBandwidth.Add(updated);
            else deviceBandwidth[deviceBandwidth.IndexOf(existing)] = updated;
        }
    }

    private async Task RefreshRouterBandwidth()
    {
        if (routerBandwidthStatus is null || routerDownload is null || routerUpload is null) return;
        try
        {
            ulong received = await ReadUpnpCounter("GetTotalBytesReceived", "NewTotalBytesReceived");
            ulong sent = await ReadUpnpCounter("GetTotalBytesSent", "NewTotalBytesSent");
            DateTime now = DateTime.UtcNow;
            if (previousRouterReceived.HasValue && previousRouterSent.HasValue)
            {
                double elapsed = Math.Max(0.1, (now - previousRouterSample).TotalSeconds);
                routerDownload.Text = FormatRate(CounterDelta(received, previousRouterReceived.Value) / elapsed);
                routerUpload.Text = FormatRate(CounterDelta(sent, previousRouterSent.Value) / elapsed);
            }
            previousRouterReceived = received;
            previousRouterSent = sent;
            previousRouterSample = now;
            routerBandwidthStatus.Text = $"NETGEAR R7000 WAN totals: {FormatBytes((long)received)} received, {FormatBytes((long)sent)} sent. Updated {DateTime.Now:T}.";
        }
        catch (Exception exception)
        {
            routerBandwidthStatus.Text = $"Could not read NETGEAR R7000 UPnP counters: {exception.Message}";
            routerTimer.Stop();
        }
    }

    private async Task<ulong> ReadUpnpCounter(string action, string responseElement)
    {
        const string service = "urn:schemas-upnp-org:service:WANCommonInterfaceConfig:1";
        using var request = new HttpRequestMessage(HttpMethod.Post, "http://10.0.0.1:5000/Public_UPNP_C2");
        request.Headers.TryAddWithoutValidation("SOAPACTION", $"\"{service}#{action}\"");
        request.Content = new StringContent($"<?xml version=\"1.0\"?><s:Envelope xmlns:s=\"http://schemas.xmlsoap.org/soap/envelope/\" s:encodingStyle=\"http://schemas.xmlsoap.org/soap/encoding/\"><s:Body><u:{action} xmlns:u=\"{service}\"></u:{action}></s:Body></s:Envelope>", Encoding.UTF8, "text/xml");
        using HttpResponseMessage response = await routerClient.SendAsync(request);
        response.EnsureSuccessStatusCode();
        string xml = await response.Content.ReadAsStringAsync();
        Match match = Regex.Match(xml, $"<{responseElement}>(\\d+)</{responseElement}>", RegexOptions.IgnoreCase);
        return match.Success && ulong.TryParse(match.Groups[1].Value, out ulong value) ? value : throw new InvalidOperationException($"The router did not return {responseElement}.");
    }

    private static double CounterDelta(ulong current, ulong previous) => current >= previous ? current - previous : uint.MaxValue - previous + current + 1d;

    private static HashSet<string> GetLocalAddresses()
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { IPAddress.Loopback.ToString(), IPAddress.IPv6Loopback.ToString() };
        foreach (NetworkInterface nic in NetworkInterface.GetAllNetworkInterfaces())
            foreach (UnicastIPAddressInformation address in nic.GetIPProperties().UnicastAddresses)
                result.Add(address.Address.ToString());
        return result;
    }

    private static string FormatRate(double bytesPerSecond) => FormatBytes(bytesPerSecond) + "/s";

    private static string FormatBytes(double bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        int unit = 0;
        while (bytes >= 1024 && unit < units.Length - 1) { bytes /= 1024; unit++; }
        return $"{bytes:0.##} {units[unit]}";
    }

    private void SaveCapture()
    {
        RawCapture[] snapshot;
        lock (captureSync) snapshot = capturedPackets.ToArray();
        if (snapshot.Length == 0)
        {
            captureStatus.Text = "There are no captured packets to save.";
            return;
        }

        var dialog = new SaveFileDialog
        {
            Title = "Save packet capture",
            Filter = "Packet capture (*.pcap)|*.pcap",
            DefaultExt = ".pcap",
            AddExtension = true,
            FileName = $"HardwareVisualizer-{DateTime.Now:yyyyMMdd-HHmmss}.pcap"
        };
        if (dialog.ShowDialog() != true)
            return;

        try
        {
            using var writer = new CaptureFileWriterDevice(dialog.FileName);
            writer.Open(new DeviceConfiguration());
            foreach (RawCapture packet in snapshot)
                writer.Write(packet);
            captureStatus.Text = $"Saved {snapshot.Length} packet(s) to {dialog.FileName}.";
        }
        catch (Exception exception)
        {
            captureStatus.Text = $"Could not save capture: {exception.Message}";
        }
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

    private static IPAddress[] ParsePrivateRange(string text)
    {
        string value = text.Trim();
        uint start;
        uint end;
        Match range = Regex.Match(value, @"^(\d{1,3}(?:\.\d{1,3}){3})\s*-\s*(\d{1,3}(?:\.\d{1,3}){3})$");
        if (range.Success)
        {
            IPAddress first = ParsePrivateIpv4(range.Groups[1].Value);
            IPAddress last = ParsePrivateIpv4(range.Groups[2].Value);
            start = IpNumber(first);
            end = IpNumber(last);
        }
        else
        {
            Match cidr = Regex.Match(value, @"^(\d{1,3}(?:\.\d{1,3}){3})(?:/(\d{1,2}))?$");
            if (!cidr.Success) throw new InvalidOperationException("Enter a private CIDR or range, such as 10.0.0.0/24 or 10.0.0.1-10.0.0.100.");
            IPAddress address = ParsePrivateIpv4(cidr.Groups[1].Value);
            int prefix = cidr.Groups[2].Success && int.TryParse(cidr.Groups[2].Value, out int parsed) ? parsed : 24;
            if (prefix is < 16 or > 30) throw new InvalidOperationException("Use a prefix from /16 through /30.");
            uint mask = uint.MaxValue << (32 - prefix);
            uint network = IpNumber(address) & mask;
            start = network + 1;
            end = (network | ~mask) - 1;
        }

        if (end < start) throw new InvalidOperationException("The ending address must be after the starting address.");
        ulong count = (ulong)end - start + 1;
        if (count > 4096) throw new InvalidOperationException("For safety and responsiveness, scan no more than 4,096 private addresses at once.");
        return Enumerable.Range(0, (int)count).Select(offset => AddressFromNumber(start + (uint)offset)).ToArray();
    }

    private static IPAddress ParsePrivateIpv4(string text)
    {
        if (!IPAddress.TryParse(text, out IPAddress? address) || address.AddressFamily != AddressFamily.InterNetwork || !IsPrivate(address))
            throw new InvalidOperationException("Only private IPv4 addresses are allowed.");
        return address;
    }

    private static uint IpNumber(IPAddress address)
    {
        byte[] bytes = address.GetAddressBytes();
        return ((uint)bytes[0] << 24) | ((uint)bytes[1] << 16) | ((uint)bytes[2] << 8) | bytes[3];
    }

    private static IPAddress AddressFromNumber(uint value) => new([(byte)(value >> 24), (byte)(value >> 16), (byte)(value >> 8), (byte)value]);

    private static string NetworkCidr(IPAddress address, int prefix)
    {
        uint mask = uint.MaxValue << (32 - prefix);
        return $"{AddressFromNumber(IpNumber(address) & mask)}/{prefix}";
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
        445 => "SMB", 554 => "RTSP", 631 => "IPP", 3389 => "RDP", 5900 => "VNC", 8080 => "HTTP-Alt", 9100 => "Printer", _ => port.ToString()
    };

    private static string InferDeviceType(IPAddress address, string hostname, IEnumerable<PortRow> checks)
    {
        var open = checks.Where(row => row.Status == "Open").Select(row => row.Port).ToHashSet();
        string name = hostname.ToLowerInvariant();
        if (open.Contains(631) || open.Contains(9100) || name.Contains("printer")) return "Printer";
        if (open.Contains(554) || name.Contains("camera") || name.Contains("cam")) return "Camera / media";
        if (open.Contains(445) || open.Contains(3389)) return "Windows computer";
        if (open.Contains(22) && (open.Contains(80) || open.Contains(443))) return "Server / appliance";
        if (open.Contains(53) || address.GetAddressBytes()[3] == 1) return "Router / DNS";
        if (open.Contains(5900)) return "Remote computer";
        if (open.Contains(80) || open.Contains(443) || open.Contains(8080)) return "Web appliance";
        return "Network device";
    }

    private static string VendorClue(string mac)
    {
        string oui = mac.Replace("-", "").Replace(":", "").ToUpperInvariant();
        if (oui.Length < 6) return "";
        if ((Convert.ToByte(oui[..2], 16) & 2) != 0) return "Private/randomized MAC";
        return oui[..6] switch
        {
            "0836C9" => "NETGEAR",
            "001B2F" or "0024B2" or "9CDC71" => "NETGEAR",
            "001A11" or "3C5AB4" or "F4F5D8" => "Google",
            "0017F2" or "3C22FB" or "F0B479" => "Apple",
            "001E58" or "0026B0" or "D850E6" => "Samsung",
            "001A2B" or "B827EB" or "DCA632" => "Raspberry Pi",
            "001D7E" or "18B430" or "84F3EB" => "Amazon",
            "001788" or "ECFABC" => "Philips Hue",
            "001A22" or "485D36" => "eero",
            "001E8C" or "F4F26D" => "ASUSTek",
            _ => "Unknown"
        };
    }

    private static string Csv(string value) => $"\"{value.Replace("\"", "\"\"")}\"";

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

    private static FrameworkElement Labeled(string label, FrameworkElement control)
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
    private sealed record DeviceRow(string Address, string Hostname, string Mac, string Vendor, string DeviceType, string Latency, string Services);
    private sealed record NetworkScanTarget(string Range);
    private sealed record PacketRow(long Number, string Time, string Protocol, string Source, string Destination, int Length, string Details);
    private sealed record ProtocolRow(string Protocol, long Packets, long Bytes);
    private sealed record DeviceBandwidthRow(string Address, string DownloadRate, string UploadRate, string Total, string Coverage);
}
