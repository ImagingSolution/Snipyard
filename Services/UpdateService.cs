using System;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Reflection;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Snipyard.Services;

/// <summary>A published release newer than the one running, and the file to fetch for it.</summary>
public sealed record UpdateInfo(
    Version Version,
    string TagName,
    string DownloadUrl,
    long Size,
    string ReleaseNotes,
    string HtmlUrl);

/// <summary>
/// Keeps the application up to date with its own GitHub releases.
///
/// Three steps, deliberately kept apart so a failure in one of them costs nothing:
///   1. Ask the releases API what the latest version is. Every failure path ends at null and
///      nothing is shown - an update notice is a convenience, never something to apologise for.
///   2. Download the new executable next to the running one, verify it, and only then commit to
///      anything. Up to here the application is untouched and cancelling is free.
///   3. Swap and relaunch. Windows will not let a running image be deleted, but it will let one
///      be renamed, so the old executable steps aside rather than being replaced in place. If
///      the second move fails the first is undone, so there is no state where the application
///      has been removed but not put back.
///
/// Only the published single-file build can do step 3. A development build has its managed
/// assemblies on disk beside the host, and dropping an 80MB self-contained executable on top of
/// that would break the output folder until the next rebuild.
/// </summary>
public static class UpdateService
{
    private const string LatestUrl = "https://api.github.com/repos/ImagingSolution/Snipyard/releases/latest";

    /// <summary>The asset the releases carry. A release without it is not one this can install.</summary>
    private static readonly string AssetName = OperatingSystem.IsMacOS() ? MacAssetName : "Snipyard.exe";

    /// <summary>
    /// macOS releases ship the app bundle as a tarball per architecture (a tar keeps the
    /// executable bit that a zip made on Windows loses). The updater only needs the binary inside.
    /// </summary>
    public static string MacAssetName =>
        $"Snipyard-macos-{(System.Runtime.InteropServices.RuntimeInformation.OSArchitecture == System.Runtime.InteropServices.Architecture.Arm64 ? "arm64" : "x64")}.tar.gz";

    /// <summary>Where the executable sits inside the bundle, as the tarball spells it.</summary>
    private const string MacBinaryEntry = "Snipyard.app/Contents/MacOS/Snipyard";

    /// <summary>
    /// The asset's name before the app was renamed from Claucraft. Releases made during the
    /// transition carry both, so builds from before the rename can still find their update;
    /// this side accepts the old one too, in case a release only has that.
    /// </summary>
    private const string LegacyAssetName = "Claucraft.exe";

    public const string ReleasesUrl = "https://github.com/ImagingSolution/Snipyard/releases";

    /// <summary>Staged download, kept beside the executable so the final step is a same-volume move.</summary>
    private static readonly string StageName = OperatingSystem.IsWindows() ? "Snipyard.new.exe" : "Snipyard.new";

    private const string OldSuffix = ".old";

    /// <summary>
    /// Test hook. The build number climbs on every local build, so a development copy is always
    /// ahead of whatever is published and the notice would never appear. With
    /// SNIPYARD_UPDATE_TEST=1 the running version reads as 0.0.0.0, which makes any release look
    /// newer, and the check runs even where <see cref="CanSelfUpdate"/> is false. The swap itself
    /// still refuses in that case, so a development build can show the notice and download
    /// without its output folder being replaced.
    /// </summary>
    public static bool TestMode =>
        Environment.GetEnvironmentVariable("SNIPYARD_UPDATE_TEST") == "1";

    /// <summary>
    /// Its own client rather than the one <see cref="RateLimitService"/> shares: that one is
    /// capped at three seconds, which is right for a status readout and hopeless for 80MB.
    /// Giving up is the job of the cancellation token here.
    /// </summary>
    private static readonly HttpClient Http = new() { Timeout = Timeout.InfiniteTimeSpan };

    /// <summary>
    /// The real build, whatever the test hook says. This is what the notice shows. Trimmed to
    /// three parts: the assembly always reports a fourth (0), but versions and tags are "1.0.X".
    /// </summary>
    public static Version RunningVersion { get; } =
        Assembly.GetExecutingAssembly().GetName().Version is { } v
            ? new Version(v.Major, v.Minor, Math.Max(v.Build, 0))
            : new Version(0, 0, 0);

    /// <summary>The version a release is measured against.</summary>
    public static Version CurrentVersion => TestMode ? new Version(0, 0, 0) : RunningVersion;

    private static readonly string UserAgent = $"Snipyard/{RunningVersion}";

    /// <summary>The running executable, or null in a host that does not report one.</summary>
    public static string? ExePath
    {
        get
        {
            var path = Environment.ProcessPath;
            return string.IsNullOrEmpty(path) ? null : path;
        }
    }

    private static string? InstallDir => ExePath is { } exe ? Path.GetDirectoryName(exe) : null;

    /// <summary>Where the download lands: beside the executable it is going to replace.</summary>
    public static string? StagePath => InstallDir is { } dir ? Path.Combine(dir, StageName) : null;

    /// <summary>
    /// True only for the published single-file build. A bundle has no assembly on disk to point
    /// at, so Location comes back empty; a development build has a real Snipyard.dll beside it
    /// and must not be overwritten.
    /// </summary>
    public static bool CanSelfUpdate =>
        // IL3000 warns that Location is always empty in a single-file app. That empty string is
        // exactly what is being asked for here - it is the one thing that tells the published
        // build apart from a development one.
#pragma warning disable IL3000
        ExePath is not null && string.IsNullOrEmpty(Assembly.GetExecutingAssembly().Location);
#pragma warning restore IL3000

    /// <summary>Whether to ask GitHub at all. The test hook lets a development build see the notice.</summary>
    public static bool CanCheck => CanSelfUpdate || (TestMode && ExePath is not null);

    /// <summary>
    /// The latest release if it is newer than what is running, otherwise null. Never throws: no
    /// network, a rate limit, a shape that has changed - all of them mean no notice.
    /// </summary>
    public static async Task<UpdateInfo?> CheckAsync(CancellationToken ct = default)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, LatestUrl);
            // GitHub answers 403 to a request that carries no user agent.
            request.Headers.UserAgent.ParseAdd(UserAgent);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(10));

            using var response = await Http.SendAsync(request, cts.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                Debug.WriteLine($"[UpdateService] releases/latest returned {(int)response.StatusCode}");
                return null;
            }

            var json = await response.Content.ReadAsStringAsync(cts.Token).ConfigureAwait(false);
            return Parse(json);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[UpdateService] Update check failed: {ex.GetType().Name}");
            return null;
        }
    }

    private static UpdateInfo? Parse(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;

            if (!TryString(root, "tag_name", out var tag)) return null;
            if (!TryParseTag(tag, out var version)) return null;
            if (version <= CurrentVersion) return null;

            if (!root.TryGetProperty("assets", out var assets) || assets.ValueKind != JsonValueKind.Array)
                return null;

            JsonElement? chosen = null;
            foreach (var asset in assets.EnumerateArray())
            {
                if (!TryString(asset, "name", out var name)) continue;
                if (!TryString(asset, "browser_download_url", out _)) continue;
                if (string.Equals(name, AssetName, StringComparison.OrdinalIgnoreCase))
                {
                    chosen = asset;
                    break;
                }
                if (chosen is null && OperatingSystem.IsWindows() && string.Equals(name, LegacyAssetName, StringComparison.OrdinalIgnoreCase))
                    chosen = asset;
            }
            if (chosen is not { } pick) return null;

            TryString(pick, "browser_download_url", out var url);
            var size = pick.TryGetProperty("size", out var s) && s.ValueKind == JsonValueKind.Number
                ? s.GetInt64()
                : 0;

            TryString(root, "body", out var notes);
            if (!TryString(root, "html_url", out var page)) page = ReleasesUrl;

            return new UpdateInfo(version, tag, url, size, notes.Trim(), page);
        }
        catch
        {
            return null;
        }
    }

    private static bool TryString(JsonElement parent, string name, out string value)
    {
        value = "";
        if (!parent.TryGetProperty(name, out var e) || e.ValueKind != JsonValueKind.String) return false;
        value = e.GetString() ?? "";
        return value.Length > 0;
    }

    /// <summary>Tags are written "v1.0.12"; the leading v is the only decoration.</summary>
    private static bool TryParseTag(string tag, out Version version)
    {
        var text = tag.TrimStart('v', 'V').Trim();
        return Version.TryParse(text, out version!);
    }

    /// <summary>
    /// Whether the executable can be replaced where it sits. Checked before the download rather
    /// than after, so an install under Program Files says so in a second instead of 80MB later.
    /// </summary>
    public static bool CanWriteToInstallDir()
    {
        if (StagePath is not { } stage) return false;
        try
        {
            using (new FileStream(stage, FileMode.Create, FileAccess.Write, FileShare.None)) { }
            TryDelete(stage);
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Fetches the new executable to <see cref="StagePath"/> and verifies it. Throws on any
    /// failure, having removed the partial file first - the application itself is untouched
    /// either way.
    /// </summary>
    public static async Task<string> DownloadAsync(
        UpdateInfo info, IProgress<(long Read, long Total)>? progress, CancellationToken ct)
    {
        if (StagePath is not { } stage)
            throw new InvalidOperationException("No install directory to download into.");

        // A previous attempt may have been cut short; start from nothing rather than append.
        TryDelete(stage);

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, info.DownloadUrl);
            request.Headers.UserAgent.ParseAdd(UserAgent);

            using var response = await Http
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct)
                .ConfigureAwait(false);
            response.EnsureSuccessStatusCode();

            var total = response.Content.Headers.ContentLength ?? info.Size;
            long read = 0;
            progress?.Report((0, total));

            var download = OperatingSystem.IsWindows() ? stage : stage + ".tar.gz";
            await using (var source = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false))
            await using (var target = new FileStream(
                download, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16, useAsync: true))
            {
                var buffer = new byte[1 << 16];
                int n;
                while ((n = await source.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
                {
                    await target.WriteAsync(buffer.AsMemory(0, n), ct).ConfigureAwait(false);
                    read += n;
                    progress?.Report((read, total));
                }
            }

            if (OperatingSystem.IsWindows())
            {
                Verify(stage, info.Size);
                return stage;
            }

            if (info.Size > 0 && new FileInfo(download).Length != info.Size)
                throw new IOException($"Downloaded {new FileInfo(download).Length} bytes, expected {info.Size}.");
            ExtractMacBinary(download, stage);
            TryDelete(download);
            VerifyMachO(stage);
            return stage;
        }
        catch
        {
            TryDelete(stage);
            TryDelete(stage + ".tar.gz");
            throw;
        }
    }

    /// <summary>
    /// A truncated download is the failure worth catching here: it would otherwise be moved into
    /// place and leave an application that cannot start. The size the release reports and a PE
    /// header are enough to tell a whole executable from half of one.
    /// </summary>
    private static void Verify(string path, long expectedSize)
    {
        var info = new FileInfo(path);
        if (!info.Exists) throw new IOException("The download did not produce a file.");
        if (expectedSize > 0 && info.Length != expectedSize)
            throw new IOException($"Downloaded {info.Length} bytes, expected {expectedSize}.");

        using var stream = File.OpenRead(path);
        if (stream.ReadByte() != 'M' || stream.ReadByte() != 'Z')
            throw new IOException("The download is not a Windows executable.");
    }

    private static void ExtractMacBinary(string archive, string target)
    {
        using var file = File.OpenRead(archive);
        using var gzip = new System.IO.Compression.GZipStream(file, System.IO.Compression.CompressionMode.Decompress);
        using var tar = new System.Formats.Tar.TarReader(gzip);
        while (tar.GetNextEntry() is { } entry)
        {
            if (entry.Name.TrimStart('.', '/') != MacBinaryEntry || entry.DataStream == null) continue;
            using var output = File.Create(target);
            entry.DataStream.CopyTo(output);
            return;
        }
        throw new IOException("The download does not contain " + MacBinaryEntry + ".");
    }

    /// <summary>64-bit Mach-O starts with 0xFEEDFACF, stored little-endian on both Mac architectures.</summary>
    private static void VerifyMachO(string path)
    {
        using var stream = File.OpenRead(path);
        Span<byte> magic = stackalloc byte[4];
        if (stream.Read(magic) != 4 || magic[0] != 0xCF || magic[1] != 0xFA || magic[2] != 0xED || magic[3] != 0xFE)
            throw new IOException("The download is not a macOS executable.");
    }

    /// <summary>
    /// Puts the downloaded executable in place and starts it. Call this with the windows already
    /// closed: it replaces the image this process is running from.
    /// </summary>
    public static void SwapAndRestart(string stagedPath)
    {
        if (ExePath is not { } exe)
            throw new InvalidOperationException("No executable to replace.");

        var old = exe + OldSuffix;
        TryDelete(old);

        // The running image cannot be deleted, but it can be renamed out of the way - which is
        // the whole trick behind an application replacing itself.
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(stagedPath, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
                | UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);

        File.Move(exe, old);
        try
        {
            File.Move(stagedPath, exe);
        }
        catch
        {
            // Never leave the user without an application: put the old one back and report the
            // failure rather than exiting into an empty folder.
            try { File.Move(old, exe); } catch { }
            throw;
        }

        if (OperatingSystem.IsMacOS())
        {
            // Relaunch the bundle the way Finder would, so the Dock sees an app and not a bare binary.
            var bundle = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(exe)!, "..", ".."));
            var psi = new ProcessStartInfo("open") { UseShellExecute = false };
            if (bundle.EndsWith(".app", StringComparison.OrdinalIgnoreCase)) { psi.ArgumentList.Add("-n"); psi.ArgumentList.Add(bundle); }
            else psi.FileName = exe;
            Process.Start(psi);
            return;
        }

        Process.Start(new ProcessStartInfo
        {
            FileName = exe,
            UseShellExecute = true,
            WorkingDirectory = Path.GetDirectoryName(exe) ?? "",
        });
    }

    /// <summary>
    /// Clears what the last update left behind. The renamed executable cannot be deleted while it
    /// is still running, so this runs at the start of the run after it.
    /// </summary>
    public static void CleanupOldExe()
    {
        if (ExePath is { } exe) TryDelete(exe + OldSuffix);
        if (StagePath is { } stage) TryDelete(stage);
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch
        {
            // Still locked, or not ours to remove. Either way it is litter, not a failure.
        }
    }

    /// <summary>Byte count for a progress readout: "84.1 MB", "912 KB".</summary>
    public static string FormatSize(long bytes)
    {
        if (bytes >= 1024L * 1024 * 1024) return $"{bytes / (1024.0 * 1024 * 1024):0.0} GB";
        if (bytes >= 1024 * 1024) return $"{bytes / (1024.0 * 1024):0.0} MB";
        if (bytes >= 1024) return $"{bytes / 1024.0:0} KB";
        return $"{bytes} B";
    }
}
