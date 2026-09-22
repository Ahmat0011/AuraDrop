using System.Buffers;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using AuraDrop.Core.Models;
using AuraDrop.Core.PinCode;

namespace AuraDrop.Core.Network;

public class TransferServer : IDisposable
{
    private readonly DeviceInfo _currentDevice;
    private readonly PinCodeManager _pinManager;
    private TcpListener? _listener;
    private CancellationTokenSource? _cts;
    private bool _isDisposed;

    public string DownloadDirectory { get; set; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads", "AuraDrop");

    public bool AutoAcceptTransfers { get; set; } = false;

    private readonly ConcurrentDictionary<string, TransferSession> _activeSessions = new();

    public event Func<TransferSession, Task<bool>>? TransferRequested;
    public event Action<TransferProgress>? TransferProgressChanged;
    public event Action<TransferSession>? TransferCompleted;
    public event Action<TransferSession, string>? TransferFailed;

    public TransferServer(DeviceInfo currentDevice, PinCodeManager pinManager)
    {
        _currentDevice = currentDevice;
        _pinManager = pinManager;
    }

    public void RegisterActiveSession(TransferSession session)
    {
        _activeSessions[session.SessionId] = session;
    }

    public TransferSession? GetSession(string sessionId)
    {
        _activeSessions.TryGetValue(sessionId, out var session);
        return session;
    }

    public void Start(int preferredPort = 52525)
    {
        if (_cts != null) return;
        _cts = new CancellationTokenSource();

        if (!Directory.Exists(DownloadDirectory))
        {
            try { Directory.CreateDirectory(DownloadDirectory); } catch { }
        }

        int port = preferredPort;
        for (int attempt = 0; attempt < 10; attempt++)
        {
            try
            {
                _listener = new TcpListener(IPAddress.Any, port);
                _listener.Server.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
                _listener.Start(100);
                _currentDevice.Port = port;
                break;
            }
            catch
            {
                port++;
            }
        }

        if (_listener == null)
            throw new InvalidOperationException("Could not bind any TCP port for TransferServer.");

        Task.Run(() => AcceptConnectionsLoopAsync(_cts.Token));
    }

    public void Stop()
    {
        _cts?.Cancel();
        _cts?.Dispose();
        _cts = null;

        try
        {
            _listener?.Stop();
        }
        catch { }
        _listener = null;
    }

    private async Task AcceptConnectionsLoopAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested && _listener != null)
        {
            try
            {
                var client = await _listener.AcceptTcpClientAsync(token);
                _ = Task.Run(() => HandleClientAsync(client, token), token);
            }
            catch (OperationCanceledException) { break; }
            catch (SocketException) { break; }
            catch (Exception ex)
            {
                Debug.WriteLine($"[TransferServer] Accept error: {ex.Message}");
            }
        }
    }

    private async Task HandleClientAsync(TcpClient client, CancellationToken token)
    {
        using (client)
        {
            client.NoDelay = true;
            client.SendBufferSize = 256 * 1024;
            client.ReceiveBufferSize = 256 * 1024;

            using var stream = client.GetStream();
            using var reader = new StreamReader(stream, Encoding.UTF8, leaveOpen: true);

            var requestLine = await reader.ReadLineAsync(token);
            if (string.IsNullOrWhiteSpace(requestLine)) return;

            var parts = requestLine.Split(' ');
            if (parts.Length < 2) return;

            var method = parts[0].ToUpperInvariant();
            var rawUrl = parts[1];

            // Read headers
            var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            string? headerLine;
            while (!string.IsNullOrEmpty(headerLine = await reader.ReadLineAsync(token)))
            {
                var colonIndex = headerLine.IndexOf(':');
                if (colonIndex > 0)
                {
                    headers[headerLine[..colonIndex].Trim()] = headerLine[(colonIndex + 1)..].Trim();
                }
            }

            long contentLength = 0;
            if (headers.TryGetValue("Content-Length", out var clStr))
            {
                long.TryParse(clStr, out contentLength);
            }

            var uri = new Uri($"http://localhost{rawUrl}");
            var path = uri.AbsolutePath.ToLowerInvariant();

            try
            {
                switch (path)
                {
                    case "/api/info":
                        await HandleInfoAsync(stream);
                        break;

                    case "/api/transfer/request":
                        await HandleTransferRequestAsync(stream, reader, contentLength, token);
                        break;

                    case "/api/transfer/download":
                        await HandleDownloadAsync(stream, uri, token);
                        break;

                    case "/api/transfer/upload":
                        await HandleUploadAsync(stream, uri, contentLength, token);
                        break;

                    case var p when p.StartsWith("/api/pin/"):
                        var pin = uri.AbsolutePath["/api/pin/".Length..];
                        await HandlePinLookupAsync(stream, pin);
                        break;

                    case "/api/transfer/cancel":
                        await HandleCancelAsync(stream, uri);
                        break;

                    default:
                        await SendResponseAsync(stream, 404, "Not Found", "{\"error\":\"Not Found\"}");
                        break;
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[TransferServer] Request error: {ex}");
                try
                {
                    await SendResponseAsync(stream, 500, "Internal Error", JsonSerializer.Serialize(new { error = ex.Message }));
                }
                catch { }
            }
        }
    }

    private async Task HandleInfoAsync(NetworkStream stream)
    {
        var json = JsonSerializer.Serialize(_currentDevice);
        await SendResponseAsync(stream, 200, "OK", json, "application/json");
    }

    private async Task HandlePinLookupAsync(NetworkStream stream, string pin)
    {
        var session = _pinManager.ResolvePin(pin);
        if (session == null)
        {
            await SendResponseAsync(stream, 404, "Not Found", "{\"error\":\"PIN invalid or expired\"}", "application/json");
            return;
        }

        var json = JsonSerializer.Serialize(session);
        await SendResponseAsync(stream, 200, "OK", json, "application/json");
    }

    private async Task HandleTransferRequestAsync(NetworkStream stream, StreamReader reader, long contentLength, CancellationToken token)
    {
        var bodyBuffer = new char[contentLength];
        int read = 0;
        while (read < contentLength)
        {
            int r = await reader.ReadAsync(bodyBuffer, read, (int)contentLength - read);
            if (r == 0) break;
            read += r;
        }
        var body = new string(bodyBuffer, 0, read);
        var session = JsonSerializer.Deserialize<TransferSession>(body);

        if (session == null || session.Files.Count == 0)
        {
            await SendResponseAsync(stream, 400, "Bad Request", "{\"error\":\"Invalid session\"}", "application/json");
            return;
        }

        _activeSessions[session.SessionId] = session;

        bool accepted = AutoAcceptTransfers;
        if (!accepted && TransferRequested != null)
        {
            accepted = await TransferRequested.Invoke(session);
        }

        session.Status = accepted ? SessionStatus.Accepted : SessionStatus.Rejected;

        var responseJson = JsonSerializer.Serialize(new
        {
            accepted,
            sessionId = session.SessionId,
            securityToken = session.SecurityToken
        });

        await SendResponseAsync(stream, 200, "OK", responseJson, "application/json");
    }

    private async Task HandleDownloadAsync(NetworkStream stream, Uri uri, CancellationToken token)
    {
        var query = System.Web.HttpUtility.ParseQueryString(uri.Query);
        var sessionId = query["sessionId"];
        var fileId = query["fileId"];
        var tokenParam = query["token"];

        if (string.IsNullOrEmpty(sessionId) || string.IsNullOrEmpty(fileId))
        {
            await SendResponseAsync(stream, 400, "Bad Request", "Missing parameters");
            return;
        }

        if (!_activeSessions.TryGetValue(sessionId, out var session) || session.SecurityToken != tokenParam)
        {
            await SendResponseAsync(stream, 403, "Forbidden", "Invalid session or token");
            return;
        }

        var file = session.Files.FirstOrDefault(f => f.Id == fileId);
        if (file == null || string.IsNullOrEmpty(file.LocalPath) || !File.Exists(file.LocalPath))
        {
            await SendResponseAsync(stream, 404, "Not Found", "File not found on sender");
            return;
        }

        session.Status = SessionStatus.Transferring;
        var fileInfo = new FileInfo(file.LocalPath);
        var length = fileInfo.Length;

        var headerStr = $"HTTP/1.1 200 OK\r\nContent-Type: application/octet-stream\r\nContent-Length: {length}\r\nAccess-Control-Allow-Origin: *\r\n\r\n";
        var headerBytes = Encoding.UTF8.GetBytes(headerStr);
        await stream.WriteAsync(headerBytes, token);

        using var fileStream = new FileStream(file.LocalPath, FileMode.Open, FileAccess.Read, FileShare.Read, 256 * 1024, FileOptions.SequentialScan | FileOptions.Asynchronous);
        var buffer = ArrayPool<byte>.Shared.Rent(256 * 1024);
        var sw = Stopwatch.StartNew();
        long bytesSent = 0;
        long lastSpeedBytes = 0;
        var lastSpeedTime = sw.ElapsedMilliseconds;

        try
        {
            int readBytes;
            while ((readBytes = await fileStream.ReadAsync(buffer, token)) > 0)
            {
                await stream.WriteAsync(buffer.AsMemory(0, readBytes), token);
                bytesSent += readBytes;
                file.BytesTransferred = bytesSent;

                var now = sw.ElapsedMilliseconds;
                if (now - lastSpeedTime >= 500)
                {
                    var deltaSec = (now - lastSpeedTime) / 1000.0;
                    var speed = (bytesSent - lastSpeedBytes) / deltaSec;
                    lastSpeedBytes = bytesSent;
                    lastSpeedTime = now;

                    ReportProgress(session, file, bytesSent, length, speed);
                }
            }

            file.IsCompleted = true;
            ReportProgress(session, file, bytesSent, length, 0);

            if (session.Files.All(f => f.IsCompleted))
            {
                session.Status = SessionStatus.Completed;
                session.CompletedAt = DateTime.UtcNow;
                TransferCompleted?.Invoke(session);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private async Task HandleUploadAsync(NetworkStream stream, Uri uri, long contentLength, CancellationToken token)
    {
        var query = System.Web.HttpUtility.ParseQueryString(uri.Query);
        var sessionId = query["sessionId"];
        var fileId = query["fileId"];
        var tokenParam = query["token"];

        if (string.IsNullOrEmpty(sessionId) || string.IsNullOrEmpty(fileId))
        {
            await SendResponseAsync(stream, 400, "Bad Request", "Missing parameters");
            return;
        }

        if (!_activeSessions.TryGetValue(sessionId, out var session) || session.SecurityToken != tokenParam)
        {
            await SendResponseAsync(stream, 403, "Forbidden", "Invalid session or token");
            return;
        }

        var file = session.Files.FirstOrDefault(f => f.Id == fileId);
        if (file == null)
        {
            await SendResponseAsync(stream, 404, "Not Found", "File metadata not found");
            return;
        }

        session.Status = SessionStatus.Transferring;

        if (!Directory.Exists(DownloadDirectory))
        {
            Directory.CreateDirectory(DownloadDirectory);
        }

        // Sanitize filename against directory traversal
        var safeFileName = Path.GetFileName(file.FileName);
        if (string.IsNullOrWhiteSpace(safeFileName)) safeFileName = $"file_{Guid.NewGuid():N}.bin";

        var targetPath = Path.Combine(DownloadDirectory, safeFileName);
        // Avoid overwriting existing files by appending counter
        int counter = 1;
        var dir = DownloadDirectory;
        var nameWithoutExt = Path.GetFileNameWithoutExtension(safeFileName);
        var ext = Path.GetExtension(safeFileName);
        while (File.Exists(targetPath))
        {
            targetPath = Path.Combine(dir, $"{nameWithoutExt} ({counter++}){ext}");
        }

        var tempPath = targetPath + ".auratmp";
        file.SavedPath = targetPath;

        var buffer = ArrayPool<byte>.Shared.Rent(256 * 1024);
        long totalReceived = 0;
        var sw = Stopwatch.StartNew();
        long lastSpeedBytes = 0;
        var lastSpeedTime = sw.ElapsedMilliseconds;

        try
        {
            using (var fileStream = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None, 256 * 1024, FileOptions.Asynchronous))
            {
                while (totalReceived < contentLength)
                {
                    int toRead = (int)Math.Min(buffer.Length, contentLength - totalReceived);
                    int read = await stream.ReadAsync(buffer.AsMemory(0, toRead), token);
                    if (read == 0) break;

                    await fileStream.WriteAsync(buffer.AsMemory(0, read), token);
                    totalReceived += read;
                    file.BytesTransferred = totalReceived;

                    var now = sw.ElapsedMilliseconds;
                    if (now - lastSpeedTime >= 500)
                    {
                        var deltaSec = (now - lastSpeedTime) / 1000.0;
                        var speed = (totalReceived - lastSpeedBytes) / deltaSec;
                        lastSpeedBytes = totalReceived;
                        lastSpeedTime = now;

                        ReportProgress(session, file, totalReceived, contentLength, speed);
                    }
                }
            }

            // Move temp file to final destination
            if (File.Exists(targetPath)) File.Delete(targetPath);
            File.Move(tempPath, targetPath);

            file.IsCompleted = true;
            ReportProgress(session, file, totalReceived, contentLength, 0);

            await SendResponseAsync(stream, 200, "OK", "{\"status\":\"uploaded\"}", "application/json");

            if (session.Files.All(f => f.IsCompleted))
            {
                session.Status = SessionStatus.Completed;
                session.CompletedAt = DateTime.UtcNow;
                TransferCompleted?.Invoke(session);
            }
        }
        catch (Exception ex)
        {
            if (File.Exists(tempPath))
            {
                try { File.Delete(tempPath); } catch { }
            }
            session.Status = SessionStatus.Failed;
            session.ErrorMessage = ex.Message;
            TransferFailed?.Invoke(session, ex.Message);
            throw;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private async Task HandleCancelAsync(NetworkStream stream, Uri uri)
    {
        var query = System.Web.HttpUtility.ParseQueryString(uri.Query);
        var sessionId = query["sessionId"];
        if (!string.IsNullOrEmpty(sessionId) && _activeSessions.TryGetValue(sessionId, out var session))
        {
            session.Status = SessionStatus.Cancelled;
        }
        await SendResponseAsync(stream, 200, "OK", "{\"status\":\"cancelled\"}", "application/json");
    }

    private void ReportProgress(TransferSession session, TransferFile file, long fileBytes, long fileTotal, double speed)
    {
        var progress = new TransferProgress
        {
            SessionId = session.SessionId,
            CurrentFileName = file.FileName,
            CurrentFileIndex = session.Files.IndexOf(file) + 1,
            TotalFiles = session.Files.Count,
            CurrentFileBytesTransferred = fileBytes,
            CurrentFileTotalBytes = fileTotal,
            OverallBytesTransferred = session.TransferredBytes,
            TotalBytes = session.TotalBytes,
            SpeedBytesPerSec = speed
        };
        TransferProgressChanged?.Invoke(progress);
    }

    private static async Task SendResponseAsync(NetworkStream stream, int statusCode, string statusDescription, string body, string contentType = "text/plain")
    {
        var bodyBytes = Encoding.UTF8.GetBytes(body);
        var headers = $"HTTP/1.1 {statusCode} {statusDescription}\r\nContent-Type: {contentType}\r\nContent-Length: {bodyBytes.Length}\r\nAccess-Control-Allow-Origin: *\r\nConnection: close\r\n\r\n";
        var headerBytes = Encoding.UTF8.GetBytes(headers);
        await stream.WriteAsync(headerBytes);
        await stream.WriteAsync(bodyBytes);
        await stream.FlushAsync();
    }

    public void Dispose()
    {
        if (_isDisposed) return;
        _isDisposed = true;
        Stop();
    }
}
