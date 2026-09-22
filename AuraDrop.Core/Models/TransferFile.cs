using System.Text.Json.Serialization;

namespace AuraDrop.Core.Models;

public class TransferFile
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string FileName { get; set; } = string.Empty;
    public long FileSize { get; set; }
    public string ContentType { get; set; } = "application/octet-stream";
    public string? ChecksumSha256 { get; set; }

    [JsonIgnore]
    public string? LocalPath { get; set; }

    [JsonIgnore]
    public Func<Task<Stream>>? OpenStreamAsync { get; set; }

    [JsonIgnore]
    public string? SavedPath { get; set; }

    [JsonIgnore]
    public long BytesTransferred { get; set; }

    [JsonIgnore]
    public bool IsCompleted { get; set; }

    public string DisplaySize => FormatSize(FileSize);

    public static string FormatSize(long bytes)
    {
        if (bytes < 1024) return $"{bytes} B";
        if (bytes < 1024 * 1024) return $"{bytes / 1024.0:F1} KB";
        if (bytes < 1024 * 1024 * 1024) return $"{bytes / (1024.0 * 1024.0):F1} MB";
        return $"{bytes / (1024.0 * 1024.0 * 1024.0):F2} GB";
    }

    public string Category => Path.GetExtension(FileName).ToLowerInvariant() switch
    {
        ".jpg" or ".jpeg" or ".png" or ".gif" or ".webp" or ".bmp" or ".heic" or ".svg" => "Media",
        ".mp4" or ".mkv" or ".mov" or ".avi" or ".webm" or ".flv" or ".3gp" => "Media",
        ".mp3" or ".wav" or ".flac" or ".m4a" or ".ogg" or ".aac" => "Audio",
        ".pdf" or ".doc" or ".docx" or ".xls" or ".xlsx" or ".ppt" or ".pptx" or ".txt" => "Document",
        ".zip" or ".rar" or ".7z" or ".tar" or ".gz" => "Archive",
        ".apk" => "App",
        _ => "File"
    };
}
