using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace AvaSnap.Services;

/// <summary>.avasnap を現在のユーザー(HKCU、管理者権限不要)で AvaSnap に関連付け、
/// Explorer でダブルクリックすると開けるようにする。既に同じ内容なら何もしない。</summary>
internal static class FileAssociation
{
    private const string ProgId = "AvaSnap.Project";
    private const uint SHCNE_ASSOCCHANGED = 0x08000000;
    private const uint SHCNF_IDLIST = 0x0000;

    [DllImport("shell32.dll")]
    private static extern void SHChangeNotify(uint eventId, uint flags, IntPtr item1, IntPtr item2);

    public static void Register()
    {
        try
        {
            string exe = Environment.ProcessPath ?? Process.GetCurrentProcess().MainModule?.FileName ?? "";
            if (string.IsNullOrEmpty(exe)) return;
            string command = $"\"{exe}\" \"%1\"";

            using var classes = Registry.CurrentUser.CreateSubKey(@"Software\Classes");

            using (var ext = classes.CreateSubKey(ProjectService.Extension))
                ext.SetValue(null, ProgId);

            using var progId = classes.CreateSubKey(ProgId);
            progId.SetValue(null, "AvaSnap プロジェクト");

            using (var icon = progId.CreateSubKey(@"DefaultIcon"))
                icon.SetValue(null, $"\"{exe}\",0");

            using var cmd = progId.CreateSubKey(@"shell\open\command");
            string? existing = cmd.GetValue(null) as string;
            if (string.Equals(existing, command, StringComparison.OrdinalIgnoreCase))
                return; // 既に登録済み ── Explorer へ通知しない(不要な再スキャンを避ける)

            cmd.SetValue(null, command);
            SHChangeNotify(SHCNE_ASSOCCHANGED, SHCNF_IDLIST, IntPtr.Zero, IntPtr.Zero);
        }
        catch (UnauthorizedAccessException) { }
        catch (System.Security.SecurityException) { }
        catch (System.IO.IOException) { }
    }
}
