using System;
using System.Windows;
using System.Windows.Interop;

namespace DiskUtility.Services;

/// <summary>
/// Small cosmetic touches that can only be done through the window manager, not XAML.
/// </summary>
internal static class WindowTheme
{
    /// <summary>
    /// Paints the system title bar dark so it matches the window instead of sitting on top of it
    /// as a white strip. Windows 10 1809 used attribute 19 before 20 was settled on, and older
    /// builds ignore both — this is decoration, so every failure is silent.
    /// </summary>
    public static void ApplyDarkTitleBar(Window window)
    {
        try
        {
            IntPtr hwnd = new WindowInteropHelper(window).Handle;
            if (hwnd == IntPtr.Zero) return;

            int enabled = 1;
            if (NativeMethods.DwmSetWindowAttribute(
                    hwnd, NativeMethods.DWMWA_USE_IMMERSIVE_DARK_MODE, ref enabled, sizeof(int)) != 0)
            {
                NativeMethods.DwmSetWindowAttribute(
                    hwnd, NativeMethods.DWMWA_USE_IMMERSIVE_DARK_MODE_LEGACY, ref enabled, sizeof(int));
            }
        }
        catch
        {
            // Purely cosmetic — a light title bar is not worth surfacing an error for.
        }
    }
}
