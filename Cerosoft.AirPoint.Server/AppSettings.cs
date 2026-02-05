using System;
using System.IO;
using System.Text.Json;

namespace Cerosoft.AirPoint.Server
{
    public static class AppSettings
    {
        // Save settings in: C:\Users\You\AppData\Local\CerosoftAirPoint\settings.json
        private static readonly string FolderPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CerosoftAirPoint");
        private static readonly string FilePath = Path.Combine(FolderPath, "settings.json");

        private class SettingsModel
        {
            public bool IsWifiPreferred { get; set; } = true;
            public bool RunAtStartup { get; set; } = true;
            public bool MinimizeToTray { get; set; } = false;
        }

        // FIX: Simplified 'new' expression
        private static SettingsModel _currentSettings = new();

        static AppSettings()
        {
            Load();
        }

        private static void Load()
        {
            try
            {
                if (File.Exists(FilePath))
                {
                    string json = File.ReadAllText(FilePath);
                    // FIX: Simplified 'new' expression
                    _currentSettings = JsonSerializer.Deserialize<SettingsModel>(json) ?? new();
                }
                else
                {
                    // FIX: Simplified 'new' expression
                    _currentSettings = new();
                }
            }
            catch
            {
                // FIX: Simplified 'new' expression
                _currentSettings = new();
            }
        }

        private static void Save()
        {
            try
            {
                if (!Directory.Exists(FolderPath)) Directory.CreateDirectory(FolderPath);
                string json = JsonSerializer.Serialize(_currentSettings);
                File.WriteAllText(FilePath, json);
            }
            catch { }
        }

        public static bool IsWifiPreferred
        {
            get => _currentSettings.IsWifiPreferred;
            set { _currentSettings.IsWifiPreferred = value; Save(); }
        }

        public static bool RunAtStartup
        {
            get => _currentSettings.RunAtStartup;
            set { _currentSettings.RunAtStartup = value; Save(); }
        }

        public static bool MinimizeToTray
        {
            get => _currentSettings.MinimizeToTray;
            set { _currentSettings.MinimizeToTray = value; Save(); }
        }
    }
}