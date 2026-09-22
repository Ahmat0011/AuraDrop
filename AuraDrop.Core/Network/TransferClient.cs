using System.Buffers;
using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using AuraDrop.Core.Models;

namespace AuraDrop.Core.Network;

public class TransferClient
{
    private readonly HttpClient _httpClient;

    public TransferClient(HttpClient? httpClient = null)
    {
        _httpClient = httpClient ?? new HttpClient
        {
            Timeout = Timeout.InfiniteTimeSpan // Important for massive file transfers (gigabytes)
        };
    }

    public async Task<DeviceInfo?> GetDeviceInfoAsync(string ip, int port, CancellationToken token = default)
    {
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(token);
            cts.CancelAfter(TimeSpan.FromSeconds(3));

            var response = await _httpClient.GetAsync($"http://{ip}:{port}/api/info", cts.Token);
            if (response.IsSuccessStatusCode)
            {
                var json = await response.Content.ReadAsStringAsync(cts.Token);
                return JsonSerializer.Deserialize<DeviceInfo>(json);
            }
        }
        catch { }
        return null;
    }

    public async Task<TransferSession?> ResolvePinFromPeerAsync(DeviceInfo peer, string pin, CancellationToken token = default)
    {
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(token);
            cts.CancelAfter(TimeSpan.FromSeconds(4));

            var response = await _httpClient.GetAsync($"http://{peer.IpAddress}:{peer.Port}/api/pin/{pin}", cts.Token);
            if (response.IsSuccessStatusCode)
            {
                var json = await response.Content.ReadAsStringAsync(cts.Token);
                var session = JsonSerializer.Deserialize<TransferSession>(json);
                if (session != null)
                {
                    session.Sender = peer;
                    return session;
                }
            }
        }
        catch { }
        return null;
    }

    public async Task<(bool Accepted, string? Token, string? ErrorMessage)> RequestTransferAsync(DeviceInfo target, TransferSession session, CancellationToken token = default)
    {
        try
        {
            var json = JsonSerializer.Serialize(session);
            var content = new StringContent(json, Encoding.UTF8, "application/json");

            var response = await _httpClient.PostAsync($"http://{target.IpAddress}:{target.Port}/api/transfer/request", content, token);
            if (response.StatusCode == HttpStatusCode.Unauthorized)
            {
                return (false, null, "PIN erforderlich oder ungültig");
            }

            if (response.IsSuccessStatusCode)
            {
                var resJson = await response.Content.ReadAsStringAsync(token);
                using var doc = JsonDocument.Parse(resJson);
                var accepted = doc.RootElement.GetProperty("accepted").GetBoolean();
                var securityToken = doc.RootElement.TryGetProperty("securityToken", out var st) ? st.GetString() : null;

                if (accepted && !string.IsNullOrEmpty(securityToken))
                {
                    session.SecurityToken = securityToken;
                }

                return (accepted, securityToken, accepted ? null : "Übertragung wurde abgelehnt.");
            }

            var err = await response.Content.ReadAsStringAsync(token);
            return (false, null, !string.IsNullOrWhiteSpace(err) ? err : $"Fehler: {response.StatusCode}");
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[TransferClient] Request error: {ex.Message}");
            return (false, null, ex.Message);
        }
    }

    public async Task UploadFilesAsync(
        DeviceInfo target,
        TransferSession session,
        Action<TransferProgress>? onProgress = null,
        CancellationToken token = default)
    {
        session.Status = SessionStatus.Transferring;
        var sw = Stopwatch.StartNew();
        long totalUploadedOverall = 0;
        long lastSpeedBytes = 0;
        var lastSpeedTime = sw.ElapsedMilliseconds;

        for (int i = 0; i < session.Files.Count; i++)
        {
            token.ThrowIfCancellationRequested();
            var file = session.Files[i];

            Stream sourceStream;
            if (file.OpenStreamAsync != null)
            {
                sourceStream = await file.OpenStreamAsync();
            }
            else if (!string.IsNullOrEmpty(file.LocalPath) && File.Exists(file.LocalPath))
            {
                sourceStream = new FileStream(file.LocalPath, FileMode.Open, FileAccess.Read, FileShare.Read, 256 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
            }
            else
            {
                throw new FileNotFoundException($"Die Datei wurde nicht gefunden: {file.FileName}");
            }

            var url = $"http://{target.IpAddress}:{target.Port}/api/transfer/upload?sessionId={session.SessionId}&fileId={file.Id}&token={session.SecurityToken}";

            using (sourceStream)
            {
                var totalLength = file.FileSize > 0 ? file.FileSize : (sourceStream.CanSeek ? sourceStream.Length : -1);
                var progressContent = new ProgressStreamContent(sourceStream, totalLength, 256 * 1024, (bytesSent, totalBytes) =>
                {
                    file.BytesTransferred = bytesSent;
                    var currentOverall = totalUploadedOverall + bytesSent;

                    var now = sw.ElapsedMilliseconds;
                    if (now - lastSpeedTime >= 400)
                    {
                        var deltaSec = (now - lastSpeedTime) / 1000.0;
                        var speed = (currentOverall - lastSpeedBytes) / deltaSec;
                        lastSpeedBytes = currentOverall;
                        lastSpeedTime = now;

                        onProgress?.Invoke(new TransferProgress
                        {
                            SessionId = session.SessionId,
                            CurrentFileName = file.FileName,
                            CurrentFileIndex = i + 1,
                            TotalFiles = session.Files.Count,
                            CurrentFileBytesTransferred = bytesSent,
                            CurrentFileTotalBytes = totalBytes,
                            OverallBytesTransferred = currentOverall,
                            TotalBytes = session.TotalBytes,
                            SpeedBytesPerSec = speed
                        });
                    }
                });

                using var request = new HttpRequestMessage(HttpMethod.Post, url)
                {
                    Content = progressContent
                };

                var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token);
                response.EnsureSuccessStatusCode();
            }

            file.IsCompleted = true;
            totalUploadedOverall += file.FileSize;

            onProgress?.Invoke(new TransferProgress
            {
                SessionId = session.SessionId,
                CurrentFileName = file.FileName,
                CurrentFileIndex = i + 1,
                TotalFiles = session.Files.Count,
                CurrentFileBytesTransferred = file.FileSize,
                CurrentFileTotalBytes = file.FileSize,
                OverallBytesTransferred = totalUploadedOverall,
                TotalBytes = session.TotalBytes,
                SpeedBytesPerSec = 0
            });
        }

        session.Status = SessionStatus.Completed;
        session.CompletedAt = DateTime.UtcNow;
    }

    public async Task DownloadFilesAsync(
        DeviceInfo sender,
        TransferSession session,
        string downloadDir,
        Action<TransferProgress>? onProgress = null,
        CancellationToken token = default)
    {
        session.Status = SessionStatus.Transferring;

        if (!Directory.Exists(downloadDir))
        {
            Directory.CreateDirectory(downloadDir);
        }

        var sw = Stopwatch.StartNew();
        long totalDownloadedOverall = 0;
        long lastSpeedBytes = 0;
        var lastSpeedTime = sw.ElapsedMilliseconds;

        for (int i = 0; i < session.Files.Count; i++)
        {
            token.ThrowIfCancellationRequested();
            var file = session.Files[i];

            var safeFileName = Path.GetFileName(file.FileName);
            if (string.IsNullOrWhiteSpace(safeFileName)) safeFileName = $"file_{Guid.NewGuid():N}.bin";

            var targetPath = Path.Combine(downloadDir, safeFileName);
            int counter = 1;
            var nameWithoutExt = Path.GetFileNameWithoutExtension(safeFileName);
            var ext = Path.GetExtension(safeFileName);
            while (File.Exists(targetPath))
            {
                targetPath = Path.Combine(downloadDir, $"{nameWithoutExt} ({counter++}){ext}");
            }

            var tempPath = targetPath + ".auratmp";
            file.SavedPath = targetPath;

            var url = $"http://{sender.IpAddress}:{sender.Port}/api/transfer/download?sessionId={session.SessionId}&fileId={file.Id}&token={session.SecurityToken}";

            using var response = await _httpClient.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, token);
            response.EnsureSuccessStatusCode();

            var totalBytes = response.Content.Headers.ContentLength ?? file.FileSize;
            using var remoteStream = await response.Content.ReadAsStreamAsync(token);
            using (var localFileStream = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None, 256 * 1024, FileOptions.Asynchronous))
            {
                var buffer = ArrayPool<byte>.Shared.Rent(256 * 1024);
                long fileReceived = 0;

                try
                {
                    int read;
                    while ((read = await remoteStream.ReadAsync(buffer, token)) > 0)
                    {
                        await localFileStream.WriteAsync(buffer.AsMemory(0, read), token);
                        fileReceived += read;
                        file.BytesTransferred = fileReceived;

                        var currentOverall = totalDownloadedOverall + fileReceived;
                        var now = sw.ElapsedMilliseconds;
                        if (now - lastSpeedTime >= 400)
                        {
                            var deltaSec = (now - lastSpeedTime) / 1000.0;
                            var speed = (currentOverall - lastSpeedBytes) / deltaSec;
                            lastSpeedBytes = currentOverall;
                            lastSpeedTime = now;

                            onProgress?.Invoke(new TransferProgress
                            {
                                SessionId = session.SessionId,
                                CurrentFileName = file.FileName,
                                CurrentFileIndex = i + 1,
                                TotalFiles = session.Files.Count,
                                CurrentFileBytesTransferred = fileReceived,
                                CurrentFileTotalBytes = totalBytes,
                                OverallBytesTransferred = currentOverall,
                                TotalBytes = session.TotalBytes,
                                SpeedBytesPerSec = speed
                            });
                        }
                    }
                }
                finally
                {
                    ArrayPool<byte>.Shared.Return(buffer);
                }
            }

            if (File.Exists(targetPath)) File.Delete(targetPath);
            File.Move(tempPath, targetPath);

            file.IsCompleted = true;
            totalDownloadedOverall += file.FileSize;

            onProgress?.Invoke(new TransferProgress
            {
                SessionId = session.SessionId,
                CurrentFileName = file.FileName,
                CurrentFileIndex = i + 1,
                TotalFiles = session.Files.Count,
                CurrentFileBytesTransferred = file.FileSize,
                CurrentFileTotalBytes = file.FileSize,
                OverallBytesTransferred = totalDownloadedOverall,
                TotalBytes = session.TotalBytes,
                SpeedBytesPerSec = 0
            });
        }

        session.Status = SessionStatus.Completed;
        session.CompletedAt = DateTime.UtcNow;
    }
}

internal class ProgressStreamContent : HttpContent
{
    private readonly Stream _stream;
    private readonly long _contentLength;
    private readonly int _bufferSize;
    private readonly Action<long, long> _progressCallback;

    public ProgressStreamContent(Stream stream, long contentLength, int bufferSize, Action<long, long> progressCallback)
    {
        _stream = stream ?? throw new ArgumentNullException(nameof(stream));
        _contentLength = contentLength > 0 ? contentLength : (stream.CanSeek ? stream.Length : -1);
        _bufferSize = bufferSize;
        _progressCallback = progressCallback ?? throw new ArgumentNullException(nameof(progressCallback));
        Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        if (_contentLength >= 0)
        {
            Headers.ContentLength = _contentLength;
        }
    }

    protected override Task SerializeToStreamAsync(Stream targetStream, TransportContext? context)
    {
        return SerializeToStreamAsync(targetStream, context, CancellationToken.None);
    }

    protected override async Task SerializeToStreamAsync(Stream targetStream, TransportContext? context, CancellationToken cancellationToken)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(_bufferSize);
        long bytesSent = 0;
        long totalLength = _contentLength;

        try
        {
            if (_stream.CanSeek)
            {
                _stream.Position = 0;
            }
            int read;
            while ((read = await _stream.ReadAsync(buffer, cancellationToken)) > 0)
            {
                await targetStream.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                bytesSent += read;
                _progressCallback(bytesSent, totalLength);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    protected override bool TryComputeLength(out long length)
    {
        if (_contentLength >= 0)
        {
            length = _contentLength;
            return true;
        }
        length = 0;
        return false;
    }
}
