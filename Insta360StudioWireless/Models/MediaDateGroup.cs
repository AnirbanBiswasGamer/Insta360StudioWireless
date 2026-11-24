using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Insta360StudioWireless.Models;

public partial class MediaDateGroup : ObservableObject
{
    [ObservableProperty]
    private string _dateHeader = string.Empty;

    [ObservableProperty]
    private DateTime _date;

    [ObservableProperty]
    private ObservableCollection<MediaFileItem> _items = new();

    public int ItemCount => Items.Count;
}
