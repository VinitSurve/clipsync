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
}
