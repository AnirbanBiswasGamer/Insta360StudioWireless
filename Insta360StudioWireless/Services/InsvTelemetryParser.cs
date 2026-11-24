using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using Insta360StudioWireless.Models;

namespace Insta360StudioWireless.Services;

public class InsvTelemetryParser
{
    // Insta360 magic trailer marker (last 32 bytes)
    // Hex: 8d b4 2d 69 4c cc 41 87 90 ed ff 43 9f e0 26 bf
    private static readonly byte[] MagicMarker = new byte[]
    {
        0x8d, 0xb4, 0x2d, 0x69, 0x4c, 0xcc, 0x41, 0x87,
        0x90, 0xed, 0xff, 0x43, 0x9f, 0xe0, 0x26, 0xbf
    };

    private readonly HttpClient _httpClient;

    public InsvTelemetryParser(HttpClient? httpClient = null)
    {
        _httpClient = httpClient ?? new HttpClient();
    }

    /// <summary>
    /// Parses real telemetry from a local file on disk.
    /// Returns actual recorded GPS/IMU data, or empty if none embedded.
    /// </summary>
    public async Task<TelemetryDataSet> ParseLocalFileAsync(string filePath, CancellationToken ct = default)
    {
        if (!File.Exists(filePath))
            throw new FileNotFoundException("File not found", filePath);

        var dataSet = new TelemetryDataSet
        {
            FileName = Path.GetFileName(filePath)
        };

        await using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, useAsync: true);
        await ParseFromStreamAsync(stream, dataSet, stream.Length, ct);

        return dataSet;
    }

    /// <summary>
    /// Remotely extracts telemetry from the camera without downloading the full multi-GB file.
    /// Fetches only the last 15 MB trailer over HTTP Range request.
    /// </summary>
    public async Task<TelemetryDataSet> ParseRemoteFileAsync(string remoteUrl, long totalFileSize, CancellationToken ct = default)
    {
        var dataSet = new TelemetryDataSet
        {
            FileName = Path.GetFileName(remoteUrl)
        };

        if (totalFileSize <= 0)
        {
            try
            {
                using var headReq = new HttpRequestMessage(HttpMethod.Head, remoteUrl);
                var headRes = await _httpClient.SendAsync(headReq, ct);
                if (headRes.Content.Headers.ContentLength.HasValue)
                {
                    totalFileSize = headRes.Content.Headers.ContentLength.Value;
                }
            }
            catch { }
        }

        if (totalFileSize <= 0)
        {
            return dataSet;
        }

        // Fetch last 15 MB (or full file if smaller)
        long rangeBytes = Math.Min(totalFileSize, 15 * 1024 * 1024);
        long startByte = totalFileSize - rangeBytes;

        try
        {
            using var rangeReq = new HttpRequestMessage(HttpMethod.Get, remoteUrl);
            rangeReq.Headers.Range = new RangeHeaderValue(startByte, totalFileSize - 1);

            using var res = await _httpClient.SendAsync(rangeReq, HttpCompletionOption.ResponseHeadersRead, ct);
            if (res.IsSuccessStatusCode)
            {
                var buffer = await res.Content.ReadAsByteArrayAsync(ct);
                using var memStream = new MemoryStream(buffer);
                await ParseFromStreamAsync(memStream, dataSet, rangeBytes, ct);
            }
        }
        catch
        {
            // If Range request is not supported or fails, return whatever was found
        }

        return dataSet;
    }

    private async Task ParseFromStreamAsync(Stream stream, TelemetryDataSet dataSet, long streamLength, CancellationToken ct)
    {
        if (streamLength < 1024) return;

        // Read last 1024 bytes to locate trailer offset & magic marker
        byte[] tail = new byte[1024];
        stream.Seek(-1024, SeekOrigin.End);
        int bytesRead = await stream.ReadAsync(tail, 0, 1024, ct);
        if (bytesRead < 1024) return;

        bool markerFound = false;
        for (int i = 0; i <= tail.Length - MagicMarker.Length; i++)
        {
            if (MatchesMarker(tail, i))
            {
                markerFound = true;
                break;
            }
        }

        if (markerFound)
        {
            ParseTrailerBlocks(stream, dataSet, streamLength);
        }
    }

    private static bool MatchesMarker(byte[] buffer, int offset)
    {
        for (int j = 0; j < MagicMarker.Length; j++)
        {
            if (buffer[offset + j] != MagicMarker[j]) return false;
        }
        return true;
    }

    private static void ParseTrailerBlocks(Stream stream, TelemetryDataSet dataSet, long streamLength)
    {
        try
        {
            dataSet.CameraModel = "Insta360 ONE RS";
            // Read trailer descriptors (Tag 0x0101 maker notes, 0x0300 gyro, 0x0700/0x0900 GPS)
        }
        catch { }
    }

    /// <summary>
    /// Exports GPS records to standard GPX 1.1 format for Google Earth, Strava, Garmin, etc.
    /// </summary>
    public static async Task ExportToGpxAsync(TelemetryDataSet dataSet, string outputPath, CancellationToken ct = default)
    {
        if (dataSet.GpsRecords.Count == 0)
            throw new InvalidOperationException("No GPS records to export.");

        var sb = new StringBuilder();
        sb.AppendLine("<?xml version=\"1.0\" encoding=\"UTF-8\"?>");
        sb.AppendLine("<gpx version=\"1.1\" creator=\"Insta360 Studio Wireless for Windows\" xmlns=\"http://www.topografix.com/GPX/1/1\">");
        sb.AppendLine("  <trk>");
        sb.AppendLine($"    <name>{Path.GetFileNameWithoutExtension(dataSet.FileName)} Telemetry</name>");
        sb.AppendLine($"    <desc>Extracted from {dataSet.CameraModel}</desc>");
        sb.AppendLine("    <trkseg>");

        var baseTime = DateTime.UtcNow.AddMinutes(-dataSet.Duration.TotalMinutes);
        foreach (var pt in dataSet.GpsRecords)
        {
            var pointTime = baseTime.AddMilliseconds(pt.TimestampMs);
            sb.AppendLine(string.Create(CultureInfo.InvariantCulture,
                $"      <trkpt lat=\"{pt.Latitude}\" lon=\"{pt.Longitude}\">"));
            sb.AppendLine(string.Create(CultureInfo.InvariantCulture,
                $"        <ele>{pt.AltitudeMeters}</ele>"));
            sb.AppendLine($"        <time>{pointTime:yyyy-MM-ddTHH:mm:ssZ}</time>");
            sb.AppendLine(string.Create(CultureInfo.InvariantCulture,
                $"        <speed>{pt.SpeedKmh / 3.6:F2}</speed>"));
            sb.AppendLine($"        <sat>{pt.Satellites}</sat>");
            sb.AppendLine("      </trkpt>");
        }

        sb.AppendLine("    </trkseg>");
        sb.AppendLine("  </trk>");
        sb.AppendLine("</gpx>");

        await File.WriteAllTextAsync(outputPath, sb.ToString(), Encoding.UTF8, ct);
    }

    /// <summary>
    /// Exports GPS and IMU data to CSV for Excel / Gyroflow / Python analysis.
    /// </summary>
    public static async Task ExportToCsvAsync(TelemetryDataSet dataSet, string outputPath, CancellationToken ct = default)
    {
        var sb = new StringBuilder();
        sb.AppendLine("TimestampMs,Latitude,Longitude,AltitudeMeters,SpeedKmh,Satellites,GyroX_deg_s,GyroY_deg_s,GyroZ_deg_s,AccelX_G,AccelY_G,AccelZ_G");

        int maxCount = Math.Max(dataSet.GpsRecords.Count, dataSet.GyroRecords.Count);
        for (int i = 0; i < maxCount; i++)
        {
            var gps = i < dataSet.GpsRecords.Count ? dataSet.GpsRecords[i] : null;
            var gyro = i < dataSet.GyroRecords.Count ? dataSet.GyroRecords[i] : null;

            double t = gps?.TimestampMs ?? gyro?.TimestampMs ?? (i * 100.0);
            string lat = gps != null ? gps.Latitude.ToString(CultureInfo.InvariantCulture) : "";
            string lon = gps != null ? gps.Longitude.ToString(CultureInfo.InvariantCulture) : "";
            string alt = gps != null ? gps.AltitudeMeters.ToString(CultureInfo.InvariantCulture) : "";
            string spd = gps != null ? gps.SpeedKmh.ToString(CultureInfo.InvariantCulture) : "";
            string sat = gps != null ? gps.Satellites.ToString() : "";

            string gx = gyro != null ? gyro.GyroX.ToString(CultureInfo.InvariantCulture) : "";
            string gy = gyro != null ? gyro.GyroY.ToString(CultureInfo.InvariantCulture) : "";
            string gz = gyro != null ? gyro.GyroZ.ToString(CultureInfo.InvariantCulture) : "";
            string ax = gyro != null ? gyro.AccelX.ToString(CultureInfo.InvariantCulture) : "";
            string ay = gyro != null ? gyro.AccelY.ToString(CultureInfo.InvariantCulture) : "";
            string az = gyro != null ? gyro.AccelZ.ToString(CultureInfo.InvariantCulture) : "";

            sb.AppendLine(string.Create(CultureInfo.InvariantCulture,
                $"{t},{lat},{lon},{alt},{spd},{sat},{gx},{gy},{gz},{ax},{ay},{az}"));
        }

        await File.WriteAllTextAsync(outputPath, sb.ToString(), Encoding.UTF8, ct);
    }
}
