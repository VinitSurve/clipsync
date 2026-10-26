using System;
using System.Drawing;
using System.Windows.Forms;

namespace ClipSync.Windows.Forms;

public sealed class SettingsForm : Form
{
    private readonly TextBox _serverUrlTextBox;
    private readonly TextBox _passphraseTextBox;
    private readonly CheckBox _startupCheckBox;
    private readonly Action<string, string, bool> _onSave;

    public SettingsForm(string currentServerUrl, string currentPassphrase, bool isStartupEnabled, Action<string, string, bool> onSave)
    {
        _onSave = onSave;

        Text = "ClipSync — Settings";
        Size = new Size(420, 310);
        StartPosition = FormStartPosition.CenterScreen;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        Font = new Font("Segoe UI", 9.5f);

        var serverLabel = new Label
        {
            Text = "Server URL:",
            Location = new Point(25, 18),
            AutoSize = true
        };

        _serverUrlTextBox = new TextBox
        {
            Text = currentServerUrl,
            Location = new Point(25, 42),
            Size = new Size(350, 26)
        };

        var cloudflareBtn = new Button
        {
            Text = "Cloudflare",
            Location = new Point(25, 75),
            Size = new Size(90, 26)
        };
        cloudflareBtn.Click += (_, _) => _serverUrlTextBox.Text = "https://clipsync-server-cloudflare.survevinit56.workers.dev";

        var localBtn = new Button
        {
            Text = "Localhost",
            Location = new Point(125, 75),
            Size = new Size(90, 26)
        };
        localBtn.Click += (_, _) => _serverUrlTextBox.Text = "http://localhost:8787";

        var keyLabel = new Label
        {
            Text = "Master Sync Key:",
            Location = new Point(25, 115),
            AutoSize = true
        };

        _passphraseTextBox = new TextBox
        {
            Text = currentPassphrase,
            Location = new Point(25, 138),
            Size = new Size(350, 26),
            UseSystemPasswordChar = true
        };

        _startupCheckBox = new CheckBox
        {
            Text = "Start ClipSync automatically when Windows logs in",
            Checked = isStartupEnabled,
            Location = new Point(25, 178),
            Size = new Size(350, 24)
        };

        var saveButton = new Button
        {
            Text = "Save & Connect",
            Location = new Point(25, 218),
            Size = new Size(350, 34),
            BackColor = Color.FromArgb(0, 120, 215),
            ForeColor = Color.White,
            FlatStyle = FlatStyle.Flat
        };
        saveButton.Click += (_, _) =>
        {
            string url = _serverUrlTextBox.Text.Trim().TrimEnd('/');
            string pass = _passphraseTextBox.Text;
            bool startup = _startupCheckBox.Checked;
            _onSave(url, pass, startup);
            Close();
        };

        Controls.Add(serverLabel);
        Controls.Add(_serverUrlTextBox);
        Controls.Add(cloudflareBtn);
        Controls.Add(localBtn);
        Controls.Add(keyLabel);
        Controls.Add(_passphraseTextBox);
        Controls.Add(_startupCheckBox);
        Controls.Add(saveButton);
    }
}
