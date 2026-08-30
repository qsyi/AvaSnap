using System;
using System.Windows;
using Velopack;

namespace AvaSnap;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        // WPF やアプリ状態に触れる前に必ず実行する。Velopack の初回起動/更新/
        // アンインストール呼び出し(ショートカット作成等)を処理し、その場合は
        // ウィンドウを開かず即プロセス終了する。Velopack インストール経由でなく
        // bin/Debug から直接起動した場合は安全な no-op。
        VelopackApp.Build().Run();

        var app = new App();
        app.InitializeComponent();
        app.Run();
    }
}
