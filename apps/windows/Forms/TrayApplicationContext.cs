using System;
using System.Drawing;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows.Forms;
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

    private string _serverUrl = "https://clipsync-server-cloudflare.survevinit56.workers.dev";
    private string _syncPassphrase = "clipsync-master-sync-passphrase";
    private string _deviceId = "";
    private string _token = "";
    private string _syncSalt = "";
    private bool _launchAtStartup = true;

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

        _clipboardMonitor = new ClipboardMonitor();
        _clipboardMonitor.Show();
        _clipboardMonitor.Hide(); // Runs message loop

        _historyForm = new HistoryForm(text => _clipboardMonitor.SetRemoteClipboard(text));

        _statusMenuItem = new ToolStripMenuItem("Status: Connecting...") { Enabled = false };
        _pauseMenuItem = new ToolStripMenuItem("Pause Sync", null, (_, _) => TogglePause());

        var contextMenu = new ContextMenuStrip();
        contextMenu.Items.Add(_statusMenuItem);
        contextMenu.Items.Add(new ToolStripSeparator());
        contextMenu.Items.Add(_pauseMenuItem);
        contextMenu.Items.Add("Clipboard History", null, (_, _) => ShowHistory());
        contextMenu.Items.Add("Pair New Device...", null, (_, _) => ShowPairing());
        contextMenu.Items.Add("Settings...", null, (_, _) => ShowSettings());
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
            Text = "ClipSync — Universal Clipboard",
            Visible = true
        };
        _notifyIcon.DoubleClick += (_, _) => ShowHistory();

        _clipboardMonitor.OnNewLocalCopy += OnNewLocalCopy;

        _ = BootstrapOrPairAsync();
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
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", false);
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
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", true);
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

    private async Task BootstrapOrPairAsync()
    {
        try
        {
            _statusMenuItem.Text = "Status: Connecting...";
            _notifyIcon.Text = "ClipSync — Connecting...";

            // Check if we already have valid credentials
            if (!string.IsNullOrEmpty(_token))
            {
                using var testHttp = new HttpClient();
                testHttp.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", _token);
                try
                {
                    var testRes = await testHttp.GetAsync($"{_serverUrl}/v1/devices");
                    if (testRes.IsSuccessStatusCode)
                    {
                        InitializeSync(_deviceId, _token, _syncSalt);
                        return;
                    }
                }
                catch {}
            }

            // Register Windows client
            using var http = new HttpClient();
            var payload = new
            {
                label = "Windows 10 PC",
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
                    InitializeSync(dev.DeviceId, dev.Token, dev.SyncSalt);
                    SaveConfig();
                }
            }
            else
            {
                _statusMenuItem.Text = $"Status: Error (HTTP {res.StatusCode})";
            }
        }
        catch (Exception ex)
        {
            _statusMenuItem.Text = $"Status: Error ({ex.Message})";
        }
    }

    private void InitializeSync(string deviceId, string token, string syncSalt)
    {
        _deviceId = deviceId;
        _token = token;
        _syncSalt = syncSalt;

        _crypto = new ClipSyncCrypto(_syncPassphrase, syncSalt);
        _syncService = new WebSocketSyncService(_serverUrl, deviceId, token, _crypto);

        _syncService.OnStateChanged += state =>
        {
            _statusMenuItem.Text = $"Status: {state}";
            _notifyIcon.Text = $"ClipSync — {state}";
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

    private void TogglePause()
    {
        bool isPaused = !_clipboardMonitor.IsPaused;
        _clipboardMonitor.IsPaused = isPaused;
        if (_syncService != null) _syncService.IsPaused = isPaused;

        _pauseMenuItem.Text = isPaused ? "Resume Sync" : "Pause Sync";
        _statusMenuItem.Text = isPaused ? "Status: Paused" : "Status: Connected";
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
        var pairingForm = new PairingForm(_serverUrl, claimed =>
        {
            _serverUrl = pairingForm.ServerUrl;
            _syncService?.Stop();
            InitializeSync(claimed.DeviceId, claimed.Token, claimed.SyncSalt);
            SaveConfig();
        });
        pairingForm.Show();
    }

    private void ShowSettings()
    {
        var settingsForm = new SettingsForm(_serverUrl, _syncPassphrase, IsStartupEnabled(), (newUrl, newPass, startup) =>
        {
            bool urlChanged = !string.Equals(_serverUrl, newUrl, StringComparison.OrdinalIgnoreCase);

            _serverUrl = newUrl;
            _syncPassphrase = newPass;
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
        });
        settingsForm.ShowDialog();
    }

    private void ExitApp()
    {
        _notifyIcon.Visible = false;
        _syncService?.Stop();
        _clipboardMonitor.Close();
        Application.Exit();
    }
}
