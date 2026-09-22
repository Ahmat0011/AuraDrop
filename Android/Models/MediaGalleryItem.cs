using System.ComponentModel;
using System.Runtime.CompilerServices;
using Microsoft.Maui.Controls;

namespace AuraDrop.AndroidApp.Models;

public class MediaGalleryItem : INotifyPropertyChanged
{
    public long Id { get; set; }
    public string UriString { get; set; } = string.Empty;
    public string FilePath { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public long FileSize { get; set; }
    public string MimeType { get; set; } = string.Empty;
    public bool IsVideo { get; set; }
    public long DurationMs { get; set; }
    public string BucketDisplayName { get; set; } = string.Empty;
    public long DateAdded { get; set; }

    public string FormattedDuration
    {
        get
        {
            if (!IsVideo || DurationMs <= 0) return string.Empty;
            var ts = TimeSpan.FromMilliseconds(DurationMs);
            if (ts.TotalHours >= 1)
            {
                return $"{(int)ts.TotalHours}:{ts.Minutes:D2}:{ts.Seconds:D2}";
            }
            return $"{ts.Minutes:D2}:{ts.Seconds:D2}";
        }
    }

    private ImageSource? _thumbnailSource;
    public ImageSource? ThumbnailSource
    {
        get => _thumbnailSource;
        set
        {
            if (_thumbnailSource != value)
            {
                _thumbnailSource = value;
                OnPropertyChanged();
            }
        }
    }

    private bool _isSelected;
    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (_isSelected != value)
            {
                _isSelected = value;
                OnPropertyChanged();
            }
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    protected void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}

public class MediaCategory : INotifyPropertyChanged
{
    public string Key { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public int Count { get; set; }
    public string DisplayTitle => $"{Title} ({Count})";

    private ImageSource? _coverImage;
    public ImageSource? CoverImage
    {
        get => _coverImage;
        set
        {
            if (_coverImage != value)
            {
                _coverImage = value;
                OnPropertyChanged();
            }
        }
    }

    private bool _isSelected;
    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (_isSelected != value)
            {
                _isSelected = value;
                OnPropertyChanged();
            }
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    protected void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
