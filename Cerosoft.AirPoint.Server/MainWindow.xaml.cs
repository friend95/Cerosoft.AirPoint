#nullable enable

using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Xaml.Media;
using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using Windows.Storage.Streams;
using QRCoder;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using System.Runtime.InteropServices;
using System.Net.NetworkInformation;
using System.Threading.Tasks;
using WindowsInput;
using WindowsInput.Native;
using Windows.Graphics;
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.Rfcomm;
using Windows.Networking.Sockets;
using Windows.Devices.Radios;
using Windows.System;

namespace Cerosoft.AirPoint.Server
{
    public sealed partial class MainWindow : Window
    {
        private AppWindow? m_appWindow;
        private WindowsSystemDispatcherQueueHelper? m_wsdqHelper;
        private Microsoft.UI.Composition.SystemBackdrops.SystemBackdropConfiguration? m_configurationSource;

        private TcpListener? _tcpServer;
        private StreamSocketListener? _bluetoothListener;
        private RfcommServiceProvider? _rfcommProvider;
        private bool _isRunning = false;
        private readonly InputSimulator _inputSim = new();
        private static readonly Guid _bluetoothServiceUuid = Guid.Parse("00001101-0000-1000-8000-00805F9B34FB");

        // --- NATIVE MOUSE CONSTANTS & IMPORT ---
        [DllImport("user32.dll", SetLastError = true)]
        static extern void mouse_event(uint dwFlags, uint dx, uint dy, uint dwData, int dwExtraInfo);

        private const uint MOUSEEVENTF_LEFTDOWN = 0x0002;
        private const uint MOUSEEVENTF_LEFTUP = 0x0004;
        private const uint MOUSEEVENTF_RIGHTDOWN = 0x0008;
        private const uint MOUSEEVENTF_RIGHTUP = 0x0010;

        public MainWindow()
        {
            this.InitializeComponent();
            this.Title = "Cerosoft AirPoint - Server";

            // 1. Window Setup (Resizable + Minimize Logic)
            CustomizeWindow();

            // 2. Apply System Settings (Startup Registry)
            ApplySystemSettings();

            // 3. UI & Network
            InitializeTheme();
            InitializeNetworkAndServer();
        }

        // --- WINDOW CUSTOMIZATION ---
        private void CustomizeWindow()
        {
            IntPtr hWnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
            WindowId wndId = Win32Interop.GetWindowIdFromWindow(hWnd);
            m_appWindow = AppWindow.GetFromWindowId(wndId);

            m_appWindow.Resize(new SizeInt32(500, 750));

            string iconPath = Path.Combine(AppContext.BaseDirectory, "Assets", "AppIcon.ico");
            m_appWindow.SetIcon(iconPath);

            if (m_appWindow.Presenter is OverlappedPresenter presenter)
            {
                presenter.IsResizable = false;
                presenter.IsMaximizable = false;
            }

            m_appWindow.Closing += OnWindowClosing;
        }

        private void OnWindowClosing(AppWindow sender, AppWindowClosingEventArgs args)
        {
            // If "Minimize to Tray" is enabled in AppSettings, minimize instead of closing
            if (AppSettings.MinimizeToTray)
            {
                args.Cancel = true; // Stop the close event
                if (m_appWindow != null && m_appWindow.Presenter is OverlappedPresenter presenter)
                {
                    presenter.Minimize();
                }
            }
        }

        public static void ApplySystemSettings()
        {
            // Handle Run at Startup using Registry
            try
            {
                string runKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run";
                using Microsoft.Win32.RegistryKey? key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(runKey, true);
                if (key != null)
                {
                    string appName = "CerosoftAirPoint";
                    if (AppSettings.RunAtStartup)
                    {
                        string? exePath = Environment.ProcessPath;
                        if (exePath != null) key.SetValue(appName, $"\"{exePath}\"");
                    }
                    else
                    {
                        key.DeleteValue(appName, false);
                    }
                }
            }
            catch { /* Ignore registry permission errors */ }
        }

        // --- NETWORK & QR LOGIC ---

        private async void GenerateQrCode(string content)
        {
            QRCodeGenerator qrGenerator = new();
            QRCodeData qrCodeData = qrGenerator.CreateQrCode(content, QRCodeGenerator.ECCLevel.Q);
            PngByteQRCode qrCode = new(qrCodeData);

            byte[] qrCodeBytes = qrCode.GetGraphic(20, [0, 0, 0, 255], [0, 0, 0, 0]);

            using InMemoryRandomAccessStream stream = new();
            using (DataWriter writer = new(stream.GetOutputStreamAt(0)))
            {
                writer.WriteBytes(qrCodeBytes);
                await writer.StoreAsync();
            }
            stream.Seek(0);
            BitmapImage image = new();
            await image.SetSourceAsync(stream);
            QrImage.Source = image;
        }

        private async void InitializeNetworkAndServer()
        {
            _isRunning = false;
            _tcpServer?.Stop();

            if (_rfcommProvider != null) { try { _rfcommProvider.StopAdvertising(); } catch { } _rfcommProvider = null; }
            if (_bluetoothListener != null) { try { await _bluetoothListener.CancelIOAsync(); } catch { } _bluetoothListener.Dispose(); _bluetoothListener = null; }

            if (AppSettings.IsWifiPreferred)
            {
                string localIp = GetBestLocalIpAddress();
                IpAddressText.Text = $"{localIp}";
                GenerateQrCode($"{localIp}:45000");
                StartTcpServer();
            }
            else
            {
                bool isBluetoothReady = await CheckBluetoothStatusAsync();
                if (isBluetoothReady)
                {
                    IpAddressText.Text = "Bluetooth Mode";
                    GenerateQrCode("BLUETOOTH_MODE");
                    StartBluetoothServer();
                }
                else
                {
                    IpAddressText.Text = "Bluetooth is OFF";
                }
            }
        }

        private async Task<bool> CheckBluetoothStatusAsync()
        {
            try
            {
                var radios = await Radio.GetRadiosAsync();
                var bluetoothRadio = radios.FirstOrDefault(r => r.Kind == RadioKind.Bluetooth);
                if (bluetoothRadio == null) { UpdateStatus("Error: No Bluetooth Adapter found.", false); return false; }
                if (bluetoothRadio.State == RadioState.Off) { await ShowBluetoothOffDialog(); return false; }
                return true;
            }
            catch { return true; }
        }

        private async Task ShowBluetoothOffDialog()
        {
            ContentDialog dialog = new()
            {
                XamlRoot = this.Content.XamlRoot,
                Title = "Bluetooth is Turned Off",
                Content = "AirPoint requires Bluetooth to be enabled. Please turn it on in Settings.",
                PrimaryButtonText = "Open Settings",
                CloseButtonText = "Cancel",
                DefaultButton = ContentDialogButton.Primary
            };
            var result = await dialog.ShowAsync();
            if (result == ContentDialogResult.Primary) await Launcher.LaunchUriAsync(new Uri("ms-settings:bluetooth"));
        }

        private async void StartTcpServer()
        {
            try
            {
                _tcpServer = new TcpListener(IPAddress.Any, 45000);
                _tcpServer.Start();
                _isRunning = true;
                UpdateStatus("Ready for Wi-Fi Connection", false);
                await Task.Run(async () =>
                {
                    while (_isRunning && _tcpServer != null)
                    {
                        try
                        {
                            TcpClient client = await _tcpServer.AcceptTcpClientAsync();
                            client.NoDelay = true;

                            this.DispatcherQueue.TryEnqueue(() => UpdateStatus("Connected via Wi-Fi!", true));
                            await HandleStreamAsync(client.GetStream());
                        }
                        catch { if (_isRunning) await Task.Delay(1000); }
                    }
                });
            }
            catch (Exception ex) { UpdateStatus($"Wi-Fi Error: {ex.Message}", false); }
        }

        private async void StartBluetoothServer()
        {
            try
            {
                _rfcommProvider = await RfcommServiceProvider.CreateAsync(RfcommServiceId.FromUuid(_bluetoothServiceUuid));
                _bluetoothListener = new();
                _bluetoothListener.ConnectionReceived += OnBluetoothConnectionReceived;
                await _bluetoothListener.BindServiceNameAsync(_rfcommProvider.ServiceId.AsString());
                _rfcommProvider.StartAdvertising(_bluetoothListener);
                _isRunning = true;
                UpdateStatus("Ready for Bluetooth Connection", false);
            }
            catch (Exception ex)
            {
                if (ex.Message.Contains("Element not found") || ex.HResult == -2147023728) { UpdateStatus("Bluetooth is OFF or Unavailable.", false); await ShowBluetoothOffDialog(); }
                else { UpdateStatus($"Bluetooth Error: {ex.Message}", false); }
            }
        }

        private async void OnBluetoothConnectionReceived(StreamSocketListener sender, StreamSocketListenerConnectionReceivedEventArgs args)
        {
            try
            {
                this.DispatcherQueue.TryEnqueue(() => UpdateStatus("Connected via Bluetooth!", true));
                using var socket = args.Socket;
                using var inputStream = socket.InputStream.AsStreamForRead();
                await HandleStreamAsync(inputStream);
            }
            catch { }
        }

        private async Task HandleStreamAsync(System.IO.Stream stream)
        {
            byte[] buffer = new byte[8192]; // Increased buffer size for smoother text
            int bytesRead;
            try
            {
                while ((bytesRead = await stream.ReadAsync(buffer)) != 0)
                {
                    ProcessCommand(buffer[0], buffer, bytesRead);
                }
            }
            catch { }
            this.DispatcherQueue.TryEnqueue(() => { if (_isRunning) UpdateStatus("Waiting for connection...", false); });
        }

        // --- INPUT PROCESSING ---
        private void ProcessCommand(byte command, byte[] data, int length)
        {
            try
            {
                switch (command)
                {
                    case 1: // Move
                        if (length >= 9)
                        {
                            float x = BitConverter.ToSingle(data, 1);
                            float y = BitConverter.ToSingle(data, 5);
                            _inputSim.Mouse.MoveMouseBy((int)x, (int)y);
                        }
                        break;
                    case 2: _inputSim.Mouse.LeftButtonClick(); break;
                    case 3: // Right Click (Native Implementation)
                        SimulateRightClick();
                        break;
                    case 4: // Scroll
                        if (length >= 5)
                        {
                            float scrollAmount = BitConverter.ToSingle(data, 1);
                            _inputSim.Mouse.VerticalScroll((int)(scrollAmount * 20));
                        }
                        break;
                    case 5: if (length >= 2) HandleShortcut(data[1]); break;
                    case 6: // Open URL
                        if (length >= 5)
                        {
                            int urlLen = BitConverter.ToInt32(data, 1);
                            if (length >= 5 + urlLen)
                            {
                                string content = System.Text.Encoding.UTF8.GetString(data, 5, urlLen);
                                OpenUrlOrFile(content);
                            }
                        }
                        break;
                    case 7: System.Diagnostics.Process.Start("shutdown", "/s /t 0"); break;
                    case 8: _inputSim.Mouse.LeftButtonDown(); break;
                    case 9: _inputSim.Mouse.LeftButtonUp(); break;
                    case 10: // Zoom
                        if (length >= 5)
                        {
                            float zoomDelta = BitConverter.ToSingle(data, 1);
                            _inputSim.Keyboard.KeyDown(VirtualKeyCode.CONTROL);
                            _inputSim.Mouse.VerticalScroll((int)(zoomDelta * 50));
                            _inputSim.Keyboard.KeyUp(VirtualKeyCode.CONTROL);
                        }
                        break;
                    case 11: System.Diagnostics.Process.Start("shutdown", "/r /t 0"); break;
                    case 12: System.Diagnostics.Process.Start("rundll32.exe", "user32.dll,LockWorkStation"); break;

                    // --- NEW KEYBOARD HANDLERS ---
                    case 20: // Text Input
                        if (length >= 5)
                        {
                            int textLen = BitConverter.ToInt32(data, 1);
                            if (length >= 5 + textLen)
                            {
                                string text = System.Text.Encoding.UTF8.GetString(data, 5, textLen);
                                SimulateText(text);
                            }
                        }
                        break;
                    case 21: // Key Command
                        if (length >= 5)
                        {
                            int keyCode = BitConverter.ToInt32(data, 1);
                            SimulateKey(keyCode);
                        }
                        break;
                }
            }
            catch { }
        }

        private void SimulateText(string text)
        {
            // WindowsInput handles special chars automatically
            _inputSim.Keyboard.TextEntry(text);
        }

        private void SimulateKey(int keyCode)
        {
            switch (keyCode)
            {
                case 1: _inputSim.Keyboard.KeyPress(VirtualKeyCode.BACK); break; // Backspace
                case 2: _inputSim.Keyboard.KeyPress(VirtualKeyCode.RETURN); break; // Enter
            }
        }

        private void SimulateRightClick()
        {
            // Perform a Right Mouse Button Click (Down + Up)
            mouse_event(MOUSEEVENTF_RIGHTDOWN | MOUSEEVENTF_RIGHTUP, 0, 0, 0, 0);
        }

        private void HandleShortcut(byte code)
        {
            switch (code)
            {
                case 1: _inputSim.Keyboard.ModifiedKeyStroke(VirtualKeyCode.LWIN, VirtualKeyCode.VK_D); break;
                case 2: _inputSim.Keyboard.KeyPress(VirtualKeyCode.LWIN); break;
                case 3: _inputSim.Keyboard.ModifiedKeyStroke(VirtualKeyCode.MENU, VirtualKeyCode.TAB); break;
                case 6: _inputSim.Keyboard.ModifiedKeyStroke(VirtualKeyCode.LWIN, VirtualKeyCode.TAB); break;
                case 4: _inputSim.Keyboard.ModifiedKeyStroke(VirtualKeyCode.CONTROL, VirtualKeyCode.VK_C); break;
                case 5: _inputSim.Keyboard.ModifiedKeyStroke(VirtualKeyCode.CONTROL, VirtualKeyCode.VK_V); break;
            }
        }

        private static void OpenUrlOrFile(string content)
        {
            try
            {
                bool isFilePath = content.Contains(":\\") || content.Contains(":/") || content.StartsWith("\\\\");
                if (!isFilePath && !content.StartsWith("http")) content = "https://" + content;
                new System.Diagnostics.Process { StartInfo = new System.Diagnostics.ProcessStartInfo(content) { UseShellExecute = true } }.Start();
            }
            catch { }
        }

        private static string GetBestLocalIpAddress()
        {
            string bestIp = "127.0.0.1";
            try
            {
                foreach (NetworkInterface item in NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (item.NetworkInterfaceType == NetworkInterfaceType.Loopback || item.OperationalStatus != OperationalStatus.Up || item.Description.Contains("Virtual")) continue;
                    if (item.NetworkInterfaceType == NetworkInterfaceType.Wireless80211 || item.NetworkInterfaceType == NetworkInterfaceType.Ethernet)
                    {
                        foreach (UnicastIPAddressInformation ip in item.GetIPProperties().UnicastAddresses)
                        {
                            if (ip.Address.AddressFamily == AddressFamily.InterNetwork && !ip.Address.ToString().StartsWith("169")) return ip.Address.ToString();
                        }
                    }
                }
            }
            catch { }
            return bestIp;
        }

        private void UpdateStatus(string text, bool isConnected)
        {
            if (this.DispatcherQueue.HasThreadAccess)
            {
                StatusText.Text = text;
                StatusDot.Fill = new SolidColorBrush(isConnected ? Microsoft.UI.Colors.Green : Microsoft.UI.Colors.Orange);
            }
            else
            {
                this.DispatcherQueue.TryEnqueue(() => UpdateStatus(text, isConnected));
            }
        }

        private async void OnSettingsClick(object sender, RoutedEventArgs e)
        {
            SettingsDialog dialog = new();
            if (this.Content != null && this.Content.XamlRoot != null)
            {
                dialog.XamlRoot = this.Content.XamlRoot;
                await dialog.ShowAsync();
                ApplySystemSettings(); // Re-apply settings (Startup/Tray)
                InitializeNetworkAndServer();
            }
        }

        private void QrImage_Tapped(object sender, TappedRoutedEventArgs e) => InitializeNetworkAndServer();

        // --- THEME & HELPERS ---
        private void InitializeTheme()
        {
            m_wsdqHelper = new();
            m_wsdqHelper.EnsureWindowsSystemDispatcherQueueController();
            m_configurationSource = new();
            this.Activated += (s, e) => m_configurationSource.IsInputActive = e.WindowActivationState != WindowActivationState.Deactivated;
            this.Closed += (s, e) => { _isRunning = false; _tcpServer?.Stop(); };
            ((FrameworkElement)this.Content).ActualThemeChanged += (s, e) => SetTheme();
            SetTheme();
        }

        private void SetTheme()
        {
            if (m_configurationSource == null) return;
            var currentTheme = ((FrameworkElement)this.Content).ActualTheme;
            m_configurationSource.Theme = currentTheme == ElementTheme.Dark ? Microsoft.UI.Composition.SystemBackdrops.SystemBackdropTheme.Dark : (currentTheme == ElementTheme.Light ? Microsoft.UI.Composition.SystemBackdrops.SystemBackdropTheme.Light : Microsoft.UI.Composition.SystemBackdrops.SystemBackdropTheme.Default);
            if (AppWindowTitleBar.IsCustomizationSupported() && m_appWindow != null) m_appWindow.TitleBar.ButtonForegroundColor = (m_configurationSource.Theme == Microsoft.UI.Composition.SystemBackdrops.SystemBackdropTheme.Dark) ? Colors.White : Colors.Black;
        }
    }

    class WindowsSystemDispatcherQueueHelper
    {
        [StructLayout(LayoutKind.Sequential)] struct DispatcherQueueOptions { internal int dwSize; internal int threadType; internal int apartmentType; }
        [DllImport("CoreMessaging.dll")] private static extern int CreateDispatcherQueueController([In] DispatcherQueueOptions options, [In, Out, MarshalAs(UnmanagedType.IUnknown)] ref object? dispatcherQueueController);
        object? m_dispatcherQueueController = null;
        public void EnsureWindowsSystemDispatcherQueueController()
        {
            if (Windows.System.DispatcherQueue.GetForCurrentThread() != null) return;
            if (m_dispatcherQueueController == null)
            {
                DispatcherQueueOptions options;
                options.dwSize = Marshal.SizeOf<DispatcherQueueOptions>();
                options.threadType = 2; options.apartmentType = 2;

                int hr = CreateDispatcherQueueController(options, ref m_dispatcherQueueController);
                if (hr != 0)
                {
                    Marshal.ThrowExceptionForHR(hr);
                }
            }
        }
    }
}