using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Windows;
using SimpleWhisper.Helpers;
using SimpleWhisper.Models;

namespace SimpleWhisper.Services;

/// <summary>
/// Checks GitHub Releases for new versions, downloads installers, and launches updates.
/// Follows the static singleton pattern used by other services in this app.
/// </summary>
public sealed class UpdateService : IDisposable
{
    // ──────────────────────────────────────────────────────────────────
    //  Constants
    // ──────────────────────────────────────────────────────────────────

    private const string GitHubApiUrl = "https://api.github.com/repos/megensel/SimpleWhisper/releases/latest";
    private const string UserAgent = "SimpleWhisper-UpdateChecker";
    private static readonly TimeSpan CheckInterval = TimeSpan.FromHours(4);
    private static readonly TimeSpan StartupDelay = TimeSpan.FromSeconds(15);

    // ──────────────────────────────────────────────────────────────────
    //  Static HttpClient (one per app, per .NET best practices)
    // ──────────────────────────────────────────────────────────────────

    private static readonly HttpClient _httpClient = CreateHttpClient();

    private static HttpClient CreateHttpClient()
    {
        var client = new HttpClient();
        client.DefaultRequestHeaders.UserAgent.ParseAdd(UserAgent);
        client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        client.Timeout = TimeSpan.FromSeconds(30);
        return client;
    }

    // ──────────────────────────────────────────────────────────────────
    //  Fields
    // ──────────────────────────────────────────────────────────────────

    private readonly SettingsService _settingsService;
    private Timer? _checkTimer;
    private bool _disposed;

    // ──────────────────────────────────────────────────────────────────
    //  Events
    // ──────────────────────────────────────────────────────────────────

    /// <summary>
    /// Raised when a newer version is detected on GitHub.
    /// </summary>
    public event EventHandler<UpdateInfo>? UpdateAvailable;

    // ──────────────────────────────────────────────────────────────────
    //  Public state
    // ──────────────────────────────────────────────────────────────────

    /// <summary>
    /// The most recently detected update, or null if up-to-date.
    /// </summary>
    public UpdateInfo? LatestUpdate { get; private set; }

    /// <summary>
    /// True while an update check is in progress.
    /// </summary>
    public bool IsChecking { get; private set; }

    /// <summary>
    /// True while an installer download is in progress.
    /// </summary>
    public bool IsDownloading { get; private set; }

    // ──────────────────────────────────────────────────────────────────
    //  Constructor
    // ──────────────────────────────────────────────────────────────────

    public UpdateService(SettingsService settingsService)
    {
        _settingsService = settingsService ?? throw new ArgumentNullException(nameof(settingsService));
    }

    // ──────────────────────────────────────────────────────────────────
    //  Periodic check
    // ──────────────────────────────────────────────────────────────────

    /// <summary>
    /// Starts the periodic update check timer. First check runs after a startup delay,
    /// subsequent checks every <see cref="CheckInterval"/>.
    /// </summary>
    public void StartPeriodicCheck()
    {
        StopPeriodicCheck();
        _checkTimer = new Timer(OnTimerElapsed, null, StartupDelay, CheckInterval);
        AppLogger.Log($"Update checker started (first check in {StartupDelay.TotalSeconds}s, then every {CheckInterval.TotalHours}h).");
    }

    /// <summary>
    /// Stops the periodic update check timer.
    /// </summary>
    public void StopPeriodicCheck()
    {
        _checkTimer?.Dispose();
        _checkTimer = null;
    }

    private async void OnTimerElapsed(object? state)
    {
        try
        {
            await CheckForUpdateAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            AppLogger.Log("Periodic update check failed", ex);
        }
    }

    // ──────────────────────────────────────────────────────────────────
    //  Check for update
    // ──────────────────────────────────────────────────────────────────

    /// <summary>
    /// Checks GitHub for a newer release. Returns <see cref="UpdateInfo"/> if an update
    /// is available, or <c>null</c> if already on the latest version.
    /// </summary>
    public async Task<UpdateInfo?> CheckForUpdateAsync()
    {
        if (IsChecking)
            return LatestUpdate;

        IsChecking = true;

        try
        {
            AppLogger.Log("Checking for updates...");

            using var response = await _httpClient.GetAsync(GitHubApiUrl).ConfigureAwait(false);

            if ((int)response.StatusCode == 403 || (int)response.StatusCode == 429)
            {
                AppLogger.Log($"GitHub API rate limited (HTTP {(int)response.StatusCode}). Skipping update check.");
                return null;
            }

            response.EnsureSuccessStatusCode();

            var json = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
            var release = JsonSerializer.Deserialize<GitHubRelease>(json);

            if (release is null || release.Draft || release.Prerelease)
            {
                AppLogger.Log("No stable release found or release is draft/prerelease.");
                return null;
            }

            var remoteVersion = ParseVersion(release.TagName);
            if (remoteVersion is null)
            {
                AppLogger.Log($"Could not parse version from tag: {release.TagName}");
                return null;
            }

            var currentVersion = GetCurrentVersion();
            AppLogger.Log($"Current: {currentVersion}, Latest: {remoteVersion}");

            if (remoteVersion <= currentVersion)
            {
                AppLogger.Log("Already up to date.");
                UpdateLastCheckTime();
                return null;
            }

            // Find the Setup installer asset
            var asset = release.Assets.FirstOrDefault(a =>
                a.Name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) &&
                a.Name.Contains("Setup", StringComparison.OrdinalIgnoreCase));

            if (asset is null)
            {
                AppLogger.Log("Update found but no Setup installer asset in the release.");
                return null;
            }

            var updateInfo = new UpdateInfo(
                NewVersion: remoteVersion,
                CurrentVersion: currentVersion,
                ReleaseName: release.Name,
                ReleaseNotes: release.Body,
                ReleaseUrl: release.HtmlUrl,
                DownloadUrl: asset.BrowserDownloadUrl,
                DownloadSize: asset.Size);

            LatestUpdate = updateInfo;
            UpdateLastCheckTime();

            AppLogger.Log($"Update available: v{remoteVersion} ({asset.Name}, {asset.Size / 1024}KB)");
            UpdateAvailable?.Invoke(this, updateInfo);

            return updateInfo;
        }
        catch (HttpRequestException ex)
        {
            AppLogger.Log($"Network error checking for updates: {ex.Message}");
            return null;
        }
        catch (JsonException ex)
        {
            AppLogger.Log($"Failed to parse GitHub release response: {ex.Message}");
            return null;
        }
        catch (TaskCanceledException)
        {
            AppLogger.Log("Update check timed out.");
            return null;
        }
        finally
        {
            IsChecking = false;
        }
    }

    // ──────────────────────────────────────────────────────────────────
    //  Download
    // ──────────────────────────────────────────────────────────────────

    /// <summary>
    /// Downloads the installer for the given update to a temp file.
    /// Returns the path to the downloaded installer.
    /// </summary>
    public async Task<string> DownloadUpdateAsync(UpdateInfo info, IProgress<double>? progress = null)
    {
        if (IsDownloading)
            throw new InvalidOperationException("A download is already in progress.");

        IsDownloading = true;

        try
        {
            var installerPath = Path.Combine(Path.GetTempPath(), "SimpleWhisper-Setup.exe");

            AppLogger.Log($"Downloading update from: {info.DownloadUrl}");

            using var response = await _httpClient.GetAsync(info.DownloadUrl, HttpCompletionOption.ResponseHeadersRead).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();

            var totalBytes = response.Content.Headers.ContentLength ?? info.DownloadSize;

            await using var contentStream = await response.Content.ReadAsStreamAsync().ConfigureAwait(false);
            await using var fileStream = new FileStream(installerPath, FileMode.Create, FileAccess.Write, FileShare.None, 8192, useAsync: true);

            var buffer = new byte[8192];
            long bytesRead = 0;
            int read;

            while ((read = await contentStream.ReadAsync(buffer).ConfigureAwait(false)) > 0)
            {
                await fileStream.WriteAsync(buffer.AsMemory(0, read)).ConfigureAwait(false);
                bytesRead += read;

                if (totalBytes > 0)
                    progress?.Report((double)bytesRead / totalBytes * 100);
            }

            AppLogger.Log($"Update downloaded to: {installerPath} ({bytesRead / 1024}KB)");
            return installerPath;
        }
        finally
        {
            IsDownloading = false;
        }
    }

    // ──────────────────────────────────────────────────────────────────
    //  Install
    // ──────────────────────────────────────────────────────────────────

    /// <summary>
    /// Launches the Inno Setup installer and shuts down the application.
    /// </summary>
    public static void LaunchInstallerAndExit(string installerPath, bool silent = true)
    {
        AppLogger.Log($"Launching installer: {installerPath} (silent={silent})");

        var arguments = silent ? "/VERYSILENT /CLOSEAPPLICATIONS" : "/CLOSEAPPLICATIONS";

        Process.Start(new ProcessStartInfo
        {
            FileName = installerPath,
            Arguments = arguments,
            UseShellExecute = true
        });

        Application.Current.Dispatcher.Invoke(() => Application.Current.Shutdown());
    }

    // ──────────────────────────────────────────────────────────────────
    //  Helpers
    // ──────────────────────────────────────────────────────────────────

    /// <summary>
    /// Parses a version from a GitHub tag name (e.g. "v1.2.0" → Version(1,2,0)).
    /// </summary>
    private static Version? ParseVersion(string tagName)
    {
        var cleaned = tagName.TrimStart('v', 'V');
        return Version.TryParse(cleaned, out var version) ? version : null;
    }

    /// <summary>
    /// Gets the current application version from the assembly metadata.
    /// </summary>
    private static Version GetCurrentVersion()
    {
        return typeof(UpdateService).Assembly.GetName().Version ?? new Version(1, 0, 0);
    }

    /// <summary>
    /// Records the current time as the last successful update check.
    /// </summary>
    private void UpdateLastCheckTime()
    {
        _settingsService.Settings.LastUpdateCheck = DateTime.UtcNow;
        try
        {
            _settingsService.Save();
        }
        catch (Exception ex)
        {
            AppLogger.Log($"Failed to save last update check time: {ex.Message}");
        }
    }

    // ──────────────────────────────────────────────────────────────────
    //  IDisposable
    // ──────────────────────────────────────────────────────────────────

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        StopPeriodicCheck();
    }
}
