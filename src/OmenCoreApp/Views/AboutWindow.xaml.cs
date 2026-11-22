using System;
using System.Diagnostics;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Navigation;
using OmenCore.Services;
using OmenCore.Utils;

namespace OmenCore.Views
{
    public partial class AboutWindow : Window
    {
        private readonly AutoUpdateService _updateService;
        private bool _isCheckingUpdates;

        public AboutWindow()
        {
            InitializeComponent();
            _updateService = new AutoUpdateService(App.Logging, "https://api.github.com/repos/theantipopau/omencore/releases/latest");
            VersionText.Text = $"Version {AppVersionProvider.GetVersionString()}";
        }

        private async void CheckUpdatesClicked(object sender, RoutedEventArgs e)
        {
            if (_isCheckingUpdates) return;
            
            _isCheckingUpdates = true;
            CheckUpdatesButton.IsEnabled = false;
            SetStatus(global::OmenCore.Localization.Strings.CsCheckingForUpdates, "TextSecondaryBrush");
            
            try
            {
                var result = await _updateService.CheckForUpdatesAsync();
                
                if (result.UpdateAvailable && result.LatestVersion != null)
                {
                    var latest = result.LatestVersion;
                    SetStatus(string.Format(global::OmenCore.Localization.Strings.CsUpdateAvailableV, latest.VersionString), "SuccessBrush");

                    if (string.IsNullOrWhiteSpace(latest.DownloadUrl))
                    {
                        var openRelease = MessageBox.Show(
                            global::OmenCore.Localization.Strings.CsANewVersionIsAvailableOnGitHub,
                            global::OmenCore.Localization.Strings.CsUpdateAvailable_8bd76,
                            MessageBoxButton.YesNo,
                            MessageBoxImage.Information);

                        if (openRelease == MessageBoxResult.Yes)
                        {
                            OpenUrl(latest.ChangelogUrl);
                        }

                        return;
                    }

                    var msgResult = MessageBox.Show(
                        string.Format(global::OmenCore.Localization.Strings.CsANewVersionIsAvailableCurrentLatest, result.CurrentVersion.VersionString, latest.VersionString),
                        global::OmenCore.Localization.Strings.CsUpdateAvailable_8bd76,
                        MessageBoxButton.YesNo,
                        MessageBoxImage.Information);

                    if (msgResult == MessageBoxResult.Yes)
                    {
                        SetStatus(global::OmenCore.Localization.Strings.CsDownloadingUpdate, "TextSecondaryBrush");
                        var installerPath = await _updateService.DownloadUpdateAsync(latest);

                        if (installerPath != null)
                        {
                            SetStatus(global::OmenCore.Localization.Strings.CsInstallingUpdate, "TextSecondaryBrush");
                            var installResult = await _updateService.InstallUpdateAsync(installerPath);
                            if (!installResult.Success)
                            {
                                SetStatus(installResult.Message, "AccentBrush");
                            }
                        }
                        else
                        {
                                SetStatus(global::OmenCore.Localization.Strings.CsDownloadFailedOpenGitHubReleasesToInstall, "AccentBrush");
                                if (!string.IsNullOrWhiteSpace(latest.ChangelogUrl))
                                {
                                    var openGitHub = MessageBox.Show(
                                        global::OmenCore.Localization.Strings.CsTheUpdateCouldNotBeDownloadedOr,
                                        global::OmenCore.Localization.Strings.CsUpdateDownloadFailed,
                                        MessageBoxButton.YesNo,
                                        MessageBoxImage.Warning);
                                    if (openGitHub == MessageBoxResult.Yes)
                                        OpenUrl(latest.ChangelogUrl);
                                }
                        }
                    }
                }
                else
                {
                    SetStatus(result.Message, "TextSecondaryBrush");
                }
            }
            catch (Exception ex)
            {
                SetStatus(string.Format(global::OmenCore.Localization.Strings.CsUpdateCheckFailed, ex.Message), "AccentBrush");
            }
            finally
            {
                _isCheckingUpdates = false;
                CheckUpdatesButton.IsEnabled = true;
            }
        }

        private void Hyperlink_RequestNavigate(object sender, RequestNavigateEventArgs e)
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = e.Uri.AbsoluteUri,
                UseShellExecute = true
            });
            e.Handled = true;
        }

        private void CloseClicked(object sender, RoutedEventArgs e)
        {
            Close();
        }

        private void Window_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
        {
            if (e.Key == System.Windows.Input.Key.Escape)
            {
                Close();
            }
        }

        private void Border_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ButtonState == MouseButtonState.Pressed)
            {
                DragMove();
            }
        }

        private void SetStatus(string message, string brushResourceKey)
        {
            UpdateStatusText.Text = message;
            if (FindResource(brushResourceKey) is Brush brush)
            {
                UpdateStatusText.Foreground = brush;
            }
        }

        private void OpenUrl(string? url)
        {
            if (string.IsNullOrWhiteSpace(url))
            {
                return;
            }

            Process.Start(new ProcessStartInfo
            {
                FileName = url,
                UseShellExecute = true
            });
        }
    }
}
