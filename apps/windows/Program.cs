using System;
using System.Windows.Forms;
using ClipSync.Windows.Forms;

namespace ClipSync.Windows;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        ApplicationConfiguration.Initialize();
        string? initialUrl = args.Length > 0 ? args[0] : null;
        Application.Run(new TrayApplicationContext(initialUrl));
    }
}
