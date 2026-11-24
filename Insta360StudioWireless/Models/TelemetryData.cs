namespace Insta360StudioWireless.Models;

public record GpsRecord(
    double TimestampMs,
    double Latitude,
    double Longitude,
    double AltitudeMeters,
    double SpeedKmh,
    int Satellites = 0
);

public record GyroRecord(
    double TimestampMs,
    double GyroX,
    double GyroY,
    double GyroZ,
    double AccelX,
    double AccelY,
    double AccelZ
);

public class TelemetryDataSet
{
    public string FileName { get; set; } = string.Empty;
    public string CameraModel { get; set; } = "Insta360 ONE RS";
    public string SerialNumber { get; set; } = string.Empty;
    public string FirmwareVersion { get; set; } = string.Empty;
    public TimeSpan Duration { get; set; }
    public List<GpsRecord> GpsRecords { get; set; } = new();
    public List<GyroRecord> GyroRecords { get; set; } = new();
    public byte[]? ThumbnailJpg { get; set; }

    public bool HasGps => GpsRecords.Count > 0;
    public bool HasGyro => GyroRecords.Count > 0;
    public double MaxSpeedKmh => GpsRecords.Count > 0 ? GpsRecords.Max(g => g.SpeedKmh) : 0;
    public double AverageSpeedKmh => GpsRecords.Count > 0 ? GpsRecords.Average(g => g.SpeedKmh) : 0;
    public double TotalDistanceKm
    {
        get
        {
            if (GpsRecords.Count < 2) return 0;
            double total = 0;
            for (int i = 1; i < GpsRecords.Count; i++)
            {
                total += CalculateHaversineDistance(
                    GpsRecords[i - 1].Latitude, GpsRecords[i - 1].Longitude,
                    GpsRecords[i].Latitude, GpsRecords[i].Longitude);
            }
            return total;
        }
    }

    private static double CalculateHaversineDistance(double lat1, double lon1, double lat2, double lon2)
    {
        const double r = 6371.0; // Earth radius in km
        double dLat = ToRadians(lat2 - lat1);
        double dLon = ToRadians(lon2 - lon1);
        double a = Math.Sin(dLat / 2) * Math.Sin(dLat / 2) +
                   Math.Cos(ToRadians(lat1)) * Math.Cos(ToRadians(lat2)) *
                   Math.Sin(dLon / 2) * Math.Sin(dLon / 2);
        double c = 2 * Math.Atan2(Math.Sqrt(a), Math.Sqrt(1 - a));
        return r * c;
    }

    private static double ToRadians(double degrees) => degrees * Math.PI / 180.0;
}
