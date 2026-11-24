using System.Buffers.Binary;
using System.IO;
using System.Net.Http;
using System.Net.Sockets;
using System.Text.RegularExpressions;
using System.Windows.Media.Imaging;
using Insta360StudioWireless.Models;

namespace Insta360StudioWireless.Services;

public record DownloadProgressUpdate(
    long BytesReceived,
    long TotalBytes,
    double ProgressPercentage,
    double SpeedBytesPerSecond,
    TimeSpan EstimatedTimeRemaining
);

public class Insta360CameraClient : IDisposable
{
    public const string DefaultCameraIp = "192.168.42.1";
    public const int DefaultControlPort = 6666;
    public const int DefaultHttpPort = 80;

    private readonly HttpClient _httpClient;
    private TcpClient? _tcpClient;
    private NetworkStream? _networkStream;
    private CancellationTokenSource? _sessionCts;
    private uint _sequenceCounter;
    private readonly object _lock = new();
    private readonly Dictionary<uint, TaskCompletionSource<(ushort code, byte[] body)>> _pendingCommands = new();
    private bool _isDisposed;

    public string CameraIp { get; set; } = DefaultCameraIp;
    public int ControlPort { get; set; } = DefaultControlPort;
    public bool IsConnected { get; private set; }

    public event Action<bool>? ConnectionChanged;
    public event Action<string>? LogMessageReceived;

    public Insta360CameraClient(HttpClient? httpClient = null)
    {
        _httpClient = httpClient ?? new HttpClient { Timeout = TimeSpan.FromSeconds(25) };
    }

    public void Log(string message)
    {
        LogMessageReceived?.Invoke($"[{DateTime.Now:HH:mm:ss}] {message}");
    }

    /// <summary>
    /// Connects to camera via TCP port 6666, performs SYNC handshake and starts keepalive.
    /// Follows the protocol implementation from veelenga/insta360-go-ultra-transfer.
    /// </summary>
    public async Task<bool> ConnectAsync(string ip = DefaultCameraIp, int port = DefaultControlPort, CancellationToken ct = default)
    {
        CameraIp = ip;
        ControlPort = port;

        Disconnect();
        _sessionCts = new CancellationTokenSource();
        var sessionToken = _sessionCts.Token;

        Log($"Connecting to camera at {CameraIp}:{ControlPort}...");

        try
        {
            _tcpClient = new TcpClient();
            using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(4));
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);

            await _tcpClient.ConnectAsync(CameraIp, ControlPort, linkedCts.Token);
            _networkStream = _tcpClient.GetStream();

            // Start reader loop
            _ = Task.Run(() => ReadLoopAsync(sessionToken), sessionToken);

            // Send SYNC handshake (0x06, 0x00, 0x00 + "syNceNdinS")
            var handshakePacket = Insta360Protocol.PackPacket(Insta360Protocol.PktSync);
            await _networkStream.WriteAsync(handshakePacket, 0, handshakePacket.Length, ct);
            await _networkStream.FlushAsync(ct);
            Log("Sent SYNC handshake (060000syNceNdinS). Session opening...");

            // Start keepalive loop every 2 seconds
            StartKeepaliveLoop(sessionToken);

            IsConnected = true;
            ConnectionChanged?.Invoke(true);
            Log("Insta360 control session established and keepalive active!");
            return true;
        }
        catch (Exception ex)
        {
            Log($"Failed to connect to camera at {CameraIp}:{ControlPort}: {ex.Message}");
            Disconnect();
            return false;
        }
    }

    private void StartKeepaliveLoop(CancellationToken ct)
    {
        Task.Run(async () =>
        {
            var keepalivePacket = Insta360Protocol.PackPacket(Insta360Protocol.PktKeepalive);
            while (!ct.IsCancellationRequested && IsConnected && _networkStream != null)
            {
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(Insta360Protocol.KeepaliveIntervalSeconds), ct);
                    if (_networkStream != null && _networkStream.CanWrite)
                    {
                        await _networkStream.WriteAsync(keepalivePacket, 0, keepalivePacket.Length, ct);
                        await _networkStream.FlushAsync(ct);
                    }
                }
                catch
                {
                    break;
                }
            }
        }, ct);
    }

    private async Task ReadLoopAsync(CancellationToken ct)
    {
        byte[] buffer = new byte[64 * 1024];
        var memoryStream = new MemoryStream();

        try
        {
            while (!ct.IsCancellationRequested && _networkStream != null)
            {
                int bytesRead = await _networkStream.ReadAsync(buffer, 0, buffer.Length, ct);
                if (bytesRead == 0) break;

                memoryStream.Write(buffer, 0, bytesRead);
                byte[] raw = memoryStream.ToArray();

                int offset = 0;
                while (raw.Length - offset >= 4)
                {
                    uint pktLen = BinaryPrimitives.ReadUInt32LittleEndian(raw.AsSpan(offset, 4));
                    if (pktLen < 4 || pktLen > 32 * 1024 * 1024)
                    {
                        Log($"Invalid packet length: {pktLen}. Resetting stream.");
                        memoryStream.SetLength(0);
                        break;
                    }

                    if (raw.Length - offset < pktLen)
                    {
                        break;
                    }

                    byte[] payload = new byte[pktLen - 4];
                    Array.Copy(raw, offset + 4, payload, 0, payload.Length);
                    offset += (int)pktLen;

                    HandleIncomingPacket(payload);
                }

                if (offset > 0)
                {
                    byte[] remaining = new byte[raw.Length - offset];
                    Array.Copy(raw, offset, remaining, 0, remaining.Length);
                    memoryStream.SetLength(0);
                    memoryStream.Write(remaining, 0, remaining.Length);
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            Log($"Socket read notice: {ex.Message}");
        }
        finally
        {
            if (IsConnected)
            {
                Disconnect();
            }
        }
    }

    private void HandleIncomingPacket(byte[] payload)
    {
        if (payload.Length < 3) return;

        if (MatchesPattern(payload, Insta360Protocol.PktSync))
        {
            Log("Received SYNC echo from camera - Handshake confirmed!");
            return;
        }

        if (MatchesPattern(payload, Insta360Protocol.PktKeepalive))
        {
            return;
        }

        // Command response
        if (payload[0] == 0x04 && payload[1] == 0x00 && payload[2] == 0x00)
        {
            if (payload.Length >= 12)
            {
                ushort code = BinaryPrimitives.ReadUInt16LittleEndian(payload.AsSpan(3, 2));
                uint seq = payload[6] | ((uint)payload[7] << 8) | ((uint)payload[8] << 16);
                byte[] body = new byte[payload.Length - 12];
                Array.Copy(payload, 12, body, 0, body.Length);

                TaskCompletionSource<(ushort, byte[])>? tcs = null;
                lock (_lock)
                {
                    _pendingCommands.Remove(seq, out tcs);
                }
                tcs?.TrySetResult((code, body));
            }
        }
    }

    private static bool MatchesPattern(byte[] buffer, byte[] pattern)
    {
        if (buffer.Length < pattern.Length) return false;
        for (int i = 0; i < pattern.Length; i++)
        {
            if (buffer[i] != pattern[i]) return false;
        }
        return true;
    }

    public async Task<(ushort code, byte[] body)> SendCommandAsync(ushort cmdId, byte[] proto, TimeSpan? timeout = null)
    {
        if (!IsConnected || _networkStream == null)
            throw new InvalidOperationException("Camera not connected.");

        uint seq;
        var tcs = new TaskCompletionSource<(ushort, byte[])>(TaskCreationOptions.RunContinuationsAsynchronously);

        lock (_lock)
        {
            _sequenceCounter++;
            seq = _sequenceCounter;
            _pendingCommands[seq] = tcs;
        }

        byte[] cmdPayload = Insta360Protocol.MakeCommand(cmdId, seq, proto);
        byte[] packet = Insta360Protocol.PackPacket(cmdPayload);

        await _networkStream.WriteAsync(packet, 0, packet.Length);
        await _networkStream.FlushAsync();

        using var cts = new CancellationTokenSource(timeout ?? TimeSpan.FromSeconds(6));
        using (cts.Token.Register(() =>
        {
            lock (_lock) { _pendingCommands.Remove(seq); }
            tcs.TrySetCanceled();
        }))
        {
            return await tcs.Task;
        }
    }

    /// <summary>
    /// Fetches all media files with exact file sizes and durations decoded from the camera catalog.
    /// Excludes LRV duplicate companion files and loads real photo thumbnails.
    /// </summary>
    public async Task<List<MediaFileItem>> FetchFileListAsync(CancellationToken ct = default)
    {
        var results = new List<MediaFileItem>();

        if (!IsConnected)
        {
            Log("Cannot fetch files: Camera not connected.");
            return results;
        }

        // Query camera HTTP catalog
        try
        {
            string listUrl = $"http://{CameraIp}/DCIM/fileinfo_list.list";
            var response = await _httpClient.GetAsync(listUrl, ct);

            if (response.IsSuccessStatusCode)
            {
                var contentBytes = await response.Content.ReadAsByteArrayAsync(ct);
                results = ParseFileInfoProtobuf(contentBytes);
                Log($"Discovered {results.Count} media files with sizes and durations from camera!");

                // Load photo thumbnails in background
                _ = Task.Run(() => LoadPhotoThumbnailsAsync(results, ct), ct);
            }
        }
        catch (Exception ex)
        {
            Log($"Error reading camera file catalog: {ex.Message}");
        }

        return results;
    }

    /// <summary>
    /// Decodes the binary protobuf records in fileinfo_list.list:
    /// - Field 1: Media URI
    /// - Field 2: Metadata (Subfield 9 = File size, Subfield 20 = FPS, Subfield 25 = Duration)
    /// </summary>
    private List<MediaFileItem> ParseFileInfoProtobuf(byte[] data)
    {
        var list = new List<MediaFileItem>();
        var rootFields = Insta360Protocol.DecodeFields(data);

        foreach (var (rootField, wire, itemBytes, _) in rootFields)
        {
            if (wire != 2 || itemBytes.Length == 0) continue;

            string uri = string.Empty;
            long fileSizeBytes = 0;
            double durationSeconds = 0;
            int fps = 0;

            var itemFields = Insta360Protocol.DecodeFields(itemBytes);
            foreach (var (f, w, valBytes, valInt) in itemFields)
            {
                if (f == 1 && w == 2)
                {
                    uri = System.Text.Encoding.UTF8.GetString(valBytes).Trim();
                }
                else if (f == 2 && w == 2)
                {
                    var metaFields = Insta360Protocol.DecodeFields(valBytes);
                    foreach (var (mf, mw, mBytes, mInt) in metaFields)
                    {
                        if (mf == 9 && mw == 0) // Exact file size in bytes
                        {
                            fileSizeBytes = (long)mInt;
                        }
                        else if (mf == 10 && mw == 0) // Exact video duration in seconds (varint)
                        {
                            durationSeconds = (double)mInt;
                        }
                        else if (mf == 20 && mw == 0) // FPS
                        {
                            fps = (int)mInt;
                        }
                    }
                }
            }

            if (string.IsNullOrEmpty(uri)) continue;

            string fileName = Path.GetFileName(uri);
            // Exclude duplicate LRV proxy files from the main gallery
            if (fileName.StartsWith("LRV_", StringComparison.OrdinalIgnoreCase))
                continue;

            var item = CreateMediaItem(uri, fileSizeBytes, durationSeconds, fps);
            if (item != null)
            {
                list.Add(item);
            }
        }

        return list.OrderByDescending(f => f.DateRecorded).ToList();
    }

    private MediaFileItem CreateMediaItem(string uri, long fileSizeBytes, double durationSeconds, int fps)
    {
        string fileName = Path.GetFileName(uri);

        var date = DateTime.Now;
        var dateMatch = Regex.Match(fileName, @"(\d{4})(\d{2})(\d{2})_(\d{2})(\d{2})(\d{2})");
        if (dateMatch.Success &&
            int.TryParse(dateMatch.Groups[1].Value, out int year) &&
            int.TryParse(dateMatch.Groups[2].Value, out int month) &&
            int.TryParse(dateMatch.Groups[3].Value, out int day) &&
            int.TryParse(dateMatch.Groups[4].Value, out int hour) &&
            int.TryParse(dateMatch.Groups[5].Value, out int minute) &&
            int.TryParse(dateMatch.Groups[6].Value, out int second))
        {
            try { date = new DateTime(year, month, day, hour, minute, second); } catch { }
        }

        var is360 = fileName.EndsWith(".insv", StringComparison.OrdinalIgnoreCase) ||
                    fileName.EndsWith(".insp", StringComparison.OrdinalIgnoreCase) ||
                    fileName.Contains("360", StringComparison.OrdinalIgnoreCase);
        var isPhoto = fileName.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase) ||
                      fileName.EndsWith(".insp", StringComparison.OrdinalIgnoreCase);

        MediaKind kind = isPhoto
            ? (is360 ? MediaKind.Photo360 : MediaKind.StandardPhoto)
            : (is360 ? MediaKind.Video360 : MediaKind.StandardVideo);

        string fullUrl = $"http://{CameraIp}{(uri.StartsWith("/") ? uri : "/" + uri)}";

        // Find companion LRV url if it exists
        string lrvUrl = string.Empty;
        if (!isPhoto)
        {
            string lrvName = fileName.Replace("VID_", "LRV_").Replace("_00_", "_01_");
            lrvUrl = $"http://{CameraIp}/DCIM/Camera01/{lrvName}";
        }

        return new MediaFileItem
        {
            FileName = fileName,
            RemoteUrl = fullUrl,
            LrvUrl = lrvUrl,
            ThumbnailUrl = isPhoto ? fullUrl : string.Empty,
            FileSizeBytes = fileSizeBytes,
            DateRecorded = date,
            Duration = TimeSpan.FromSeconds(durationSeconds),
            Fps = fps,
            Kind = kind,
            HasGps = false,  // Do NOT assume GPS exists until verified
            HasGyro = is360  // Only 360 files have FlowState Gyro by default
        };
    }

    private async Task LoadPhotoThumbnailsAsync(List<MediaFileItem> items, CancellationToken ct)
    {
        foreach (var item in items)
        {
            if (ct.IsCancellationRequested) break;
            if (item.IsPhoto && !string.IsNullOrEmpty(item.ThumbnailUrl))
            {
                try
                {
                    var bytes = await _httpClient.GetByteArrayAsync(item.ThumbnailUrl, ct);
                    await System.Windows.Application.Current.Dispatcher.InvokeAsync(() =>
                    {
                        var bmp = new BitmapImage();
                        using var ms = new MemoryStream(bytes);
                        bmp.BeginInit();
                        bmp.CacheOption = BitmapCacheOption.OnLoad;
                        bmp.DecodePixelWidth = 360;
                        bmp.StreamSource = ms;
                        bmp.EndInit();
                        bmp.Freeze();
                        item.ThumbnailBitmap = bmp;
                    });
                }
                catch { }
            }
        }
    }

    /// <summary>
    /// Downloads a selected file directly from the camera HTTP server.
    /// </summary>
    public async Task DownloadFileAsync(
        MediaFileItem item,
        string destinationFolderPath,
        IProgress<DownloadProgressUpdate> progress,
        CancellationToken ct = default)
    {
        if (!IsConnected)
            throw new InvalidOperationException("Camera not connected. Connect to camera Wi-Fi first.");

        item.IsDownloading = true;
        item.DownloadProgressPercent = 0;

        string localFilePath = Path.Combine(destinationFolderPath, item.FileName);
        Directory.CreateDirectory(destinationFolderPath);

        Log($"Downloading {item.FileName} from {item.RemoteUrl}...");
        using var response = await _httpClient.GetAsync(item.RemoteUrl, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();

        long total = response.Content.Headers.ContentLength ?? item.FileSizeBytes;
        if (total > 0) item.FileSizeBytes = total;

        long received = 0;
        byte[] buffer = new byte[256 * 1024];

        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        await using var contentStream = await response.Content.ReadAsStreamAsync(ct);
        await using var fileStream = new FileStream(localFilePath, FileMode.Create, FileAccess.Write, FileShare.None, buffer.Length, true);

        int read;
        while ((read = await contentStream.ReadAsync(buffer, 0, buffer.Length, ct)) > 0)
        {
            await fileStream.WriteAsync(buffer, 0, read, ct);
            received += read;

            double percent = total > 0 ? (double)received / total * 100.0 : 0;
            double speed = received / Math.Max(0.1, stopwatch.Elapsed.TotalSeconds);
            double remainingSeconds = speed > 0 ? (total - received) / speed : 0;

            item.DownloadProgressPercent = percent;
            item.DownloadSpeedText = $"{speed / (1024 * 1024):F1} MB/s";

            progress.Report(new DownloadProgressUpdate(
                received, total, percent, speed, TimeSpan.FromSeconds(remainingSeconds)));
        }

        item.LocalPath = localFilePath;
        item.IsDownloaded = true;
        item.IsDownloading = false;
        Log($"Successfully downloaded {item.FileName} ({received / (1024 * 1024.0):F1} MB) to {localFilePath}!");
    }

    public async Task<string> StartLivePreviewStreamAsync(CancellationToken ct = default)
    {
        Log("Requesting wireless live preview stream from camera...");
        if (_networkStream != null && _networkStream.CanWrite)
        {
            byte[] cmd = new byte[] { 0x00, 0x00, 0x00, 0x08, 0x02, 0x01, 0x00, 0x01 };
            await _networkStream.WriteAsync(cmd, 0, cmd.Length, ct);
            await _networkStream.FlushAsync(ct);
        }

        return $"rtsp://{CameraIp}:554/live/preview";
    }

    public async Task StopLivePreviewStreamAsync(CancellationToken ct = default)
    {
        Log("Stopping live preview stream...");
        if (_networkStream != null && _networkStream.CanWrite)
        {
            byte[] cmd = new byte[] { 0x00, 0x00, 0x00, 0x08, 0x02, 0x02, 0x00, 0x00 };
            await _networkStream.WriteAsync(cmd, 0, cmd.Length, ct);
            await _networkStream.FlushAsync(ct);
        }
    }

    public void Disconnect()
    {
        _sessionCts?.Cancel();
        _sessionCts?.Dispose();
        _sessionCts = null;

        _networkStream?.Dispose();
        _tcpClient?.Dispose();
        _networkStream = null;
        _tcpClient = null;

        lock (_lock)
        {
            foreach (var tcs in _pendingCommands.Values)
            {
                tcs.TrySetCanceled();
            }
            _pendingCommands.Clear();
        }

        if (IsConnected)
        {
            IsConnected = false;
            ConnectionChanged?.Invoke(false);
            Log("Camera disconnected.");
        }
    }

    public void Dispose()
    {
        if (_isDisposed) return;
        _isDisposed = true;
        Disconnect();
        _httpClient.Dispose();
    }
}
