using System.Buffers;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace SmartClean.Core.Updates;

public sealed record ReleaseInfo(Version Version, string Tag, Uri InstallerUrl,
    string Sha256, long ByteCount, Uri ReleasePage);

/// <summary>
/// Public, stable GitHub releases only. No GitHub token is stored or requested.
/// Never executes an update: the UI must ask for consent before launching its installer.
/// </summary>
public sealed class ReleaseClient : IDisposable
{
    public const string AssetName = "SmartClean-Setup.exe";
    public const string OwnerAndRepo = "3AYZE/SmartClean";
    private static readonly Uri LatestReleaseUri =
        new("https://api.github.com/repos/3AYZE/SmartClean/releases/latest");
    private static readonly Regex Sha256Pattern = new("^[0-9a-fA-F]{64}$", RegexOptions.CultureInvariant);
    private readonly HttpClient _http;
    private readonly bool _ownsClient;

    public ReleaseClient(HttpClient? http = null)
    {
        _ownsClient = http is null;
        _http = http ?? new HttpClient { Timeout = TimeSpan.FromMinutes(20) };
        _http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("SmartClean", "0.3"));
        _http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
    }

    public static Version InstalledVersion =>
        typeof(ReleaseClient).Assembly.GetName().Version ?? new Version(0, 0, 0, 0);

    public async Task<ReleaseInfo> GetLatestAsync(CancellationToken cancellationToken = default)
    {
        // The timeout for checking metadata is short; installer downloads get a separate budget.
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        limit.CancelAfter(TimeSpan.FromSeconds(15));
        using var request = new HttpRequestMessage(HttpMethod.Get, LatestReleaseUri);
        request.Headers.CacheControl = new CacheControlHeaderValue { NoCache = true };
        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, limit.Token)
            .ConfigureAwait(false);
        if (response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Unauthorized)
            throw new UpdateException("No public release is accessible. If the repository is private, public update checking is unavailable.");
        if (response.StatusCode == HttpStatusCode.Forbidden)
            throw new UpdateException("GitHub denied the update check. Try again later (the API may be rate-limited).");
        response.EnsureSuccessStatusCode();
        await using var body = await response.Content.ReadAsStreamAsync(limit.Token).ConfigureAwait(false);
        using var json = await JsonDocument.ParseAsync(body, cancellationToken: limit.Token).ConfigureAwait(false);
        return ParseLatestRelease(json.RootElement);
    }

    public static ReleaseInfo ParseLatestRelease(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object ||
            !root.TryGetProperty("tag_name", out var tagValue) || tagValue.ValueKind != JsonValueKind.String)
            throw new UpdateException("The GitHub release response has no usable version tag.");
        if (root.TryGetProperty("draft", out var draft) && draft.ValueKind == JsonValueKind.True ||
            root.TryGetProperty("prerelease", out var prerelease) && prerelease.ValueKind == JsonValueKind.True)
            throw new UpdateException("Only published stable releases may be installed.");
        string tag = tagValue.GetString()!;
        string versionText = tag.StartsWith('v') ? tag[1..] : tag;
        if (!Version.TryParse(versionText, out var version) || version.Major < 0 ||
            version.Build < 0 || version.Revision < 0)
            throw new UpdateException("The release version has an unsupported format.");
        if (!root.TryGetProperty("assets", out var assets) || assets.ValueKind != JsonValueKind.Array)
            throw new UpdateException("The release is missing an installer asset.");

        foreach (var asset in assets.EnumerateArray())
        {
            if (!asset.TryGetProperty("name", out var name) || name.GetString() != AssetName) continue;
            if (!asset.TryGetProperty("browser_download_url", out var value) ||
                !Uri.TryCreate(value.GetString(), UriKind.Absolute, out var uri) ||
                uri.Scheme != Uri.UriSchemeHttps || !uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase) ||
                uri.AbsolutePath != $"/{OwnerAndRepo}/releases/download/{Uri.EscapeDataString(tag)}/{AssetName}")
                throw new UpdateException("The release installer URL does not match the expected repository.");
            string digest = asset.TryGetProperty("digest", out var digestValue) &&
                digestValue.ValueKind == JsonValueKind.String ? digestValue.GetString()! : "";
            if (!digest.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase) ||
                !Sha256Pattern.IsMatch(digest[7..]))
                throw new UpdateException("GitHub has not provided a valid SHA-256 digest for the installer. Refusing to download.");
            if (!asset.TryGetProperty("size", out var sizeValue) || !sizeValue.TryGetInt64(out long size) ||
                size is < 1_000_000 or > 900_000_000)
                throw new UpdateException("The release installer has an unexpected size.");
            return new ReleaseInfo(version, tag, uri, digest[7..].ToLowerInvariant(), size,
                new Uri($"https://github.com/{OwnerAndRepo}/releases/tag/{Uri.EscapeDataString(tag)}"));
        }
        throw new UpdateException("The release contains no SmartClean-Setup.exe installer.");
    }

    public async Task<string> DownloadAndVerifyAsync(ReleaseInfo release,
        IProgress<double>? progress = null, CancellationToken cancellationToken = default)
    {
        // Download to a private per-user staging directory, never into the running app directory.
        string folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "SmartClean", "updates", release.Version.ToString());
        Directory.CreateDirectory(folder);
        string installer = Path.Combine(folder, AssetName);
        if (File.Exists(installer) && await MatchesAsync(installer, release, cancellationToken).ConfigureAwait(false))
            return installer;
        string pending = Path.Combine(folder, $"{Guid.NewGuid():N}.download");
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, release.InstallerUrl);
            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            await using var source = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            await using (var target = new FileStream(pending, FileMode.CreateNew, FileAccess.Write,
                             FileShare.None, 128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan))
            using (var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256))
            {
                var buffer = ArrayPool<byte>.Shared.Rent(128 * 1024);
                try
                {
                    long written = 0;
                    int count;
                    while ((count = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
                    {
                        written += count;
                        if (written > release.ByteCount || written > 900_000_000)
                            throw new UpdateException("The downloaded installer is larger than GitHub reported.");
                        hash.AppendData(buffer, 0, count);
                        await target.WriteAsync(buffer.AsMemory(0, count), cancellationToken).ConfigureAwait(false);
                        progress?.Report((double)written / release.ByteCount);
                    }
                    await target.FlushAsync(cancellationToken).ConfigureAwait(false);
                    if (written != release.ByteCount ||
                        !CryptographicOperations.FixedTimeEquals(
                            Convert.FromHexString(release.Sha256), hash.GetHashAndReset()))
                        throw new UpdateException("The installer did not match GitHub's size and SHA-256 digest.");
                }
                finally { ArrayPool<byte>.Shared.Return(buffer); }
            }
            File.Move(pending, installer, overwrite: true);
            return installer;
        }
        finally
        {
            try { if (File.Exists(pending)) File.Delete(pending); }
            catch (IOException) { /* Cleanup only; never mask the original error. */ }
        }
    }

    private static async Task<bool> MatchesAsync(string path, ReleaseInfo release, CancellationToken cancellationToken)
    {
        if (new FileInfo(path).Length != release.ByteCount) return false;
        await using var stream = File.OpenRead(path);
        byte[] computed = await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
        return CryptographicOperations.FixedTimeEquals(computed, Convert.FromHexString(release.Sha256));
    }

    public void Dispose() { if (_ownsClient) _http.Dispose(); }
}

public sealed class UpdateException(string message) : Exception(message) { }
