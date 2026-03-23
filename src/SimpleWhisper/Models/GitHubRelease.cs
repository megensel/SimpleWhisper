using System.Text.Json.Serialization;

namespace SimpleWhisper.Models;

/// <summary>
/// Minimal model for the GitHub Releases API response (GET /repos/{owner}/{repo}/releases/latest).
/// </summary>
public sealed class GitHubRelease
{
    [JsonPropertyName("tag_name")]
    public string TagName { get; set; } = string.Empty;

    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("html_url")]
    public string HtmlUrl { get; set; } = string.Empty;

    [JsonPropertyName("body")]
    public string Body { get; set; } = string.Empty;

    [JsonPropertyName("prerelease")]
    public bool Prerelease { get; set; }

    [JsonPropertyName("draft")]
    public bool Draft { get; set; }

    [JsonPropertyName("assets")]
    public List<GitHubAsset> Assets { get; set; } = [];
}

/// <summary>
/// A single downloadable asset attached to a GitHub release.
/// </summary>
public sealed class GitHubAsset
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("browser_download_url")]
    public string BrowserDownloadUrl { get; set; } = string.Empty;

    [JsonPropertyName("size")]
    public long Size { get; set; }

    [JsonPropertyName("content_type")]
    public string ContentType { get; set; } = string.Empty;
}

/// <summary>
/// Describes an available update, including version info and download details.
/// </summary>
public sealed record UpdateInfo(
    Version NewVersion,
    Version CurrentVersion,
    string ReleaseName,
    string ReleaseNotes,
    string ReleaseUrl,
    string DownloadUrl,
    long DownloadSize);
