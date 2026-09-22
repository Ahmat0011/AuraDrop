# ⚡ AuraDrop

<p align="center">
  <b>Sicherer, ultraschneller und dateigrößenunabhängiger P2P-Dateitransfer für Windows & Android.</b><br>
  Inspiriert von den modernen Design- und Funktionsprinzipien von <b>LocalSend</b> und <b>Send Anywhere</b>.
</p>

<p align="center">
  <img src="https://img.shields.io/badge/.NET-10.0-512BD4?style=for-the-badge&logo=dotnet&logoColor=white" alt=".NET 10" />
  <img src="https://img.shields.io/badge/Windows-WPF-0078D4?style=for-the-badge&logo=windows&logoColor=white" alt="Windows" />
  <img src="https://img.shields.io/badge/Android-MAUI-3DDC84?style=for-the-badge&logo=android&logoColor=white" alt="Android" />
  <img src="https://img.shields.io/badge/P2P-LAN%20Direct-5CD6B0?style=for-the-badge" alt="P2P Direct" />
</p>

---

## 🌟 Highlights

- 📶 **Lokale WLAN-Erkennung (LocalSend-Prinzip)**: Geräte im selben Netzwerk finden sich automatisch und in Sekundenschnelle per UDP-Multicast & Broadcast (Port 52526).
- 🔑 **6-Stelliger PIN-Code (Send Anywhere-Prinzip)**: Wähle Dateien aus und generiere einen 6-stelligen Code (z. B. `482 109`). Der Empfänger gibt den Code ein – der Download startet sofort direkt P2P. Funktioniert selbst dann, wenn der Router Multicast/Broadcast blockiert!
- 🚀 **Keine Größenbeschränkung & Beliebig viele Dateien**: Chunked Streaming in 256-KB-Blöcken direkt von der Festplatte/Flash-Speicher. Riesige 50-GB-Videos, ISOs oder hunderte Fotos belasten den RAM kaum.
- 📝 **Text- & Zwischenablage**: Sende Textnachrichten oder Zwischenablage-Inhalte mit einem Klick zwischen PC und Smartphone.
- ⚡ **Quick Save**: Dreistufiger Schalter (`Aus` / `Favoriten` / `An`). Eingehende Dateien können ohne manuelle Bestätigung direkt im Download-Ordner gespeichert werden.
- 🎨 **Modernes LocalSend-Design**: Dark Emerald (`#101A18`) mit Mint-Akzenten (`#5CD6B0`), segmentiertem Radar-Orb und pillenförmiger Navigation.

---

## 📂 Repository-Struktur

```
AuraDrop/
├── AuraDrop.slnx                      # Globale .NET 10 Solution
├── AuraDrop.Core/                     # Gemeinsame Netzwerk-, Radar- & PIN-Engine
│   ├── Models/                        # DeviceInfo, TransferFile, TransferSession, Progress
│   ├── Discovery/                     # UDP Broadcast & Multicast Discovery
│   ├── PinCode/                       # 6-Stelliger PIN-Manager
│   └── Network/                       # Asynchroner Streaming Server & Client
│
├── Windows/                           # Windows Desktop App (.NET 10 WPF)
│   ├── MainWindow.xaml                # Modernes LocalSend UI (Drag & Drop, Radar, PIN)
│   └── Release-Build/                 # Startfertige Windows-Anwendung
│       └── AuraDrop.exe               # 🚀 Ausführbare Datei
│
└── Android/                           # Android App (.NET 10 MAUI)
    ├── Views/MainPage.xaml            # Mobile LocalSend UI (Radar-Orb, Selection Chips)
    ├── build-release.ps1              # Automatisiertes APK-Build- & Signierungs-Skript
    └── Release-Build/                 # Installationsfertige Release-APK
        └── AuraDrop-v1.0.apk          # 📱 Signierte Android-APK
```

---

## 🚀 Installation & Verwendung

### Windows
Starte die fertige App direkt aus dem Release-Ordner:
```powershell
.\Windows\Release-Build\AuraDrop.exe
```
* **Dateien senden**: Dateien per Drag & Drop in das Fenster ziehen oder über die Auswahlschaltflächen (`Media`, `Text`, `File`, `Folder`) hinzufügen. Wähle das gewünschte Gerät in der Liste oder klicke auf `PIN Code`.
* **Dateien empfangen**: Im Tab `Empfangen` siehst du deinen Status. Dateien werden standardmäßig nach Bestätigung (oder sofort bei `Quick Save: An`) in `Downloads\AuraDrop` abgelegt.

### Android
Installiere die signierte APK auf deinem Android-Gerät:
```powershell
adb install .\Android\Release-Build\AuraDrop-v1.0.apk
```
Oder kopiere `AuraDrop-v1.0.apk` direkt auf dein Smartphone.

---

## 🛠️ Selbst kompilieren

### Voraussetzungen
- [.NET 10 SDK](https://dotnet.microsoft.com/)
- .NET Workloads: `android`, `maui-windows`

### Kompilierung

```powershell
# Gesamtes Projekt bauen
dotnet build AuraDrop.slnx

# Windows Release veröffentlichen
dotnet publish Windows\AuraDrop.Windows.csproj -c Release -r win-x64 --self-contained false -o Windows\Release-Build

# Android signierte APK bauen
powershell -ExecutionPolicy Bypass -File Android\build-release.ps1
```

---

## 🔒 Sicherheit & Privatsphäre

- **100% Offline-fähig**: Keine Cloud-Server, kein Tracking, keine Account-Registrierung.
- **Lokales Netzwerk (LAN)**: Die Daten verlassen zu keinem Zeitpunkt dein lokales Heimnetzwerk.
- **Transaktions-Token & Pfadbereinigung**: Jede Übertragung wird kryptografisch autorisiert und gegen Directory-Traversal-Angriffe gesichert.

---

## 📄 Lizenz
MIT License.
