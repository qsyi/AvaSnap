using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AvaSnap.Services;

/// <summary>VRCX-style background auto-update, with no server of our own:
/// checks a PUBLIC GitHub repo's Releases API for a newer tag than the
/// currently-running build, downloads the new exe into %AppData%\AvaSnap\
/// Update in the background, and swaps it into place the NEXT time AvaSnap
/// starts (see ApplyPendingUpdateIfAny). GitHub hosts the release asset and
/// serves its Releases API for free with no auth needed -- the ONLY reason
/// the repo has to stay public even though AvaSnap itself is sold: a
/// private repo's Releases API requires a token, and shipping one inside
/// every customer's copy of the app would mean shipping a secret every
/// customer could extract. Only the built exe is uploaded to that repo,
/// never the source.</summary>
public static class UpdateService
{
    private const string RepoOwner = "qsyi";
    private const string RepoName = "avasnap";

    // Must match the exact file name attached to each GitHub Release's
    // assets (upload the published single-file exe under this exact name).
    private const string AssetName = "AvaSnap.exe";

    private static readonly string UpdateDir =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "AvaSnap", "Update");

    private static readonly string PendingExePath = Path.Combine(UpdateDir, AssetName);

    // An unauthenticated GitHub API call is cheap, but there's no reason to
    // make it on every single launch -- SettingsService.LastUpdateCheckUtc
    // throttles it to roughly once a day.
    private static readonly TimeSpan CheckInterval = TimeSpan.FromHours(24);

    private sealed class GitHubRelease
    {
        [JsonPropertyName("tag_name")] public string? TagName { get; set; }
        [JsonPropertyName("assets")] public List<GitHubAsset>? Assets { get; set; }
    }

    private sealed class GitHubAsset
    {
        [JsonPropertyName("name")] public string? Name { get; set; }
        [JsonPropertyName("browser_download_url")] public string? BrowserDownloadUrl { get; set; }
        [JsonPropertyName("size")] public long Size { get; set; }
    }

    /// <summary>Called once, at the very top of App.OnStartup before
    /// anything else (no window, no GPU check) -- if a previous background
    /// check already downloaded a newer build, swaps it into place and
    /// relaunches. Returns true if it did, in which case the caller should
    /// Shutdown() immediately without doing anything else: this process is
    /// handing off to the freshly-relaunched one. Also cleans up the
    /// ".old" file a PRIOR swap couldn't delete (it was still the file
    /// this exact process was running from at the time).</summary>
    public static bool ApplyPendingUpdateIfAny()
    {
        string currentExe = Environment.ProcessPath ?? Process.GetCurrentProcess().MainModule?.FileName ?? "";
        if (string.IsNullOrEmpty(currentExe)) return false;

        string oldExe = currentExe + ".old";
        try { if (File.Exists(oldExe)) File.Delete(oldExe); } catch { /* still locked by the process that WAS running from it; next launch retries */ }

        if (!File.Exists(PendingExePath)) return false;

        try
        {
            // Renaming a running exe out from under itself is allowed on
            // NTFS -- the process keeps executing fine via its existing
            // open handle to the (now differently-named) file. That's what
            // makes swapping possible with no separate updater helper
            // process: this process finishes the swap and relaunches a NEW
            // process pointed at the freshly-placed file, all before this
            // one exits.
            File.Move(currentExe, oldExe, overwrite: true);
            File.Move(PendingExePath, currentExe, overwrite: true);

            Process.Start(new ProcessStartInfo(currentExe) { UseShellExecute = true });
            return true;
        }
        catch
        {
            // Swap failed partway through -- leave it for the next launch
            // to sort out rather than risk starting a half-written exe.
            return false;
        }
    }

    /// <summary>Fire-and-forget background check -- call once per session,
    /// after the main window is already visible, so a slow or unreachable
    /// GitHub never delays startup. Entirely best-effort: no network, the
    /// repo not existing yet, GitHub being down, etc. should never surface
    /// to the user or interrupt normal use of the app.</summary>
    public static async Task CheckAndDownloadUpdateAsync()
    {
        try
        {
            var saved = SettingsService.Load();
            if (saved?.LastUpdateCheckUtc is { } last && DateTime.UtcNow - last < CheckInterval) return;

            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
            // GitHub's API rejects requests with no User-Agent.
            http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("AvaSnap", CurrentVersion.ToString()));
            http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));

            var json = await http.GetStringAsync($"https://api.github.com/repos/{RepoOwner}/{RepoName}/releases/latest");
            var release = JsonSerializer.Deserialize<GitHubRelease>(json);

            SettingsService.SaveLastUpdateCheck(DateTime.UtcNow);

            if (release?.TagName is null) return;
            if (!TryParseVersion(release.TagName, out var latest)) return;
            if (latest <= CurrentVersion) return;

            var asset = release.Assets?.Find(a => a.Name == AssetName);
            if (asset?.BrowserDownloadUrl is not { } url) return;

            Directory.CreateDirectory(UpdateDir);
            var tempPath = PendingExePath + ".part";
            await using (var stream = await http.GetStreamAsync(url))
            await using (var file = File.Create(tempPath))
            {
                await stream.CopyToAsync(file);
            }
            // Size check against the release asset's own reported size --
            // a cheap sanity check that the download wasn't cut short, not
            // a full integrity hash (the asset already travels over HTTPS
            // straight from GitHub).
            if (asset.Size > 0 && new FileInfo(tempPath).Length != asset.Size)
            {
                File.Delete(tempPath);
                return;
            }
            File.Move(tempPath, PendingExePath, overwrite: true);
        }
        catch
        {
            // best-effort, see doc comment above
        }
    }

    private static Version CurrentVersion =>
        Assembly.GetExecutingAssembly().GetName().Version ?? new Version(0, 0, 0, 0);

    private static bool TryParseVersion(string tag, out Version version)
    {
        var trimmed = tag.TrimStart('v', 'V');
        return Version.TryParse(trimmed, out version!);
    }
}
