using System;
using System.Windows;
using AvaSnap.Services;
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
        // bin/Debug から直接起動した場合は安全な no-op。初回起動時に .avasnap の
        // ファイル関連付けを登録する。
        VelopackApp.Build()
            .OnFirstRun(_ => FileAssociation.Register())
            .Run();

        // 通常起動でも毎回登録を確認する(内容が同じなら no-op)。パスが変わる
        // 更新後や、Velopack を介さない起動でも関連付けを最新に保つ。
        FileAssociation.Register();

        // 多重起動を1つに束ねる。2つ目以降は開きたい .avasnap を既存インスタンスへ
        // 渡して即終了する(ダブルクリック起動が毎回新プロセスになるのを防ぐ)。
        if (!SingleInstance.TryAcquire(args))
            return;

        var app = new App();
        app.InitializeComponent();
        app.Run();

        SingleInstance.Release();
    }
}
