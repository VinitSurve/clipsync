using System;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using ClipSync.Windows.Crypto;

namespace ClipSync.Windows.Services;

public sealed class ClipboardMonitor : Form
{
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AddClipboardFormatListener(IntPtr hwnd);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RemoveClipboardFormatListener(IntPtr hwnd);

    private const int WM_CLIPBOARDUPDATE = 0x031D;

    private string _lastReceivedHash = string.Empty;
    private string _lastSentHash = string.Empty;

    public bool IsPaused { get; set; } = false;
    public event Action<string>? OnNewLocalCopy;

    public ClipboardMonitor()
    {
        // Hidden message window to receive WM_CLIPBOARDUPDATE
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        WindowState = FormWindowState.Minimized;
        Opacity = 0;
        Size = new System.Drawing.Size(0, 0);
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        AddClipboardFormatListener(Handle);
    }

    protected override void OnHandleDestroyed(EventArgs e)
    {
        RemoveClipboardFormatListener(Handle);
        base.OnHandleDestroyed(e);
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == WM_CLIPBOARDUPDATE)
        {
            HandleClipboardChange();
        }
        base.WndProc(ref m);
    }

    private void HandleClipboardChange()
    {
        if (IsPaused) return;

        try
        {
            if (Clipboard.ContainsText())
            {
                string text = Clipboard.GetText();
                if (string.IsNullOrWhiteSpace(text)) return;

                string hash = ClipSyncCrypto.ComputeSha256(text);

                // Prevent echo loop: if text matches last remote received item, ignore
                if (hash == _lastReceivedHash) return;

                // Prevent duplicate broadcast if identical to last sent
                if (hash == _lastSentHash) return;

                _lastSentHash = hash;
                OnNewLocalCopy?.Invoke(text);
            }
        }
        catch
        {
            // Windows clipboard may be locked by another application temporarily
        }
    }

    public void SetRemoteClipboard(string text)
    {
        try
        {
            _lastReceivedHash = ClipSyncCrypto.ComputeSha256(text);
            Clipboard.SetText(text);
        }
        catch
        {
            // Clipboard retry logic can be added if locked
        }
    }
}
