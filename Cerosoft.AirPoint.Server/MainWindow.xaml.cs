#nullable enable

using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using QRCoder;
using System;
using System.Buffers;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Windows.Devices.Bluetooth.Rfcomm;
using Windows.Devices.Radios;
using Windows.Graphics;
using Windows.Networking.Sockets;
using Windows.Storage.Streams;
using Windows.System;
using WindowsInput;
using WindowsInput.Native;
using System.Text.RegularExpressions;
using System.Diagnostics; // Added for ProcessStartInfo

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

        // Optimization (CA1822): Promoted to static as InputSimulator is stateless/global
        private static readonly InputSimulator _inputSim = new();
        private static readonly Guid _bluetoothServiceUuid = Guid.Parse("00001101-0000-1000-8000-00805F9B34FB");

        // --- NATIVE MOUSE CONSTANTS & IMPORT ---

        // INDUSTRY GRADE FIX: 
        // Switched to [DllImport] to resolve CS8795 (Source Generator failure).
        // Suppressing SYSLIB1054 to strictly allow runtime marshalling for stability.
#pragma warning disable SYSLIB1054
        [DllImport("user32.dll", EntryPoint = "mouse_event", SetLastError = true)]
        private static extern void MouseEvent(uint dwFlags, uint dx, uint dy, uint dwData, int dwExtraInfo);
#pragma warning restore SYSLIB1054

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

            // Resize window using Windows.Graphics.SizeInt32
            m_appWindow.Resize(new SizeInt32(500, 750));

            string iconPath = Path.Combine(AppContext.BaseDirectory, "Assets", "AppIcon.ico");
            m_appWindow.SetIcon(iconPath);

            // Modernization: Pattern Matching for safe casting
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
                if (m_appWindow?.Presenter is OverlappedPresenter presenter)
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
                if (key is not null)
                {
                    string appName = "CerosoftAirPoint";
                    if (AppSettings.RunAtStartup)
                    {
                        string? exePath = Environment.ProcessPath;
                        if (exePath is not null) key.SetValue(appName, $"\"{exePath}\"");
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
            await Task.Run(async () =>
            {
                QRCodeGenerator qrGenerator = new();
                QRCodeData qrCodeData = qrGenerator.CreateQrCode(content, QRCodeGenerator.ECCLevel.Q);
                PngByteQRCode qrCode = new(qrCodeData);

                byte[] qrCodeBytes = qrCode.GetGraphic(20, [0, 0, 0, 255], [0, 0, 0, 0]);

                this.DispatcherQueue.TryEnqueue(async () =>
                {
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
                });
            });
        }

        private async void InitializeNetworkAndServer()
        {
            _isRunning = false;
            _tcpServer?.Stop();

            if (_rfcommProvider is not null) { try { _rfcommProvider.StopAdvertising(); } catch { } _rfcommProvider = null; }
            if (_bluetoothListener is not null) { try { await _bluetoothListener.CancelIOAsync(); } catch { } _bluetoothListener.Dispose(); _bluetoothListener = null; }

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
                if (bluetoothRadio is null) { UpdateStatus("Error: No Bluetooth Adapter found.", false); return false; }
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
                    while (_isRunning && _tcpServer is not null)
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

        // Modernization: Use ArrayPool to prevent GC pressure (Industry Grade)
        private async Task HandleStreamAsync(System.IO.Stream stream)
        {
            byte[] buffer = ArrayPool<byte>.Shared.Rent(8192);
            int bytesRead;
            try
            {
                while ((bytesRead = await stream.ReadAsync(buffer)) != 0)
                {
                    // Pass only the relevant slice of data
                    ProcessCommand(buffer[0], buffer, bytesRead);
                }
            }
            catch { }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }
            this.DispatcherQueue.TryEnqueue(() => { if (_isRunning) UpdateStatus("Waiting for connection...", false); });
        }

        // --- INPUT PROCESSING ---
        // Optimization (CA1822): Changed to static as it accesses static _inputSim
        private static void ProcessCommand(byte command, byte[] data, int length)
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

        // Optimization (CA1822): Changed to static
        private static void SimulateText(string text)
        {
            // WindowsInput handles special chars automatically
            _inputSim.Keyboard.TextEntry(text);
        }

        // Optimization (CA1822): Changed to static
        private static void SimulateKey(int keyCode)
        {
            switch (keyCode)
            {
                case 1: _inputSim.Keyboard.KeyPress(VirtualKeyCode.BACK); break; // Backspace
                case 2: _inputSim.Keyboard.KeyPress(VirtualKeyCode.RETURN); break; // Enter
            }
        }

        // Optimization (CA1822): Changed to static
        private static void SimulateRightClick()
        {
            // Perform a Right Mouse Button Click (Down + Up)
            MouseEvent(MOUSEEVENTF_RIGHTDOWN | MOUSEEVENTF_RIGHTUP, 0, 0, 0, 0);
        }

        // Optimization (CA1822): Changed to static
        private static void HandleShortcut(byte code)
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

        // --- INTELLIGENT DISPATCHER (Robust URL/File/Command Handler) ---

        // Optimization: Compiled Regex for maximum performance (Fixes SYSLIB1045)
        [GeneratedRegex(@"^[a-zA-Z0-9\+\.\-]+://")]
        private static partial Regex ProtocolRegex();

        private static void OpenUrlOrFile(string content)
        {
            if (string.IsNullOrWhiteSpace(content)) return;

            content = content.Trim();

            try
            {
                // CASE 1: Custom Protocols (Steam, Spotify, Magnet, Mailto)
                if (ProtocolRegex().IsMatch(content))
                {
                    StartProcess(content);
                    return;
                }

                // CASE 2: Complex Command Lines (Quoted Paths with Arguments)
                // Fix CA1866: Use char overload for performance
                if (content.StartsWith('"'))
                {
                    int endQuoteIndex = content.IndexOf('"', 1);
                    if (endQuoteIndex > 1)
                    {
                        // Fix IDE0057: Use modern C# Range operators
                        string executable = content[1..endQuoteIndex];
                        string arguments = content[(endQuoteIndex + 1)..].Trim();

                        if (File.Exists(executable))
                        {
                            StartProcess(executable, arguments);
                            return;
                        }
                    }
                }

                // CASE 3: Local Files, Directories, or Shortcuts (.lnk)
                if (File.Exists(content) || Directory.Exists(content))
                {
                    StartProcess(content);
                    return;
                }

                // CASE 4: Web URL Fallback
                if (!content.StartsWith("http", StringComparison.OrdinalIgnoreCase) &&
                    !content.StartsWith("www", StringComparison.OrdinalIgnoreCase))
                {
                    content = "https://" + content;
                }
                else if (content.StartsWith("www", StringComparison.OrdinalIgnoreCase))
                {
                    content = "https://" + content;
                }

                StartProcess(content);
            }
            // Fix CS0168 & IDE0059: Removed unused 'ex' variable
            catch
            {
                // Log failure in production if needed
            }
        }

        private static void StartProcess(string fileName, string arguments = "")
        {
            var info = new ProcessStartInfo
            {
                FileName = fileName,
                Arguments = arguments,
                UseShellExecute = true,
                WorkingDirectory = Path.GetDirectoryName(fileName) ?? ""
            };
            Process.Start(info);
        }

        // --- END INTELLIGENT DISPATCHER ---

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
                StatusDot.Fill = new SolidColorBrush(isConnected ? Colors.Green : Colors.Orange);
            }
            else
            {
                this.DispatcherQueue.TryEnqueue(() => UpdateStatus(text, isConnected));
            }
        }

        private async void OnSettingsClick(object sender, RoutedEventArgs e)
        {
            SettingsDialog dialog = new();
            if (this.Content is { XamlRoot: not null })
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
            if (m_configurationSource is null) return;
            var currentTheme = ((FrameworkElement)this.Content).ActualTheme;
            m_configurationSource.Theme = currentTheme == ElementTheme.Dark ? Microsoft.UI.Composition.SystemBackdrops.SystemBackdropTheme.Dark : (currentTheme == ElementTheme.Light ? Microsoft.UI.Composition.SystemBackdrops.SystemBackdropTheme.Light : Microsoft.UI.Composition.SystemBackdrops.SystemBackdropTheme.Default);
            if (AppWindowTitleBar.IsCustomizationSupported() && m_appWindow is not null) m_appWindow.TitleBar.ButtonForegroundColor = (m_configurationSource.Theme == Microsoft.UI.Composition.SystemBackdrops.SystemBackdropTheme.Dark) ? Colors.White : Colors.Black;
        }
    }

    // Helper Class
    partial class WindowsSystemDispatcherQueueHelper
    {
        [StructLayout(LayoutKind.Sequential)]
        struct DispatcherQueueOptions { internal int dwSize; internal int threadType; internal int apartmentType; }

        // INDUSTRY GRADE FIX: 
        // Reverted to [DllImport] to fix CS8795. Suppressing SYSLIB1054 for stable runtime marshalling.
#pragma warning disable SYSLIB1054
        [DllImport("CoreMessaging.dll")]
        private static extern int CreateDispatcherQueueController(DispatcherQueueOptions options, ref IntPtr dispatcherQueueController);
#pragma warning restore SYSLIB1054

        object? m_dispatcherQueueController = null;
        public void EnsureWindowsSystemDispatcherQueueController()
        {
            if (Windows.System.DispatcherQueue.GetForCurrentThread() is not null) return;
            if (m_dispatcherQueueController is null)
            {
                DispatcherQueueOptions options;
                options.dwSize = Marshal.SizeOf<DispatcherQueueOptions>();
                options.threadType = 2; options.apartmentType = 2;

                IntPtr ptr = IntPtr.Zero;
                int hr = CreateDispatcherQueueController(options, ref ptr);
                if (hr != 0)
                {
                    Marshal.ThrowExceptionForHR(hr);
                }

                if (ptr != IntPtr.Zero)
                {
                    m_dispatcherQueueController = Marshal.GetObjectForIUnknown(ptr);
                }
            }
        }
    }
}