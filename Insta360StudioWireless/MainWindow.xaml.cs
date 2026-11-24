using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using Insta360StudioWireless.ViewModels;
using Wpf.Ui.Appearance;
using Wpf.Ui.Controls;

namespace Insta360StudioWireless;

public partial class MainWindow : FluentWindow
{
    private readonly MainViewModel _viewModel;
    private readonly DispatcherTimer _playbackTimer;
    private bool _isUserDraggingSlider;

    public MainWindow()
    {
        InitializeComponent();
        try
        {
            ApplicationThemeManager.Apply(ApplicationTheme.Dark, WindowBackdropType.Mica);
        }
        catch { }

        _viewModel = new MainViewModel();
        DataContext = _viewModel;

        _playbackTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(150)
        };
        _playbackTimer.Tick += PlaybackTimer_Tick;

        _viewModel.PlayMediaRequested += url =>
        {
            Dispatcher.Invoke(() =>
            {
                try
                {
                    PopupPlayer.Stop();
                    PopupPlayer.Source = new Uri(url);
                    PopupPlayer.Play();
                    _playbackTimer.Start();
                }
                catch { }
            });
        };

        _viewModel.StopMediaRequested += () =>
        {
            Dispatcher.Invoke(() =>
            {
                _playbackTimer.Stop();
                PopupPlayer.Stop();
                PopupPlayer.Close();
                PopupPlayer.Source = null;
            });
        };

        _viewModel.TogglePlayPauseRequested += () =>
        {
            Dispatcher.Invoke(() =>
            {
                if (_viewModel.IsPlaying)
                {
                    PopupPlayer.Play();
                    _playbackTimer.Start();
                }
                else
                {
                    PopupPlayer.Pause();
                    _playbackTimer.Stop();
                }
            });
        };

        KeyDown += (s, e) =>
        {
            if (_viewModel.IsPlayerPopupOpen)
            {
                if (e.Key == Key.Escape)
                {
                    _viewModel.CloseVideoPopup();
                    e.Handled = true;
                }
                else if (e.Key == Key.Left)
                {
                    _viewModel.PreviousMedia();
                    e.Handled = true;
                }
                else if (e.Key == Key.Right)
                {
                    _viewModel.NextMedia();
                    e.Handled = true;
                }
                else if (e.Key == Key.Space)
                {
                    _viewModel.TogglePlayPause();
                    e.Handled = true;
                }
            }
        };
    }

    private void OnBackButtonClicked(object sender, RoutedEventArgs e)
    {
        _viewModel.CloseVideoPopup();
        e.Handled = true;
    }

    private async void OnRefreshButtonClicked(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (_viewModel.RefreshGalleryCommand.CanExecute(null))
        {
            await _viewModel.RefreshGalleryCommand.ExecuteAsync(null);
        }
    }

    private void OnDisconnectButtonClicked(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (_viewModel.DisconnectCommand.CanExecute(null))
        {
            _viewModel.DisconnectCommand.Execute(null);
        }
    }

    private void OnBrowseFolderClicked(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (_viewModel.BrowseDownloadFolderCommand.CanExecute(null))
        {
            _viewModel.BrowseDownloadFolderCommand.Execute(null);
        }
    }

    private async void OnConnectButtonClicked(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (_viewModel.ConnectCommand.CanExecute(null))
        {
            await _viewModel.ConnectCommand.ExecuteAsync(null);
        }
    }

    private void PlaybackTimer_Tick(object? sender, EventArgs e)
    {
        if (!_isUserDraggingSlider && PopupPlayer.NaturalDuration.HasTimeSpan)
        {
            double pos = PopupPlayer.Position.TotalSeconds;
            _viewModel.PlayerTotalSeconds = PopupPlayer.NaturalDuration.TimeSpan.TotalSeconds;
            _viewModel.UpdatePlaybackTime(pos);
        }
    }

    private void OnPlayerMediaOpened(object sender, RoutedEventArgs e)
    {
        if (PopupPlayer.NaturalDuration.HasTimeSpan)
        {
            var span = PopupPlayer.NaturalDuration.TimeSpan;
            _viewModel.PlayerTotalSeconds = span.TotalSeconds;
            if (_viewModel.ActivePlayingMedia != null && _viewModel.ActivePlayingMedia.Duration <= TimeSpan.Zero)
            {
                _viewModel.ActivePlayingMedia.Duration = span;
            }
            _viewModel.UpdatePlaybackTime(0);
        }
    }

    private void OnPlayerMediaEnded(object sender, RoutedEventArgs e)
    {
        PopupPlayer.Position = TimeSpan.Zero;
        _viewModel.UpdatePlaybackTime(0);
        _viewModel.IsPlaying = false;
        _playbackTimer.Stop();
    }

    private void OnTimelineSliderValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_isUserDraggingSlider)
        {
            PopupPlayer.Position = TimeSpan.FromSeconds(e.NewValue);
            _viewModel.UpdatePlaybackTime(e.NewValue);
        }
    }

    private void SeekToMousePosition(MouseEventArgs e)
    {
        if (PlayerSlider.ActualWidth > 0 && PlayerSlider.Maximum > 0)
        {
            var pos = e.GetPosition(PlayerSlider);
            double ratio = Math.Clamp(pos.X / PlayerSlider.ActualWidth, 0.0, 1.0);
            double newSec = ratio * PlayerSlider.Maximum;
            PlayerSlider.Value = newSec;
            PopupPlayer.Position = TimeSpan.FromSeconds(newSec);
            _viewModel.UpdatePlaybackTime(newSec);
        }
    }

    private void OnTimelineSliderPreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.LeftButton == MouseButtonState.Pressed)
        {
            _isUserDraggingSlider = true;
            PlayerSlider.CaptureMouse();
            SeekToMousePosition(e);
        }
    }

    private void OnTimelineSliderPreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (_isUserDraggingSlider && e.LeftButton == MouseButtonState.Pressed)
        {
            SeekToMousePosition(e);
        }
    }

    private void OnTimelineSliderPreviewMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (_isUserDraggingSlider)
        {
            SeekToMousePosition(e);
            _isUserDraggingSlider = false;
            PlayerSlider.ReleaseMouseCapture();
        }
    }
}
