using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using AuraDrop.Core.Models;
using AuraDrop.Core.PinCode;
using AuraDrop.Core.Services;
using Microsoft.Win32;

namespace AuraDrop.Windows;

public partial class MainWindow : Window
{
    private readonly AuraDropManager _manager;
    private readonly ObservableCollection<TransferFile> _selectedFiles = new();
    private readonly ObservableCollection<DeviceInfo> _nearbyDevices = new();
    private readonly ObservableCollection<TransferHistoryItem> _historyItems = new();
    private CancellationTokenSource? _activeTransferCts;
    private TaskCompletionSource<bool>? _incomingRequestTcs;

    public MainWindow()
    {
        var deviceName = Environment.MachineName;
        _manager = new AuraDropManager(deviceName, "Windows", 52525);

        InitializeComponent();

        ListNearbyDevices.ItemsSource = _nearbyDevices;
        ListHistory.ItemsSource = _historyItems;

        SetupManagerEvents();
        UpdateUiState();

        Loaded += MainWindow_Loaded;
        Closing += MainWindow_Closing;
    }

    private void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        TxtLocalDeviceName.Text = _manager.CurrentDevice.Name;
        TxtCenterDeviceName.Text = _manager.CurrentDevice.Name;

        var ipParts = _manager.CurrentDevice.IpAddress.Split('.');
        var hash = ipParts.Length > 0 ? $"#{ipParts[^1]}" : "#190";
        TxtCenterDeviceHash.Text = hash;

        TxtLocalIp.Text = $"IP: {_manager.CurrentDevice.IpAddress}:{_manager.CurrentDevice.Port}";
        TxtSettingsDeviceName.Text = _manager.CurrentDevice.Name;
        TxtSettingsDownloadFolder.Text = _manager.Server.DownloadDirectory;
        TxtReceiveFolder.Text = _manager.Server.DownloadDirectory;
        TxtSettingsNetwork.Text = $"IP: {_manager.CurrentDevice.IpAddress} | Port: {_manager.CurrentDevice.Port} | Discovery: 52526";

        ChkPinProtection.IsChecked = _manager.PinRequired;
        TxtSettingsPinCode.Text = _manager.ConfiguredPin;

        UpdateQuickSaveButtons();
        RefreshHistoryList();

        try
        {
            _manager.Start();
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Server konnte nicht gestartet werden: {ex.Message}", "AuraDrop", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void MainWindow_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        _manager.Dispose();
    }

    private void SetupManagerEvents()
    {
        _manager.Discovery.DeviceDiscovered += device =>
        {
            Dispatcher.InvokeAsync(() =>
            {
                if (!_nearbyDevices.Any(d => d.Id == device.Id))
                {
                    _nearbyDevices.Add(device);
                }
                UpdateUiState();
            });
        };

        _manager.Discovery.DeviceUpdated += device =>
        {
            Dispatcher.InvokeAsync(() =>
            {
                var existing = _nearbyDevices.FirstOrDefault(d => d.Id == device.Id);
                if (existing != null)
                {
                    existing.Name = device.Name;
                    existing.IpAddress = device.IpAddress;
                    existing.Port = device.Port;
                    existing.PinRequired = device.PinRequired;
                    existing.LastSeen = device.LastSeen;
                }
            });
        };

        _manager.Discovery.DeviceLost += deviceId =>
        {
            Dispatcher.InvokeAsync(() =>
            {
                var existing = _nearbyDevices.FirstOrDefault(d => d.Id == deviceId);
                if (existing != null)
                {
                    _nearbyDevices.Remove(existing);
                }
                UpdateUiState();
            });
        };

        // Incoming transfer request prompt
        _manager.Server.TransferRequested += async session =>
        {
            return await Dispatcher.InvokeAsync(() =>
            {
                _incomingRequestTcs = new TaskCompletionSource<bool>();
                TxtIncomingPromptMessage.Text = $"{session.Sender.Name} ({session.Sender.IpAddress}) möchte {session.Files.Count} Datei(en) ({session.FormattedTotalSize}) an dich senden.";
                BorderIncomingPrompt.Visibility = Visibility.Visible;
                return _incomingRequestTcs.Task;
            }).Task.Unwrap();
        };

        // Incoming transfer progress
        _manager.Server.TransferProgressChanged += progress =>
        {
            Dispatcher.InvokeAsync(() =>
            {
                UpdateProgressUi(progress, isReceiving: true);
            });
        };

        _manager.Server.TransferCompleted += session =>
        {
            Dispatcher.InvokeAsync(() =>
            {
                BorderLiveProgress.Visibility = Visibility.Collapsed;
                RefreshHistoryList();
                MessageBox.Show($"✅ {session.Files.Count} Datei(en) erfolgreich empfangen!\nGespeichert in: {_manager.Server.DownloadDirectory}", "AuraDrop", MessageBoxButton.OK, MessageBoxImage.Information);
            });
        };

        _manager.Server.TransferFailed += (session, error) =>
        {
            Dispatcher.InvokeAsync(() =>
            {
                BorderLiveProgress.Visibility = Visibility.Collapsed;
                MessageBox.Show($"❌ Übertragung fehlgeschlagen:\n{error}", "AuraDrop", MessageBoxButton.OK, MessageBoxImage.Error);
            });
        };

        _manager.HistoryItemAdded += _ =>
        {
            Dispatcher.InvokeAsync(RefreshHistoryList);
        };
    }

    private void UpdateUiState()
    {
        PanelNoDevicesNearby.Visibility = _nearbyDevices.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        PanelNoHistory.Visibility = _historyItems.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        if (_selectedFiles.Count > 0)
        {
            BorderSelectedFiles.Visibility = Visibility.Visible;
            var totalSize = _selectedFiles.Sum(f => f.FileSize);
            TxtSelectedFilesSummary.Text = $"{_selectedFiles.Count} Datei(en) ausgewählt ({TransferFile.FormatSize(totalSize)})";
        }
        else
        {
            BorderSelectedFiles.Visibility = Visibility.Collapsed;
        }
    }

    private void RefreshHistoryList()
    {
        _historyItems.Clear();
        foreach (var item in _manager.History)
        {
            _historyItems.Add(item);
        }
        UpdateUiState();
    }

    private void UpdateProgressUi(TransferProgress progress, bool isReceiving)
    {
        BorderLiveProgress.Visibility = Visibility.Visible;
        TxtProgressTitle.Text = isReceiving ? "Dateien werden empfangen..." : "Dateien werden gesendet...";
        TxtProgressFileName.Text = $"{progress.CurrentFileName} ({progress.CurrentFileIndex}/{progress.TotalFiles})";
        ProgressBarTransfer.Value = progress.OverallProgressPercentage;
        TxtProgressPercent.Text = $"{progress.OverallProgressPercentage:F0}%";
        TxtProgressSpeed.Text = progress.FormattedSpeed;
        TxtProgressEta.Text = $"Restzeit: {progress.FormattedEta}";
    }

    #region Window Controls & Dragging

    private void TitleBar_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Left)
        {
            if (e.ClickCount == 2)
            {
                WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
            }
            else
            {
                DragMove();
            }
        }
    }

    private void BtnMinimize_Click(object sender, RoutedEventArgs e)
    {
        WindowState = WindowState.Minimized;
    }

    private void BtnMaximize_Click(object sender, RoutedEventArgs e)
    {
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
    }

    private void BtnClose_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }

    #endregion

    #region Drag and Drop & File Selection

    private void Window_DragEnter(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
    }

    private void Window_Drop(object sender, DragEventArgs e)
    {
        if (e.Data.GetDataPresent(DataFormats.FileDrop))
        {
            var files = (string[])e.Data.GetData(DataFormats.FileDrop);
            AddFilesToSelection(files);
            TabBtnSend.IsChecked = true;
        }
    }

    private void BtnPickMedia_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog
        {
            Multiselect = true,
            Title = "Bilder & Videos auswählen",
            Filter = "Medien (*.jpg;*.png;*.mp4;*.mkv;*.mov)|*.jpg;*.jpeg;*.png;*.gif;*.webp;*.bmp;*.mp4;*.mkv;*.mov;*.avi|Alle Dateien (*.*)|*.*"
        };
        if (dlg.ShowDialog() == true)
        {
            AddFilesToSelection(dlg.FileNames);
        }
    }

    private void BtnPickFiles_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog
        {
            Multiselect = true,
            Title = "Dateien zum Senden auswählen"
        };
        if (dlg.ShowDialog() == true)
        {
            AddFilesToSelection(dlg.FileNames);
        }
    }

    private void BtnPickFolder_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFolderDialog
        {
            Title = "Ordner zum Senden auswählen"
        };
        if (dlg.ShowDialog() == true && !string.IsNullOrWhiteSpace(dlg.FolderName))
        {
            AddFilesToSelection(new[] { dlg.FolderName });
        }
    }

    private void BtnSendText_Click(object sender, RoutedEventArgs e)
    {
        var clip = Clipboard.GetText();
        var tempFile = Path.Combine(Path.GetTempPath(), $"Text_{DateTime.Now:yyyyMMdd_HHmmss}.txt");
        
        var promptText = !string.IsNullOrWhiteSpace(clip) ? clip : "AuraDrop Text-Nachricht";
        File.WriteAllText(tempFile, promptText);
        AddFilesToSelection(new[] { tempFile });
        MessageBox.Show("Text aus Zwischenablage für den Transfer bereitgestellt!", "AuraDrop Text", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private void AddFilesToSelection(IEnumerable<string> paths)
    {
        foreach (var path in paths)
        {
            if (File.Exists(path))
            {
                if (!_selectedFiles.Any(f => f.LocalPath == path))
                {
                    var fi = new FileInfo(path);
                    _selectedFiles.Add(new TransferFile
                    {
                        FileName = fi.Name,
                        FileSize = fi.Length,
                        LocalPath = fi.FullName
                    });
                }
            }
            else if (Directory.Exists(path))
            {
                foreach (var file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
                {
                    if (!_selectedFiles.Any(f => f.LocalPath == file))
                    {
                        var fi = new FileInfo(file);
                        _selectedFiles.Add(new TransferFile
                        {
                            FileName = Path.GetRelativePath(Directory.GetParent(path)?.FullName ?? path, file),
                            FileSize = fi.Length,
                            LocalPath = fi.FullName
                        });
                    }
                }
            }
        }
        UpdateUiState();
    }

    private void BtnClearFiles_Click(object sender, RoutedEventArgs e)
    {
        _selectedFiles.Clear();
        UpdateUiState();
    }

    private void BtnToggleFileList_Click(object sender, RoutedEventArgs e)
    {
        var names = string.Join("\n", _selectedFiles.Take(10).Select(f => $"• {f.FileName} ({f.DisplaySize})"));
        if (_selectedFiles.Count > 10) names += $"\n... und {_selectedFiles.Count - 10} weitere Datei(en)";
        MessageBox.Show($"Ausgewählte Dateien:\n\n{names}", "AuraDrop Auswahl", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    #endregion

    #region Transfer Actions

    private async void BtnSendToDevice_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button btn || btn.Tag is not DeviceInfo target) return;

        if (_selectedFiles.Count == 0)
        {
            MessageBox.Show("Bitte wähle zuerst mindestens eine Datei aus (Medien, Dateien oder Ordner).", "AuraDrop", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        string? pinCode = null;
        if (target.PinRequired)
        {
            pinCode = Microsoft.VisualBasic.Interaction.InputBox(
                $"{target.Name} erfordert einen 6-stelligen Sicherheits-PIN zur Übertragung.\nBitte gib den PIN des Empfängers ein:",
                "PIN-Schutz Autorisierung", "");
            
            if (string.IsNullOrWhiteSpace(pinCode)) return;
        }

        var filePaths = _selectedFiles.Where(f => !string.IsNullOrEmpty(f.LocalPath)).Select(f => f.LocalPath!).ToList();

        _activeTransferCts = new CancellationTokenSource();
        BorderLiveProgress.Visibility = Visibility.Visible;
        TxtProgressTitle.Text = $"Verbinde mit {target.Name}...";

        try
        {
            var success = await _manager.SendFilesToDeviceAsync(target, filePaths, pinCode, progress =>
            {
                Dispatcher.InvokeAsync(() => UpdateProgressUi(progress, isReceiving: false));
            }, _activeTransferCts.Token);

            BorderLiveProgress.Visibility = Visibility.Collapsed;

            if (success)
            {
                MessageBox.Show($"✅ Übertragung an {target.Name} erfolgreich abgeschlossen!", "AuraDrop", MessageBoxButton.OK, MessageBoxImage.Information);
                _selectedFiles.Clear();
                UpdateUiState();
            }
        }
        catch (OperationCanceledException)
        {
            BorderLiveProgress.Visibility = Visibility.Collapsed;
            MessageBox.Show("Übertragung wurde abgebrochen.", "AuraDrop", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            BorderLiveProgress.Visibility = Visibility.Collapsed;
            MessageBox.Show($"Fehler bei der Übertragung:\n{ex.Message}", "AuraDrop", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            _activeTransferCts = null;
        }
    }

    private void BtnGeneratePin_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedFiles.Count == 0)
        {
            MessageBox.Show("Bitte wähle zuerst Dateien aus, um einen Transfer-PIN zu generieren.", "AuraDrop", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var filePaths = _selectedFiles.Where(f => !string.IsNullOrEmpty(f.LocalPath)).Select(f => f.LocalPath!).ToList();
        var pin = _manager.CreatePinSession(filePaths);

        TxtGeneratedPin.Text = PinCodeManager.FormatPin(pin);
        BorderPinDisplay.Visibility = Visibility.Visible;
    }

    private void BtnClosePinOverlay_Click(object sender, RoutedEventArgs e)
    {
        BorderPinDisplay.Visibility = Visibility.Collapsed;
    }

    private void BtnOpenFetchPinDialog_Click(object sender, RoutedEventArgs e)
    {
        TxtInputPin.Text = "";
        BorderFetchPinModal.Visibility = Visibility.Visible;
    }

    private void BtnCloseFetchPinModal_Click(object sender, RoutedEventArgs e)
    {
        BorderFetchPinModal.Visibility = Visibility.Collapsed;
    }

    private void BtnCancelTransfer_Click(object sender, RoutedEventArgs e)
    {
        _activeTransferCts?.Cancel();
    }

    private async void BtnFetchViaPin_Click(object sender, RoutedEventArgs e)
    {
        var pin = TxtInputPin.Text.Trim();
        var cleanPin = PinCodeManager.NormalizePin(pin);

        if (cleanPin.Length != 6)
        {
            MessageBox.Show("Bitte gib einen gültigen 6-stelligen PIN ein.", "AuraDrop", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        BorderFetchPinModal.Visibility = Visibility.Collapsed;

        _activeTransferCts = new CancellationTokenSource();
        BorderLiveProgress.Visibility = Visibility.Visible;
        TxtProgressTitle.Text = $"Suche nach PIN {PinCodeManager.FormatPin(cleanPin)}...";

        try
        {
            var success = await _manager.DownloadViaPinAsync(cleanPin, progress =>
            {
                Dispatcher.InvokeAsync(() => UpdateProgressUi(progress, isReceiving: true));
            }, _activeTransferCts.Token);

            BorderLiveProgress.Visibility = Visibility.Collapsed;

            if (success)
            {
                MessageBox.Show($"✅ Download erfolgreich!\nGespeichert in: {_manager.Server.DownloadDirectory}", "AuraDrop", MessageBoxButton.OK, MessageBoxImage.Information);
            }
        }
        catch (Exception ex)
        {
            BorderLiveProgress.Visibility = Visibility.Collapsed;
            MessageBox.Show($"Download fehlgeschlagen:\n{ex.Message}", "AuraDrop", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            _activeTransferCts = null;
        }
    }

    private async void BtnDirectIpDialog_Click(object sender, RoutedEventArgs e)
    {
        var targetIp = Microsoft.VisualBasic.Interaction.InputBox("Gib die IP-Adresse des Zielgeräts ein (z.B. 192.168.0.75):", "Direkt verbinden", "192.168.0.");
        if (!string.IsNullOrWhiteSpace(targetIp))
        {
            await _manager.Discovery.BroadcastDirectPingAsync(targetIp);
            MessageBox.Show($"Signal an {targetIp} gesendet! Suche Gerät...", "AuraDrop", MessageBoxButton.OK, MessageBoxImage.Information);
        }
    }

    private async void BtnRefreshDevices_Click(object sender, RoutedEventArgs e)
    {
        _nearbyDevices.Clear();
        UpdateUiState();
        await _manager.Discovery.BroadcastDirectPingAsync("255.255.255.255");
    }

    private void BtnAcceptIncoming_Click(object sender, RoutedEventArgs e)
    {
        BorderIncomingPrompt.Visibility = Visibility.Collapsed;
        _incomingRequestTcs?.TrySetResult(true);
    }

    private void BtnRejectIncoming_Click(object sender, RoutedEventArgs e)
    {
        BorderIncomingPrompt.Visibility = Visibility.Collapsed;
        _incomingRequestTcs?.TrySetResult(false);
    }

    #endregion

    #region Settings & Navigation

    private void ChkPinProtection_Click(object sender, RoutedEventArgs e)
    {
        _manager.PinRequired = ChkPinProtection.IsChecked == true;
        _manager.SaveSettings();
    }

    private void TxtSettingsPinCode_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_manager == null || TxtSettingsPinCode == null) return;
        var text = TxtSettingsPinCode.Text?.Trim() ?? "";
        if (text.Length == 6 && int.TryParse(text, out _))
        {
            _manager.ConfiguredPin = text;
            _manager.SaveSettings();
        }
    }

    private void BtnGenerateNewPin_Click(object sender, RoutedEventArgs e)
    {
        var randomPin = Random.Shared.Next(100000, 999999).ToString();
        TxtSettingsPinCode.Text = randomPin;
        _manager.ConfiguredPin = randomPin;
        _manager.SaveSettings();
    }

    private void BtnQuickSave_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is string mode)
        {
            _manager.Server.AutoAcceptTransfers = (mode == "On");
            _manager.SaveSettings();
            UpdateQuickSaveButtons();
        }
    }

    private void UpdateQuickSaveButtons()
    {
        var isAuto = _manager.Server.AutoAcceptTransfers;
        var activeBrush = (System.Windows.Media.Brush)FindResource("PillActive");
        var cyanBrush = (System.Windows.Media.Brush)FindResource("AccentCyan");
        var transparentBrush = System.Windows.Media.Brushes.Transparent;
        var textMuted = (System.Windows.Media.Brush)FindResource("TextMuted");

        BtnQuickSaveOff.Background = !isAuto ? activeBrush : transparentBrush;
        BtnQuickSaveOff.Foreground = !isAuto ? cyanBrush : textMuted;

        BtnQuickSaveOn.Background = isAuto ? activeBrush : transparentBrush;
        BtnQuickSaveOn.Foreground = isAuto ? cyanBrush : textMuted;
    }

    private void TabBtn_Checked(object sender, RoutedEventArgs e)
    {
        if (ViewSend == null || ViewReceive == null || ViewSettings == null) return;

        ViewReceive.Visibility = TabBtnReceive.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        ViewSend.Visibility = TabBtnSend.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        ViewSettings.Visibility = TabBtnSettings.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
    }

    private void TxtReceiveFolder_MouseDown(object sender, MouseButtonEventArgs e)
    {
        var dir = _manager.Server.DownloadDirectory;
        if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
        Process.Start(new ProcessStartInfo("explorer.exe", dir) { UseShellExecute = true });
    }

    private void BtnOpenHistory_Click(object sender, RoutedEventArgs e)
    {
        RefreshHistoryList();
        BorderHistoryModal.Visibility = Visibility.Visible;
    }

    private void BtnCloseHistory_Click(object sender, RoutedEventArgs e)
    {
        BorderHistoryModal.Visibility = Visibility.Collapsed;
    }

    private void BtnOpenInfo_Click(object sender, RoutedEventArgs e)
    {
        MessageBox.Show("AuraDrop v1.0.0\n\nUltraschneller, direkter P2P-Dateitransfer über lokales WLAN.\nKompatibel zwischen Windows und Android.\nVolle Originalqualität ohne Komprimierung.", "Über AuraDrop", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private void BtnOpenHistoryFolder_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is TransferHistoryItem item)
        {
            var path = item.SavedPaths.FirstOrDefault(p => File.Exists(p) || Directory.Exists(p));
            var dir = !string.IsNullOrEmpty(path) && File.Exists(path) ? Path.GetDirectoryName(path)! : _manager.Server.DownloadDirectory;
            if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
            Process.Start(new ProcessStartInfo("explorer.exe", dir) { UseShellExecute = true });
        }
    }

    private void BtnClearHistory_Click(object sender, RoutedEventArgs e)
    {
        _manager.History.Clear();
        _manager.SaveSettings();
        RefreshHistoryList();
    }

    private void BtnSaveDeviceName_Click(object sender, RoutedEventArgs e)
    {
        var newName = TxtSettingsDeviceName.Text.Trim();
        if (!string.IsNullOrEmpty(newName))
        {
            _manager.CurrentDevice.Name = newName;
            TxtLocalDeviceName.Text = newName;
            TxtCenterDeviceName.Text = newName;
            _manager.SaveSettings();
            MessageBox.Show("Gerätename aktualisiert!", "AuraDrop", MessageBoxButton.OK, MessageBoxImage.Information);
        }
    }

    private void BtnBrowseDownloadFolder_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFolderDialog { Title = "Download-Ordner wählen" };
        if (dlg.ShowDialog() == true && !string.IsNullOrWhiteSpace(dlg.FolderName))
        {
            _manager.Server.DownloadDirectory = dlg.FolderName;
            TxtSettingsDownloadFolder.Text = dlg.FolderName;
            TxtReceiveFolder.Text = dlg.FolderName;
            _manager.SaveSettings();
        }
    }

    #endregion
}
