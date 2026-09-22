using System.Collections.Concurrent;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using AuraDrop.Core.Models;

namespace AuraDrop.Core.Discovery;

public class DeviceDiscoveryService : IDisposable
{
    public const int DiscoveryPort = 52526;
    private const string MulticastAddress = "239.255.52.52";

    private readonly DeviceInfo _currentDevice;
    private readonly ConcurrentDictionary<string, DeviceInfo> _discoveredDevices = new();
    private CancellationTokenSource? _cts;
    private UdpClient? _listener;
    private bool _isDisposed;

    public event Action<DeviceInfo>? DeviceDiscovered;
    public event Action<DeviceInfo>? DeviceUpdated;
    public event Action<string>? DeviceLost;

    public IReadOnlyCollection<DeviceInfo> DiscoveredDevices => _discoveredDevices.Values.ToList();

    public DeviceDiscoveryService(DeviceInfo currentDevice)
    {
        _currentDevice = currentDevice;
    }

    public void Start()
    {
        if (_cts != null) return;
        _cts = new CancellationTokenSource();

        try
        {
            _listener = new UdpClient();
            _listener.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            _listener.Client.Bind(new IPEndPoint(IPAddress.Any, DiscoveryPort));

            try
            {
                _listener.JoinMulticastGroup(IPAddress.Parse(MulticastAddress));
            }
            catch
            {
                // Multicast may not be supported on some virtual adapters, broadcast fallback will work
            }

            _listener.EnableBroadcast = true;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[Discovery] Failed to bind discovery port: {ex.Message}");
        }

        // Start listening
        Task.Run(() => ListenLoopAsync(_cts.Token));

        // Start advertising beacon
        Task.Run(() => BroadcastLoopAsync(_cts.Token));

        // Start cleanup monitor
        Task.Run(() => CleanupLoopAsync(_cts.Token));
    }

    public void Stop()
    {
        _cts?.Cancel();
        _cts?.Dispose();
        _cts = null;

        try
        {
            _listener?.Close();
            _listener?.Dispose();
        }
        catch { }
        _listener = null;

        _discoveredDevices.Clear();
    }

    public async Task BroadcastDirectPingAsync(string targetIp)
    {
        try
        {
            using var client = new UdpClient();
            client.EnableBroadcast = true;
            var data = CreateBeaconBytes();
            await client.SendAsync(data, data.Length, targetIp, DiscoveryPort);
        }
        catch { }
    }

    private byte[] CreateBeaconBytes()
    {
        var json = JsonSerializer.Serialize(new
        {
            type = "aura_beacon",
            id = _currentDevice.Id,
            name = _currentDevice.Name,
            deviceType = _currentDevice.DeviceType,
            port = _currentDevice.Port,
            version = _currentDevice.Version
        });
        return Encoding.UTF8.GetBytes(json);
    }

    private async Task BroadcastLoopAsync(CancellationToken token)
    {
        using var client = new UdpClient();
        client.EnableBroadcast = true;

        while (!token.IsCancellationRequested)
        {
            try
            {
                // Refresh local IP
                var localIp = GetLocalIPv4Address();
                if (!string.IsNullOrEmpty(localIp))
                {
                    _currentDevice.IpAddress = localIp;
                }

                var bytes = CreateBeaconBytes();

                // 1. Send via local subnet broadcast
                await client.SendAsync(bytes.AsMemory(), new IPEndPoint(IPAddress.Broadcast, DiscoveryPort), token);

                // 2. Send via multicast
                try
                {
                    await client.SendAsync(bytes.AsMemory(), new IPEndPoint(IPAddress.Parse(MulticastAddress), DiscoveryPort), token);
                }
                catch { }

                // 3. Send directed broadcast to all active network adapters' broadcast addresses
                foreach (var broadcastIp in GetSubnetBroadcastAddresses())
                {
                    try
                    {
                        await client.SendAsync(bytes.AsMemory(), new IPEndPoint(broadcastIp, DiscoveryPort), token);
                    }
                    catch { }
                }
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[Discovery] Beacon error: {ex.Message}");
            }

            try
            {
                await Task.Delay(2500, token);
            }
            catch (OperationCanceledException) { break; }
        }
    }

    private async Task ListenLoopAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested && _listener != null)
        {
            try
            {
                var result = await _listener.ReceiveAsync(token);
                var message = Encoding.UTF8.GetString(result.Buffer);

                using var doc = JsonDocument.Parse(message);
                var root = doc.RootElement;

                if (!root.TryGetProperty("type", out var typeProp) || typeProp.GetString() != "aura_beacon")
                    continue;

                var id = root.GetProperty("id").GetString();
                if (string.IsNullOrEmpty(id) || id == _currentDevice.Id)
                    continue; // Skip self

                var name = root.GetProperty("name").GetString() ?? "Unknown";
                var deviceType = root.GetProperty("deviceType").GetString() ?? "Unknown";
                var port = root.GetProperty("port").GetInt32();
                var version = root.TryGetProperty("version", out var vProp) ? vProp.GetString() ?? "1.0.0" : "1.0.0";
                var remoteIp = result.RemoteEndPoint.Address.ToString();

                // Normalize IPv6-mapped IPv4
                if (result.RemoteEndPoint.Address.IsIPv4MappedToIPv6)
                {
                    remoteIp = result.RemoteEndPoint.Address.MapToIPv4().ToString();
                }

                var isNew = !_discoveredDevices.ContainsKey(id);
                var device = _discoveredDevices.GetOrAdd(id, _ => new DeviceInfo());
                device.Id = id;
                device.Name = name;
                device.DeviceType = deviceType;
                device.IpAddress = remoteIp;
                device.Port = port;
                device.Version = version;
                device.LastSeen = DateTime.UtcNow;

                if (isNew)
                {
                    DeviceDiscovered?.Invoke(device);
                }
                else
                {
                    DeviceUpdated?.Invoke(device);
                }
            }
            catch (OperationCanceledException) { break; }
            catch (SocketException) { break; }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[Discovery] Listen error: {ex.Message}");
            }
        }
    }

    private async Task CleanupLoopAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(4000, token);
                var cutoff = DateTime.UtcNow.AddSeconds(-10);

                foreach (var pair in _discoveredDevices.ToArray())
                {
                    if (pair.Value.LastSeen < cutoff)
                    {
                        if (_discoveredDevices.TryRemove(pair.Key, out _))
                        {
                            DeviceLost?.Invoke(pair.Key);
                        }
                    }
                }
            }
            catch (OperationCanceledException) { break; }
            catch { }
        }
    }

    public static string GetLocalIPv4Address()
    {
        try
        {
            var activeInterfaces = NetworkInterface.GetAllNetworkInterfaces()
                .Where(n => n.OperationalStatus == OperationalStatus.Up &&
                            n.NetworkInterfaceType != NetworkInterfaceType.Loopback)
                .OrderByDescending(n => n.NetworkInterfaceType == NetworkInterfaceType.Wireless80211 ||
                                        n.NetworkInterfaceType == NetworkInterfaceType.Ethernet);

            foreach (var iface in activeInterfaces)
            {
                var props = iface.GetIPProperties();
                foreach (var addr in props.UnicastAddresses)
                {
                    if (addr.Address.AddressFamily == AddressFamily.InterNetwork &&
                        !IPAddress.IsLoopback(addr.Address) &&
                        !addr.Address.ToString().StartsWith("169.254"))
                    {
                        return addr.Address.ToString();
                    }
                }
            }
        }
        catch { }
        return "127.0.0.1";
    }

    public static IEnumerable<IPAddress> GetSubnetBroadcastAddresses()
    {
        var list = new List<IPAddress>();
        try
        {
            foreach (var iface in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (iface.OperationalStatus != OperationalStatus.Up || iface.NetworkInterfaceType == NetworkInterfaceType.Loopback)
                    continue;

                foreach (var uni in iface.GetIPProperties().UnicastAddresses)
                {
                    if (uni.Address.AddressFamily != AddressFamily.InterNetwork || uni.IPv4Mask == null)
                        continue;

                    var ipBytes = uni.Address.GetAddressBytes();
                    var maskBytes = uni.IPv4Mask.GetAddressBytes();
                    if (ipBytes.Length != 4 || maskBytes.Length != 4) continue;

                    var broadcastBytes = new byte[4];
                    for (int i = 0; i < 4; i++)
                    {
                        broadcastBytes[i] = (byte)(ipBytes[i] | (maskBytes[i] ^ 255));
                    }
                    list.Add(new IPAddress(broadcastBytes));
                }
            }
        }
        catch { }
        return list;
    }

    public void Dispose()
    {
        if (_isDisposed) return;
        _isDisposed = true;
        Stop();
    }
}
