using System;
using System.Windows.Forms;
using ClipSync.Windows.Forms;

namespace ClipSync.Windows;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        ApplicationConfiguration.Initialize();
        Application.Run(new TrayApplicationContext());
    }
}
