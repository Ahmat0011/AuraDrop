using System.Collections.ObjectModel;
using AuraDrop.AndroidApp.Models;
using AuraDrop.AndroidApp.Services;
using AuraDrop.Core.Models;
using AuraDrop.Core.PinCode;
using AuraDrop.Core.Services;
using CoreDeviceInfo = AuraDrop.Core.Models.DeviceInfo;
using MauiDeviceInfo = Microsoft.Maui.Devices.DeviceInfo;

namespace AuraDrop.AndroidApp.Views;

public partial class MainPage : ContentPage
{
    private readonly AuraDropManager _manager;
    private readonly ObservableCollection<TransferFile> _selectedFiles = new();
    private readonly ObservableCollection<CoreDeviceInfo> _nearbyDevices = new();
    private readonly ObservableCollection<TransferHistoryItem> _historyItems = new();
    private List<MediaGalleryItem> _allMediaItems = new();
    private List<MediaGalleryItem> _currentMediaItems = new();
    private List<MediaCategory> _categories = new();
    private string _selectedCategoryKey = "all";
    private CancellationTokenSource? _activeTransferCts;

    public MainPage()
    {
        InitializeComponent();

        var deviceName = MauiDeviceInfo.Current.Name;
        if (string.IsNullOrWhiteSpace(deviceName))
        {
            deviceName = $"{MauiDeviceInfo.Current.Manufacturer} {MauiDeviceInfo.Current.Model}";
        }

        _manager = new AuraDropManager(deviceName, "Android", 52525);

#if ANDROID
        try
        {
            var androidDownloads = Android.OS.Environment.GetExternalStoragePublicDirectory(Android.OS.Environment.DirectoryDownloads)?.AbsolutePath;
            if (!string.IsNullOrEmpty(androidDownloads))
            {
                var auraFolder = Path.Combine(androidDownloads, "AuraDrop");
                if (!Directory.Exists(auraFolder)) Directory.CreateDirectory(auraFolder);
                _manager.Server.DownloadDirectory = auraFolder;
            }
        }
        catch { }
#endif

        ColNearbyDevices.ItemsSource = _nearbyDevices;
        RadarCanvas.Drawable = new RadarOrbDrawable();

        SetupManagerEvents();
        UpdateUiState();
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();

        LblCenterDeviceName.Text = _manager.CurrentDevice.Name;
        var ipParts = _manager.CurrentDevice.IpAddress.Split('.');
        var hash = ipParts.Length > 0 ? $"#{ipParts[^1]}" : "#79";
        LblCenterDeviceHash.Text = hash;

        LblNetDetails.Text = $"IP: {_manager.CurrentDevice.IpAddress} | Port: {_manager.CurrentDevice.Port}";
        EntryDeviceName.Text = _manager.CurrentDevice.Name;
        SwitchAutoAccept.IsToggled = _manager.Server.AutoAcceptTransfers;
        LblQuickSaveStatus.Text = _manager.Server.AutoAcceptTransfers ? "An" : "Aus";

        SwitchPinProtection.IsToggled = _manager.PinRequired;
        EntryPinConfig.Text = _manager.ConfiguredPin;

        RefreshHistory();

        try
        {
            _manager.Start();
        }
        catch (Exception ex)
        {
            await DisplayAlert("Server Start", $"Konnte Server nicht starten: {ex.Message}", "OK");
        }
    }

    protected override void OnDisappearing()
    {
        base.OnDisappearing();
        _manager.Stop();
    }

    private void SetupManagerEvents()
    {
        _manager.Discovery.DeviceDiscovered += device =>
        {
            MainThread.BeginInvokeOnMainThread(() =>
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
            MainThread.BeginInvokeOnMainThread(() =>
            {
                var existing = _nearbyDevices.FirstOrDefault(d => d.Id == device.Id);
                if (existing != null)
                {
                    existing.Name = device.Name;
                    existing.IpAddress = device.IpAddress;
                    existing.Port = device.Port;
                    existing.PinRequired = device.PinRequired;
                }
            });
        };

        _manager.Discovery.DeviceLost += id =>
        {
            MainThread.BeginInvokeOnMainThread(() =>
            {
                var existing = _nearbyDevices.FirstOrDefault(d => d.Id == id);
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
            return await MainThread.InvokeOnMainThreadAsync(async () =>
            {
                return await DisplayAlert(
                    "Eingehende Übertragung",
                    $"{session.Sender.Name} ({session.Sender.IpAddress}) möchte {session.Files.Count} Datei(en) ({session.FormattedTotalSize}) senden.\n\nAkzeptieren?",
                    "Akzeptieren",
                    "Ablehnen");
            });
        };

        // Incoming transfer progress
        _manager.Server.TransferProgressChanged += progress =>
        {
            MainThread.BeginInvokeOnMainThread(() =>
            {
                UpdateProgress(progress, isReceiving: true);
            });
        };

        _manager.Server.TransferCompleted += session =>
        {
            MainThread.BeginInvokeOnMainThread(async () =>
            {
                ModalProgress.IsVisible = false;
                RefreshHistory();
                await DisplayAlert("Erfolgreich", $"{session.Files.Count} Datei(en) in Originalqualität empfangen!", "OK");
            });
        };

        _manager.Server.TransferFailed += (session, error) =>
        {
            MainThread.BeginInvokeOnMainThread(async () =>
            {
                ModalProgress.IsVisible = false;
                await DisplayAlert("Fehler", $"Übertragung fehlgeschlagen:\n{error}", "OK");
            });
        };

        _manager.HistoryItemAdded += _ =>
        {
            MainThread.BeginInvokeOnMainThread(RefreshHistory);
        };
    }

    private void UpdateUiState()
    {
        LblNoDevices.IsVisible = _nearbyDevices.Count == 0;

        if (_selectedFiles.Count > 0)
        {
            BorderSelectedFiles.IsVisible = true;
            LblSelectedCount.Text = $"{_selectedFiles.Count} Datei(en) ausgewählt";
            var total = _selectedFiles.Sum(f => f.FileSize);
            LblSelectedSize.Text = TransferFile.FormatSize(total);
        }
        else
        {
            BorderSelectedFiles.IsVisible = false;
        }
    }

    private void RefreshHistory()
    {
        _historyItems.Clear();
        foreach (var item in _manager.History)
        {
            _historyItems.Add(item);
        }
    }

    private void UpdateProgress(TransferProgress progress, bool isReceiving)
    {
        ModalProgress.IsVisible = true;
        LblProgressTitle.Text = isReceiving ? "Dateien werden empfangen..." : "Dateien werden gesendet...";
        LblProgressFile.Text = $"{progress.CurrentFileName} ({progress.CurrentFileIndex}/{progress.TotalFiles})";
        ProgressTransferBar.Progress = progress.OverallProgressPercentage / 100.0;
        LblProgressPercent.Text = $"{progress.OverallProgressPercentage:F0}%";
        LblProgressSpeed.Text = progress.FormattedSpeed;
        LblProgressEta.Text = $"Rest: {progress.FormattedEta}";
    }

    #region File Selection

    private async void OnPickPhotosClicked(object? sender, EventArgs e)
    {
        await OpenMediaPickerAsync();
    }

    private async void OnPickFilesClicked(object? sender, EventArgs e)
    {
        try
        {
            var results = await FilePicker.Default.PickMultipleAsync(new PickOptions
            {
                PickerTitle = "Dateien auswählen"
            });

            if (results != null) await AddPickedFilesAsync(results);
        }
        catch (Exception ex)
        {
            await DisplayAlert("Fehler", $"Datei-Auswahl fehlgeschlagen: {ex.Message}", "OK");
        }
    }

    private async void OnSendTextClicked(object? sender, EventArgs e)
    {
        var text = await DisplayPromptAsync("Text senden", "Gib einen Text oder eine Nachricht ein:");
        if (!string.IsNullOrWhiteSpace(text))
        {
            var tempFile = Path.Combine(FileSystem.CacheDirectory, $"Text_{DateTime.Now:yyyyMMdd_HHmmss}.txt");
            File.WriteAllText(tempFile, text);
            _selectedFiles.Add(new TransferFile
            {
                FileName = Path.GetFileName(tempFile),
                FileSize = new FileInfo(tempFile).Length,
                LocalPath = tempFile
            });
            UpdateUiState();
            await DisplayAlert("Text bereit", "Der Text ist bereit zur Übertragung!", "OK");
        }
    }

    private async Task AddPickedFilesAsync(IEnumerable<FileResult?> files)
    {
        foreach (var file in files)
        {
            if (file == null) continue;
            if (_selectedFiles.Any(f => f.LocalPath == file.FullPath || f.FileName == file.FileName))
                continue;

            long size = 0;
            try
            {
                using var stream = await file.OpenReadAsync();
                size = stream.Length;
            }
            catch
            {
                try { size = new FileInfo(file.FullPath).Length; } catch { }
            }

            _selectedFiles.Add(new TransferFile
            {
                FileName = file.FileName,
                FileSize = size,
                LocalPath = file.FullPath,
                OpenStreamAsync = async () => await file.OpenReadAsync()
            });
        }
        UpdateUiState();
    }

    private void OnClearFilesClicked(object? sender, EventArgs e)
    {
        _selectedFiles.Clear();
        UpdateUiState();
    }

    #endregion

    #region Send & Receive Actions

    private async void OnSendToDeviceClicked(object? sender, EventArgs e)
    {
        if (sender is not Button btn || btn.CommandParameter is not CoreDeviceInfo target) return;

        if (_selectedFiles.Count == 0)
        {
            await DisplayAlert("AuraDrop", "Bitte wähle zuerst mindestens eine Datei aus.", "OK");
            return;
        }

        string? pinCode = null;
        if (target.PinRequired)
        {
            pinCode = await DisplayPromptAsync("PIN erforderlich", $"{target.Name} erfordert einen 6-stelligen PIN:", keyboard: Keyboard.Numeric, maxLength: 6);
            if (string.IsNullOrWhiteSpace(pinCode)) return;
        }

        var filesToSend = _selectedFiles.ToList();

        _activeTransferCts = new CancellationTokenSource();
        ModalProgress.IsVisible = true;
        LblProgressTitle.Text = $"Verbinde mit {target.Name}...";

        try
        {
            var success = await _manager.SendTransferFilesAsync(target, filesToSend, pinCode, progress =>
            {
                MainThread.BeginInvokeOnMainThread(() => UpdateProgress(progress, isReceiving: false));
            }, _activeTransferCts.Token);

            ModalProgress.IsVisible = false;

            if (success)
            {
                await DisplayAlert("Erfolgreich", $"Dateien erfolgreich an {target.Name} übertragen!", "OK");
                _selectedFiles.Clear();
                UpdateUiState();
            }
        }
        catch (OperationCanceledException)
        {
            ModalProgress.IsVisible = false;
            await DisplayAlert("Abgebrochen", "Übertragung wurde abgebrochen.", "OK");
        }
        catch (Exception ex)
        {
            ModalProgress.IsVisible = false;
            await DisplayAlert("Fehler", $"Fehler bei der Übertragung:\n{ex.Message}", "OK");
        }
        finally
        {
            _activeTransferCts = null;
        }
    }

    private void OnGeneratePinClicked(object? sender, EventArgs e)
    {
        if (_selectedFiles.Count == 0)
        {
            DisplayAlert("AuraDrop", "Bitte wähle zuerst Dateien aus, um einen PIN zu generieren.", "OK");
            return;
        }

        var files = _selectedFiles.ToList();
        var pin = _manager.CreatePinSession(files);

        LblGeneratedPin.Text = PinCodeManager.FormatPin(pin);
        ModalPinDisplay.IsVisible = true;
    }

    private void OnClosePinModalClicked(object? sender, EventArgs e)
    {
        ModalPinDisplay.IsVisible = false;
    }

    private void OnCancelTransferClicked(object? sender, EventArgs e)
    {
        _activeTransferCts?.Cancel();
    }

    private async void OnPromptFetchPinClicked(object? sender, EventArgs e)
    {
        var pin = await DisplayPromptAsync("PIN abholen", "Gib den 6-stelligen Code des Absenders ein:", keyboard: Keyboard.Numeric, maxLength: 6);
        if (string.IsNullOrWhiteSpace(pin)) return;

        var cleanPin = PinCodeManager.NormalizePin(pin);
        if (cleanPin.Length != 6)
        {
            await DisplayAlert("Ungültiger PIN", "Der PIN muss genau 6 Ziffern lang sein.", "OK");
            return;
        }

        _activeTransferCts = new CancellationTokenSource();
        ModalProgress.IsVisible = true;
        LblProgressTitle.Text = $"Suche nach PIN {PinCodeManager.FormatPin(cleanPin)}...";

        try
        {
            var success = await _manager.DownloadViaPinAsync(cleanPin, progress =>
            {
                MainThread.BeginInvokeOnMainThread(() => UpdateProgress(progress, isReceiving: true));
            }, _activeTransferCts.Token);

            ModalProgress.IsVisible = false;

            if (success)
            {
                await DisplayAlert("Fertig", "Dateien erfolgreich empfangen!", "OK");
            }
        }
        catch (Exception ex)
        {
            ModalProgress.IsVisible = false;
            await DisplayAlert("Fehler", $"Download fehlgeschlagen:\n{ex.Message}", "OK");
        }
        finally
        {
            _activeTransferCts = null;
        }
    }

    private async void OnRefreshDevicesClicked(object? sender, EventArgs e)
    {
        _nearbyDevices.Clear();
        UpdateUiState();
        await _manager.Discovery.BroadcastDirectPingAsync("255.255.255.255");
    }

    private async void OnDirectIpClicked(object? sender, EventArgs e)
    {
        var ip = await DisplayPromptAsync("Direkt verbinden", "IP-Adresse des Empfängers/Absenders eingeben:", initialValue: "192.168.0.");
        if (!string.IsNullOrWhiteSpace(ip))
        {
            await _manager.Discovery.BroadcastDirectPingAsync(ip);
            await DisplayAlert("Signal gesendet", $"Suche nach Gerät auf {ip}...", "OK");
        }
    }

    private void OnToggleQuickSaveClicked(object? sender, EventArgs e)
    {
        _manager.Server.AutoAcceptTransfers = !_manager.Server.AutoAcceptTransfers;
        SwitchAutoAccept.IsToggled = _manager.Server.AutoAcceptTransfers;
        LblQuickSaveStatus.Text = _manager.Server.AutoAcceptTransfers ? "An" : "Aus";
        _manager.SaveSettings();
    }

    private async void OnOpenHistoryClicked(object? sender, EventArgs e)
    {
        var count = _manager.History.Count;
        if (count == 0)
        {
            await DisplayAlert("Verlauf", "Noch keine Dateiübertragungen vorhanden.", "OK");
            return;
        }

        var summary = string.Join("\n\n", _manager.History.Take(6).Select(h => $"{h.DirectionIcon} {h.PeerName}: {h.FileCount} Datei(en) ({h.FormattedSize})\n{h.Timestamp:dd.MM.yyyy HH:mm}"));
        await DisplayAlert("Übertragungsverlauf", summary, "OK");
    }

    private async void OnOpenInfoClicked(object? sender, EventArgs e)
    {
        await DisplayAlert("Über AuraDrop", "AuraDrop v1.0.0\n\nDirekter, unkomprimierter P2P-Dateitransfer im lokalen Netzwerk.\nBilder und Videos werden in 100% Originalqualität übertragen.", "OK");
    }

    private void OnSaveDeviceNameClicked(object? sender, EventArgs e)
    {
        var newName = EntryDeviceName.Text?.Trim();
        if (!string.IsNullOrWhiteSpace(newName))
        {
            _manager.CurrentDevice.Name = newName;
            LblCenterDeviceName.Text = newName;
            _manager.SaveSettings();
            DisplayAlert("Gespeichert", "Gerätename wurde aktualisiert.", "OK");
        }
    }

    private void OnAutoAcceptToggled(object? sender, ToggledEventArgs e)
    {
        _manager.Server.AutoAcceptTransfers = e.Value;
        LblQuickSaveStatus.Text = e.Value ? "An" : "Aus";
        _manager.SaveSettings();
    }

    private void OnPinProtectionToggled(object? sender, ToggledEventArgs e)
    {
        _manager.PinRequired = e.Value;
        _manager.SaveSettings();
    }

    private void OnPinConfigChanged(object? sender, TextChangedEventArgs e)
    {
        if (_manager == null || EntryPinConfig == null) return;
        var text = EntryPinConfig.Text?.Trim() ?? "";
        if (text.Length == 6 && int.TryParse(text, out _))
        {
            _manager.ConfiguredPin = text;
            _manager.SaveSettings();
        }
    }

    private void OnGenerateNewPinClicked(object? sender, EventArgs e)
    {
        var randomPin = Random.Shared.Next(100000, 999999).ToString();
        EntryPinConfig.Text = randomPin;
        _manager.ConfiguredPin = randomPin;
        _manager.SaveSettings();
    }

    #endregion

    #region Navigation

    private void OnNavReceiveClicked(object? sender, EventArgs e)
    {
        ViewReceive.IsVisible = true;
        ViewSend.IsVisible = false;
        ViewSettings.IsVisible = false;
        UpdateNavPills(isReceive: true, isSend: false, isSettings: false);
    }

    private void OnNavSendClicked(object? sender, EventArgs e)
    {
        ViewReceive.IsVisible = false;
        ViewSend.IsVisible = true;
        ViewSettings.IsVisible = false;
        UpdateNavPills(isReceive: false, isSend: true, isSettings: false);
    }

    private void OnNavSettingsClicked(object? sender, EventArgs e)
    {
        ViewReceive.IsVisible = false;
        ViewSend.IsVisible = false;
        ViewSettings.IsVisible = true;
        UpdateNavPills(isReceive: false, isSend: false, isSettings: true);
    }

    private void UpdateNavPills(bool isReceive, bool isSend, bool isSettings)
    {
        var secondaryColor = (Color)Application.Current!.Resources["Secondary"];
        var primaryColor = (Color)Application.Current!.Resources["Primary"];
        var textColor = (Color)Application.Current!.Resources["TextSecondary"];
        var mutedColor = (Color)Application.Current!.Resources["TextMuted"];

        BorderNavReceive.BackgroundColor = isReceive ? secondaryColor : Colors.Transparent;
        LblNavReceive.TextColor = isReceive ? primaryColor : textColor;
        LblNavReceive.FontAttributes = isReceive ? FontAttributes.Bold : FontAttributes.None;
        PathNavReceive.Stroke = isReceive ? primaryColor : mutedColor;

        BorderNavSend.BackgroundColor = isSend ? secondaryColor : Colors.Transparent;
        LblNavSend.TextColor = isSend ? primaryColor : textColor;
        LblNavSend.FontAttributes = isSend ? FontAttributes.Bold : FontAttributes.None;
        PathNavSend.Stroke = isSend ? primaryColor : mutedColor;

        BorderNavSettings.BackgroundColor = isSettings ? secondaryColor : Colors.Transparent;
        LblNavSettings.TextColor = isSettings ? primaryColor : textColor;
        LblNavSettings.FontAttributes = isSettings ? FontAttributes.Bold : FontAttributes.None;
        PathNavSettings.Stroke = isSettings ? primaryColor : mutedColor;
    }

    #endregion

    #region Native Media Gallery Picker

    private async Task OpenMediaPickerAsync()
    {
        try
        {
            ViewMediaPicker.IsVisible = true;
            BorderCategoryDropdown.IsVisible = false;
            LayoutMediaLoading.IsVisible = true;
            LayoutMediaEmpty.IsVisible = false;

            var granted = await AndroidMediaService.Instance.RequestPermissionsAsync();
            if (!granted)
            {
                await DisplayAlert("Berechtigung erforderlich", "Bitte erlaube Zugriff auf Fotos und Videos, um Medien auszuwählen.", "OK");
            }

            var (items, categories) = await AndroidMediaService.Instance.LoadAllMediaAsync();
            _allMediaItems = items;
            _categories = categories;

            // Mark items already in transfer queue as selected
            foreach (var item in _allMediaItems)
            {
                item.IsSelected = _selectedFiles.Any(f => f.LocalPath == item.FilePath || f.FileName == item.DisplayName);
            }

            _selectedCategoryKey = "all";
            LblCurrentCategoryTitle.Text = "Alle Medien";

            PopulateCategoryDropdown();
            RefreshMediaGrid();

            LayoutMediaLoading.IsVisible = false;
            UpdateMediaPickerSelectionState();
        }
        catch (Exception ex)
        {
            LayoutMediaLoading.IsVisible = false;
            await DisplayAlert("Fehler", $"Medien konnten nicht geladen werden: {ex.Message}", "OK");
        }
    }

    private void PopulateCategoryDropdown()
    {
        StackCategories.Children.Clear();

        foreach (var category in _categories)
        {
            var rowGrid = new Grid
            {
                ColumnDefinitions = new ColumnDefinitionCollection
                {
                    new ColumnDefinition(new GridLength(48)),
                    new ColumnDefinition(GridLength.Star),
                    new ColumnDefinition(new GridLength(32))
                },
                Padding = new Thickness(14, 8),
                BackgroundColor = category.Key == _selectedCategoryKey 
                    ? Color.FromArgb("#1E293B") 
                    : Colors.Transparent
            };

            // Thumbnail Preview
            var borderThumb = new Border
            {
                WidthRequest = 42,
                HeightRequest = 42,
                StrokeShape = new Microsoft.Maui.Controls.Shapes.RoundRectangle { CornerRadius = 6 },
                Stroke = Colors.Transparent,
                BackgroundColor = Color.FromArgb("#1E2C48")
            };
            if (category.CoverImage != null)
            {
                borderThumb.Content = new Image
                {
                    Source = category.CoverImage,
                    Aspect = Aspect.AspectFill,
                    WidthRequest = 42,
                    HeightRequest = 42
                };
            }
            rowGrid.Children.Add(borderThumb);
            Grid.SetColumn(borderThumb, 0);

            // Title & Count (e.g. "Screenshots (21)")
            var lblTitle = new Label
            {
                Text = category.DisplayTitle,
                FontSize = 14,
                FontAttributes = category.Key == _selectedCategoryKey ? FontAttributes.Bold : FontAttributes.None,
                TextColor = category.Key == _selectedCategoryKey ? Color.FromArgb("#38BDF8") : Color.FromArgb("#F8FAFC"),
                VerticalOptions = LayoutOptions.Center,
                Margin = new Thickness(12, 0, 0, 0)
            };
            rowGrid.Children.Add(lblTitle);
            Grid.SetColumn(lblTitle, 1);

            // Active Category Checkmark
            if (category.Key == _selectedCategoryKey)
            {
                var lblCheck = new Label
                {
                    Text = "✓",
                    TextColor = Color.FromArgb("#10B981"),
                    FontSize = 18,
                    FontAttributes = FontAttributes.Bold,
                    HorizontalOptions = LayoutOptions.Center,
                    VerticalOptions = LayoutOptions.Center
                };
                rowGrid.Children.Add(lblCheck);
                Grid.SetColumn(lblCheck, 2);
            }

            var tapGesture = new TapGestureRecognizer();
            var catKey = category.Key;
            var catTitle = category.Title;
            tapGesture.Tapped += (s, e) =>
            {
                _selectedCategoryKey = catKey;
                LblCurrentCategoryTitle.Text = catTitle;
                BorderCategoryDropdown.IsVisible = false;
                PathCategoryChevron.Rotation = 0;
                PopulateCategoryDropdown();
                RefreshMediaGrid();
            };
            rowGrid.GestureRecognizers.Add(tapGesture);

            StackCategories.Children.Add(rowGrid);
        }
    }

    private void RefreshMediaGrid()
    {
        _currentMediaItems = AndroidMediaService.Instance.FilterByCategory(_selectedCategoryKey);
        CvMediaGrid.ItemsSource = null;
        CvMediaGrid.ItemsSource = _currentMediaItems;
        LayoutMediaEmpty.IsVisible = _currentMediaItems.Count == 0;
    }

    private void OnCloseMediaPickerClicked(object? sender, EventArgs e)
    {
        ViewMediaPicker.IsVisible = false;
        BorderCategoryDropdown.IsVisible = false;
        PathCategoryChevron.Rotation = 0;
    }

    private void OnToggleCategoryDropdownClicked(object? sender, EventArgs e)
    {
        BorderCategoryDropdown.IsVisible = !BorderCategoryDropdown.IsVisible;
        PathCategoryChevron.Rotation = BorderCategoryDropdown.IsVisible ? 180 : 0;
    }

    private void OnMediaGridItemTapped(object? sender, EventArgs e)
    {
        if (sender is Element element && element.BindingContext is MediaGalleryItem item)
        {
            item.IsSelected = !item.IsSelected;
            UpdateMediaPickerSelectionState();
        }
    }

    private void OnClearMediaPickerSelectionClicked(object? sender, EventArgs e)
    {
        foreach (var item in _allMediaItems)
        {
            item.IsSelected = false;
        }
        UpdateMediaPickerSelectionState();
    }

    private void UpdateMediaPickerSelectionState()
    {
        int selectedCount = _allMediaItems.Count(m => m.IsSelected);
        LblMediaSelectionSummary.Text = selectedCount == 1 ? "1 ausgewählt" : $"{selectedCount} ausgewählt";
        BtnConfirmMediaSelection.Text = selectedCount > 0 ? $"Bestätigen ({selectedCount})" : "Bestätigen";
        BtnConfirmMediaSelection.IsEnabled = selectedCount > 0;
        BtnConfirmMediaSelection.Opacity = selectedCount > 0 ? 1.0 : 0.6;
    }

    private void OnConfirmMediaSelectionClicked(object? sender, EventArgs e)
    {
        var selectedMedia = _allMediaItems.Where(m => m.IsSelected).ToList();
        foreach (var item in selectedMedia)
        {
            if (_selectedFiles.Any(f => f.LocalPath == item.FilePath || f.FileName == item.DisplayName))
                continue;

            _selectedFiles.Add(new TransferFile
            {
                FileName = item.DisplayName,
                FileSize = item.FileSize,
                ContentType = item.MimeType,
                LocalPath = item.FilePath,
#if ANDROID
                OpenStreamAsync = () => Task.FromResult<Stream>(
                    !string.IsNullOrEmpty(item.FilePath) && File.Exists(item.FilePath)
                        ? File.OpenRead(item.FilePath)
                        : Android.App.Application.Context.ContentResolver!.OpenInputStream(Android.Net.Uri.Parse(item.UriString)!)!
                )
#endif
            });
        }

        ViewMediaPicker.IsVisible = false;
        BorderCategoryDropdown.IsVisible = false;
        PathCategoryChevron.Rotation = 0;
        UpdateUiState();
    }

    private async void OnReloadMediaClicked(object? sender, EventArgs e)
    {
        await OpenMediaPickerAsync();
    }

    #endregion
}

public class RadarOrbDrawable : IDrawable
{
    public void Draw(ICanvas canvas, RectF dirtyRect)
    {
        var cx = dirtyRect.Width / 2;
        var cy = dirtyRect.Height / 2;
        var cyanColor = Color.FromArgb("#38BDF8");
        var indigoColor = Color.FromArgb("#6366F1");

        canvas.Antialias = true;

        // Draw solid center core circle
        canvas.FillColor = indigoColor;
        canvas.FillCircle(cx, cy, 32);

        canvas.FillColor = cyanColor;
        canvas.FillCircle(cx, cy, 24);

        // Draw continuous solid outer glowing neon ring (no dashes, no gaps, completely filled!)
        canvas.StrokeColor = cyanColor;
        canvas.StrokeSize = 7;
        canvas.StrokeLineCap = LineCap.Round;
        canvas.DrawCircle(cx, cy, 56);

        // Draw inner subtle guide circle
        canvas.StrokeColor = Color.FromArgb("#1E2C48");
        canvas.StrokeSize = 2;
        canvas.DrawCircle(cx, cy, 42);

        // Draw crisp modern white download symbol in center
        canvas.StrokeColor = Colors.White;
        canvas.StrokeSize = 2.4f;
        canvas.StrokeLineCap = LineCap.Round;
        canvas.StrokeLineJoin = LineJoin.Round;

        // Downward Arrow (vertical stem + chevron head)
        canvas.DrawLine(cx, cy - 8, cx, cy + 3);
        var arrowHead = new PathF();
        arrowHead.MoveTo(cx - 5, cy - 1);
        arrowHead.LineTo(cx, cy + 4);
        arrowHead.LineTo(cx + 5, cy - 1);
        canvas.DrawPath(arrowHead);

        // Open Tray
        var tray = new PathF();
        tray.MoveTo(cx - 8, cy + 4);
        tray.LineTo(cx - 8, cy + 8);
        tray.LineTo(cx + 8, cy + 8);
        tray.LineTo(cx + 8, cy + 4);
        canvas.DrawPath(tray);
    }
}
