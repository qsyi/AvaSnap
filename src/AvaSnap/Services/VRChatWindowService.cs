using System.Diagnostics;
using System.Drawing;
using System.Runtime.InteropServices;

namespace AvaSnap.Services;

/// <summary>実行中の VRChat.exe ウィンドウを探し、その画面上のクライアント領域矩形を読む。</summary>
public static class VRChatWindowService
{
    private const string ProcessName = "vrchat";

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern int GetWindowTextLength(IntPtr hWnd);

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    [DllImport("user32.dll")]
    private static extern bool GetClientRect(IntPtr hWnd, out RECT lpRect);

    [DllImport("user32.dll")]
    private static extern bool ClientToScreen(IntPtr hWnd, ref POINT lpPoint);

    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X, Y; }

    public static IntPtr? FindVRChatWindow()
    {
        IntPtr found = IntPtr.Zero;
        EnumWindows((hWnd, _) =>
        {
            if (!IsWindowVisible(hWnd) || GetWindowTextLength(hWnd) == 0)
            {
                return true;
            }
            GetWindowThreadProcessId(hWnd, out uint pid);
            try
            {
                using var proc = Process.GetProcessById((int)pid);
                if (string.Equals(proc.ProcessName, ProcessName, StringComparison.OrdinalIgnoreCase))
                {
                    found = hWnd;
                    return false; // 列挙終了
                }
            }
            catch (ArgumentException)
            {
                // 列挙〜lookup の間にプロセス終了。無視
            }
            return true;
        }, IntPtr.Zero);

        return found == IntPtr.Zero ? null : found;
    }

    public static Rectangle? GetClientRectOnScreen(IntPtr hWnd)
    {
        if (!GetClientRect(hWnd, out var rect))
        {
            return null;
        }
        var topLeft = new POINT { X = rect.Left, Y = rect.Top };
        var bottomRight = new POINT { X = rect.Right, Y = rect.Bottom };
        ClientToScreen(hWnd, ref topLeft);
        ClientToScreen(hWnd, ref bottomRight);
        return new Rectangle(topLeft.X, topLeft.Y, bottomRight.X - topLeft.X, bottomRight.Y - topLeft.Y);
    }

    // ---- カメラフレームの推定値: サンプルログ(5解像度・12サンプル)から。フレームは
    //      常にクライアント領域の中央、アス比は 16:9(横)/ 9:16(縦)固定、高さは
    //      ウィンドウ自身のアス比に依らずクライアント高の ほぼ一定比。あくまで初期推定で
    //      あり(極小解像度・非100% DPI・将来の VRChat UI 変更では未検証)、ユーザーが
    //      後から手で微調整できる前提。OverlayWindow の FOVガイドが、アバターを合わせる
    //      のと同じ推定値を使えるようここに置く。 ----

    public const double LandscapeHeightFraction = 0.4678;
    public const double PortraitHeightFraction = 0.8296;
    public const double LandscapeAspect = 16.0 / 9.0;
    public const double PortraitAspect = 9.0 / 16.0;

    /// <summary>フレーム高(画面ピクセル)は常に推定値だが、幅は
    /// <paramref name="aspectOverride"/> で 16:9/9:16 固定の代わりに既知のアス比から
    /// 出せる。合成モードは実写真を持っている(VRChat カメラの実出力の正解)ので、
    /// 別解像度の写真を 16:9/9:16 と決めつけるとオーバーレイが不均一に伸びる。</summary>
    public static (double Left, double Top, double Width, double Height) ComputeCameraFrameRect(
        Rectangle region, bool landscape, double? aspectOverride = null)
    {
        double heightFraction = landscape ? LandscapeHeightFraction : PortraitHeightFraction;
        double frameAspect = aspectOverride ?? (landscape ? LandscapeAspect : PortraitAspect);
        double frameHeight = region.Height * heightFraction;
        double frameWidth = frameHeight * frameAspect;
        double frameLeft = region.Left + (region.Width - frameWidth) / 2;
        double frameTop = region.Top + (region.Height - frameHeight) / 2;
        return (frameLeft, frameTop, frameWidth, frameHeight);
    }
}
