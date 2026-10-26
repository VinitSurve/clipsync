using System;
using System.Drawing;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows.Forms;
using ClipSync.Windows.Models;

namespace ClipSync.Windows.Forms;

public sealed class PairingForm : Form
{
    private readonly TextBox _codeTextBox;
    private readonly TextBox _serverUrlTextBox;
    private readonly Button _pairButton;
    private readonly Label _statusLabel;
    private readonly Action<ClaimPairingResponse> _onPairSuccess;
    public string ServerUrl => _serverUrlTextBox.Text.Trim().TrimEnd('/');

    public PairingForm(string initialServerUrl, Action<ClaimPairingResponse> onPairSuccess)
    {
        _onPairSuccess = onPairSuccess;

        Text = "ClipSync — Pair Device";
        Size = new Size(380, 270);
        StartPosition = FormStartPosition.CenterScreen;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        Font = new Font("Segoe UI", 9.5f);

        var serverLabel = new Label { Text = "Server URL:", Location = new Point(25, 20), AutoSize = true };
        _serverUrlTextBox = new TextBox { Text = initialServerUrl, Location = new Point(25, 45), Size = new Size(310, 26) };

        var codeLabel = new Label { Text = "Enter 6-Digit Pairing Code (from Mac or Phone):", Location = new Point(25, 85), AutoSize = true };
        _codeTextBox = new TextBox
        {
            Location = new Point(25, 110),
            Size = new Size(310, 32),
            Font = new Font("Segoe UI", 14f, FontStyle.Bold),
            TextAlign = HorizontalAlignment.Center,
            MaxLength = 6
        };

        _pairButton = new Button
        {
            Text = "Pair This PC",
            Location = new Point(25, 155),
            Size = new Size(310, 35),
            BackColor = Color.FromArgb(0, 120, 215),
            ForeColor = Color.White,
            FlatStyle = FlatStyle.Flat
        };
        _pairButton.Click += async (_, _) => await HandlePairAsync();

        _statusLabel = new Label
        {
            Location = new Point(25, 198),
            Size = new Size(310, 20),
            TextAlign = ContentAlignment.MiddleCenter,
            ForeColor = Color.DimGray
        };

        Controls.Add(serverLabel);
        Controls.Add(_serverUrlTextBox);
        Controls.Add(codeLabel);
        Controls.Add(_codeTextBox);
        Controls.Add(_pairButton);
        Controls.Add(_statusLabel);
    }

    private async Task HandlePairAsync()
    {
        string code = _codeTextBox.Text.Trim();
        if (code.Length != 6)
        {
            _statusLabel.Text = "Please enter a valid 6-digit code";
            _statusLabel.ForeColor = Color.Red;
            return;
        }

        string serverUrl = _serverUrlTextBox.Text.Trim().TrimEnd('/');
        _pairButton.Enabled = false;
        _statusLabel.Text = "Connecting & pairing...";
        _statusLabel.ForeColor = Color.DimGray;

        try
        {
            using var http = new HttpClient();
            var payload = new
            {
                code,
                label = "Windows 10 PC",
                platform = "windows"
            };

            string json = JsonSerializer.Serialize(payload);
            var content = new StringContent(json, Encoding.UTF8, "application/json");

            var res = await http.PostAsync($"{serverUrl}/v1/auth/pair", content);
            string responseBody = await res.Content.ReadAsStringAsync();

            if (!res.IsSuccessStatusCode)
            {
                _statusLabel.Text = "Pairing failed: Invalid or expired code";
                _statusLabel.ForeColor = Color.Red;
                _pairButton.Enabled = true;
                return;
            }

            var claimResponse = JsonSerializer.Deserialize<ClaimPairingResponse>(responseBody);
            if (claimResponse != null)
            {
                _statusLabel.Text = "Paired successfully!";
                _statusLabel.ForeColor = Color.Green;
                await Task.Delay(800);
                _onPairSuccess(claimResponse);
                Close();
            }
        }
        catch (Exception ex)
        {
            _statusLabel.Text = $"Error: {ex.Message}";
            _statusLabel.ForeColor = Color.Red;
            _pairButton.Enabled = true;
        }
    }
}
