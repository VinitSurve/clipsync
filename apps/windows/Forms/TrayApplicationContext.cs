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

    private string _serverUrl = "http://localhost:8787";
    private string _syncPassphrase = "clipsync-master-sync-passphrase";
    private string _deviceId = "";
    private string _token = "";
    private string _syncSalt = "";

    public TrayApplicationContext()
    {
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
        contextMenu.Items.Add(new ToolStripSeparator());
        contextMenu.Items.Add("Exit ClipSync", null, (_, _) => ExitApp());

        _notifyIcon = new NotifyIcon
        {
            Icon = SystemIcons.Application,
            ContextMenuStrip = contextMenu,
            Text = "ClipSync — Universal Clipboard",
            Visible = true
        };
        _notifyIcon.DoubleClick += (_, _) => ShowHistory();

        _clipboardMonitor.OnNewLocalCopy += OnNewLocalCopy;

        _ = BootstrapOrPairAsync();
    }

    private async Task BootstrapOrPairAsync()
    {
        try
        {
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
                }
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
            _syncService?.Stop();
            InitializeSync(claimed.DeviceId, claimed.Token, claimed.SyncSalt);
        });
        pairingForm.Show();
    }

    private void ExitApp()
    {
        _notifyIcon.Visible = false;
        _syncService?.Stop();
        _clipboardMonitor.Close();
        Application.Exit();
    }
}
