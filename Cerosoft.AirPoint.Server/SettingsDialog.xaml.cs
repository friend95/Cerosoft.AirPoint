using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Cerosoft.AirPoint.Server
{
    public sealed partial class SettingsDialog : ContentDialog
    {
        public SettingsDialog()
        {
            this.InitializeComponent();
            LoadSettings();

            // Hide default buttons since we are using custom ones in the Grid
            this.IsPrimaryButtonEnabled = false;
            this.IsSecondaryButtonEnabled = false;
            // We use a trick to hide the default button bar if desired, 
            // but standard ContentDialog doesn't easily allow removing the bottom area completely
            // without overriding the Template. 
            // However, since we define our own buttons inside the Grid, we just leave the default ones empty.
        }

        private void LoadSettings()
        {
            // 1. Load Connection Preference
            if (AppSettings.IsWifiPreferred)
            {
                WifiRadio.IsChecked = true;
            }
            else
            {
                BluetoothRadio.IsChecked = true;
            }

            // 2. Load System Toggles
            StartupToggle.IsOn = AppSettings.RunAtStartup;
            TrayToggle.IsOn = AppSettings.MinimizeToTray;
        }

        private void Save_Click(object sender, RoutedEventArgs e)
        {
            // 1. Save Connection Preference
            AppSettings.IsWifiPreferred = WifiRadio.IsChecked == true;

            // 2. Save System Toggles
            AppSettings.RunAtStartup = StartupToggle.IsOn;
            AppSettings.MinimizeToTray = TrayToggle.IsOn;

            // Close Dialog
            this.Hide();
        }

        private void Cancel_Click(object sender, RoutedEventArgs e)
        {
            this.Hide();
        }
    }
}