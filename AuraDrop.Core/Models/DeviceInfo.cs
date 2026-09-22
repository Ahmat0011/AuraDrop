namespace AuraDrop.Core.Models;

public class DeviceInfo
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = Environment.MachineName;
    public string DeviceType { get; set; } = "Windows"; // "Windows", "Android"
    public string IpAddress { get; set; } = "127.0.0.1";
    public int Port { get; set; } = 52525;
    public string Version { get; set; } = "1.0.0";
    public DateTime LastSeen { get; set; } = DateTime.UtcNow;

    public string BaseUrl => $"http://{IpAddress}:{Port}";

    public string DisplayIcon => DeviceType.ToLowerInvariant() switch
    {
        "android" => "📱",
        "windows" => "💻",
        _ => "🖥️"
    };

    public string DisplaySummary => $"{Name} ({IpAddress})";
}
