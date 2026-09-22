namespace AuraDrop.Core.Models;

public enum SessionStatus
{
    Pending,
    Accepted,
    Rejected,
    Transferring,
    Completed,
    Failed,
    Cancelled
}

public class TransferSession
{
    public string SessionId { get; set; } = Guid.NewGuid().ToString("N");
    public DeviceInfo Sender { get; set; } = new();
    public DeviceInfo? Receiver { get; set; }
    public List<TransferFile> Files { get; set; } = new();
    public long TotalBytes => Files.Sum(f => f.FileSize);
    public long TransferredBytes => Files.Sum(f => f.BytesTransferred);
    public string? PinCode { get; set; }
    public string SecurityToken { get; set; } = Guid.NewGuid().ToString("N");
    public SessionStatus Status { get; set; } = SessionStatus.Pending;
    public string? ErrorMessage { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? CompletedAt { get; set; }

    public string FormattedTotalSize => TransferFile.FormatSize(TotalBytes);
}
