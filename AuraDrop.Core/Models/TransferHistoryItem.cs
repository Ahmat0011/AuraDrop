namespace AuraDrop.Core.Models;

public enum TransferDirection
{
    Incoming,
    Outgoing
}

public class TransferHistoryItem
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string SessionId { get; set; } = string.Empty;
    public TransferDirection Direction { get; set; }
    public string PeerName { get; set; } = string.Empty;
    public string PeerIp { get; set; } = string.Empty;
    public int FileCount { get; set; }
    public long TotalBytes { get; set; }
    public List<string> FileNames { get; set; } = new();
    public List<string> SavedPaths { get; set; } = new();
    public SessionStatus Status { get; set; }
    public DateTime Timestamp { get; set; } = DateTime.Now;

    public string FormattedSize => TransferFile.FormatSize(TotalBytes);
    public string DirectionIcon => Direction == TransferDirection.Incoming ? "⬇️" : "⬆️";
    public string SummaryText => $"{DirectionIcon} {(Direction == TransferDirection.Incoming ? "Empfangen von" : "Gesendet an")} {PeerName}: {FileCount} Datei(en) ({FormattedSize})";
}
