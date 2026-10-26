using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;

namespace ClipSync.Windows.Forms;

public record DisplayHistoryItem(string Id, string Text, string Source, DateTime Timestamp);

public sealed class HistoryForm : Form
{
    private readonly ListBox _listBox;
    private readonly Button _copyButton;
    private readonly Button _clearButton;
    private readonly Label _statusLabel;
    private readonly List<DisplayHistoryItem> _items = new();
    private readonly Action<string> _onCopy;

    public HistoryForm(Action<string> onCopy)
    {
        _onCopy = onCopy;

        Text = "ClipSync — Clipboard History";
        Size = new Size(460, 480);
        StartPosition = FormStartPosition.CenterScreen;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        Font = new Font("Segoe UI", 9.5f);

        var panelTop = new Panel { Dock = DockStyle.Top, Height = 40, Padding = new Padding(10) };
        var titleLabel = new Label { Text = "Recent Clipboard Items", Font = new Font("Segoe UI", 11f, FontStyle.Bold), AutoSize = true, Location = new Point(10, 10) };
        _clearButton = new Button { Text = "Clear", Location = new Point(350, 6), Size = new Size(80, 28) };
        _clearButton.Click += (_, _) => ClearItems();
        panelTop.Controls.Add(titleLabel);
        panelTop.Controls.Add(_clearButton);

        _listBox = new ListBox
        {
            Dock = DockStyle.Fill,
            ItemHeight = 22,
            DrawMode = DrawMode.OwnerDrawFixed
        };
        _listBox.DrawItem += ListBox_DrawItem;
        _listBox.DoubleClick += (_, _) => CopySelected();

        var panelBottom = new Panel { Dock = DockStyle.Bottom, Height = 50, Padding = new Padding(10) };
        _statusLabel = new Label { Text = "Double-click item to copy", AutoSize = true, Location = new Point(10, 15), ForeColor = Color.Gray };
        _copyButton = new Button { Text = "Copy to Clipboard", Location = new Point(300, 10), Size = new Size(130, 30) };
        _copyButton.Click += (_, _) => CopySelected();
        panelBottom.Controls.Add(_statusLabel);
        panelBottom.Controls.Add(_copyButton);

        Controls.Add(_listBox);
        Controls.Add(panelTop);
        Controls.Add(panelBottom);
    }

    public void AddItem(string text, string source)
    {
        if (InvokeRequired)
        {
            Invoke(() => AddItem(text, source));
            return;
        }

        var item = new DisplayHistoryItem(Guid.NewGuid().ToString("N"), text, source, DateTime.Now);
        _items.Insert(0, item);
        if (_items.Count > 100) _items.RemoveAt(_items.Count - 1);

        _listBox.Items.Insert(0, item);
        if (_listBox.SelectedIndex == -1 && _listBox.Items.Count > 0)
        {
            _listBox.SelectedIndex = 0;
        }
    }

    private void ClearItems()
    {
        _items.Clear();
        _listBox.Items.Clear();
        _statusLabel.Text = "History cleared";
    }

    private void CopySelected()
    {
        if (_listBox.SelectedItem is DisplayHistoryItem item)
        {
            _onCopy(item.Text);
            _statusLabel.Text = "Copied to clipboard!";
        }
    }

    private void ListBox_DrawItem(object? sender, DrawItemEventArgs e)
    {
        if (e.Index < 0 || e.Index >= _listBox.Items.Count) return;
        e.DrawBackground();

        if (_listBox.Items[e.Index] is DisplayHistoryItem item)
        {
            using var brushText = new SolidBrush(e.ForeColor);
            using var brushSub = new SolidBrush(Color.Gray);

            string preview = item.Text.Replace("\r\n", " ").Replace("\n", " ");
            if (preview.Length > 45) preview = preview[..45] + "...";

            e.Graphics.DrawString(preview, e.Font!, brushText, e.Bounds.X + 5, e.Bounds.Y + 2);
            string subText = $"[{item.Source}] {item.Timestamp:HH:mm:ss}";
            e.Graphics.DrawString(subText, new Font("Segoe UI", 7.5f), brushSub, e.Bounds.X + 320, e.Bounds.Y + 4);
        }

        e.DrawFocusRectangle();
    }
}
