using Velopack;
using Velopack.Logging;
using Velopack.Sources;

namespace AvaSnap.Services;

/// <summary>Velopack の UpdateManager の薄いラッパー。リリースバイナリ
/// (Setup.exe/.nupkg)のホスト専用に分けた別リポジトリ qsyi-dist/as-rel を指す
/// (ソースの qsyi/AvaSnap とは別。ビルド成果物だらけの公開リポジトリが「AvaSnap」
/// 検索や qsyi プロフィールからワンクリックで見えないように分けてある。アセットの
/// ファイル名も効くので `vpk pack` は --packId as-rel・--packTitle "AvaSnap" で分ける。
/// 公開リポジトリのリリースアセットにアクセス制御は無いので、これは「うっかり発見」
/// のハードルを上げるだけ。アプリにはこの URL が文字列で埋め込まれている)。
///
/// 実際のインストール/更新は Velopack が持つ: 専用 Setup.exe 経由で %LocalAppData%
/// 配下へインストールされている必要がある(Program.cs の VelopackApp.Build().Run())。
///
/// ここの各呼び出しはベストエフォートで例外を握りつぶす: 実 Velopack インストール
/// でない exe(開発中の bin/Debug 直起動等)では UpdateManager が投げるが、通常利用を
/// クラッシュさせてはいけない。「更新なし」と「そもそもチェック不能」を区別したければ
/// 先に <see cref="IsInstalled"/> を見ること。</summary>
public static class UpdateService
{
    private const string RepoUrl = "https://github.com/qsyi-dist/as-rel";

    private static readonly GithubSource _source = new(RepoUrl, accessToken: null, prerelease: false);
    private static readonly UpdateManager _manager = new(_source);

    /// <summary>実 Velopack インストールから起動していなければ false(開発中の
    /// dotnet build + 直起動など)。その場合、他のメンバも投げはしないが実質何もしない。</summary>
    public static bool IsInstalled => _manager.IsInstalled;

    /// <summary><see cref="IsInstalled"/> が false なら null(報告できるバージョンが無い)。</summary>
    public static SemanticVersion? CurrentVersion => _manager.CurrentVersion;

    public static async Task<UpdateInfo?> CheckForUpdatesAsync()
    {
        try
        {
            return await _manager.CheckForUpdatesAsync();
        }
        catch
        {
            return null;
        }
    }

    /// <summary>リリースフィードに公開済みの Full パッケージ全バージョン(新しい順)。
    /// 「過去の任意バージョンも選んで戻せる」バージョンピッカー用。CheckForUpdatesAsync は
    /// 「最新(より新しければ)」しか出さないので、フィードを直接読む。</summary>
    public static async Task<IReadOnlyList<VelopackAsset>> GetAvailableVersionsAsync()
    {
        try
        {
            // AppId が null なのは未インストール時だけ(IsInstalled)。呼び出し側が先に確認する前提。
            var feed = await _source.GetReleaseFeed(NullVelopackLogger.Instance, _manager.AppId!, channel: null!);
            return feed.Assets
                .Where(a => a.Type == VelopackAssetType.Full)
                .OrderByDescending(a => a.Version)
                .ToList();
        }
        catch
        {
            return Array.Empty<VelopackAsset>();
        }
    }

    /// <summary><paramref name="target"/> をダウンロード・インストールして再起動する。
    /// target は現在より古いこともある(ユーザーが過去リリースへ戻す)ので、その場合は
    /// IsDowngrade を立てて Velopack が delta ではなくフル再インストールするようにする。</summary>
    public static async Task DownloadAndApplyAsync(VelopackAsset target, Action<int>? onProgress = null)
    {
        bool isDowngrade = _manager.CurrentVersion is { } current && target.Version < current;
        var info = new UpdateInfo(target, isDowngrade);
        await _manager.DownloadUpdatesAsync(info, onProgress);
        _manager.ApplyUpdatesAndRestart(info);
    }
}
