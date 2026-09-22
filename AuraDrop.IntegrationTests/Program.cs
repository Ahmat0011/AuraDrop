using System.Security.Cryptography;
using AuraDrop.Core.Models;
using AuraDrop.Core.PinCode;
using AuraDrop.Core.Services;

Console.WriteLine("==================================================");
Console.WriteLine("     AuraDrop End-to-End P2P Protocol Test        ");
Console.WriteLine("==================================================");

// 1. Create temporary folders
var testTempDir = Path.Combine(Path.GetTempPath(), "AuraDrop_Test_" + Guid.NewGuid().ToString("N")[..8]);
var senderDir = Path.Combine(testTempDir, "Sender");
var receiverDir = Path.Combine(testTempDir, "Receiver");
Directory.CreateDirectory(senderDir);
Directory.CreateDirectory(receiverDir);

// 2. Create sample test files
var sampleFile1 = Path.Combine(senderDir, "test_document.txt");
File.WriteAllText(sampleFile1, "Hello from AuraDrop P2P transfer! Testing document streaming.");

var sampleFile2 = Path.Combine(senderDir, "large_media_test.bin");
var randomBytes = new byte[5 * 1024 * 1024]; // 5 MB binary file
Random.Shared.NextBytes(randomBytes);
File.WriteAllBytes(sampleFile2, randomBytes);

var originalHash2 = Convert.ToHexString(SHA256.HashData(randomBytes));
Console.WriteLine($"[1] Erstellte Testdateien: 1 Textdatei + 1 Binärdatei (5 MB, SHA256={originalHash2[..8]}...)");

// 3. Initialize Sender Manager (Windows instance) on Port 52580
using var senderManager = new AuraDropManager("Test-Windows-PC", "Windows", 52580);
senderManager.Start();
Console.WriteLine($"[2] Sender (Windows PC) gestartet auf Port {senderManager.CurrentDevice.Port}");

// 4. Initialize Receiver Manager (Android instance) on Port 52581
using var receiverManager = new AuraDropManager("Test-Android-Phone", "Android", 52581);
receiverManager.Server.DownloadDirectory = receiverDir;
receiverManager.Server.AutoAcceptTransfers = true; // Auto-accept for test
receiverManager.Start();
Console.WriteLine($"[3] Receiver (Android Phone) gestartet auf Port {receiverManager.CurrentDevice.Port}");

// 5. Test Direct Transfer (LocalSend Mode)
Console.WriteLine("\n[Test A] Starte Direkt-Transfer (LocalSend-Modus: Windows -> Android)...");
var targetDeviceInfo = new DeviceInfo
{
    Id = receiverManager.CurrentDevice.Id,
    Name = receiverManager.CurrentDevice.Name,
    DeviceType = receiverManager.CurrentDevice.DeviceType,
    IpAddress = "127.0.0.1",
    Port = receiverManager.CurrentDevice.Port
};

var transferFinished = false;
var successA = await senderManager.SendFilesToDeviceAsync(
    targetDeviceInfo,
    new[] { sampleFile1, sampleFile2 },
    progress =>
    {
        Console.Write($"\r   -> Übertrage: {progress.CurrentFileName} [{progress.OverallProgressPercentage:F0}%] @ {progress.FormattedSpeed}   ");
    });

Console.WriteLine();
if (successA)
{
    Console.WriteLine("   ✅ Direkt-Transfer erfolgreich abgeschlossen!");
}
else
{
    Console.WriteLine("   ❌ Direkt-Transfer fehlgeschlagen!");
    Environment.Exit(1);
}

// Verify received files in Receiver Directory
var receivedFile1 = Path.Combine(receiverDir, "test_document.txt");
var receivedFile2 = Path.Combine(receiverDir, "large_media_test.bin");

if (!File.Exists(receivedFile1) || !File.Exists(receivedFile2))
{
    Console.WriteLine("   ❌ Fehler: Empfangene Dateien fehlen im Download-Ordner!");
    Environment.Exit(1);
}

var receivedHash2 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(receivedFile2)));
if (originalHash2 != receivedHash2)
{
    Console.WriteLine($"   ❌ Prüfsummen-Fehler! Original: {originalHash2}, Empfangen: {receivedHash2}");
    Environment.Exit(1);
}
Console.WriteLine($"   ✅ Prüfsumme verifiziert: SHA256 stimmt 100% überein ({receivedHash2[..8]}...)");

// 6. Test 6-Digit PIN Mode (Send Anywhere Mode)
Console.WriteLine("\n[Test B] Starte PIN-Transfer (Send Anywhere-Modus: 6-Stelliger Code)...");
var pinReceiverDir = Path.Combine(testTempDir, "PinReceiver");
Directory.CreateDirectory(pinReceiverDir);

var pin = senderManager.CreatePinSession(new[] { sampleFile2 });
Console.WriteLine($"   -> Generierter PIN-Code: {PinCodeManager.FormatPin(pin)}");

// Direct resolve via peer
var resolvedSession = await receiverManager.Client.ResolvePinFromPeerAsync(
    new DeviceInfo { IpAddress = "127.0.0.1", Port = senderManager.CurrentDevice.Port },
    pin);

if (resolvedSession == null)
{
    Console.WriteLine("   ❌ PIN-Auflösung fehlgeschlagen!");
    Environment.Exit(1);
}
Console.WriteLine($"   ✅ PIN erfolgreich aufgelöst: {resolvedSession.Files.Count} Datei(en) ({resolvedSession.FormattedTotalSize})");

await receiverManager.Client.DownloadFilesAsync(
    new DeviceInfo { IpAddress = "127.0.0.1", Port = senderManager.CurrentDevice.Port },
    resolvedSession,
    pinReceiverDir,
    progress =>
    {
        Console.Write($"\r   -> PIN-Download: {progress.CurrentFileName} [{progress.OverallProgressPercentage:F0}%] @ {progress.FormattedSpeed}   ");
    });

Console.WriteLine();
var pinReceivedFile = Path.Combine(pinReceiverDir, "large_media_test.bin");
if (!File.Exists(pinReceivedFile))
{
    Console.WriteLine("   ❌ Fehler: PIN-Download Datei fehlt!");
    Environment.Exit(1);
}

var pinHash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(pinReceivedFile)));
if (pinHash != originalHash2)
{
    Console.WriteLine("   ❌ Prüfsumme nach PIN-Download ungültig!");
    Environment.Exit(1);
}
Console.WriteLine($"   ✅ PIN-Download Prüfsumme verifiziert: 100% identisch!");

// Cleanup temp test files
try { Directory.Delete(testTempDir, true); } catch { }

Console.WriteLine("\n==================================================");
Console.WriteLine("🎉 ALLE TESTS ERFOLGREICH BESTANDEN! (100% PASS)  ");
Console.WriteLine("==================================================");
