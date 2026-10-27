using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;
using ClipSync.Windows.Crypto;
using ClipSync.Windows.Models;

namespace ClipSync.Windows.Services;

public enum ClipboardEventType
{
    Text,
    Image,
    Files,
    Error
}

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

    // Events
    public event Action<string>? OnNewLocalCopy; // Backwards compatibility for text sync
    public event Action<byte[], string, string>? OnNewLocalImage; // data, mimeType, fileName
    public event Action<string[]>? OnNewLocalFiles; // file paths
    public event Action<string>? OnClipboardError;

    public ClipboardMonitor()
    {
        // Hidden message window to receive WM_CLIPBOARDUPDATE
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        WindowState = FormWindowState.Minimized;
        Opacity = 0;
        Size = new Size(0, 0);
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

        // Try reading clipboard with retry in case clipboard is temporarily locked by another app
        for (int retry = 0; retry < 5; retry++)
        {
            try
            {
                // 1. Check for Copied Files / Folders (CF_HDROP from File Explorer)
                if (Clipboard.ContainsFileDropList())
                {
                    var fileDropList = Clipboard.GetFileDropList();
                    if (fileDropList != null && fileDropList.Count > 0)
                    {
                        var paths = new List<string>(fileDropList.Count);
                        foreach (string? path in fileDropList)
                        {
                            if (!string.IsNullOrEmpty(path) && (File.Exists(path) || Directory.Exists(path)))
                            {
                                paths.Add(path);
                            }
                        }

                        if (paths.Count > 0)
                        {
                            HandleFilesCopy(paths.ToArray());
                            return;
                        }
                    }
                }

                // 2. Check for Copied Image (Screenshots, Browser Images, Paint, Photo Viewer)
                byte[]? imageData = ExtractPngBytesFromClipboard();
                if (imageData != null && imageData.Length > 0)
                {
                    HandleImageCopy(imageData);
                    return;
                }

                // 3. Check for Copied Text
                if (Clipboard.ContainsText())
                {
                    string text = Clipboard.GetText();
                    if (!string.IsNullOrWhiteSpace(text))
                    {
                        HandleTextCopy(text);
                        return;
                    }
                }

                break;
            }
            catch (ExternalException)
            {
                // Clipboard contention / locked by source app; brief wait before retry
                Thread.Sleep(30);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Clipboard] Error reading clipboard: {ex.Message}");
                break;
            }
        }
    }

    private void HandleFilesCopy(string[] paths)
    {
        // Enforce 100 MB max transfer limit across items
        long totalSize = 0;
        var validPaths = new List<string>();

        foreach (string path in paths)
        {
            try
            {
                long itemSize;
                if (Directory.Exists(path))
                {
                    itemSize = CalculateDirectorySize(new DirectoryInfo(path));
                }
                else
                {
                    var fileInfo = new FileInfo(path);
                    itemSize = fileInfo.Length;
                }

                if (itemSize > TransferConstants.MaxTransferBytes)
                {
                    string sizeStr = TransferRecord.FormatByteCount(itemSize);
                    OnClipboardError?.Invoke($"'{Path.GetFileName(path)}' exceeds 100 MB limit ({sizeStr}).");
                    return;
                }

                totalSize += itemSize;
                validPaths.Add(path);
            }
            catch (Exception ex)
            {
                OnClipboardError?.Invoke($"Failed to access '{Path.GetFileName(path)}': {ex.Message}");
                return;
            }
        }

        if (validPaths.Count == 0) return;

        if (totalSize > TransferConstants.MaxTransferBytes)
        {
            string totalStr = TransferRecord.FormatByteCount(totalSize);
            OnClipboardError?.Invoke($"Selected files exceed 100 MB limit ({totalStr}).");
            return;
        }

        // Build stable hash over paths and modification timestamps
        var sb = new System.Text.StringBuilder();
        foreach (string p in validPaths)
        {
            DateTime lastWrite = File.Exists(p) ? File.GetLastWriteTimeUtc(p) : Directory.GetLastWriteTimeUtc(p);
            sb.Append($"{p}:{lastWrite.Ticks};");
        }
        string hash = ClipSyncCrypto.ComputeSha256(sb.ToString());

        if (hash == _lastReceivedHash || hash == _lastSentHash)
        {
            return;
        }

        _lastSentHash = hash;
        OnNewLocalFiles?.Invoke(validPaths.ToArray());
    }

    private void HandleImageCopy(byte[] imageData)
    {
        if (imageData.Length > TransferConstants.MaxTransferBytes)
        {
            OnClipboardError?.Invoke("Copied image exceeds 100 MB limit.");
            return;
        }

        string hash = ClipSyncCrypto.ComputeSha256(imageData);

        if (hash == _lastReceivedHash || hash == _lastSentHash)
        {
            return;
        }

        _lastSentHash = hash;
        string fileName = $"Clipboard_{DateTime.UtcNow:yyyyMMdd_HHmmss}.png";
        OnNewLocalImage?.Invoke(imageData, "image/png", fileName);
    }

    private void HandleTextCopy(string text)
    {
        string hash = ClipSyncCrypto.ComputeSha256(text);

        if (hash == _lastReceivedHash || hash == _lastSentHash)
        {
            return;
        }

        _lastSentHash = hash;
        OnNewLocalCopy?.Invoke(text);
    }

    private byte[]? ExtractPngBytesFromClipboard()
    {
        try
        {
            var dataObj = Clipboard.GetDataObject();
            if (dataObj == null) return null;

            // 1. Try reading raw "PNG" format from IDataObject (preserves transparency)
            if (dataObj.GetDataPresent("PNG"))
            {
                object? data = dataObj.GetData("PNG");
                if (data is Stream stream)
                {
                    using var ms = new MemoryStream();
                    stream.CopyTo(ms);
                    return ms.ToArray();
                }
                else if (data is byte[] bytes)
                {
                    return bytes;
                }
            }

            // 2. Try standard Clipboard.ContainsImage()
            if (Clipboard.ContainsImage())
            {
                using Image? img = Clipboard.GetImage();
                if (img != null)
                {
                    using var ms = new MemoryStream();
                    img.Save(ms, ImageFormat.Png);
                    return ms.ToArray();
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Clipboard] Error extracting image: {ex.Message}");
        }

        return null;
    }

    private static long CalculateDirectorySize(DirectoryInfo dir)
    {
        long size = 0;
        foreach (FileInfo file in dir.GetFiles())
        {
            size += file.Length;
        }
        foreach (DirectoryInfo subDir in dir.GetDirectories())
        {
            size += CalculateDirectorySize(subDir);
        }
        return size;
    }

    // -------------------------------------------------------------------------
    // Remote Clipboard Setters (with Echo-Loop Prevention)
    // -------------------------------------------------------------------------

    public void SetRemoteClipboard(string text)
    {
        _lastReceivedHash = ClipSyncCrypto.ComputeSha256(text);

        for (int retry = 0; retry < 5; retry++)
        {
            try
            {
                Clipboard.SetText(text);
                break;
            }
            catch (ExternalException)
            {
                Thread.Sleep(30);
            }
            catch { break; }
        }
    }

    public void SetRemoteClipboardImage(byte[] pngBytes)
    {
        _lastReceivedHash = ClipSyncCrypto.ComputeSha256(pngBytes);

        for (int retry = 0; retry < 5; retry++)
        {
            try
            {
                using var ms = new MemoryStream(pngBytes);
                using var img = Image.FromStream(ms);

                var dataObj = new DataObject();
                dataObj.SetData(DataFormats.Bitmap, true, img);
                dataObj.SetData("PNG", false, new MemoryStream(pngBytes));
                Clipboard.SetDataObject(dataObj, true);
                break;
            }
            catch (ExternalException)
            {
                Thread.Sleep(30);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Clipboard] Failed to set remote image: {ex.Message}");
                break;
            }
        }
    }

    public void SetRemoteClipboardFiles(string[] filePaths)
    {
        var sb = new System.Text.StringBuilder();
        foreach (string p in filePaths)
        {
            DateTime lastWrite = File.Exists(p) ? File.GetLastWriteTimeUtc(p) : Directory.GetLastWriteTimeUtc(p);
            sb.Append($"{p}:{lastWrite.Ticks};");
        }
        _lastReceivedHash = ClipSyncCrypto.ComputeSha256(sb.ToString());

        for (int retry = 0; retry < 5; retry++)
        {
            try
            {
                var col = new StringCollection();
                col.AddRange(filePaths);
                Clipboard.SetFileDropList(col);
                break;
            }
            catch (ExternalException)
            {
                Thread.Sleep(30);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Clipboard] Failed to set remote file drop list: {ex.Message}");
                break;
            }
        }
    }
}
