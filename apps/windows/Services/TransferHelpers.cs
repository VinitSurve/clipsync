using System;
using System.IO;
using System.IO.Compression;

namespace ClipSync.Windows.Services;

public static class TransferHelpers
{
    public static byte[] CreateZipArchive(string[] paths)
    {
        using var ms = new MemoryStream();
        using (var archive = new ZipArchive(ms, ZipArchiveMode.Create, true))
        {
            foreach (string path in paths)
            {
                if (File.Exists(path))
                {
                    string entryName = Path.GetFileName(path);
                    archive.CreateEntryFromFile(path, entryName, CompressionLevel.Optimal);
                }
                else if (Directory.Exists(path))
                {
                    string dirName = Path.GetFileName(path.TrimEnd('\\', '/'));
                    AddDirectoryToZip(archive, path, dirName);
                }
            }
        }
        return ms.ToArray();
    }

    private static void AddDirectoryToZip(ZipArchive archive, string dirPath, string relativeRoot)
    {
        foreach (string file in Directory.GetFiles(dirPath))
        {
            string entryName = Path.Combine(relativeRoot, Path.GetFileName(file));
            archive.CreateEntryFromFile(file, entryName, CompressionLevel.Optimal);
        }

        foreach (string subDir in Directory.GetDirectories(dirPath))
        {
            string subDirName = Path.GetFileName(subDir.TrimEnd('\\', '/'));
            AddDirectoryToZip(archive, subDir, Path.Combine(relativeRoot, subDirName));
        }
    }

    public static string GetMimeType(string fileName)
    {
        string ext = Path.GetExtension(fileName).ToLowerInvariant();
        return ext switch
        {
            ".png" => "image/png",
            ".jpg" or ".jpeg" => "image/jpeg",
            ".gif" => "image/gif",
            ".bmp" => "image/bmp",
            ".webp" => "image/webp",
            ".pdf" => "application/pdf",
            ".zip" => "application/zip",
            ".txt" => "text/plain",
            ".json" => "application/json",
            ".mp4" => "video/mp4",
            ".mp3" => "audio/mpeg",
            ".docx" => "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
            ".xlsx" => "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
            _ => "application/octet-stream"
        };
    }

    private static readonly System.Collections.Generic.HashSet<char> InvalidFileNameChars = new(
        System.Linq.Enumerable.Concat(
            new[] { '\\', '/', ':', '*', '?', '"', '<', '>', '|' },
            Path.GetInvalidFileNameChars()
        )
    );

    public static string SanitizeFileName(string fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName))
        {
            return $"Clipboard_{DateTime.UtcNow:yyyyMMdd_HHmmss}.png";
        }

        var sb = new System.Text.StringBuilder(fileName.Length);
        foreach (char c in fileName)
        {
            sb.Append(InvalidFileNameChars.Contains(c) || c < 32 ? '_' : c);
        }
        return sb.ToString();
    }

    public static bool IsValidClipSyncProject(string? projectId)
    {
        if (string.IsNullOrWhiteSpace(projectId)) return true; // Legacy or manual client ID without project_id
        return projectId.IndexOf("clipsync", StringComparison.OrdinalIgnoreCase) >= 0;
    }

    public static string FormatProjectMismatchWarning(string? projectId)
    {
        return $"Warning: Connected Google Cloud Project '{projectId}' does not match 'clipsync'. " +
               "Google Drive scope restricts file access to the same project. " +
               "Please import the ClipSync credentials.json in Settings.";
    }

    public static string ParseDriveError(string rawJson, System.Net.HttpStatusCode statusCode)
    {
        if (statusCode == System.Net.HttpStatusCode.NotFound)
        {
            return "Download failed (HTTP 404): File not found in Google Drive. " +
                   "The temporary file may have expired (>24h), been purged, " +
                   "or was uploaded using a different Google Cloud Project or account. " +
                   "Ensure Mac and Windows are authorized under the same Google Cloud Project (e.g. clipsync-512808) " +
                   "and the same Google account. Verify Project ID in Settings.";
        }

        if (statusCode is System.Net.HttpStatusCode.Unauthorized or System.Net.HttpStatusCode.Forbidden)
        {
            return $"Download failed (HTTP {(int)statusCode}): Access denied by Google Drive. " +
                   "Your Google authorization may have expired or lacks access to this file. " +
                   "Please reconnect Google Drive in Settings.";
        }


        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(rawJson);
            var root = doc.RootElement;
            if (root.TryGetProperty("error", out var errProp))
            {
                if (errProp.ValueKind == System.Text.Json.JsonValueKind.Object && errProp.TryGetProperty("message", out var msgProp))
                {
                    string msg = msgProp.GetString() ?? "";
                    if (!string.IsNullOrWhiteSpace(msg))
                    {
                        return $"Google Drive error (HTTP {(int)statusCode}): {msg}";
                    }
                }
            }
        }
        catch { }

        return $"Download failed (HTTP {(int)statusCode})";
    }
}
