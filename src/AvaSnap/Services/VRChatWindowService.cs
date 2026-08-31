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

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hWnd);

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

    /// <summary>クライアント領域の画面矩形を「物理ピクセル」で返す。Win32 の生値なので、
    /// WPF 座標系(DIP)で使う場合は <see cref="GetClientRectInDips"/> を使うこと。</summary>
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

    /// <summary>hWnd のあるモニタの表示スケール(1.0 = 100%)。取得失敗や DPI 非対応
    /// プロセスでは 1.0。</summary>
    public static double GetWindowDpiScale(IntPtr hWnd)
    {
        uint dpi = GetDpiForWindow(hWnd);
        return dpi == 0 ? 1.0 : dpi / 96.0;
    }

    /// <summary>クライアント領域を WPF の DIP 座標系(仮想スクリーン原点基準)で返す。
    /// Win32 は物理ピクセルを返すので、表示スケール 100% 以外でそのまま
    /// <see cref="GetClientRectOnScreen"/> の値を WPF 座標(Canvas.SetLeft、
    /// _state.X 等)へ入れるとスケール分ずれる。オーバーレイ配置・カメラ枠推定・
    /// FOVガイドはすべて WPF 座標系なのでこちらを使う。</summary>
    public static System.Windows.Rect? GetClientRectInDips(IntPtr hWnd)
    {
        if (GetClientRectOnScreen(hWnd) is not { } px) return null;
        double s = GetWindowDpiScale(hWnd);
        return new System.Windows.Rect(
            px.Left / s - System.Windows.SystemParameters.VirtualScreenLeft,
            px.Top / s - System.Windows.SystemParameters.VirtualScreenTop,
            Math.Max(0, px.Width / s),   // Rect ctor は負の幅/高さで throw する
            Math.Max(0, px.Height / s)); // 呼び出し側は { Width: > 0 } で弾く
    }

    // ---- カメラフレームの推定値: サンプルログ(5解像度・12サンプル)から。フレームは
    //      常にクライアント領域の中央、アス比は 16:9(横)/ 9:16(縦)固定、高さは
    //      ウィンドウ自身のアス比に依らずクライアント高の ほぼ一定比。あくまで初期推定で
    //      あり(極小解像度・将来の VRChat UI 変更では未検証)、ユーザーが後から手で
    //      微調整できる前提。region は DIP(GetClientRectInDips)で渡すこと。OverlayWindow の
    //      FOVガイドが、アバターを合わせるのと同じ推定値を使えるようここに置く。 ----

    public const double LandscapeHeightFraction = 0.4678;
    public const double PortraitHeightFraction = 0.8296;
    public const double LandscapeAspect = 16.0 / 9.0;
    public const double PortraitAspect = 9.0 / 16.0;

    /// <summary>フレーム高は常に推定値だが、幅は <paramref name="aspectOverride"/> で
    /// 16:9/9:16 固定の代わりに既知のアス比から出せる。合成モードは実写真を持っている
    /// (VRChat カメラの実出力の正解)ので、別解像度の写真を 16:9/9:16 と決めつけると
    /// オーバーレイが不均一に伸びる。<paramref name="region"/> は DIP
    /// (<see cref="GetClientRectInDips"/>)で渡すこと ── 返り値もその座標系。</summary>
    public static (double Left, double Top, double Width, double Height) ComputeCameraFrameRect(
        System.Windows.Rect region, bool landscape, double? aspectOverride = null)
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
