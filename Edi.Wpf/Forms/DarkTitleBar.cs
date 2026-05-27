using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace Edi.Forms
{
    internal static class DarkTitleBar
    {
        private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;
        private const int DWMWA_USE_IMMERSIVE_DARK_MODE_OLD = 19;

        [DllImport("dwmapi.dll", PreserveSig = true)]
        private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);

        public static void Apply(Window window)
        {
            window.SourceInitialized += (_, _) =>
            {
                try
                {
                    var hwnd = new WindowInteropHelper(window).Handle;
                    if (hwnd == IntPtr.Zero) return;
                    int useDark = 1;
                    if (DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, ref useDark, sizeof(int)) != 0)
                    {
                        DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE_OLD, ref useDark, sizeof(int));
                    }
                }
                catch { }
            };
        }
    }
}
