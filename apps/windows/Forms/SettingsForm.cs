using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows.Forms;
using ClipSync.Windows.Models;
using ClipSync.Windows.Services;

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

    // Google Drive controls
    private TextBox _googleClientIdTextBox = null!;
    private TextBox _googleClientSecretTextBox = null!;
    private Label _googleSecretStatusLabel = null!;
    private Button _googleConnectButton = null!;
    private Button _googleDisconnectButton = null!;
    private Button _googleImportButton = null!;
    private Label _googleStatusLabel = null!;

    // Transfers controls
    private ListView _transfersListView = null!;
    private Button _retryTransferButton = null!;
    private Button _viewDetailsButton = null!;
    private Button _cancelTransferButton = null!;
    private Button _clearCompletedTransfersButton = null!;
    private Button _openDownloadsButton = null!;
    private Label _transfersStatusLabel = null!;

    private readonly string _deviceId;
    private readonly string _token;
    private readonly Action<string, string, string, bool> _onSave;
    private readonly Action _onReconnect;
    private readonly Func<bool> _onTogglePause;
    private readonly Action _onOpenPairing;
    private readonly GoogleDriveService? _googleDrive;
    private readonly TransferManager? _transferManager;
    private readonly string _appVersion;
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
        Action onOpenPairing,
        GoogleDriveService? googleDrive = null,
        TransferManager? transferManager = null)
    {
        _deviceId = currentDeviceId;
        _token = currentToken;
        _onSave = onSave;
        _onReconnect = onReconnect;
        _onTogglePause = onTogglePause;
        _onOpenPairing = onOpenPairing;
        _googleDrive = googleDrive;
        _transferManager = transferManager;
        _isPaused = isPaused;

        _appVersion = typeof(SettingsForm).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
            .InformationalVersion ?? "1.0.0";

        Text = $"ClipSync - Settings (v{_appVersion})";
        Size = new Size(580, 640);
        StartPosition = FormStartPosition.CenterScreen;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        Font = new Font("Segoe UI", 9.25f);
        BackColor = Color.FromArgb(246, 248, 250);

        var tabControl = new TabControl
        {
            Dock = DockStyle.Fill,
            Padding = new Point(14, 8),
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

        // Tab 3: Google Drive & System
        var tabSystem = new TabPage("Google Drive & System") { BackColor = Color.White };
        BuildSystemTab(tabSystem, isStartupEnabled);
        tabControl.TabPages.Add(tabSystem);

        // Tab 4: Transfers
        var tabTransfers = new TabPage("Transfers") { BackColor = Color.White };
        BuildTransfersTab(tabTransfers);
        tabControl.TabPages.Add(tabTransfers);

        Controls.Add(tabControl);

        // Load devices list in background
        _ = LoadDevicesAsync(currentServerUrl);

        // Wire Google Drive status update
        if (_googleDrive != null)
        {
            _googleDrive.OnAuthStatusChanged += () =>
            {
                if (IsHandleCreated && !IsDisposed)
                {
                    BeginInvoke(new Action(UpdateGoogleDriveUI));
                }
            };
            UpdateGoogleDriveUI();
        }

        // Wire Transfers update
        if (_transferManager != null)
        {
            _transferManager.OnTransfersChanged += () =>
            {
                if (IsHandleCreated && !IsDisposed)
                {
                    BeginInvoke(new Action(PopulateTransfersList));
                }
            };
            PopulateTransfersList();
        }
    }

    private void BuildConnectionTab(TabPage page, string url, string pass, string label, string connState, string detail)
    {
        var panel = new Panel { Dock = DockStyle.Fill, AutoScroll = true, Padding = new Padding(14) };

        // 1. Live Connection Status GroupBox
        var grpStatus = new GroupBox
        {
            Text = "Live Connection Status",
            Location = new Point(14, 10),
            Size = new Size(520, 95),
            Font = new Font("Segoe UI", 9.25f, FontStyle.Bold)
        };

        _statusIconLabel = new Label
        {
            Location = new Point(16, 26),
            Size = new Size(200, 24),
            Font = new Font("Segoe UI", 10.5f, FontStyle.Bold)
        };
        UpdateConnectionStateUI(connState);

        _statusDetailLabel = new Label
        {
            Text = string.IsNullOrWhiteSpace(detail) ? "Ready" : detail,
            Location = new Point(16, 55),
            Size = new Size(280, 32),
            Font = new Font("Segoe UI", 8.5f, FontStyle.Regular),
            ForeColor = Color.DimGray
        };

        _reconnectButton = new Button
        {
            Text = "Reconnect",
            Location = new Point(310, 24),
            Size = new Size(92, 30),
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
            Location = new Point(410, 24),
            Size = new Size(96, 30),
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
            Size = new Size(520, 340),
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
            Size = new Size(488, 25),
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
            Size = new Size(415, 25),
            UseSystemPasswordChar = true,
            Font = new Font("Segoe UI", 9.5f, FontStyle.Regular)
        };

        var chkShowPass = new CheckBox
        {
            Text = "Show",
            Location = new Point(438, 138),
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
            Size = new Size(488, 25),
            Font = new Font("Segoe UI", 9.5f, FontStyle.Regular)
        };

        var saveButton = new Button
        {
            Text = "Save & Connect",
            Location = new Point(16, 255),
            Size = new Size(488, 42),
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
            Size = new Size(520, 80),
            Font = new Font("Segoe UI", 9.25f, FontStyle.Bold)
        };

        string truncatedId = deviceId.Length > 20 ? deviceId[..18] + "..." : (string.IsNullOrEmpty(deviceId) ? "Unregistered" : deviceId);
        var lblId = new Label
        {
            Text = $"Device Label: {deviceLabel}\nDevice ID: {truncatedId}",
            Location = new Point(16, 26),
            Size = new Size(330, 42),
            Font = new Font("Segoe UI", 9f, FontStyle.Regular),
            ForeColor = Color.FromArgb(40, 40, 40)
        };

        var btnPair = new Button
        {
            Text = "Pair Another Device...",
            Location = new Point(356, 26),
            Size = new Size(150, 36),
            Font = new Font("Segoe UI", 8.75f, FontStyle.Regular),
            Cursor = Cursors.Hand
        };
        btnPair.Click += (_, _) => _onOpenPairing();

        grpThisDevice.Controls.Add(lblId);
        grpThisDevice.Controls.Add(btnPair);

        var grpList = new GroupBox
        {
            Text = "Registered Sync Group Devices",
            Location = new Point(14, 100),
            Size = new Size(520, 360),
            Font = new Font("Segoe UI", 9.25f, FontStyle.Bold)
        };

        _devicesListView = new ListView
        {
            Location = new Point(16, 26),
            Size = new Size(488, 280),
            View = View.Details,
            FullRowSelect = true,
            GridLines = true,
            Font = new Font("Segoe UI", 9f, FontStyle.Regular)
        };
        _devicesListView.Columns.Add("Device Label", 180);
        _devicesListView.Columns.Add("Platform", 90);
        _devicesListView.Columns.Add("Last Seen", 120);
        _devicesListView.Columns.Add("Status", 80);

        _devicesStatusLabel = new Label
        {
            Text = "Loading paired devices...",
            Location = new Point(16, 318),
            Size = new Size(360, 24),
            Font = new Font("Segoe UI", 8.5f, FontStyle.Regular),
            ForeColor = Color.DimGray
        };

        var btnRefresh = new Button
        {
            Text = "Refresh List",
            Location = new Point(388, 313),
            Size = new Size(116, 30),
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

        // 1. Google Drive Integration GroupBox
        var grpDrive = new GroupBox
        {
            Text = "Google Drive (Encrypted Image & File Storage)",
            Location = new Point(14, 10),
            Size = new Size(520, 245),
            Font = new Font("Segoe UI", 9.25f, FontStyle.Bold)
        };

        var lblClientId = new Label
        {
            Text = "Google OAuth Client ID (Desktop App):",
            Location = new Point(16, 26),
            AutoSize = true,
            Font = new Font("Segoe UI", 8.75f, FontStyle.Regular)
        };

        _googleClientIdTextBox = new TextBox
        {
            Text = _googleDrive?.ClientId ?? "",
            Location = new Point(16, 46),
            Size = new Size(370, 25),
            Font = new Font("Segoe UI", 9f, FontStyle.Regular)
        };
        _googleClientIdTextBox.TextChanged += (_, _) =>
        {
            if (_googleDrive != null) _googleDrive.ClientId = _googleClientIdTextBox.Text;
        };

        _googleImportButton = new Button
        {
            Text = "Import JSON...",
            Location = new Point(394, 44),
            Size = new Size(110, 27),
            Font = new Font("Segoe UI", 8.5f, FontStyle.Regular),
            Cursor = Cursors.Hand
        };
        _googleImportButton.Click += (_, _) => ImportCredentialsJson();

        var lblClientSecret = new Label
        {
            Text = "Client Secret (Required for Google Desktop App):",
            Location = new Point(16, 78),
            AutoSize = true,
            Font = new Font("Segoe UI", 8.75f, FontStyle.Regular)
        };

        _googleClientSecretTextBox = new TextBox
        {
            Text = _googleDrive?.ClientSecret ?? "",
            Location = new Point(16, 98),
            Size = new Size(370, 25),
            UseSystemPasswordChar = true,
            Font = new Font("Segoe UI", 9f, FontStyle.Regular)
        };
        _googleClientSecretTextBox.TextChanged += (_, _) =>
        {
            if (_googleDrive != null)
            {
                _googleDrive.ClientSecret = _googleClientSecretTextBox.Text;
                UpdateGoogleDriveUI();
            }
        };

        _googleSecretStatusLabel = new Label
        {
            Text = (_googleDrive?.HasClientSecret ?? false)
                ? "[OK] Client Secret configured (Stored securely in DPAPI)"
                : "[Required] Client Secret missing. Paste above or click 'Import JSON...'.",
            Location = new Point(16, 126),
            Size = new Size(488, 18),
            Font = new Font("Segoe UI", 8.25f, FontStyle.Regular),
            ForeColor = (_googleDrive?.HasClientSecret ?? false) ? Color.FromArgb(16, 124, 65) : Color.FromArgb(190, 100, 0)
        };

        _googleConnectButton = new Button
        {
            Text = "Connect Google Drive",
            Location = new Point(16, 150),
            Size = new Size(170, 34),
            BackColor = Color.FromArgb(16, 124, 65),
            ForeColor = Color.White,
            FlatStyle = FlatStyle.Flat,
            Font = new Font("Segoe UI", 9f, FontStyle.Bold),
            Cursor = Cursors.Hand
        };
        _googleConnectButton.FlatAppearance.BorderSize = 0;
        _googleConnectButton.Click += async (_, _) =>
        {
            if (_googleDrive == null) return;

            if (string.IsNullOrWhiteSpace(_googleDrive.ClientId))
            {
                MessageBox.Show(this, "Please enter your Google Client ID or click 'Import JSON...'.", "ClipSync", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            if (!_googleDrive.HasClientSecret)
            {
                var dr = MessageBox.Show(this,
                    "No Google Client Secret is configured.\n\n" +
                    "Google Cloud Desktop App clients require both a Client ID and a Client Secret during token exchange. Without it, Google returns 'client_secret is missing'.\n\n" +
                    "Would you like to import your Google credentials.json or client_secret_*.json now?",
                    "Client Secret Required",
                    MessageBoxButtons.YesNo,
                    MessageBoxIcon.Information);

                if (dr == DialogResult.Yes)
                {
                    ImportCredentialsJson();
                    if (!_googleDrive.HasClientSecret)
                    {
                        return;
                    }
                }
            }

            _googleConnectButton.Enabled = false;
            _googleConnectButton.Text = "Connecting...";
            await _googleDrive.StartAuthorizationAsync();
            _googleConnectButton.Enabled = true;
            _googleConnectButton.Text = "Connect Google Drive";
            UpdateGoogleDriveUI();
        };

        _googleDisconnectButton = new Button
        {
            Text = "Disconnect",
            Location = new Point(194, 150),
            Size = new Size(100, 34),
            Font = new Font("Segoe UI", 9f, FontStyle.Regular),
            Cursor = Cursors.Hand
        };
        _googleDisconnectButton.Click += (_, _) =>
        {
            _googleDrive?.Disconnect();
            UpdateGoogleDriveUI();
        };

        _googleStatusLabel = new Label
        {
            Text = "Status: Disconnected",
            Location = new Point(16, 192),
            Size = new Size(488, 45),
            Font = new Font("Segoe UI", 8.75f, FontStyle.Regular),
            ForeColor = Color.DimGray
        };

        grpDrive.Controls.Add(lblClientId);
        grpDrive.Controls.Add(_googleClientIdTextBox);
        grpDrive.Controls.Add(_googleImportButton);
        grpDrive.Controls.Add(lblClientSecret);
        grpDrive.Controls.Add(_googleClientSecretTextBox);
        grpDrive.Controls.Add(_googleSecretStatusLabel);
        grpDrive.Controls.Add(_googleConnectButton);
        grpDrive.Controls.Add(_googleDisconnectButton);
        grpDrive.Controls.Add(_googleStatusLabel);

        // 2. Windows Startup GroupBox
        var grpStartup = new GroupBox
        {
            Text = "Windows Startup Configuration",
            Location = new Point(14, 265),
            Size = new Size(520, 105),
            Font = new Font("Segoe UI", 9.25f, FontStyle.Bold)
        };

        _startupCheckBox = new CheckBox
        {
            Text = "Start ClipSync automatically when Windows logs in",
            Checked = isStartupEnabled,
            Location = new Point(16, 26),
            Size = new Size(485, 24),
            Font = new Font("Segoe UI", 9f, FontStyle.Regular)
        };

        _startupStatusLabel = new Label
        {
            Text = isStartupEnabled ? "Status: Enabled in Windows Startup (Registry HKCU\\...\\Run)" : "Status: Disabled",
            Location = new Point(16, 53),
            Size = new Size(485, 20),
            Font = new Font("Segoe UI", 8.5f, FontStyle.Italic),
            ForeColor = isStartupEnabled ? Color.Green : Color.DimGray
        };

        var lblStartupHint = new Label
        {
            Text = "Runs ClipSync silently in your system tray upon Windows login without terminal commands.",
            Location = new Point(16, 75),
            Size = new Size(485, 22),
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

        // 3. About GroupBox
        var grpAbout = new GroupBox
        {
            Text = "About ClipSync",
            Location = new Point(14, 380),
            Size = new Size(520, 160),
            Font = new Font("Segoe UI", 9.25f, FontStyle.Bold)
        };

        var lblAboutText = new Label
        {
            Text = "ClipSync - Universal End-to-End Encrypted Clipboard\n" +
                   $"Version: {_appVersion} (Native Windows 64-bit Standalone)\n" +
                   "Protocol Version: 1.0 | Phase 2C Image & File Sync Active\n" +
                   "Storage: Google Drive (100% Free, 100 MB max per item, 24h retention)\n" +
                   "Cloud Backend: Cloudflare Workers + Durable Objects (100% Free Tier)\n" +
                   "Cryptography: AES-256-GCM, PBKDF2 100,000 iterations\n" +
                   "Security: End-to-end encrypted. Zero plaintext stored on cloud servers.",
            Location = new Point(16, 25),
            Size = new Size(488, 125),
            Font = new Font("Segoe UI", 8.75f, FontStyle.Regular),
            ForeColor = Color.FromArgb(50, 50, 50)
        };

        grpAbout.Controls.Add(lblAboutText);

        panel.Controls.Add(grpDrive);
        panel.Controls.Add(grpStartup);
        panel.Controls.Add(grpAbout);
        page.Controls.Add(panel);
    }

    private void BuildTransfersTab(TabPage page)
    {
        var panel = new Panel { Dock = DockStyle.Fill, Padding = new Padding(14) };

        var grpTransfers = new GroupBox
        {
            Text = "Active & Recent Transfers",
            Location = new Point(14, 10),
            Size = new Size(520, 480),
            Font = new Font("Segoe UI", 9.25f, FontStyle.Bold)
        };

        _transfersListView = new ListView
        {
            Location = new Point(16, 26),
            Size = new Size(488, 350),
            View = View.Details,
            FullRowSelect = true,
            GridLines = true,
            Font = new Font("Segoe UI", 9f, FontStyle.Regular)
        };
        _transfersListView.Columns.Add("File Name", 145);
        _transfersListView.Columns.Add("Type", 55);
        _transfersListView.Columns.Add("Size", 65);
        _transfersListView.Columns.Add("Direction", 65);
        _transfersListView.Columns.Add("Status", 215);
        _transfersListView.SelectedIndexChanged += (_, _) => OnTransferSelectionChanged();
        _transfersListView.DoubleClick += (_, _) => ShowSelectedTransferDetails();

        _transfersStatusLabel = new Label
        {
            Text = "No transfers recorded yet.",
            Location = new Point(16, 386),
            Size = new Size(488, 24),
            Font = new Font("Segoe UI", 8.5f, FontStyle.Regular),
            ForeColor = Color.DimGray
        };

        _retryTransferButton = new Button
        {
            Text = "Retry",
            Location = new Point(16, 420),
            Size = new Size(70, 30),
            Font = new Font("Segoe UI", 8.75f, FontStyle.Regular),
            Cursor = Cursors.Hand
        };
        _retryTransferButton.Click += (_, _) =>
        {
            if (_transfersListView.SelectedItems.Count > 0)
            {
                string? tid = _transfersListView.SelectedItems[0].Tag as string;
                if (!string.IsNullOrEmpty(tid)) _transferManager?.RetryTransfer(tid);
            }
        };

        _viewDetailsButton = new Button
        {
            Text = "Details...",
            Location = new Point(92, 420),
            Size = new Size(82, 30),
            Font = new Font("Segoe UI", 8.75f, FontStyle.Regular),
            Cursor = Cursors.Hand
        };
        _viewDetailsButton.Click += (_, _) => ShowSelectedTransferDetails();

        _cancelTransferButton = new Button
        {
            Text = "Cancel",
            Location = new Point(180, 420),
            Size = new Size(72, 30),
            Font = new Font("Segoe UI", 8.75f, FontStyle.Regular),
            Cursor = Cursors.Hand
        };
        _cancelTransferButton.Click += (_, _) =>
        {
            if (_transfersListView.SelectedItems.Count > 0)
            {
                string? tid = _transfersListView.SelectedItems[0].Tag as string;
                if (!string.IsNullOrEmpty(tid)) _transferManager?.CancelTransfer(tid);
            }
        };

        _clearCompletedTransfersButton = new Button
        {
            Text = "Clear Finished",
            Location = new Point(258, 420),
            Size = new Size(104, 30),
            Font = new Font("Segoe UI", 8.75f, FontStyle.Regular),
            Cursor = Cursors.Hand
        };
        _clearCompletedTransfersButton.Click += (_, _) => _transferManager?.ClearCompleted();

        _openDownloadsButton = new Button
        {
            Text = "Open Downloads",
            Location = new Point(368, 420),
            Size = new Size(136, 30),
            Font = new Font("Segoe UI", 8.75f, FontStyle.Regular),
            Cursor = Cursors.Hand
        };
        _openDownloadsButton.Click += (_, _) =>
        {
            string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads", "ClipSync");
            Directory.CreateDirectory(dir);
            Process.Start(new ProcessStartInfo { FileName = dir, UseShellExecute = true });
        };

        grpTransfers.Controls.Add(_transfersListView);
        grpTransfers.Controls.Add(_transfersStatusLabel);
        grpTransfers.Controls.Add(_retryTransferButton);
        grpTransfers.Controls.Add(_viewDetailsButton);
        grpTransfers.Controls.Add(_cancelTransferButton);
        grpTransfers.Controls.Add(_clearCompletedTransfersButton);
        grpTransfers.Controls.Add(_openDownloadsButton);

        panel.Controls.Add(grpTransfers);
        page.Controls.Add(panel);
    }

    private void OnTransferSelectionChanged()
    {
        if (_transfersListView.SelectedItems.Count > 0 && _transferManager != null)
        {
            string? tid = _transfersListView.SelectedItems[0].Tag as string;
            var record = _transferManager.Transfers.FirstOrDefault(t => t.Id == tid);
            if (record != null)
            {
                _transfersStatusLabel.Text = $"{record.FileName}: {record.StatusText}";
                _transfersStatusLabel.ForeColor = record.Status == TransferStatus.Failed
                    ? Color.FromArgb(190, 30, 30)
                    : (record.Status == TransferStatus.Delivered ? Color.FromArgb(16, 124, 65) : Color.DimGray);
                return;
            }
        }
        _transfersStatusLabel.Text = $"Showing {_transferManager?.Transfers.Count ?? 0} transfer(s). Limit: 100 MB per item.";
        _transfersStatusLabel.ForeColor = Color.DimGray;
    }

    private void ShowSelectedTransferDetails()
    {
        if (_transfersListView.SelectedItems.Count == 0 || _transferManager == null)
        {
            MessageBox.Show(this, "Please select a transfer from the list first.", "ClipSync Transfers", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        string? tid = _transfersListView.SelectedItems[0].Tag as string;
        if (string.IsNullOrEmpty(tid)) return;
        var record = _transferManager.Transfers.FirstOrDefault(t => t.Id == tid);
        if (record == null) return;

        using var detailsDialog = new Form
        {
            Text = $"Transfer Details - {record.FileName}",
            Size = new Size(580, 460),
            StartPosition = FormStartPosition.CenterParent,
            FormBorderStyle = FormBorderStyle.FixedDialog,
            MaximizeBox = false,
            MinimizeBox = false,
            ShowInTaskbar = false,
            BackColor = Color.White,
            Font = new Font("Segoe UI", 9f, FontStyle.Regular)
        };

        var txtDetails = new TextBox
        {
            Multiline = true,
            ReadOnly = true,
            ScrollBars = ScrollBars.Vertical,
            Location = new Point(16, 16),
            Size = new Size(532, 340),
            BackColor = Color.FromArgb(248, 249, 250),
            Font = new Font("Consolas", 8.75f, FontStyle.Regular)
        };

        var sb = new StringBuilder();
        sb.AppendLine($"Transfer ID:      {record.Id}");
        sb.AppendLine($"File Name:        {record.FileName}");
        sb.AppendLine($"Direction:        {(record.IsOutgoing ? "Outgoing" : "Incoming")}");
        sb.AppendLine($"Size:             {record.FormattedSize} ({record.FileSizeBytes} bytes)");
        sb.AppendLine($"MIME Type:        {record.MimeType}");
        sb.AppendLine($"Status:           {record.Status.ToUpperInvariant()}");
        sb.AppendLine($"Status Detail:    {record.StatusText}");
        sb.AppendLine($"Created:          {record.CreatedAt:yyyy-MM-dd HH:mm:ss} UTC");
        sb.AppendLine($"Expires:          {record.ExpiresAt:yyyy-MM-dd HH:mm:ss} UTC");
        if (!string.IsNullOrEmpty(record.DriveFileId))
        {
            sb.AppendLine($"Drive File ID:    {record.DriveFileId}");
        }
        if (!string.IsNullOrEmpty(record.LocalPath))
        {
            sb.AppendLine($"Local Path:       {record.LocalPath}");
        }
        sb.AppendLine(new string('-', 60));

        if (!string.IsNullOrEmpty(record.ErrorDetails))
        {
            sb.AppendLine("DIAGNOSTIC ERROR DETAILS:");
            sb.AppendLine(record.ErrorDetails);
            sb.AppendLine(new string('-', 60));
            sb.AppendLine("REMEDIATION TIPS:");
            if (record.ErrorDetails.Contains("404"))
            {
                sb.AppendLine("- File Not Found (HTTP 404): The temporary Drive file may have expired (>24h)");
                sb.AppendLine("  or was uploaded using a different Google account.");
                sb.AppendLine("- Verify both Mac and Windows are signed into the exact same Google account.");
            }
            else if (record.ErrorDetails.Contains("Not connected"))
            {
                sb.AppendLine("- Connect Google Drive in Settings -> Google Drive & System tab,");
                sb.AppendLine("  then click 'Retry' to resume downloading.");
            }
            else if (record.ErrorDetails.Contains("Integrity check failed"))
            {
                sb.AppendLine("- The decrypted content hash does not match the sender hash.");
                sb.AppendLine("- Verify that the encryption passphrase matches between devices.");
            }
            else
            {
                sb.AppendLine("- Click 'Retry' below to re-attempt this transfer.");
            }
        }
        else
        {
            sb.AppendLine("No error recorded for this transfer.");
        }

        txtDetails.Text = sb.ToString();

        var btnClose = new Button
        {
            Text = "Close",
            DialogResult = DialogResult.OK,
            Location = new Point(448, 372),
            Size = new Size(100, 32),
            Cursor = Cursors.Hand
        };

        var btnRetryModal = new Button
        {
            Text = "Retry Transfer",
            Location = new Point(16, 372),
            Size = new Size(120, 32),
            Enabled = record.CanRetry,
            Cursor = Cursors.Hand
        };
        btnRetryModal.Click += (_, _) =>
        {
            _transferManager.RetryTransfer(record.Id);
            detailsDialog.Close();
        };

        detailsDialog.Controls.Add(txtDetails);
        detailsDialog.Controls.Add(btnClose);
        detailsDialog.Controls.Add(btnRetryModal);
        detailsDialog.AcceptButton = btnClose;

        detailsDialog.ShowDialog(this);
    }

    private void UpdateGoogleDriveUI()
    {
        if (_googleDrive == null) return;

        if (_googleSecretStatusLabel != null)
        {
            if (_googleDrive.HasClientSecret)
            {
                _googleSecretStatusLabel.Text = "[OK] Client Secret configured (Stored securely in DPAPI)";
                _googleSecretStatusLabel.ForeColor = Color.FromArgb(16, 124, 65);
            }
            else
            {
                _googleSecretStatusLabel.Text = "[Required] Client Secret missing. Paste above or click 'Import JSON...'.";
                _googleSecretStatusLabel.ForeColor = Color.FromArgb(190, 100, 0);
            }
        }

        switch (_googleDrive.Status)
        {
            case GoogleDriveAuthStatus.Connected:
                _googleStatusLabel.Text = $"[Connected] Google Account: {_googleDrive.AccountEmail ?? "Authorized"}";
                _googleStatusLabel.ForeColor = Color.FromArgb(16, 124, 65);
                _googleConnectButton.Enabled = false;
                _googleDisconnectButton.Enabled = true;
                break;
            case GoogleDriveAuthStatus.Connecting:
                _googleStatusLabel.Text = "[Connecting...] Waiting for browser authorization...";
                _googleStatusLabel.ForeColor = Color.FromArgb(180, 130, 0);
                _googleConnectButton.Enabled = false;
                _googleDisconnectButton.Enabled = true;
                break;
            case GoogleDriveAuthStatus.Error:
                _googleStatusLabel.Text = $"[Error] {_googleDrive.LastErrorMessage ?? "Authorization failed"}";
                _googleStatusLabel.ForeColor = Color.FromArgb(190, 30, 30);
                _googleConnectButton.Enabled = true;
                _googleDisconnectButton.Enabled = false;
                break;
            default:
                _googleStatusLabel.Text = "[Disconnected] Connect to enable encrypted image & file transfers";
                _googleStatusLabel.ForeColor = Color.DimGray;
                _googleConnectButton.Enabled = true;
                _googleDisconnectButton.Enabled = false;
                break;
        }
    }

    private void ImportCredentialsJson()
    {
        using var ofd = new OpenFileDialog
        {
            Title = "Select Google Cloud credentials.json or client_secret_*.json",
            Filter = "JSON Files (*.json)|*.json|All Files (*.*)|*.*"
        };

        if (ofd.ShowDialog(this) == DialogResult.OK)
        {
            if (_googleDrive != null)
            {
                var (cid, secret) = _googleDrive.ImportCredentialsFile(ofd.FileName);
                if (!string.IsNullOrEmpty(cid))
                {
                    _googleClientIdTextBox.Text = cid;
                    if (!string.IsNullOrEmpty(secret))
                    {
                        _googleClientSecretTextBox.Text = secret;
                    }
                    UpdateGoogleDriveUI();

                    string previewCid = cid.Length > 20 ? cid[..20] + "..." : cid;
                    string secretStatus = !string.IsNullOrEmpty(secret) ? "Configured & saved to DPAPI" : "None found in file";
                    MessageBox.Show(this,
                        $"Credentials successfully imported!\n\nClient ID: {previewCid}\nClient Secret: {secretStatus}",
                        "ClipSync - Credentials Imported",
                        MessageBoxButtons.OK,
                        MessageBoxIcon.Information);
                }
                else
                {
                    MessageBox.Show(this, "Could not find 'client_id' in the selected file.\nEnsure the file contains an 'installed' or 'web' configuration.", "ClipSync", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                }
            }
        }
    }

    private void PopulateTransfersList()
    {
        if (_transferManager == null || _transfersListView == null) return;

        _transfersListView.BeginUpdate();
        _transfersListView.Items.Clear();

        var transfers = _transferManager.Transfers;
        if (transfers.Count == 0)
        {
            _transfersStatusLabel.Text = "No transfers recorded yet.";
        }
        else
        {
            _transfersStatusLabel.Text = $"Showing {transfers.Count} transfer(s). Limit: 100 MB per item.";
        }

        foreach (var t in transfers)
        {
            string typeDesc = t.MimeType.StartsWith("image/") ? "Image" : (t.MimeType == "application/zip" ? "Archive" : "File");
            string direction = t.IsOutgoing ? "Outgoing" : "Incoming";
            string statusDesc = t.StatusText;
            if (t.Status is TransferStatus.Uploading or TransferStatus.Downloading)
            {
                statusDesc = $"{t.StatusText} ({t.Progress:P0})";
            }

            var item = new ListViewItem(t.FileName);
            item.SubItems.Add(typeDesc);
            item.SubItems.Add(t.FormattedSize);
            item.SubItems.Add(direction);
            item.SubItems.Add(statusDesc);
            item.Tag = t.Id;

            if (t.Status == TransferStatus.Delivered) item.ForeColor = Color.FromArgb(16, 124, 65);
            else if (t.Status == TransferStatus.Failed) item.ForeColor = Color.FromArgb(190, 30, 30);
            else if (t.Status is TransferStatus.Uploading or TransferStatus.Downloading) item.ForeColor = Color.FromArgb(0, 102, 204);

            _transfersListView.Items.Add(item);
        }

        _transfersListView.EndUpdate();
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
            using var client = new HttpClient();
            using var req = new HttpRequestMessage(HttpMethod.Get, $"{serverUrl}/v1/devices");
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _token);

            using var res = await client.SendAsync(req);
            if (!res.IsSuccessStatusCode)
            {
                _devicesStatusLabel.Text = $"Failed to load devices (HTTP {res.StatusCode})";
                return;
            }

            string json = await res.Content.ReadAsStringAsync();
            var listResp = JsonSerializer.Deserialize<DeviceListResponse>(json);

            if (listResp == null || listResp.Devices == null)
            {
                _devicesStatusLabel.Text = "No paired devices found.";
                return;
            }

            _devicesListView.Items.Clear();
            foreach (var dev in listResp.Devices)
            {
                string isCurrent = (dev.IsCurrent ?? false) || dev.DeviceId == _deviceId ? " (This PC)" : "";
                var item = new ListViewItem(dev.Label + isCurrent);
                item.SubItems.Add(dev.Platform.ToUpperInvariant());
                item.SubItems.Add(string.IsNullOrEmpty(dev.LastSeen) ? "Active" : dev.LastSeen);
                item.SubItems.Add((dev.IsCurrent ?? false) || dev.DeviceId == _deviceId ? "Current" : "Paired");
                _devicesListView.Items.Add(item);
            }

            _devicesStatusLabel.Text = $"Total Paired Devices: {listResp.Devices.Length}";
        }
        catch (Exception ex)
        {
            _devicesStatusLabel.Text = $"Network error: {ex.Message}";
        }
    }
}
