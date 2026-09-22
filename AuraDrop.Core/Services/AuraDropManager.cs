using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using AuraDrop.Core.Discovery;
using AuraDrop.Core.Models;
using AuraDrop.Core.Network;
using AuraDrop.Core.PinCode;

namespace AuraDrop.Core.Services;

public class AuraDropManager : IDisposable
{
    public DeviceInfo CurrentDevice { get; } = new();
    public DeviceDiscoveryService Discovery { get; }
    public PinCodeManager PinManager { get; } = new();
    public TransferServer Server { get; }
    public TransferClient Client { get; } = new();

    public List<TransferHistoryItem> History { get; private set; } = new();

    private readonly string _historyFilePath;
    private readonly string _settingsFilePath;
    private bool _isDisposed;

    public event Action<TransferHistoryItem>? HistoryItemAdded;
    public event Action<TransferSession>? OutgoingTransferStarted;
    public event Action<TransferSession>? OutgoingTransferCompleted;
    public event Action<TransferSession, string>? OutgoingTransferFailed;

    public AuraDropManager(string deviceName, string deviceType, int port = 52525)
    {
        CurrentDevice.Name = deviceName;
        CurrentDevice.DeviceType = deviceType;
        CurrentDevice.Port = port;
        CurrentDevice.IpAddress = DeviceDiscoveryService.GetLocalIPv4Address();

        Discovery = new DeviceDiscoveryService(CurrentDevice);
        Server = new TransferServer(CurrentDevice, PinManager);

        var appData = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "AuraDrop");
        try { Directory.CreateDirectory(appData); } catch { }
        _historyFilePath = Path.Combine(appData, "history.json");
        _settingsFilePath = Path.Combine(appData, "settings.json");

        LoadHistory();
        LoadSettings();

        Server.TransferCompleted += OnServerTransferCompleted;
    }

    public void Start()
    {
        CurrentDevice.IpAddress = DeviceDiscoveryService.GetLocalIPv4Address();
        Server.Start(CurrentDevice.Port);
        Discovery.Start();
    }

    public void Stop()
    {
        Discovery.Stop();
        Server.Stop();
    }

    public async Task<bool> SendFilesToDeviceAsync(
        DeviceInfo target,
        IEnumerable<string> filePaths,
        Action<TransferProgress>? onProgress = null,
        CancellationToken token = default)
    {
        var files = CreateFileList(filePaths);
        if (files.Count == 0) throw new ArgumentException("Keine gültigen Dateien ausgewählt.");

        var session = new TransferSession
        {
            Sender = CurrentDevice,
            Receiver = target,
            Files = files,
            Status = SessionStatus.Pending
        };

        OutgoingTransferStarted?.Invoke(session);

        // 1. Request transfer
        var (accepted, tokenStr) = await Client.RequestTransferAsync(target, session, token);
        if (!accepted)
        {
            session.Status = SessionStatus.Rejected;
            OutgoingTransferFailed?.Invoke(session, "Übertragung wurde vom Empfänger abgelehnt.");
            return false;
        }

        // 2. Upload files
        try
        {
            await Client.UploadFilesAsync(target, session, onProgress, token);
            OutgoingTransferCompleted?.Invoke(session);

            AddHistoryItem(new TransferHistoryItem
            {
                SessionId = session.SessionId,
                Direction = TransferDirection.Outgoing,
                PeerName = target.Name,
                PeerIp = target.IpAddress,
                FileCount = files.Count,
                TotalBytes = session.TotalBytes,
                FileNames = files.Select(f => f.FileName).ToList(),
                SavedPaths = files.Select(f => f.LocalPath ?? "").ToList(),
                Status = SessionStatus.Completed
            });

            return true;
        }
        catch (Exception ex)
        {
            session.Status = SessionStatus.Failed;
            session.ErrorMessage = ex.Message;
            OutgoingTransferFailed?.Invoke(session, ex.Message);
            throw;
        }
    }

    public string CreatePinSession(IEnumerable<string> filePaths)
    {
        var files = CreateFileList(filePaths);
        if (files.Count == 0) throw new ArgumentException("Keine gültigen Dateien ausgewählt.");

        var session = new TransferSession
        {
            Sender = CurrentDevice,
            Files = files,
            Status = SessionStatus.Pending
        };

        Server.RegisterActiveSession(session);
        return PinManager.GeneratePin(session);
    }

    public async Task<bool> DownloadViaPinAsync(
        string pinCode,
        Action<TransferProgress>? onProgress = null,
        CancellationToken token = default)
    {
        var cleanPin = PinCodeManager.NormalizePin(pinCode);
        if (cleanPin.Length != 6) throw new ArgumentException("Der PIN muss genau 6-stellig sein.");

        // Query all currently known peers in parallel
        var peers = Discovery.DiscoveredDevices.ToList();
        TransferSession? foundSession = null;
        DeviceInfo? hostPeer = null;

        var tasks = peers.Select(async peer =>
        {
            var s = await Client.ResolvePinFromPeerAsync(peer, cleanPin, token);
            if (s != null)
            {
                return (Peer: peer, Session: s);
            }
            return (Peer: peer, Session: (TransferSession?)null);
        });

        var results = await Task.WhenAll(tasks);
        foreach (var r in results)
        {
            if (r.Session != null)
            {
                hostPeer = r.Peer;
                foundSession = r.Session;
                break;
            }
        }

        if (foundSession == null || hostPeer == null)
        {
            throw new InvalidOperationException($"PIN '{PinCodeManager.FormatPin(cleanPin)}' wurde auf keinem aktiven Gerät im Netzwerk gefunden.");
        }

        await Client.DownloadFilesAsync(hostPeer, foundSession, Server.DownloadDirectory, onProgress, token);

        AddHistoryItem(new TransferHistoryItem
        {
            SessionId = foundSession.SessionId,
            Direction = TransferDirection.Incoming,
            PeerName = hostPeer.Name,
            PeerIp = hostPeer.IpAddress,
            FileCount = foundSession.Files.Count,
            TotalBytes = foundSession.TotalBytes,
            FileNames = foundSession.Files.Select(f => f.FileName).ToList(),
            SavedPaths = foundSession.Files.Select(f => f.SavedPath ?? "").ToList(),
            Status = SessionStatus.Completed
        });

        return true;
    }

    public async Task<bool> DownloadViaDirectIpAsync(
        string ip,
        int port,
        string? pin = null,
        Action<TransferProgress>? onProgress = null,
        CancellationToken token = default)
    {
        var targetDevice = await Client.GetDeviceInfoAsync(ip, port, token)
            ?? new DeviceInfo { Name = $"Gerät ({ip})", IpAddress = ip, Port = port };

        if (!string.IsNullOrEmpty(pin))
        {
            var cleanPin = PinCodeManager.NormalizePin(pin);
            var session = await Client.ResolvePinFromPeerAsync(targetDevice, cleanPin, token)
                ?? throw new InvalidOperationException("PIN nicht gefunden auf diesem Gerät.");

            await Client.DownloadFilesAsync(targetDevice, session, Server.DownloadDirectory, onProgress, token);

            AddHistoryItem(new TransferHistoryItem
            {
                SessionId = session.SessionId,
                Direction = TransferDirection.Incoming,
                PeerName = targetDevice.Name,
                PeerIp = targetDevice.IpAddress,
                FileCount = session.Files.Count,
                TotalBytes = session.TotalBytes,
                FileNames = session.Files.Select(f => f.FileName).ToList(),
                SavedPaths = session.Files.Select(f => f.SavedPath ?? "").ToList(),
                Status = SessionStatus.Completed
            });
            return true;
        }

        return false;
    }

    private static List<TransferFile> CreateFileList(IEnumerable<string> filePaths)
    {
        var list = new List<TransferFile>();
        foreach (var path in filePaths)
        {
            if (File.Exists(path))
            {
                var fi = new FileInfo(path);
                list.Add(new TransferFile
                {
                    FileName = fi.Name,
                    FileSize = fi.Length,
                    LocalPath = fi.FullName
                });
            }
            else if (Directory.Exists(path))
            {
                // Recursive folder support
                foreach (var subFile in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
                {
                    var sfi = new FileInfo(subFile);
                    list.Add(new TransferFile
                    {
                        FileName = Path.GetRelativePath(Directory.GetParent(path)?.FullName ?? path, subFile),
                        FileSize = sfi.Length,
                        LocalPath = sfi.FullName
                    });
                }
            }
        }
        return list;
    }

    private void OnServerTransferCompleted(TransferSession session)
    {
        AddHistoryItem(new TransferHistoryItem
        {
            SessionId = session.SessionId,
            Direction = TransferDirection.Incoming,
            PeerName = session.Sender.Name,
            PeerIp = session.Sender.IpAddress,
            FileCount = session.Files.Count,
            TotalBytes = session.TotalBytes,
            FileNames = session.Files.Select(f => f.FileName).ToList(),
            SavedPaths = session.Files.Select(f => f.SavedPath ?? "").ToList(),
            Status = SessionStatus.Completed
        });
    }

    private void AddHistoryItem(TransferHistoryItem item)
    {
        lock (History)
        {
            History.Insert(0, item);
            if (History.Count > 100) History.RemoveAt(History.Count - 1);
            SaveHistory();
        }
        HistoryItemAdded?.Invoke(item);
    }

    public void SaveSettings()
    {
        try
        {
            var data = new
            {
                DeviceName = CurrentDevice.Name,
                DownloadDirectory = Server.DownloadDirectory,
                AutoAccept = Server.AutoAcceptTransfers
            };
            File.WriteAllText(_settingsFilePath, JsonSerializer.Serialize(data, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { }
    }

    private void LoadSettings()
    {
        try
        {
            if (File.Exists(_settingsFilePath))
            {
                var json = File.ReadAllText(_settingsFilePath);
                using var doc = JsonDocument.Parse(json);
                if (doc.RootElement.TryGetProperty("DeviceName", out var dnProp) && !string.IsNullOrWhiteSpace(dnProp.GetString()))
                    CurrentDevice.Name = dnProp.GetString()!;
                if (doc.RootElement.TryGetProperty("DownloadDirectory", out var ddProp) && !string.IsNullOrWhiteSpace(ddProp.GetString()))
                    Server.DownloadDirectory = ddProp.GetString()!;
                if (doc.RootElement.TryGetProperty("AutoAccept", out var aaProp))
                    Server.AutoAcceptTransfers = aaProp.GetBoolean();
            }
        }
        catch { }
    }

    private void SaveHistory()
    {
        try
        {
            var json = JsonSerializer.Serialize(History, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(_historyFilePath, json);
        }
        catch { }
    }

    private void LoadHistory()
    {
        try
        {
            if (File.Exists(_historyFilePath))
            {
                var json = File.ReadAllText(_historyFilePath);
                var items = JsonSerializer.Deserialize<List<TransferHistoryItem>>(json);
                if (items != null) History = items;
            }
        }
        catch { }
    }

    public void Dispose()
    {
        if (_isDisposed) return;
        _isDisposed = true;
        Stop();
        Discovery.Dispose();
        Server.Dispose();
    }
}
