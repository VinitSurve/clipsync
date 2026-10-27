using System;
using System.Drawing;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows.Forms;
using ClipSync.Windows.Models;

namespace ClipSync.Windows.Forms;

public sealed class SettingsForm : Form
{
    private TextBox _serverUrlTextBox = null!;
    private TextBox _passphraseTextBox = null!;
    private TextBox _deviceLabelTextBox = null!;
    private CheckBox _startupCheckBox = null!;
    private Label _startupStatusLabel = null!;
    private Label _statusIconLabel = null!;
    private Label _statusDetailLabel = null!;
    private Button _reconnectButton = null!;
    private Button _pauseButton = null!;
    private ListView _devicesListView = null!;
    private Label _devicesStatusLabel = null!;

    private readonly string _deviceId;
    private readonly string _token;
    private readonly Action<string, string, string, bool> _onSave;
    private readonly Action _onReconnect;
    private readonly Func<bool> _onTogglePause;
    private readonly Action _onOpenPairing;
    private bool _isPaused;

    public SettingsForm(
        string currentServerUrl,
        string currentPassphrase,
        string currentDeviceLabel,
        string currentDeviceId,
        string currentToken,
        string currentConnectionState,
        string lastStatusDetail,
        bool isStartupEnabled,
        bool isPaused,
        Action<string, string, string, bool> onSave,
        Action onReconnect,
        Func<bool> onTogglePause,
        Action onOpenPairing)
    {
        _deviceId = currentDeviceId;
        _token = currentToken;
        _onSave = onSave;
        _onReconnect = onReconnect;
        _onTogglePause = onTogglePause;
        _onOpenPairing = onOpenPairing;
        _isPaused = isPaused;

        Text = "ClipSync - Settings";
        Size = new Size(550, 580);
        StartPosition = FormStartPosition.CenterScreen;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        Font = new Font("Segoe UI", 9.25f);
        BackColor = Color.FromArgb(246, 248, 250);

        var tabControl = new TabControl
        {
            Dock = DockStyle.Fill,
            Padding = new Point(16, 8),
            Font = new Font("Segoe UI", 9.25f, FontStyle.Regular)
        };

        // Tab 1: Server & Connection
        var tabConnection = new TabPage("Server & Connection") { BackColor = Color.White };
        BuildConnectionTab(tabConnection, currentServerUrl, currentPassphrase, currentDeviceLabel, currentConnectionState, lastStatusDetail);
        tabControl.TabPages.Add(tabConnection);

        // Tab 2: Devices & Pairing
        var tabDevices = new TabPage("Devices & Pairing") { BackColor = Color.White };
        BuildDevicesTab(tabDevices, currentDeviceId, currentDeviceLabel);
        tabControl.TabPages.Add(tabDevices);

        // Tab 3: System & Storage
        var tabSystem = new TabPage("System & Storage") { BackColor = Color.White };
        BuildSystemTab(tabSystem, isStartupEnabled);
        tabControl.TabPages.Add(tabSystem);

        Controls.Add(tabControl);

        // Load devices list in background
        _ = LoadDevicesAsync(currentServerUrl);
    }

    private void BuildConnectionTab(TabPage page, string url, string pass, string label, string connState, string detail)
    {
        var panel = new Panel { Dock = DockStyle.Fill, AutoScroll = true, Padding = new Padding(14) };

        // 1. Live Connection Status GroupBox
        var grpStatus = new GroupBox
        {
            Text = "Live Connection Status",
            Location = new Point(14, 10),
            Size = new Size(490, 95),
            Font = new Font("Segoe UI", 9.25f, FontStyle.Bold)
        };

        _statusIconLabel = new Label
        {
            Location = new Point(16, 26),
            Size = new Size(190, 24),
            Font = new Font("Segoe UI", 10.5f, FontStyle.Bold)
        };
        UpdateConnectionStateUI(connState);

        _statusDetailLabel = new Label
        {
            Text = string.IsNullOrWhiteSpace(detail) ? "Ready" : detail,
            Location = new Point(16, 55),
            Size = new Size(270, 32),
            Font = new Font("Segoe UI", 8.5f, FontStyle.Regular),
            ForeColor = Color.DimGray
        };

        _reconnectButton = new Button
        {
            Text = "Reconnect",
            Location = new Point(295, 24),
            Size = new Size(88, 30),
            Font = new Font("Segoe UI", 9f, FontStyle.Regular),
            Cursor = Cursors.Hand
        };
        _reconnectButton.Click += (_, _) =>
        {
            _statusIconLabel.Text = "[Connecting...]";
            _statusIconLabel.ForeColor = Color.FromArgb(180, 130, 0);
            _statusDetailLabel.Text = "Attempting immediate reconnection...";
            _onReconnect();
        };

        _pauseButton = new Button
        {
            Text = _isPaused ? "Resume Sync" : "Pause Sync",
            Location = new Point(389, 24),
            Size = new Size(90, 30),
            Font = new Font("Segoe UI", 9f, FontStyle.Regular),
            Cursor = Cursors.Hand
        };
        _pauseButton.Click += (_, _) =>
        {
            _isPaused = _onTogglePause();
            _pauseButton.Text = _isPaused ? "Resume Sync" : "Pause Sync";
            UpdateConnectionStateUI(_isPaused ? "Paused" : "Connected");
        };

        grpStatus.Controls.Add(_statusIconLabel);
        grpStatus.Controls.Add(_statusDetailLabel);
        grpStatus.Controls.Add(_reconnectButton);
        grpStatus.Controls.Add(_pauseButton);

        // 2. Server Configuration GroupBox
        var grpServer = new GroupBox
        {
            Text = "Server Settings",
            Location = new Point(14, 115),
            Size = new Size(490, 310),
            Font = new Font("Segoe UI", 9.25f, FontStyle.Bold)
        };

        var lblUrl = new Label
        {
            Text = "Server URL (Cloudflare Worker or Local Server):",
            Location = new Point(16, 26),
            AutoSize = true,
            Font = new Font("Segoe UI", 9f, FontStyle.Regular)
        };

        _serverUrlTextBox = new TextBox
        {
            Text = url,
            Location = new Point(16, 48),
            Size = new Size(458, 25),
            Font = new Font("Segoe UI", 9.5f, FontStyle.Regular)
        };

        var btnCloudflare = new Button
        {
            Text = "Cloudflare Preset",
            Location = new Point(16, 78),
            Size = new Size(130, 26),
            Font = new Font("Segoe UI", 8.5f, FontStyle.Regular),
            Cursor = Cursors.Hand
        };
        btnCloudflare.Click += (_, _) => _serverUrlTextBox.Text = "https://clipsync-server-cloudflare.survevinit56.workers.dev";

        var btnLocal = new Button
        {
            Text = "Localhost Preset",
            Location = new Point(152, 78),
            Size = new Size(130, 26),
            Font = new Font("Segoe UI", 8.5f, FontStyle.Regular),
            Cursor = Cursors.Hand
        };
        btnLocal.Click += (_, _) => _serverUrlTextBox.Text = "http://localhost:8787";

        var lblKey = new Label
        {
            Text = "Master Sync Passphrase (E2E Encryption Key):",
            Location = new Point(16, 116),
            AutoSize = true,
            Font = new Font("Segoe UI", 9f, FontStyle.Regular)
        };

        _passphraseTextBox = new TextBox
        {
            Text = pass,
            Location = new Point(16, 138),
            Size = new Size(385, 25),
            UseSystemPasswordChar = true,
            Font = new Font("Segoe UI", 9.5f, FontStyle.Regular)
        };

        var chkShowPass = new CheckBox
        {
            Text = "Show",
            Location = new Point(408, 138),
            Size = new Size(66, 25),
            Font = new Font("Segoe UI", 8.5f, FontStyle.Regular)
        };
        chkShowPass.CheckedChanged += (_, _) => _passphraseTextBox.UseSystemPasswordChar = !chkShowPass.Checked;

        var lblDeviceName = new Label
        {
            Text = "Device Label (Identifier shown to paired devices):",
            Location = new Point(16, 176),
            AutoSize = true,
            Font = new Font("Segoe UI", 9f, FontStyle.Regular)
        };

        _deviceLabelTextBox = new TextBox
        {
            Text = string.IsNullOrWhiteSpace(label) ? "Windows 10 PC" : label,
            Location = new Point(16, 198),
            Size = new Size(458, 25),
            Font = new Font("Segoe UI", 9.5f, FontStyle.Regular)
        };

        var saveButton = new Button
        {
            Text = "Save & Connect",
            Location = new Point(16, 245),
            Size = new Size(458, 42),
            BackColor = Color.FromArgb(0, 120, 215),
            ForeColor = Color.White,
            FlatStyle = FlatStyle.Flat,
            Font = new Font("Segoe UI", 10f, FontStyle.Bold),
            Cursor = Cursors.Hand
        };
        saveButton.FlatAppearance.BorderSize = 0;
        saveButton.Click += (_, _) =>
        {
            string newUrl = _serverUrlTextBox.Text.Trim().TrimEnd('/');
            string newPass = _passphraseTextBox.Text;
            string newLabel = _deviceLabelTextBox.Text.Trim();
            if (string.IsNullOrEmpty(newLabel)) newLabel = "Windows 10 PC";
            bool startup = _startupCheckBox?.Checked ?? true;
            _onSave(newUrl, newPass, newLabel, startup);
            Close();
        };

        grpServer.Controls.Add(lblUrl);
        grpServer.Controls.Add(_serverUrlTextBox);
        grpServer.Controls.Add(btnCloudflare);
        grpServer.Controls.Add(btnLocal);
        grpServer.Controls.Add(lblKey);
        grpServer.Controls.Add(_passphraseTextBox);
        grpServer.Controls.Add(chkShowPass);
        grpServer.Controls.Add(lblDeviceName);
        grpServer.Controls.Add(_deviceLabelTextBox);
        grpServer.Controls.Add(saveButton);

        panel.Controls.Add(grpStatus);
        panel.Controls.Add(grpServer);
        page.Controls.Add(panel);
    }

    private void BuildDevicesTab(TabPage page, string deviceId, string deviceLabel)
    {
        var panel = new Panel { Dock = DockStyle.Fill, Padding = new Padding(14) };

        var grpThisDevice = new GroupBox
        {
            Text = "This PC",
            Location = new Point(14, 10),
            Size = new Size(490, 80),
            Font = new Font("Segoe UI", 9.25f, FontStyle.Bold)
        };

        string truncatedId = deviceId.Length > 20 ? deviceId[..18] + "..." : (string.IsNullOrEmpty(deviceId) ? "Unregistered" : deviceId);
        var lblId = new Label
        {
            Text = $"Device Label: {deviceLabel}\nDevice ID: {truncatedId}",
            Location = new Point(16, 26),
            Size = new Size(310, 42),
            Font = new Font("Segoe UI", 9f, FontStyle.Regular),
            ForeColor = Color.FromArgb(40, 40, 40)
        };

        var btnPair = new Button
        {
            Text = "Pair Another Device...",
            Location = new Point(332, 26),
            Size = new Size(146, 36),
            Font = new Font("Segoe UI", 8.75f, FontStyle.Regular),
            Cursor = Cursors.Hand
        };
        btnPair.Click += (_, _) =>
        {
            _onOpenPairing();
        };

        grpThisDevice.Controls.Add(lblId);
        grpThisDevice.Controls.Add(btnPair);

        var grpList = new GroupBox
        {
            Text = "Registered Sync Group Devices",
            Location = new Point(14, 100),
            Size = new Size(490, 325),
            Font = new Font("Segoe UI", 9.25f, FontStyle.Bold)
        };

        _devicesListView = new ListView
        {
            Location = new Point(16, 26),
            Size = new Size(458, 245),
            View = View.Details,
            FullRowSelect = true,
            GridLines = true,
            Font = new Font("Segoe UI", 9f, FontStyle.Regular)
        };
        _devicesListView.Columns.Add("Device Label", 170);
        _devicesListView.Columns.Add("Platform", 85);
        _devicesListView.Columns.Add("Last Seen", 110);
        _devicesListView.Columns.Add("Status", 80);

        _devicesStatusLabel = new Label
        {
            Text = "Loading paired devices...",
            Location = new Point(16, 283),
            Size = new Size(335, 24),
            Font = new Font("Segoe UI", 8.5f, FontStyle.Regular),
            ForeColor = Color.DimGray
        };

        var btnRefresh = new Button
        {
            Text = "Refresh List",
            Location = new Point(360, 278),
            Size = new Size(114, 30),
            Font = new Font("Segoe UI", 8.5f, FontStyle.Regular),
            Cursor = Cursors.Hand
        };
        btnRefresh.Click += (_, _) => _ = LoadDevicesAsync(_serverUrlTextBox.Text.Trim().TrimEnd('/'));

        grpList.Controls.Add(_devicesListView);
        grpList.Controls.Add(_devicesStatusLabel);
        grpList.Controls.Add(btnRefresh);

        panel.Controls.Add(grpThisDevice);
        panel.Controls.Add(grpList);
        page.Controls.Add(panel);
    }

    private void BuildSystemTab(TabPage page, bool isStartupEnabled)
    {
        var panel = new Panel { Dock = DockStyle.Fill, AutoScroll = true, Padding = new Padding(14) };

        // 1. Windows Startup GroupBox
        var grpStartup = new GroupBox
        {
            Text = "Windows Startup Configuration",
            Location = new Point(14, 10),
            Size = new Size(490, 115),
            Font = new Font("Segoe UI", 9.25f, FontStyle.Bold)
        };

        _startupCheckBox = new CheckBox
        {
            Text = "Start ClipSync automatically when Windows logs in",
            Checked = isStartupEnabled,
            Location = new Point(16, 26),
            Size = new Size(455, 24),
            Font = new Font("Segoe UI", 9f, FontStyle.Regular)
        };

        _startupStatusLabel = new Label
        {
            Text = isStartupEnabled ? "Status: Enabled in Windows Startup (Registry HKCU\\...\\Run)" : "Status: Disabled",
            Location = new Point(16, 53),
            Size = new Size(455, 20),
            Font = new Font("Segoe UI", 8.5f, FontStyle.Italic),
            ForeColor = isStartupEnabled ? Color.Green : Color.DimGray
        };

        var lblStartupHint = new Label
        {
            Text = "Runs ClipSync silently in your system tray upon Windows login without requiring terminal commands or console windows.",
            Location = new Point(16, 75),
            Size = new Size(455, 30),
            Font = new Font("Segoe UI", 8f, FontStyle.Regular),
            ForeColor = Color.Gray
        };

        _startupCheckBox.CheckedChanged += (_, _) =>
        {
            bool enabled = _startupCheckBox.Checked;
            _startupStatusLabel.Text = enabled ? "Status: Enabled in Windows Startup (Registry HKCU\\...\\Run)" : "Status: Disabled";
            _startupStatusLabel.ForeColor = enabled ? Color.Green : Color.DimGray;
        };

        grpStartup.Controls.Add(_startupCheckBox);
        grpStartup.Controls.Add(_startupStatusLabel);
        grpStartup.Controls.Add(lblStartupHint);

        // 2. File & Image Transfers Status GroupBox
        var grpStorage = new GroupBox
        {
            Text = "File & Image Transfers (Google Drive)",
            Location = new Point(14, 135),
            Size = new Size(490, 110),
            Font = new Font("Segoe UI", 9.25f, FontStyle.Bold)
        };

        var lblStorageStatus = new Label
        {
            Text = "Status: Not Available on Windows (Planned for Phase 2C)",
            Location = new Point(16, 26),
            Size = new Size(455, 20),
            Font = new Font("Segoe UI", 9f, FontStyle.Bold),
            ForeColor = Color.FromArgb(190, 100, 0)
        };

        var lblStorageDetail = new Label
        {
            Text = "Encrypted text clipboard sync is fully active. Temporary encrypted Google Drive storage (100 MB limit, 24h retention) for image and file sync is currently active on macOS and will be brought to Windows in Phase 2C.",
            Location = new Point(16, 48),
            Size = new Size(455, 52),
            Font = new Font("Segoe UI", 8.5f, FontStyle.Regular),
            ForeColor = Color.FromArgb(60, 60, 60)
        };

        grpStorage.Controls.Add(lblStorageStatus);
        grpStorage.Controls.Add(lblStorageDetail);

        // 3. About GroupBox
        var grpAbout = new GroupBox
        {
            Text = "About ClipSync",
            Location = new Point(14, 255),
            Size = new Size(490, 170),
            Font = new Font("Segoe UI", 9.25f, FontStyle.Bold)
        };

        var lblAboutText = new Label
        {
            Text = "ClipSync - Universal End-to-End Encrypted Clipboard\n" +
                   "Version: 1.0.0 (Native Windows 64-bit Standalone)\n" +
                   "Protocol Version: 1.0\n" +
                   "Cloud Backend: Cloudflare Workers + Durable Objects (100% Free Tier)\n" +
                   "Cryptography: AES-256-GCM, PBKDF2 100,000 iterations\n" +
                   "Security Guarantee: End-to-end encrypted. Zero plaintext stored on cloud servers.",
            Location = new Point(16, 25),
            Size = new Size(455, 135),
            Font = new Font("Segoe UI", 8.75f, FontStyle.Regular),
            ForeColor = Color.FromArgb(50, 50, 50)
        };

        grpAbout.Controls.Add(lblAboutText);

        panel.Controls.Add(grpStartup);
        panel.Controls.Add(grpStorage);
        panel.Controls.Add(grpAbout);
        page.Controls.Add(panel);
    }

    private void UpdateConnectionStateUI(string state)
    {
        switch (state.ToLowerInvariant())
        {
            case "connected":
                _statusIconLabel.Text = "[Connected]";
                _statusIconLabel.ForeColor = Color.FromArgb(16, 124, 65);
                break;
            case "connecting...":
            case "connecting":
                _statusIconLabel.Text = "[Connecting...]";
                _statusIconLabel.ForeColor = Color.FromArgb(180, 130, 0);
                break;
            case "paused":
                _statusIconLabel.Text = "[Paused]";
                _statusIconLabel.ForeColor = Color.FromArgb(200, 100, 0);
                break;
            default:
                _statusIconLabel.Text = "[Disconnected]";
                _statusIconLabel.ForeColor = Color.FromArgb(190, 30, 30);
                break;
        }
    }

    private async Task LoadDevicesAsync(string serverUrl)
    {
        if (string.IsNullOrEmpty(_token))
        {
            _devicesStatusLabel.Text = "Device not registered on server yet.";
            return;
        }

        try
        {
            _devicesStatusLabel.Text = "Fetching device list from server...";
            using var http = new HttpClient();
            http.Timeout = TimeSpan.FromSeconds(10);
            http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", _token);

            var res = await http.GetAsync($"{serverUrl}/v1/devices");
            if (res.IsSuccessStatusCode)
            {
                string json = await res.Content.ReadAsStringAsync();
                var list = JsonSerializer.Deserialize<DeviceListResponse>(json);
                _devicesListView.Items.Clear();

                if (list?.Devices != null)
                {
                    foreach (var d in list.Devices)
                    {
                        var item = new ListViewItem(d.Label);
                        item.SubItems.Add(d.Platform ?? "unknown");
                        item.SubItems.Add(d.LastSeen != null && DateTime.TryParse(d.LastSeen, out var dt) ? dt.ToLocalTime().ToString("MMM dd HH:mm") : "Online");
                        bool isCurrent = d.DeviceId == _deviceId || (d.IsCurrent.HasValue && d.IsCurrent.Value);
                        item.SubItems.Add(isCurrent ? "This PC" : "Active");
                        if (isCurrent)
                        {
                            item.Font = new Font(_devicesListView.Font, FontStyle.Bold);
                            item.ForeColor = Color.FromArgb(0, 120, 215);
                        }
                        _devicesListView.Items.Add(item);
                    }
                    _devicesStatusLabel.Text = $"{list.Devices.Length} device(s) connected to this sync group.";
                }
            }
            else
            {
                _devicesStatusLabel.Text = $"Could not fetch devices (HTTP {(int)res.StatusCode}).";
            }
        }
        catch (Exception ex)
        {
            _devicesStatusLabel.Text = $"Device refresh failed: {ex.Message}";
        }
    }
}
