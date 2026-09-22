namespace AuraDrop.Core.Models;

public class TransferProgress
{
    public string SessionId { get; set; } = string.Empty;
    public string CurrentFileName { get; set; } = string.Empty;
    public int CurrentFileIndex { get; set; }
    public int TotalFiles { get; set; }
    public long CurrentFileBytesTransferred { get; set; }
    public long CurrentFileTotalBytes { get; set; }
    public long OverallBytesTransferred { get; set; }
    public long TotalBytes { get; set; }
    public double SpeedBytesPerSec { get; set; }
    public double OverallProgressPercentage => TotalBytes > 0 ? Math.Min(100.0, (double)OverallBytesTransferred / TotalBytes * 100.0) : 0;
    public double CurrentFileProgressPercentage => CurrentFileTotalBytes > 0 ? Math.Min(100.0, (double)CurrentFileBytesTransferred / CurrentFileTotalBytes * 100.0) : 0;
    public double EtaSeconds => SpeedBytesPerSec > 0 ? Math.Max(0, (TotalBytes - OverallBytesTransferred) / SpeedBytesPerSec) : 0;

    public string FormattedSpeed => SpeedBytesPerSec switch
    {
        < 1024 => $"{SpeedBytesPerSec:F0} B/s",
        < 1024 * 1024 => $"{SpeedBytesPerSec / 1024.0:F1} KB/s",
        _ => $"{SpeedBytesPerSec / (1024.0 * 1024.0):F1} MB/s"
    };

    public string FormattedEta
    {
        get
        {
            if (SpeedBytesPerSec <= 0) return "--:--";
            var ts = TimeSpan.FromSeconds(EtaSeconds);
            return ts.TotalHours >= 1 ? ts.ToString(@"hh\:mm\:ss") : ts.ToString(@"mm\:ss");
        }
    }
}
