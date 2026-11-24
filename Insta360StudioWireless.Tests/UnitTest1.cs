using System.IO;
using System.Xml.Linq;
using Insta360StudioWireless.Models;
using Insta360StudioWireless.Services;
using Insta360StudioWireless.ViewModels;
using Xunit;

namespace Insta360StudioWireless.Tests;

public class TelemetryAndCameraTests
{
    private static TelemetryDataSet CreateSampleTelemetry()
    {
        var dataSet = new TelemetryDataSet
        {
            FileName = "VID_20260601_165212_00_052.mp4",
            CameraModel = "Insta360 ONE RS"
        };

        dataSet.GpsRecords.Add(new GpsRecord(0, 37.7749, -122.4194, 50.0, 25.0, 12));
        dataSet.GpsRecords.Add(new GpsRecord(1000, 37.7750, -122.4193, 51.0, 26.5, 13));
        dataSet.GpsRecords.Add(new GpsRecord(2000, 37.7752, -122.4191, 52.0, 28.0, 14));

        dataSet.GyroRecords.Add(new GyroRecord(0, 1.2, -0.5, 3.4, 0.05, -0.02, 1.0));
        dataSet.GyroRecords.Add(new GyroRecord(50, 1.4, -0.3, 3.1, 0.06, -0.01, 0.99));

        return dataSet;
    }

    [Fact]
    public void Protocol_PacksPacketWithCorrectLittleEndianLength()
    {
        byte[] payload = new byte[] { 0x01, 0x02, 0x03 };
        byte[] packed = Insta360Protocol.PackPacket(payload);

        Assert.Equal(7, packed.Length);
        Assert.Equal(7, packed[0]); // 3 + 4 = 7
        Assert.Equal(0, packed[1]);
        Assert.Equal(0x01, packed[4]);
    }

    [Fact]
    public void Protocol_EncodesAndDecodesVarints()
    {
        ulong val = 300;
        byte[] encoded = Insta360Protocol.EncodeVarint(val);
        var fields = Insta360Protocol.DecodeFields(new byte[] { 0x08, encoded[0], encoded[1] });

        Assert.Single(fields);
        Assert.Equal(1, fields[0].field);
        Assert.Equal(0, fields[0].wire);
        Assert.Equal(val, fields[0].varintValue);
    }

    [Fact]
    public void Protocol_DecodesWire1Double()
    {
        // Field 25, wire 1 = (25 << 3) | 1 = 201 = 0xC9, 0x01
        double expectedDuration = 14.5;
        byte[] doubleBytes = BitConverter.GetBytes(expectedDuration);
        byte[] buffer = new byte[] { 0xC9, 0x01 }
            .Concat(doubleBytes)
            .ToArray();

        var fields = Insta360Protocol.DecodeFields(buffer);
        Assert.Single(fields);
        Assert.Equal(25, fields[0].field);
        Assert.Equal(1, fields[0].wire);
        double decoded = BitConverter.ToDouble(fields[0].data);
        Assert.Equal(expectedDuration, decoded);
    }

    [Fact]
    public void TelemetryDataSet_CalculatesDistancesAndStatsCorrectly()
    {
        var dataSet = CreateSampleTelemetry();

        Assert.True(dataSet.HasGps);
        Assert.True(dataSet.HasGyro);
        Assert.True(dataSet.TotalDistanceKm > 0);
        Assert.Equal(28.0, dataSet.MaxSpeedKmh);
    }

    [Fact]
    public async Task TelemetryParser_ExportsValidGpxXml()
    {
        var dataSet = CreateSampleTelemetry();

        string tempGpx = Path.GetTempFileName() + ".gpx";
        try
        {
            await InsvTelemetryParser.ExportToGpxAsync(dataSet, tempGpx);

            Assert.True(File.Exists(tempGpx));
            string content = await File.ReadAllTextAsync(tempGpx);
            Assert.Contains("<gpx version=\"1.1\"", content);
            Assert.Contains("<trkpt", content);

            var doc = XDocument.Parse(content);
            Assert.NotNull(doc.Root);
        }
        finally
        {
            if (File.Exists(tempGpx)) File.Delete(tempGpx);
        }
    }

    [Fact]
    public void ViewModel_SyncsPlaybackTimeWithGyroAndGps()
    {
        var vm = new MainViewModel();
        var dataSet = CreateSampleTelemetry();
        vm.ActiveTelemetry = dataSet;

        vm.UpdatePlaybackTime(1.0);
        Assert.Equal(26.5, vm.CurrentSpeedKmh);
        Assert.Equal(51.0, vm.CurrentAltitudeMeters);
        Assert.NotEqual("No GPS lock", vm.CurrentGpsCoordinateText);
    }

    [Fact]
    public void ViewModel_GroupsMediaByDate_GooglePhotosStyle()
    {
        var vm = new MainViewModel();
        vm.MediaFiles.Add(new MediaFileItem
        {
            FileName = "VID_20260830_100000.mp4",
            DateRecorded = new DateTime(2026, 8, 30, 10, 0, 0),
            Kind = MediaKind.StandardVideo
        });
        vm.MediaFiles.Add(new MediaFileItem
        {
            FileName = "VID_20260830_120000.mp4",
            DateRecorded = new DateTime(2026, 8, 30, 12, 0, 0),
            Kind = MediaKind.StandardVideo
        });
        vm.MediaFiles.Add(new MediaFileItem
        {
            FileName = "IMG_20260829_150000.jpg",
            DateRecorded = new DateTime(2026, 8, 29, 15, 0, 0),
            Kind = MediaKind.StandardPhoto
        });

        vm.RefreshFilteredGroups();

        Assert.Equal(2, vm.DateGroups.Count);
        Assert.Equal(2, vm.DateGroups[0].Items.Count);
        Assert.Equal(1, vm.DateGroups[1].Items.Count);
        Assert.Contains("Aug 30", vm.DateGroups[0].DateHeader);
        Assert.Contains("Aug 29", vm.DateGroups[1].DateHeader);

        // Filter to Photos only
        vm.SelectedFilter = "Photos";
        Assert.Single(vm.DateGroups);
        Assert.Single(vm.DateGroups[0].Items);
        Assert.Equal("IMG_20260829_150000.jpg", vm.DateGroups[0].Items[0].FileName);
    }

    [Fact]
    public void MediaItem_FormatsDurationCorrectly()
    {
        var item1 = new MediaFileItem { Duration = TimeSpan.FromSeconds(6) };
        Assert.Equal("0:06", item1.FormattedDuration);

        var item2 = new MediaFileItem { Duration = TimeSpan.FromSeconds(190) };
        Assert.Equal("3:10", item2.FormattedDuration);

        var item3 = new MediaFileItem { Duration = TimeSpan.FromSeconds(3665) };
        Assert.Equal("1:01:05", item3.FormattedDuration);

        var photo = new MediaFileItem { Duration = TimeSpan.Zero };
        Assert.Equal("--:--", photo.FormattedDuration);
    }

    [Fact]
    public void MainViewModel_DisconnectCommand_ResetsConnectionAndClearsGallery()
    {
        var vm = new MainViewModel();
        vm.IsConnected = true;
        vm.MediaFiles.Add(new MediaFileItem { FileName = "test.mp4" });
        vm.DateGroups.Add(new MediaDateGroup { DateHeader = "Today" });

        vm.DisconnectCommand.Execute(null);

        Assert.False(vm.IsConnected);
        Assert.Empty(vm.MediaFiles);
        Assert.Empty(vm.DateGroups);
        Assert.Equal("#D32F2F", vm.ConnectionBadgeColor);
    }

    [Fact]
    public void Test_WpfUiSymbols()
    {
        var asm = typeof(Wpf.Ui.Controls.FluentWindow).Assembly;
        var symbolType = asm.GetType("Wpf.Ui.Controls.SymbolRegular")!;
        
        string[] testSymbols = new[] 
        {
            "Camera24",
            "ArrowSync24",
            "ArrowSync20",
            "ArrowDownload24",
            "ArrowDownload16",
            "Folder24",
            "Folder16",
            "Play24",
            "Play16",
            "Pause24",
            "Pause16",
            "ArrowLeft24",
            "ChevronLeft24",
            "ChevronRight24",
            "Dismiss24",
            "Dismiss16",
            "Checkmark24",
            "Checkmark16",
            "Video24",
            "LinkDismiss24",
            "PlugDisconnected24",
            "PlugDisconnected20",
            "Globe24",
            "Share24",
            "ArrowExportRtl24",
            "ArrowExportLtr24",
            "WifiSettings20"
        };

        foreach (var s in testSymbols)
        {
            Assert.True(Enum.IsDefined(symbolType, s), $"Symbol {s} is not defined in SymbolRegular");
        }
    }

    [Fact]
    public void Test_TitleBar_IconProperty()
    {
        var asm = typeof(Wpf.Ui.Controls.FluentWindow).Assembly;
        var tbType = asm.GetType("Wpf.Ui.Controls.TitleBar");
        var iconProp = tbType?.GetProperty("Icon");
        Assert.NotNull(iconProp);
    }

    [Fact]
    public void MainViewModel_BrowseDownloadFolderCommand_Initialized()
    {
        var vm = new MainViewModel();
        Assert.NotNull(vm.BrowseDownloadFolderCommand);
        Assert.True(vm.BrowseDownloadFolderCommand.CanExecute(null));
    }
}
