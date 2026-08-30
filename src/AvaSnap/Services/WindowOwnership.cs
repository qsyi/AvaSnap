using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace AvaSnap.Services;

/// <summary>WPF ウィンドウをネイティブ HWND(例: VRChat)の owned window にする。
/// owner が前面に来ると Windows が owned window を Z 順で owner の直上に保つので、
/// 全てより手前(WPF Topmost)でも owner の裏(無関係な2窓の既定)でもなくなる。
/// オーバーレイとコントロールパネル両方で使い、VRChat を前面にすると両方付いてくる。</summary>
public static class WindowOwnership
{
    private const int GWLP_HWNDPARENT = -8;

    // 実際のエクスポート名は SetWindowLongPtrW(win-x64 専用ビルドなので 32bit 用の
    // SetWindowLong にフォールバックする必要は無い)。
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)]
    private static extern IntPtr SetWindowLongPtrNative(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

    public static void SetOwner(Window window, IntPtr ownerHwnd)
    {
        var hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd == IntPtr.Zero) return;
        SetWindowLongPtrNative(hwnd, GWLP_HWNDPARENT, ownerHwnd);
    }
}
