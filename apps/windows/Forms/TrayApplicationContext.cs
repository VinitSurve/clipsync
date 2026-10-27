using System;
using System.Drawing;
using System.IO;
using System.Net.Http;
using System.Net.NetworkInformation;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Win32;
using ClipSync.Windows.Crypto;
using ClipSync.Windows.Models;
using ClipSync.Windows.Services;

namespace ClipSync.Windows.Forms;

public sealed class TrayApplicationContext : ApplicationContext
{
    private readonly NotifyIcon _notifyIcon;
    private readonly ToolStripMenuItem _statusMenuItem;
    private readonly ToolStripMenuItem _pauseMenuItem;
    private readonly ClipboardMonitor _clipboardMonitor;
    private readonly HistoryForm _historyForm;

    private WebSocketSyncService? _syncService;
    private ClipSyncCrypto? _crypto;
    private GoogleDriveService? _googleDriveService;
    private TransferManager? _transferManager;

    private string _serverUrl = "https://clipsync-server-cloudflare.survevinit56.workers.dev";
    private string _syncPassphrase = "clipsync-master-sync-passphrase";
    private string _deviceLabel = "Windows 10 PC";
    private string _deviceId = "";
    private string _token = "";
    private string _syncSalt = "";
    private bool _launchAtStartup = true;
    private string _connectionState = "Disconnected";
    private string _lastStatusDetail = "Ready";

    private static string ConfigPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ClipSync", "config.json");

    public TrayApplicationContext(string? initialServerUrl = null)
    {
        LoadConfig();

        if (!string.IsNullOrWhiteSpace(initialServerUrl))
        {
            _serverUrl = initialServerUrl.TrimEnd('/');
        }
        else if (!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("CLIPSYNC_SERVER_URL")))
        {
            _serverUrl = Environment.GetEnvironmentVariable("CLIPSYNC_SERVER_URL")!.Trim().TrimEnd('/');
        }

        // Apply startup registry setting if needed
        if (_launchAtStartup && !IsStartupEnabled())
        {
            SetStartup(true);
        }

        _googleDriveService = new GoogleDriveService();

        _clipboardMonitor = new ClipboardMonitor();
        _clipboardMonitor.Show();
        _clipboardMonitor.Hide(); // Message loop window

        _historyForm = new HistoryForm(text => _clipboardMonitor.SetRemoteClipboard(text));

        _statusMenuItem = new ToolStripMenuItem("Status: Connecting...") { Enabled = false };
        _pauseMenuItem = new ToolStripMenuItem("Pause Sync", null, (_, _) => TogglePause());

        var contextMenu = new ContextMenuStrip();
        contextMenu.Items.Add(_statusMenuItem);
        contextMenu.Items.Add(new ToolStripSeparator());

        var openHistoryItem = new ToolStripMenuItem("Open Clipboard History", null, (_, _) => ShowHistory())
        {
            Font = new Font(contextMenu.Font, FontStyle.Bold)
        };
        contextMenu.Items.Add(openHistoryItem);
        contextMenu.Items.Add("Settings...", null, (_, _) => ShowSettings());
        contextMenu.Items.Add("Reconnect", null, (_, _) => _ = ReconnectAsync("Manual reconnect requested"));
        contextMenu.Items.Add(_pauseMenuItem);
        contextMenu.Items.Add("Pair New Device...", null, (_, _) => ShowPairing());
        contextMenu.Items.Add(new ToolStripSeparator());
        contextMenu.Items.Add("Exit ClipSync", null, (_, _) => ExitApp());

        Icon trayIcon = SystemIcons.Application;
        try
        {
            string iconPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Resources", "AppIcon.ico");
            if (File.Exists(iconPath))
            {
                trayIcon = new Icon(iconPath);
            }
            else if (File.Exists(Path.Combine("Resources", "AppIcon.ico")))
            {
                trayIcon = new Icon(Path.Combine("Resources", "AppIcon.ico"));
            }
        }
        catch
        {
            trayIcon = SystemIcons.Application;
        }

        _notifyIcon = new NotifyIcon
        {
            Icon = trayIcon,
            ContextMenuStrip = contextMenu,
            Text = "ClipSync - Universal Clipboard",
            Visible = true
        };
        _notifyIcon.DoubleClick += (_, _) => ShowHistory();

        _clipboardMonitor.OnNewLocalCopy += OnNewLocalCopy;

        // Register power & network change listeners for automatic background reconnection
        RegisterSystemEventListeners();

        _ = BootstrapOrPairAsync();
    }

    private void RegisterSystemEventListeners()
    {
        try
        {
            SystemEvents.PowerModeChanged += OnPowerModeChanged;
            NetworkChange.NetworkAvailabilityChanged += OnNetworkAvailabilityChanged;
            NetworkChange.NetworkAddressChanged += OnNetworkAddressChanged;
        }
        catch
        {
            // Event registration fallback
        }
    }

    private void UnregisterSystemEventListeners()
    {
        try
        {
            SystemEvents.PowerModeChanged -= OnPowerModeChanged;
            NetworkChange.NetworkAvailabilityChanged -= OnNetworkAvailabilityChanged;
            NetworkChange.NetworkAddressChanged -= OnNetworkAddressChanged;
        }
        catch {}
    }

    private void OnPowerModeChanged(object? sender, PowerModeChangedEventArgs e)
    {
        if (e.Mode == PowerModes.Resume)
        {
            _ = Task.Run(async () =>
            {
                await Task.Delay(2000); // Allow network interface to associate after sleep
                await ReconnectAsync("Resumed from sleep");
            });
        }
    }

    private void OnNetworkAvailabilityChanged(object? sender, NetworkAvailabilityEventArgs e)
    {
        if (e.IsAvailable)
        {
            _ = Task.Run(async () =>
            {
                await Task.Delay(1000);
                await ReconnectAsync("Network interface online");
            });
        }
    }

    private void OnNetworkAddressChanged(object? sender, EventArgs e)
    {
        _ = Task.Run(async () =>
        {
            await Task.Delay(1000);
            await ReconnectAsync("Network route updated");
        });
    }

    private void LoadConfig()
    {
        try
        {
            if (File.Exists(ConfigPath))
            {
                string json = File.ReadAllText(ConfigPath);
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;

                if (root.TryGetProperty("serverUrl", out var urlEl) && !string.IsNullOrWhiteSpace(urlEl.GetString()))
                    _serverUrl = urlEl.GetString()!.TrimEnd('/');

                if (root.TryGetProperty("syncPassphrase", out var passEl) && !string.IsNullOrWhiteSpace(passEl.GetString()))
                    _syncPassphrase = passEl.GetString()!;

                if (root.TryGetProperty("deviceLabel", out var labelEl) && !string.IsNullOrWhiteSpace(labelEl.GetString()))
                    _deviceLabel = labelEl.GetString()!;

                if (root.TryGetProperty("deviceId", out var devEl))
                    _deviceId = devEl.GetString() ?? "";

                if (root.TryGetProperty("token", out var tokEl))
                    _token = tokEl.GetString() ?? "";

                if (root.TryGetProperty("syncSalt", out var saltEl))
                    _syncSalt = saltEl.GetString() ?? "";

                if (root.TryGetProperty("launchAtStartup", out var startEl))
                    _launchAtStartup = startEl.GetBoolean();
            }
        }
        catch {}
    }

    private void SaveConfig()
    {
        try
        {
            string? dir = Path.GetDirectoryName(ConfigPath);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            {
                Directory.CreateDirectory(dir);
            }

            var payload = new
            {
                serverUrl = _serverUrl,
                syncPassphrase = _syncPassphrase,
                deviceLabel = _deviceLabel,
                deviceId = _deviceId,
                token = _token,
                syncSalt = _syncSalt,
                launchAtStartup = _launchAtStartup
            };
            string json = JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(ConfigPath, json);
        }
        catch {}
    }

    public static bool IsStartupEnabled()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", false);
            return key?.GetValue("ClipSync") != null;
        }
        catch
        {
            return false;
        }
    }

    public static void SetStartup(bool enable)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", true);
            if (key != null)
            {
                if (enable)
                {
                    string exePath = Application.ExecutablePath;
                    key.SetValue("ClipSync", $"\"{exePath}\"");
                }
                else
                {
                    key.DeleteValue("ClipSync", false);
                }
            }
        }
        catch {}
    }

    public async Task ReconnectAsync(string reason = "")
    {
        try
        {
            _connectionState = "Connecting...";
            _lastStatusDetail = string.IsNullOrEmpty(reason) ? "Reconnecting..." : reason;
            UpdateTrayStatus();

            _syncService?.Stop();
            await BootstrapOrPairAsync();
        }
        catch (Exception ex)
        {
            _connectionState = "Disconnected";
            _lastStatusDetail = $"Reconnect failed: {ex.Message}";
            UpdateTrayStatus();
        }
    }

    private void UpdateTrayStatus()
    {
        _statusMenuItem.Text = $"Status: {_connectionState}";

        string tipText = $"ClipSync - {_connectionState}";
        if (tipText.Length > 63) tipText = tipText[..63];
        _notifyIcon.Text = tipText;
    }

    private async Task BootstrapOrPairAsync()
    {
        try
        {
            _connectionState = "Connecting...";
            _lastStatusDetail = "Validating connection to server...";
            UpdateTrayStatus();

            // 1. Check if we already have valid credentials on this server
            if (!string.IsNullOrEmpty(_token))
            {
                using var testHttp = new HttpClient();
                testHttp.Timeout = TimeSpan.FromSeconds(10);
                testHttp.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", _token);
                try
                {
                    var testRes = await testHttp.GetAsync($"{_serverUrl}/v1/devices");
                    if (testRes.IsSuccessStatusCode)
                    {
                        InitializeSync(_deviceId, _token, _syncSalt);
                        return;
                    }
                    else if (testRes.StatusCode == System.Net.HttpStatusCode.Unauthorized)
                    {
                        // Token was revoked or invalid on server; clear and re-register
                        _token = "";
                        _deviceId = "";
                        _syncSalt = "";
                        SaveConfig();
                    }
                    else
                    {
                        _connectionState = "Disconnected";
                        _lastStatusDetail = $"Server returned HTTP {(int)testRes.StatusCode}";
                        UpdateTrayStatus();
                        return;
                    }
                }
                catch (Exception ex)
                {
                    // Network timeout or unreachable: keep token and report status without wiping credentials
                    _connectionState = "Disconnected";
                    _lastStatusDetail = $"Server unreachable: {ex.Message}";
                    UpdateTrayStatus();
                    return;
                }
            }

            // 2. Register Windows client if no token present
            using var http = new HttpClient();
            http.Timeout = TimeSpan.FromSeconds(10);
            var payload = new
            {
                label = string.IsNullOrWhiteSpace(_deviceLabel) ? "Windows 10 PC" : _deviceLabel,
                platform = "windows"
            };
            string json = JsonSerializer.Serialize(payload);
            var content = new StringContent(json, Encoding.UTF8, "application/json");

            var res = await http.PostAsync($"{_serverUrl}/v1/devices/register", content);
            if (res.IsSuccessStatusCode)
            {
                string body = await res.Content.ReadAsStringAsync();
                var dev = JsonSerializer.Deserialize<RegisterDeviceResponse>(body);
                if (dev != null)
                {
                    _deviceLabel = dev.Label;
                    InitializeSync(dev.DeviceId, dev.Token, dev.SyncSalt);
                    SaveConfig();
                }
            }
            else
            {
                _connectionState = "Disconnected";
                _lastStatusDetail = $"Registration failed (HTTP {(int)res.StatusCode})";
                UpdateTrayStatus();
            }
        }
        catch (Exception ex)
        {
            _connectionState = "Disconnected";
            _lastStatusDetail = $"Connection error: {ex.Message}";
            UpdateTrayStatus();
        }
    }

    private void InitializeSync(string deviceId, string token, string syncSalt)
    {
        _deviceId = deviceId;
        _token = token;
        _syncSalt = syncSalt;

        _crypto = new ClipSyncCrypto(_syncPassphrase, syncSalt);
        _syncService = new WebSocketSyncService(_serverUrl, deviceId, token, _crypto);

        _googleDriveService ??= new GoogleDriveService();
        _transferManager = new TransferManager(
            _serverUrl,
            deviceId,
            token,
            _crypto,
            _googleDriveService,
            _syncService,
            _clipboardMonitor
        );

        _transferManager.OnTransferError += err =>
        {
            _notifyIcon.ShowBalloonTip(2500, "ClipSync Transfer", err, ToolTipIcon.Warning);
        };

        _syncService.OnStateChanged += state =>
        {
            _connectionState = state;
            _lastStatusDetail = state == "Connected" ? "Connected to ClipSync" : state;
            UpdateTrayStatus();

            if (state == "Connected")
            {
                _ = _transferManager.FetchPendingTransfersAsync();
                _ = _googleDriveService.SweepOrphanedFilesAsync(TimeSpan.FromHours(24));
            }
        };

        _syncService.OnStatusMessage += msg =>
        {
            _lastStatusDetail = msg;
        };

        _syncService.OnRemoteItemReceived += (item, text) =>
        {
            _clipboardMonitor.SetRemoteClipboard(text);
            _historyForm.AddItem(text, "Remote");
            _notifyIcon.ShowBalloonTip(1500, "ClipSync", "Clipboard synced from remote device", ToolTipIcon.Info);
        };

        _syncService.Start();
    }

    private void OnNewLocalCopy(string text)
    {
        _historyForm.AddItem(text, "Local PC");
        if (_syncService != null && !_clipboardMonitor.IsPaused)
        {
            _ = _syncService.BroadcastTextAsync(text);
        }
    }

    private bool TogglePause()
    {
        bool isPaused = !_clipboardMonitor.IsPaused;
        _clipboardMonitor.IsPaused = isPaused;
        if (_syncService != null) _syncService.IsPaused = isPaused;

        _pauseMenuItem.Text = isPaused ? "Resume Sync" : "Pause Sync";
        _connectionState = isPaused ? "Paused" : (_syncService?.IsConnected == true ? "Connected" : "Disconnected");
        UpdateTrayStatus();
        return isPaused;
    }

    private void ShowHistory()
    {
        if (!_historyForm.Visible)
        {
            _historyForm.Show();
        }
        _historyForm.BringToFront();
    }

    private void ShowPairing()
    {
        PairingForm? pairingForm = null;
        pairingForm = new PairingForm(_serverUrl, claimed =>
        {
            if (pairingForm != null)
            {
                _serverUrl = pairingForm.ServerUrl;
            }
            _deviceLabel = claimed.Label;
            _syncService?.Stop();
            InitializeSync(claimed.DeviceId, claimed.Token, claimed.SyncSalt);
            SaveConfig();
        });
        pairingForm.Show();
    }

    private void ShowSettings()
    {
        var settingsForm = new SettingsForm(
            currentServerUrl: _serverUrl,
            currentPassphrase: _syncPassphrase,
            currentDeviceLabel: _deviceLabel,
            currentDeviceId: _deviceId,
            currentToken: _token,
            currentConnectionState: _connectionState,
            lastStatusDetail: _lastStatusDetail,
            isStartupEnabled: IsStartupEnabled(),
            isPaused: _clipboardMonitor.IsPaused,
            onSave: (newUrl, newPass, newLabel, startup) =>
            {
                bool urlChanged = !string.Equals(_serverUrl, newUrl, StringComparison.OrdinalIgnoreCase);

                _serverUrl = newUrl;
                _syncPassphrase = newPass;
                _deviceLabel = newLabel;
                _launchAtStartup = startup;
                SetStartup(startup);

                if (urlChanged)
                {
                    _token = "";
                    _deviceId = "";
                    _syncSalt = "";
                }

                SaveConfig();

                _syncService?.Stop();
                _ = BootstrapOrPairAsync();
            },
            onReconnect: () =>
            {
                _ = ReconnectAsync("Manual reconnect requested from Settings");
            },
            onTogglePause: () =>
            {
                return TogglePause();
            },
            onOpenPairing: () =>
            {
                ShowPairing();
            },
            googleDrive: _googleDriveService,
            transferManager: _transferManager
        );

        settingsForm.ShowDialog();
    }

    private void ExitApp()
    {
        UnregisterSystemEventListeners();
        _notifyIcon.Visible = false;
        _syncService?.Stop();
        _clipboardMonitor.Close();
        Application.Exit();
    }
}
