using System.Diagnostics;
using System.Drawing;
using System.Runtime.InteropServices;

namespace AvaSnap.Services;

/// <summary>Finds the running VRChat.exe window and reads its client-area
/// rectangle on screen.</summary>
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
                    return false; // stop enumeration
                }
            }
            catch (ArgumentException)
            {
                // process exited between enumeration and lookup; ignore
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

    // ---- Camera frame estimate: derived from the "記録" sample log (12
    //      samples across 5 resolutions, including near-square and taller-
    //      than-wide windows): the camera frame is always dead-centered in
    //      the client area, with a fixed 16:9 (landscape) / 9:16 (portrait)
    //      aspect ratio, and its height is a near-constant fraction of the
    //      window's client HEIGHT regardless of the window's own aspect
    //      ratio (landscape height-fraction stdev 0.00022; portrait stdev
    //      0.00195, looser but still tight). Treated as a starting estimate,
    //      not an authoritative lookup -- untested at very small
    //      resolutions, under non-100% Windows DPI scaling, or against a
    //      future VRChat UI layout change, so the user can and should still
    //      nudge it manually afterward. Shared here (not private to
    //      ControlPanelWindow, which originally owned this) so
    //      OverlayWindow's own FOVガイド clipping can use the exact same
    //      estimate the avatar itself gets fitted to. ----

    public const double LandscapeHeightFraction = 0.4678;
    public const double PortraitHeightFraction = 0.8296;
    public const double LandscapeAspect = 16.0 / 9.0;
    public const double PortraitAspect = 9.0 / 16.0;

    /// <summary>The frame's height (in screen pixels) is always the recorded-
    /// sample estimate, but its WIDTH can optionally be derived from a known
    /// aspect ratio instead of the hardcoded 16:9/9:16 assumption --
    /// compositing has an actual photo on hand (ground truth for what VRChat's
    /// camera actually output), and assuming that photo is exactly 16:9/9:16
    /// when the user's camera resolution is anything else stretches the
    /// overlay non-uniformly onto it. See <paramref name="aspectOverride"/>.</summary>
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
