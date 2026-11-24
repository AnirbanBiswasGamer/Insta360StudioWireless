using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Insta360StudioWireless.Models;
using Insta360StudioWireless.Services;

namespace Insta360StudioWireless.ViewModels;

public partial class MainViewModel : ObservableObject
{
    private readonly Insta360CameraClient _cameraClient;
    private readonly InsvTelemetryParser _telemetryParser;
    private CancellationTokenSource? _activeDownloadCts;

    [ObservableProperty]
    private string _cameraIp = Insta360CameraClient.DefaultCameraIp;

    [ObservableProperty]
    private int _cameraPort = Insta360CameraClient.DefaultControlPort;

    [ObservableProperty]
    private bool _isConnected;

    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    private string _statusText = "Ready. Connect PC to camera Wi-Fi and click Connect.";

    [ObservableProperty]
    private string _connectionBadgeText = "DISCONNECTED";

    [ObservableProperty]
    private string _connectionBadgeColor = "#D32F2F"; // Red

    [ObservableProperty]
    private string _downloadFolder = @"D:\Insta360\Footage";

    [ObservableProperty]
    private ObservableCollection<MediaFileItem> _mediaFiles = new();

    [ObservableProperty]
    private ObservableCollection<MediaDateGroup> _dateGroups = new();

    [ObservableProperty]
    private string _searchText = string.Empty;

    partial void OnSearchTextChanged(string value) => RefreshFilteredGroups();

    [ObservableProperty]
    private MediaFileItem? _selectedMedia;

    [ObservableProperty]
    private string _selectedFilter = "All";

    partial void OnSelectedFilterChanged(string value) => RefreshFilteredGroups();

    // Video Popup Player State
    [ObservableProperty]
    private bool _isPlayerPopupOpen;

    [ObservableProperty]
    private MediaFileItem? _activePlayingMedia;

    [ObservableProperty]
    private bool _isPlaying;

    [ObservableProperty]
    private double _playerCurrentSeconds;

    [ObservableProperty]
    private double _playerTotalSeconds = 1;

    [ObservableProperty]
    private string _playerTimeText = "00:00 / 00:00";

    [ObservableProperty]
    private double _currentSpeedKmh;

    [ObservableProperty]
    private double _currentAltitudeMeters;

    [ObservableProperty]
    private double _currentPitchDeg;

    [ObservableProperty]
    private double _currentRollDeg;

    [ObservableProperty]
    private string _currentGpsText = "No GPS lock";

    public string CurrentGpsCoordinateText => CurrentGpsText;

    public TelemetryDataSet? ActiveTelemetry
    {
        get => ActivePlayingMedia?.Telemetry;
        set
        {
            if (ActivePlayingMedia == null)
            {
                ActivePlayingMedia = new MediaFileItem { FileName = "active_media.mp4", Telemetry = value };
            }
            else
            {
                ActivePlayingMedia.Telemetry = value;
            }
        }
    }

    [ObservableProperty]
    private ObservableCollection<string> _logEntries = new();

    public event Action<string>? PlayMediaRequested;
    public event Action? StopMediaRequested;
    public event Action? TogglePlayPauseRequested;

    public MainViewModel()
    {
        _cameraClient = new Insta360CameraClient();
        _telemetryParser = new InsvTelemetryParser();

        _cameraClient.LogMessageReceived += msg =>
        {
            Application.Current?.Dispatcher?.Invoke(() =>
            {
                LogEntries.Insert(0, msg);
                if (LogEntries.Count > 150) LogEntries.RemoveAt(LogEntries.Count - 1);
            });
        };

        _cameraClient.ConnectionChanged += connected =>
        {
            Application.Current?.Dispatcher?.Invoke(() =>
            {
                IsConnected = connected;
                if (connected)
                {
                    ConnectionBadgeText = "CONNECTED (ONE RS)";
                    ConnectionBadgeColor = "#2E7D32"; // Green
                    StatusText = $"Connected to Insta360 at {CameraIp}";
                }
                else
                {
                    ConnectionBadgeText = "DISCONNECTED";
                    ConnectionBadgeColor = "#D32F2F"; // Red
                    StatusText = "Disconnected. Connect PC to camera Wi-Fi.";
                    MediaFiles.Clear();
                    DateGroups.Clear();
                }
            });
        };

        try
        {
            Directory.CreateDirectory(DownloadFolder);
        }
        catch { }

        // Automatically connect on launch in background
        Task.Run(async () =>
        {
            await Task.Delay(400);
            await ConnectAsync();
        });
    }

    [RelayCommand]
    private async Task ConnectAsync()
    {
        IsBusy = true;
        StatusText = $"Connecting to {CameraIp}...";
        try
        {
            bool ok = await _cameraClient.ConnectAsync(CameraIp, CameraPort);
            if (ok)
            {
                await RefreshGalleryAsync();
            }
            else
            {
                StatusText = "Camera not reachable. Turn on camera Wi-Fi and connect PC.";
            }
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    public void BrowseDownloadFolder()
    {
        try
        {
            var dialog = new Microsoft.Win32.OpenFolderDialog
            {
                Title = "Select Footage Download Folder",
                InitialDirectory = Directory.Exists(DownloadFolder) ? DownloadFolder : @"D:\Insta360"
            };

            if (dialog.ShowDialog() == true && !string.IsNullOrWhiteSpace(dialog.FolderName))
            {
                DownloadFolder = dialog.FolderName;
                Directory.CreateDirectory(DownloadFolder);
                StatusText = $"Download folder set to: {DownloadFolder}";
            }
        }
        catch (Exception ex)
        {
            StatusText = $"Failed to change folder: {ex.Message}";
        }
    }

    [RelayCommand]
    private void Disconnect()
    {
        _cameraClient.Disconnect();
        IsConnected = false;
        ConnectionBadgeText = "DISCONNECTED";
        ConnectionBadgeColor = "#D32F2F"; // Red
        StatusText = "Disconnected. Connect PC to camera Wi-Fi.";
        MediaFiles.Clear();
        DateGroups.Clear();
        if (IsPlayerPopupOpen)
        {
            CloseVideoPopup();
        }
    }

    [RelayCommand]
    private async Task RefreshGalleryAsync()
    {
        if (!IsConnected)
        {
            StatusText = "Not connected. Connect to camera first.";
            return;
        }

        IsBusy = true;
        StatusText = "Loading gallery from camera SD card...";
        try
        {
            var files = await _cameraClient.FetchFileListAsync();
            Application.Current?.Dispatcher?.Invoke(() =>
            {
                MediaFiles.Clear();
                foreach (var f in files)
                {
                    MediaFiles.Add(f);
                }
                RefreshFilteredGroups();
                StatusText = $"{MediaFiles.Count} items on camera";
            });
        }
        catch (Exception ex)
        {
            StatusText = $"Refresh failed: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    public void OpenVideoPopup(MediaFileItem? item)
    {
        item ??= SelectedMedia;
        if (item == null) return;

        ActivePlayingMedia = item;
        SelectedMedia = item;
        IsPlayerPopupOpen = true;
        IsPlaying = true;

        PlayerTotalSeconds = item.Duration.TotalSeconds > 0 ? item.Duration.TotalSeconds : 1;
        PlayerCurrentSeconds = 0;
        PlayerTimeText = $"00:00 / {item.FormattedDuration}";

        // Prefer local file if downloaded, else LRV (fast streaming proxy) if available, else remote URL
        string streamUrl = !string.IsNullOrEmpty(item.LocalPath) && File.Exists(item.LocalPath)
            ? item.LocalPath
            : (!string.IsNullOrEmpty(item.LrvUrl) ? item.LrvUrl : item.RemoteUrl);

        PlayMediaRequested?.Invoke(streamUrl);

        // Try extracting telemetry in background if it's a 360/INSE video
        if (item.Telemetry == null && (item.Kind == MediaKind.Video360 || item.FileName.EndsWith(".insv", StringComparison.OrdinalIgnoreCase)))
        {
            Task.Run(async () =>
            {
                try
                {
                    var data = await _telemetryParser.ParseRemoteFileAsync(item.RemoteUrl, item.FileSizeBytes);
                    Application.Current?.Dispatcher?.Invoke(() =>
                    {
                        item.Telemetry = data;
                        if (data.HasGps) item.HasGps = true;
                        if (data.HasGyro) item.HasGyro = true;
                    });
                }
                catch { }
            });
        }
    }

    [RelayCommand]
    public void CloseVideoPopup()
    {
        IsPlayerPopupOpen = false;
        IsPlaying = false;
        StopMediaRequested?.Invoke();
    }

    [RelayCommand]
    public void TogglePlayPause()
    {
        IsPlaying = !IsPlaying;
        TogglePlayPauseRequested?.Invoke();
    }

    [RelayCommand]
    private async Task DownloadMediaAsync(MediaFileItem? item)
    {
        item ??= ActivePlayingMedia ?? SelectedMedia;
        if (item == null) return;

        _activeDownloadCts = new CancellationTokenSource();
        var progress = new Progress<DownloadProgressUpdate>(update =>
        {
            StatusText = $"Downloading {item.FileName}: {update.ProgressPercentage:F1}% ({update.SpeedBytesPerSecond / (1024 * 1024):F1} MB/s) - ETA: {update.EstimatedTimeRemaining:mm\\:ss}";
        });

        try
        {
            await _cameraClient.DownloadFileAsync(item, DownloadFolder, progress, _activeDownloadCts.Token);
            StatusText = $"Downloaded {item.FileName} successfully to {DownloadFolder}!";
        }
        catch (OperationCanceledException)
        {
            StatusText = $"Download of {item.FileName} canceled.";
            item.IsDownloading = false;
        }
        catch (Exception ex)
        {
            StatusText = $"Download error: {ex.Message}";
            item.IsDownloading = false;
        }
    }

    [RelayCommand]
    private void CancelDownload(MediaFileItem? item)
    {
        _activeDownloadCts?.Cancel();
    }

    [RelayCommand]
    private async Task ExportGpxAsync()
    {
        var item = ActivePlayingMedia ?? SelectedMedia;
        if (item?.Telemetry == null || !item.Telemetry.HasGps)
        {
            StatusText = "No GPS points found to export.";
            return;
        }

        string baseName = Path.GetFileNameWithoutExtension(item.Telemetry.FileName);
        string gpxPath = Path.Combine(DownloadFolder, $"{baseName}_track.gpx");

        await InsvTelemetryParser.ExportToGpxAsync(item.Telemetry, gpxPath);
        StatusText = $"Exported GPX file to {gpxPath}";
    }

    public void UpdatePlaybackTime(double seconds)
    {
        PlayerCurrentSeconds = seconds;
        var ts = TimeSpan.FromSeconds(seconds);
        var totalTs = TimeSpan.FromSeconds(PlayerTotalSeconds);
        PlayerTimeText = $"{ts:mm\\:ss} / {totalTs:mm\\:ss}";

        var telem = ActivePlayingMedia?.Telemetry;
        if (telem == null) return;

        double timeMs = seconds * 1000.0;
        var gps = telem.GpsRecords.OrderBy(g => Math.Abs(g.TimestampMs - timeMs)).FirstOrDefault();
        if (gps != null)
        {
            CurrentSpeedKmh = gps.SpeedKmh;
            CurrentAltitudeMeters = gps.AltitudeMeters;
            CurrentGpsText = $"{gps.Latitude:F5}°, {gps.Longitude:F5}° ({gps.Satellites} sats)";
        }

        var gyro = telem.GyroRecords.OrderBy(g => Math.Abs(g.TimestampMs - timeMs)).FirstOrDefault();
        if (gyro != null)
        {
            CurrentPitchDeg = Math.Clamp(gyro.GyroX * 2.5, -45, 45);
            CurrentRollDeg = Math.Clamp(gyro.GyroY * 2.5, -45, 45);
        }
    }

    [RelayCommand]
    public void NextMedia()
    {
        if (ActivePlayingMedia == null || MediaFiles.Count == 0) return;
        var currentList = DateGroups.SelectMany(g => g.Items).ToList();
        int idx = currentList.IndexOf(ActivePlayingMedia);
        if (idx >= 0 && idx < currentList.Count - 1)
        {
            OpenVideoPopup(currentList[idx + 1]);
        }
    }

    [RelayCommand]
    public void PreviousMedia()
    {
        if (ActivePlayingMedia == null || MediaFiles.Count == 0) return;
        var currentList = DateGroups.SelectMany(g => g.Items).ToList();
        int idx = currentList.IndexOf(ActivePlayingMedia);
        if (idx > 0)
        {
            OpenVideoPopup(currentList[idx - 1]);
        }
    }

    [RelayCommand]
    public void SetFilter(string filter)
    {
        SelectedFilter = filter;
    }

    public void RefreshFilteredGroups()
    {
        var filtered = MediaFiles.AsEnumerable();

        if (!string.IsNullOrWhiteSpace(SearchText))
        {
            string q = SearchText.Trim().ToLowerInvariant();
            filtered = filtered.Where(f =>
                f.FileName.ToLowerInvariant().Contains(q) ||
                f.BadgeText.ToLowerInvariant().Contains(q) ||
                f.DateRecorded.ToString("yyyy-MM-dd").Contains(q) ||
                f.DateRecorded.ToString("MMMM").ToLowerInvariant().Contains(q));
        }

        if (SelectedFilter == "Videos")
        {
            filtered = filtered.Where(f => f.IsVideo);
        }
        else if (SelectedFilter == "Photos")
        {
            filtered = filtered.Where(f => f.IsPhoto);
        }
        else if (SelectedFilter == "360°")
        {
            filtered = filtered.Where(f => f.Is360);
        }

        var groups = filtered
            .GroupBy(f => f.DateRecorded.Date)
            .OrderByDescending(g => g.Key)
            .Select(g => new MediaDateGroup
            {
                Date = g.Key,
                DateHeader = FormatDateHeader(g.Key),
                Items = new ObservableCollection<MediaFileItem>(g.OrderByDescending(x => x.DateRecorded))
            })
            .ToList();

        DateGroups.Clear();
        foreach (var g in groups)
        {
            DateGroups.Add(g);
        }
    }

    private static string FormatDateHeader(DateTime date)
    {
        var today = DateTime.Today;
        if (date.Date == today) return $"Today • {date:ddd, MMM d}";
        if (date.Date == today.AddDays(-1)) return $"Yesterday • {date:ddd, MMM d}";
        return date.ToString("ddd, MMM d");
    }
}
