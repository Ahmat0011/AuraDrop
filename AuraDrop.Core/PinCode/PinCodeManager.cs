using System.Collections.Concurrent;
using System.Security.Cryptography;
using AuraDrop.Core.Models;

namespace AuraDrop.Core.PinCode;

public class PinCodeManager
{
    private readonly ConcurrentDictionary<string, TransferSession> _activePins = new();

    public string GeneratePin(TransferSession session, TimeSpan? lifetime = null)
    {
        // Clean expired pins first
        CleanupExpiredPins();

        string pin;
        do
        {
            // Generate 6-digit number (100000 - 999999)
            var num = RandomNumberGenerator.GetInt32(100000, 1000000);
            pin = num.ToString();
        } while (_activePins.ContainsKey(pin));

        session.PinCode = pin;
        _activePins[pin] = session;
        return pin;
    }

    public TransferSession? ResolvePin(string pin)
    {
        var normalized = NormalizePin(pin);
        if (_activePins.TryGetValue(normalized, out var session))
        {
            if (session.Status != SessionStatus.Cancelled && session.Status != SessionStatus.Failed)
            {
                return session;
            }
        }
        return null;
    }

    public void RemovePin(string pin)
    {
        var normalized = NormalizePin(pin);
        _activePins.TryRemove(normalized, out _);
    }

    public static string NormalizePin(string pin)
    {
        return new string(pin.Where(char.IsDigit).ToArray());
    }

    public static string FormatPin(string pin)
    {
        var clean = NormalizePin(pin);
        if (clean.Length == 6)
        {
            return $"{clean[..3]} {clean[3..]}";
        }
        return clean;
    }

    private void CleanupExpiredPins()
    {
        var cutoff = DateTime.UtcNow.AddMinutes(-15);
        foreach (var pair in _activePins.ToArray())
        {
            if (pair.Value.CreatedAt < cutoff)
            {
                _activePins.TryRemove(pair.Key, out _);
            }
        }
    }
}
