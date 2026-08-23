using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace AvaSnap.Services;

/// <summary>Sets a WPF window as an owned window of a native HWND (e.g.
/// VRChat's). Windows automatically keeps an owned window immediately above
/// its owner in Z-order whenever the owner is brought to the foreground,
/// instead of floating above literally everything (WPF Topmost) or getting
/// hidden behind the owner (the default for two unrelated top-level windows).
/// Shared by both AvaSnap windows -- the overlay and the control panel -- so
/// bringing VRChat forward brings both of them along with it.</summary>
public static class WindowOwnership
{
    private const int GWLP_HWNDPARENT = -8;

    // Real exported name is SetWindowLongPtrW (win-x64 only build, so no need to
    // fall back to the 32-bit-only SetWindowLong for pointer-sized values).
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)]
    private static extern IntPtr SetWindowLongPtrNative(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

    public static void SetOwner(Window window, IntPtr ownerHwnd)
    {
        var hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd == IntPtr.Zero) return;
        SetWindowLongPtrNative(hwnd, GWLP_HWNDPARENT, ownerHwnd);
    }
}
