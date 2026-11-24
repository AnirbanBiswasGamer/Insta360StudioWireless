using System.Windows.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Insta360StudioWireless.Models;

public enum MediaKind
{
    Video360,
    Photo360,
    StandardVideo,
    StandardPhoto,
    TimeLapse
}

public partial class MediaFileItem : ObservableObject
{
    [ObservableProperty]
    private string _fileName = string.Empty;

    [ObservableProperty]
    private string _remoteUrl = string.Empty;

    [ObservableProperty]
    private string _lrvUrl = string.Empty;

    [ObservableProperty]
    private string _localPath = string.Empty;

    [ObservableProperty]
    private string _thumbnailUrl = string.Empty;

    [ObservableProperty]
    private BitmapSource? _thumbnailBitmap;

    [ObservableProperty]
    private long _fileSizeBytes;

    [ObservableProperty]
    private DateTime _dateRecorded = DateTime.Now;

    [ObservableProperty]
    private TimeSpan _duration = TimeSpan.Zero;

    [ObservableProperty]
    private int _fps;

    [ObservableProperty]
    private MediaKind _kind = MediaKind.StandardVideo;

    [ObservableProperty]
    private bool _isDownloaded;

    [ObservableProperty]
    private bool _isDownloading;

    [ObservableProperty]
    private double _downloadProgressPercent;

    [ObservableProperty]
    private string _downloadSpeedText = string.Empty;

    [ObservableProperty]
    private bool _hasGps;

    [ObservableProperty]
    private bool _hasGyro;

    [ObservableProperty]
    private TelemetryDataSet? _telemetry;

    public string FormattedSize
    {
        get
        {
            if (FileSizeBytes <= 0) return "--";
            if (FileSizeBytes < 1024) return $"{FileSizeBytes} B";
            if (FileSizeBytes < 1024 * 1024) return $"{FileSizeBytes / 1024.0:F1} KB";
            if (FileSizeBytes < 1024 * 1024 * 1024) return $"{FileSizeBytes / (1024.0 * 1024.0):F1} MB";
            return $"{FileSizeBytes / (1024.0 * 1024.0 * 1024.0):F2} GB";
        }
    }

    public string FormattedDuration
    {
        get
        {
            if (Duration <= TimeSpan.Zero) return "--:--";
            if (Duration.TotalHours >= 1)
            {
                return $"{(int)Duration.TotalHours}:{Duration.Minutes:D2}:{Duration.Seconds:D2}";
            }
            return $"{Duration.Minutes}:{Duration.Seconds:D2}";
        }
    }

    public bool IsVideo => Kind == MediaKind.Video360 || Kind == MediaKind.StandardVideo || Kind == MediaKind.TimeLapse;
    public bool IsPhoto => Kind == MediaKind.Photo360 || Kind == MediaKind.StandardPhoto;
    public bool Is360 => Kind == MediaKind.Video360 || Kind == MediaKind.Photo360;
    public string FormattedDateDetail => DateRecorded.ToString("MMMM d, yyyy • h:mm tt");

    public string BadgeText => Kind switch
    {
        MediaKind.Video360 => "360° Video",
        MediaKind.Photo360 => "360° Photo",
        MediaKind.StandardPhoto => "Photo (JPG)",
        MediaKind.TimeLapse => "Timelapse",
        _ => "Video (MP4)"
    };

    public string TelemetryStatusText => HasGps
        ? (HasGyro ? "● GPS & 6-Axis Gyro Track" : "● GPS Telemetry Available")
        : (HasGyro ? "● 6-Axis Gyro (No GPS)" : "○ No GPS Data");

    public string TelemetryStatusColor => HasGps ? "#81C784" : (HasGyro ? "#60CDFF" : "#888894");
}
