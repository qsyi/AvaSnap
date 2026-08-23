using Velopack;
using Velopack.Logging;
using Velopack.Sources;

namespace AvaSnap.Services;

/// <summary>Thin wrapper around Velopack's UpdateManager, pointed at
/// qsyi-dist/as-rel -- a SEPARATE, unlinked GitHub org+repo used only to
/// host release binaries (Setup.exe/.nupkg), not the qsyi/AvaSnap source
/// repo. Deliberately split (different account, name unrelated to
/// "AvaSnap") so a public repo full of build artifacts isn't sitting one
/// click away from qsyi/AvaSnap's README, the qsyi profile's own
/// repository list, or a GitHub/web search for "AvaSnap" -- released
/// asset FILE NAMES matter here too, which is why `vpk pack` is run with
/// --packId as-rel (this same unrelated name) and --packTitle "AvaSnap"
/// kept separately, so installed copies still display/register normally
/// for legitimate users. A public repo's release assets are never
/// access-controlled either way (the repo itself still has to be public
/// since GithubSource needs no token), so none of this actually prevents
/// access -- it only raises the bar against CASUAL discovery. This repo's
/// URL is still embedded as a plain string in every shipped copy of the
/// app, so anyone motivated enough to inspect that copy can always find
/// it -- true for any app that self-updates from a public/unauthenticated
/// feed, not specific to Velopack or GitHub.
///
/// Velopack (not a hand-rolled GitHub-API-plus-exe-swap scheme, which this
/// file used to be) owns the actual install/update mechanics: it requires
/// the app to be installed through its own Setup.exe into a managed
/// %LocalAppData% folder -- see Program.cs's VelopackApp.Build().Run() call
/// and the csproj's own doc comment on why PublishSingleFile is gone.
///
/// Every call here is best-effort and swallows its own exceptions:
/// UpdateManager throws when the running exe isn't a real Velopack install
/// (e.g. launched directly from bin/Debug during development, which is how
/// this app is normally run and tested outside of a packaged release), and
/// none of that should ever crash normal use of the app. Check
/// <see cref="IsInstalled"/> first if the caller wants to distinguish "no
/// update available" from "can't check at all".</summary>
public static class UpdateService
{
    private const string RepoUrl = "https://github.com/qsyi-dist/as-rel";

    private static readonly GithubSource _source = new(RepoUrl, accessToken: null, prerelease: false);
    private static readonly UpdateManager _manager = new(_source);

    /// <summary>False when this exe isn't running from a real Velopack
    /// install (e.g. a plain `dotnet build` + direct run during
    /// development) -- every other member here still won't throw in that
    /// case, but there's nothing meaningful for them to do.</summary>
    public static bool IsInstalled => _manager.IsInstalled;

    /// <summary>Null when <see cref="IsInstalled"/> is false -- there's no
    /// installed version to report in that case.</summary>
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

    /// <summary>Every Full-package version currently published to the
    /// repo's release feed, newest first -- for the "過去の任意バージョン
    /// も選んで戻せる" version picker. Not exposed by UpdateManager's own
    /// CheckForUpdatesAsync (that only ever surfaces "latest, if newer"),
    /// so this reads the source's release feed directly instead, per
    /// Velopack's documented pattern for targeting a specific version.</summary>
    public static async Task<IReadOnlyList<VelopackAsset>> GetAvailableVersionsAsync()
    {
        try
        {
            // AppId is only null when not installed (see IsInstalled) --
            // callers are expected to check that first, same as every other
            // member here that assumes a real install.
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

    /// <summary>Downloads and installs <paramref name="target"/>, then
    /// restarts into it. <paramref name="target"/> may be OLDER than the
    /// currently-installed version (rolling back to a past release the
    /// user picked) -- IsDowngrade is set accordingly so Velopack skips
    /// delta-patching and does a full reinstall instead, same as its own
    /// documented rollback recipe.</summary>
    public static async Task DownloadAndApplyAsync(VelopackAsset target, Action<int>? onProgress = null)
    {
        bool isDowngrade = _manager.CurrentVersion is { } current && target.Version < current;
        var info = new UpdateInfo(target, isDowngrade);
        await _manager.DownloadUpdatesAsync(info, onProgress);
        _manager.ApplyUpdatesAndRestart(info);
    }
}
