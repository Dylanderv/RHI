using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;

namespace RenoDXCommander.Services;

/// <summary>
/// Manages the 20/30 FG Unlock component (dlssg_for_sm86).
/// GitHub: sdli1995/dlssg_for_sm86.
/// Since 0.4.0 a release is three things that must all sit beside the rendering exe:
/// the proxy DLL, dlssg_sm86.ini, and the whole dlssg_sm86\ folder (backend, kernel pack,
/// bundled runtime and the optional SR/XeSS packs). Copying only the proxy does nothing.
/// Releases also carry the alternate proxy names in alternatives\.
/// </summary>
public class Dlssg20_30Service
{
    // ── Constants ─────────────────────────────────────────────────────────────

    private const string RepoOwner          = "sdli1995";
    private const string RepoName           = "dlssg_for_sm86";
    private const string LatestReleaseApiUrl = $"https://api.github.com/repos/{RepoOwner}/{RepoName}/releases/latest";

    public const string StagedDllName = "version.dll";
    public const string IniFileName   = "dlssg_sm86.ini";
    public const string ContentDirName = "dlssg_sm86";

    private const string AlternativesDirName = "alternatives";

    public const string GpuGenRtx30 = "RTX 30 Series";
    public const string GpuGenRtx20 = "RTX 20 Series";

    /// <summary>All valid proxy DLL names (version.dll default + alternatives folder).</summary>
    public static readonly string[] ProxyNames =
    {
        "version.dll", "winmm.dll", "dinput8.dll", "winhttp.dll", "dxgi.dll",
    };

    /// <summary>Files that must be present before staging is considered usable.</summary>
    private static readonly string[] RequiredStagedFiles =
    {
        StagedDllName,
        IniFileName,
        Path.Combine(ContentDirName, "backend", "sm86_backend.dll"),
        Path.Combine(ContentDirName, "kernels", "fg_kernels.dll"),
        Path.Combine(ContentDirName, "runtime", "nvngx_dlssg.dll"),
    };

    // ── Instance state ─────────────────────────────────────────────────────────

    private readonly HttpClient      _http;
    private readonly GitHubETagCache _etagCache;
    private readonly ICrashReporter  _crashReporter;
    private readonly string          _stagingDir;
    private readonly string          _versionFile;

    public Dlssg20_30Service(HttpClient http, ICrashReporter crashReporter, GitHubETagCache etagCache)
    {
        _http          = http;
        _etagCache     = etagCache;
        _crashReporter = crashReporter;
        _stagingDir    = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "RHI", "dlssg2030");
        _versionFile   = Path.Combine(_stagingDir, "version.txt");
    }

    public bool IsStagingReady =>
        RequiredStagedFiles.All(f => File.Exists(Path.Combine(_stagingDir, f)));

    /// <summary>Release version of the staged package (e.g. "0.4.1"), or null if not staged.</summary>
    public string? StagedVersion => File.Exists(_versionFile)
        ? File.ReadAllText(_versionFile).Trim() : null;

    public bool     HasUpdate     { get; private set; }
    public string?  LatestVersion { get; private set; }

    // ── Detection ─────────────────────────────────────────────────────────────

    public bool IsInstalledIn(string installPath, string? installedAs)
        => !string.IsNullOrEmpty(installPath) && !string.IsNullOrEmpty(installedAs)
        && File.Exists(Path.Combine(installPath, installedAs));

    /// <summary>
    /// True when an installed game is missing files from the current release (for example a
    /// 0.4.0 install made before RHI copied the dlssg_sm86\ folder with the proxy).
    /// </summary>
    public bool IsOutdatedIn(string installPath, string? installedAs)
    {
        if (!IsStagingReady || string.IsNullOrEmpty(installPath) || string.IsNullOrEmpty(installedAs))
            return false;

        // A removed proxy means the user uninstalled it: don't put it back.
        var proxyDest = Path.Combine(installPath, installedAs);
        if (!File.Exists(proxyDest)) return false;

        try
        {
            var proxySource = ResolveProxySource(installedAs);
            if (proxySource == null || !SameFile(proxySource, proxyDest)) return true;

            if (!File.Exists(Path.Combine(installPath, IniFileName))) return true;

            var srcRoot = Path.Combine(_stagingDir, ContentDirName);
            foreach (var src in Directory.EnumerateFiles(srcRoot, "*", SearchOption.AllDirectories))
            {
                var rel  = Path.GetRelativePath(srcRoot, src);
                var dest = FileHelper.SafeCombine(Path.Combine(installPath, ContentDirName), rel);
                if (dest == null || !File.Exists(dest) || !SameFile(src, dest)) return true;
            }

            return false;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _crashReporter.Log($"[Dlssg20_30Service.IsOutdatedIn] Could not inspect '{installPath}' — {ex.Message}");
            return false;
        }
    }

    // ── Staging ───────────────────────────────────────────────────────────────

    public async Task CheckForUpdateAsync()
    {
        var (version, _) = await FetchLatestReleaseInfoAsync().ConfigureAwait(false);
        if (string.IsNullOrEmpty(version)) return;

        LatestVersion = version;
        var current = StagedVersion;
        HasUpdate = !string.Equals(current, version, StringComparison.OrdinalIgnoreCase);
        _crashReporter.Log($"[Dlssg20_30Service.CheckForUpdate] Cached={current ?? "(none)"}, Remote={version}, HasUpdate={HasUpdate}");
    }

    public async Task EnsureStagingAsync(IProgress<(string message, double percent)>? progress = null)
    {
        if (IsStagingReady && !HasUpdate)
        {
            _crashReporter.Log("[Dlssg20_30Service.EnsureStaging] Already up to date");
            return;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(_stagingDir)!);

        var (version, downloadUrl) = await FetchLatestReleaseInfoAsync().ConfigureAwait(false);
        if (string.IsNullOrEmpty(version) || string.IsNullOrEmpty(downloadUrl))
        {
            _crashReporter.Log("[Dlssg20_30Service.EnsureStaging] Could not resolve latest release");
            return;
        }

        LatestVersion = version;

        var zipPath  = Path.Combine(Path.GetTempPath(), $"rhi-dlssg2030-{Guid.NewGuid():N}.zip");
        var newDir   = _stagingDir + ".new";

        try
        {
            progress?.Report(("Downloading 20/30 FG Unlock...", 5));
            if (!await DownloadReleaseAsync(downloadUrl, zipPath, progress).ConfigureAwait(false))
                return;

            if (Directory.Exists(newDir)) Directory.Delete(newDir, recursive: true);
            Directory.CreateDirectory(newDir);

            progress?.Report(("Extracting 20/30 FG Unlock...", 55));
            var extracted = ExtractRelease(zipPath, newDir);
            if (extracted == 0)
            {
                _crashReporter.Log("[Dlssg20_30Service.EnsureStaging] Release zip contained none of the expected files");
                return;
            }

            var missing = RequiredStagedFiles
                .Where(f => !File.Exists(Path.Combine(newDir, f)))
                .ToList();
            if (missing.Count > 0)
            {
                // Don't record the new version — what we extracted cannot run.
                _crashReporter.Log($"[Dlssg20_30Service.EnsureStaging] {version} package is missing [{string.Join(", ", missing)}] — keeping staged {StagedVersion ?? "(none)"}");
                return;
            }

            progress?.Report(("Installing 20/30 FG Unlock files...", 90));
            SwapIntoStaging(newDir);
            File.WriteAllText(_versionFile, version);

            LatestVersion = version;
            HasUpdate = false;
            _crashReporter.Log($"[Dlssg20_30Service.EnsureStaging] Staged {version} ({extracted} files)");
            progress?.Report(("20/30 FG Unlock ready", 100));
        }
        catch (Exception ex)
        {
            _crashReporter.Log($"[Dlssg20_30Service.EnsureStaging] Failed — {ex.Message}");
        }
        finally
        {
            try { if (File.Exists(zipPath)) File.Delete(zipPath); }
            catch (Exception ex) { _crashReporter.Log($"[Dlssg20_30Service.EnsureStaging] Could not delete '{zipPath}' — {ex.Message}"); }

            try { if (Directory.Exists(newDir)) Directory.Delete(newDir, recursive: true); }
            catch (Exception ex) { _crashReporter.Log($"[Dlssg20_30Service.EnsureStaging] Could not delete '{newDir}' — {ex.Message}"); }
        }
    }

    // ── Install ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Deploys the proxy DLL (renamed to dllName), dlssg_sm86.ini and the dlssg_sm86\ folder
    /// to installPath. Writes Router=SM86/SM75 to the INI based on gpuGen.
    /// </summary>
    public bool Install(string installPath, string dllName, string gpuGen)
    {
        if (string.IsNullOrEmpty(installPath) || !IsStagingReady) return false;

        var proxySource = ResolveProxySource(dllName);
        if (proxySource == null)
        {
            _crashReporter.Log($"[Dlssg20_30Service.Install] No staged proxy for '{dllName}'");
            return false;
        }

        try
        {
            var dllDest = Path.Combine(installPath, dllName);
            var iniDest = Path.Combine(installPath, IniFileName);

            // Sentinel backup for the DLL slot
            AuxInstallService.SentinelBackup(dllDest);
            File.Copy(proxySource, dllDest, overwrite: true);
            _crashReporter.Log($"[Dlssg20_30Service.Install] Deployed as '{dllName}' to '{installPath}'");

            DeployContentFolder(installPath);

            // Deploy INI then apply router setting
            File.Copy(Path.Combine(_stagingDir, IniFileName), iniDest, overwrite: true);
            ApplyRouterToIni(iniDest, gpuGen);
            _crashReporter.Log($"[Dlssg20_30Service.Install] Deployed {IniFileName} with Router for {gpuGen}");

            return true;
        }
        catch (Exception ex)
        {
            _crashReporter.Log($"[Dlssg20_30Service.Install] Failed — {ex.Message}");
            return false;
        }
    }

    // ── Update ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// Updates an already-installed game: overwrites the proxy, the dlssg_sm86\ folder and the
    /// INI (preserves user's Router/GPU gen setting while picking up any new upstream keys).
    /// </summary>
    public bool Update(string installPath, string installedAs, string gpuGen)
    {
        if (string.IsNullOrEmpty(installPath) || !IsStagingReady) return false;

        var proxySource = ResolveProxySource(installedAs);
        if (proxySource == null)
        {
            _crashReporter.Log($"[Dlssg20_30Service.Update] No staged proxy for '{installedAs}'");
            return false;
        }

        try
        {
            var dllDest = Path.Combine(installPath, installedAs);
            var iniDest = Path.Combine(installPath, IniFileName);

            // Overwrite DLL — no new sentinel (original sentinel from first install preserved)
            File.Copy(proxySource, dllDest, overwrite: true);
            _crashReporter.Log($"[Dlssg20_30Service.Update] Updated DLL '{installedAs}' in '{installPath}'");

            DeployContentFolder(installPath);

            // Merge INI: deploy fresh staging INI (picks up new settings), then re-apply router
            File.Copy(Path.Combine(_stagingDir, IniFileName), iniDest, overwrite: true);
            ApplyRouterToIni(iniDest, gpuGen);
            _crashReporter.Log($"[Dlssg20_30Service.Update] Merged INI with Router for {gpuGen}");

            return true;
        }
        catch (Exception ex)
        {
            _crashReporter.Log($"[Dlssg20_30Service.Update] Failed — {ex.Message}");
            return false;
        }
    }

    // ── Uninstall ─────────────────────────────────────────────────────────────

    public void Uninstall(string installPath, string? installedAs)
    {
        if (string.IsNullOrEmpty(installPath)) return;
        try
        {
            if (!string.IsNullOrEmpty(installedAs))
                AuxInstallService.SentinelRestore(Path.Combine(installPath, installedAs));

            var iniPath = Path.Combine(installPath, IniFileName);
            try { if (File.Exists(iniPath)) File.Delete(iniPath); } catch (Exception ex) { _crashReporter.Log($"[Dlssg20_30Service.Uninstall] Failed to delete '{IniFileName}' — {ex.Message}"); }

            var contentDir = Path.Combine(installPath, ContentDirName);
            try { if (Directory.Exists(contentDir)) Directory.Delete(contentDir, recursive: true); } catch (Exception ex) { _crashReporter.Log($"[Dlssg20_30Service.Uninstall] Failed to delete '{ContentDirName}' — {ex.Message}"); }

            _crashReporter.Log($"[Dlssg20_30Service.Uninstall] Removed from '{installPath}'");
        }
        catch (Exception ex)
        {
            _crashReporter.Log($"[Dlssg20_30Service.Uninstall] Failed — {ex.Message}");
        }
    }

    // ── INI helper ────────────────────────────────────────────────────────────

    /// <summary>
    /// Writes the correct Router value to the [Compatibility] section of the deployed INI.
    /// RTX 30 → SM86, RTX 20 → SM75. Public so the Extras cog can call it immediately on GPU gen change.
    /// </summary>
    public static void ApplyRouterToIniPublic(string iniPath, string gpuGen)
        => ApplyRouterToIni(iniPath, gpuGen);

    private static void ApplyRouterToIni(string iniPath, string gpuGen)
    {
        if (!File.Exists(iniPath)) return;

        var router = gpuGen.Equals(GpuGenRtx20, StringComparison.OrdinalIgnoreCase) ? "SM75" : "SM86";

        try
        {
            var lines = File.ReadAllLines(iniPath).ToList();
            bool inCompatibility = false;
            bool written = false;

            for (int i = 0; i < lines.Count; i++)
            {
                var trimmed = lines[i].Trim();

                if (trimmed.StartsWith("[", StringComparison.Ordinal))
                    inCompatibility = trimmed.Equals("[Compatibility]", StringComparison.OrdinalIgnoreCase);

                if (inCompatibility && trimmed.StartsWith("Router=", StringComparison.OrdinalIgnoreCase))
                {
                    lines[i] = $"Router={router}";
                    written = true;
                    break;
                }
            }

            // If no Router key found, append it to [Compatibility] (0.4.x factory INIs omit the
            // whole section, so create it instead of silently dropping the selected GPU generation).
            if (!written)
            {
                var compatIdx = lines.FindIndex(l => l.Trim().Equals("[Compatibility]", StringComparison.OrdinalIgnoreCase));
                if (compatIdx >= 0)
                    lines.Insert(compatIdx + 1, $"Router={router}");
                else
                {
                    if (lines.Count > 0 && !string.IsNullOrWhiteSpace(lines[^1])) lines.Add("");
                    lines.Add("[Compatibility]");
                    lines.Add($"Router={router}");
                }
            }

            File.WriteAllLines(iniPath, lines);
        }
        catch (Exception ex)
        {
            CrashReporter.Log($"[Dlssg20_30Service.ApplyRouterToIni] Failed — {ex.Message}");
        }
    }

    // ── Private helpers ───────────────────────────────────────────────────────

    private string? ResolveProxySource(string dllName)
    {
        // Prefer the matching alternatives\ proxy: each name forwards to its own system DLL.
        var alt = FileHelper.SafeCombine(Path.Combine(_stagingDir, AlternativesDirName), dllName);
        if (alt != null && File.Exists(alt)) return alt;

        // Older staging (and names the release does not ship) fall back to version.dll.
        var root = Path.Combine(_stagingDir, StagedDllName);
        return File.Exists(root) ? root : null;
    }

    private void DeployContentFolder(string installPath)
    {
        var srcRoot = Path.Combine(_stagingDir, ContentDirName);
        var dstRoot = Path.Combine(installPath, ContentDirName);
        Directory.CreateDirectory(dstRoot);

        foreach (var src in Directory.EnumerateFiles(srcRoot, "*", SearchOption.AllDirectories))
        {
            var rel  = Path.GetRelativePath(srcRoot, src);
            var dest = FileHelper.SafeCombine(dstRoot, rel);
            if (dest == null)
            {
                _crashReporter.Log($"[Dlssg20_30Service.DeployContentFolder] Skipping unsafe path '{rel}'");
                continue;
            }

            var dir = Path.GetDirectoryName(dest);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            File.Copy(src, dest, overwrite: true);
        }

        _crashReporter.Log($"[Dlssg20_30Service.DeployContentFolder] Deployed {ContentDirName}\\ to '{installPath}'");
    }

    private static bool SameFile(string source, string destination)
    {
        var a = new FileInfo(source);
        var b = new FileInfo(destination);
        return a.Length == b.Length && a.LastWriteTimeUtc == b.LastWriteTimeUtc;
    }

    private static bool IsWantedReleaseEntry(string relativePath)
    {
        if (relativePath.Equals(StagedDllName, StringComparison.OrdinalIgnoreCase)) return true;
        if (relativePath.Equals(IniFileName, StringComparison.OrdinalIgnoreCase)) return true;
        if (relativePath.StartsWith(ContentDirName + "/", StringComparison.OrdinalIgnoreCase)) return true;
        if (relativePath.StartsWith(AlternativesDirName + "/", StringComparison.OrdinalIgnoreCase)
            && relativePath.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    private int ExtractRelease(string zipPath, string destRoot)
    {
        using var zip = ZipFile.OpenRead(zipPath);

        // Releases normally have their files at the zip root. If upstream wraps them in a
        // folder, strip that common prefix so the staging layout stays the same.
        var prefix = "";
        var versionEntry = zip.Entries.FirstOrDefault(e =>
            string.Equals(Path.GetFileName(e.FullName), StagedDllName, StringComparison.OrdinalIgnoreCase)
            && !e.FullName.Replace('\\', '/').Contains("/" + AlternativesDirName + "/", StringComparison.OrdinalIgnoreCase));
        if (versionEntry != null)
        {
            var full = versionEntry.FullName.Replace('\\', '/');
            prefix = full[..^StagedDllName.Length];
        }

        int extracted = 0;
        foreach (var entry in zip.Entries)
        {
            if (string.IsNullOrEmpty(entry.Name)) continue;

            var rel = entry.FullName.Replace('\\', '/');
            if (prefix.Length > 0 && rel.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                rel = rel[prefix.Length..];
            rel = rel.TrimStart('/');

            if (rel.Length == 0 || !IsWantedReleaseEntry(rel)) continue;

            var dest = FileHelper.SafeCombine(destRoot, rel);
            if (dest == null)
            {
                _crashReporter.Log($"[Dlssg20_30Service.ExtractRelease] Skipping unsafe archive path '{entry.FullName}'");
                continue;
            }

            var dir = Path.GetDirectoryName(dest);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            entry.ExtractToFile(dest, overwrite: true);
            extracted++;
        }

        return extracted;
    }

    private void SwapIntoStaging(string newDir)
    {
        var oldDir = _stagingDir + ".old";

        if (Directory.Exists(oldDir)) Directory.Delete(oldDir, recursive: true);
        if (Directory.Exists(_stagingDir)) Directory.Move(_stagingDir, oldDir);

        try
        {
            Directory.Move(newDir, _stagingDir);
        }
        catch
        {
            if (!Directory.Exists(_stagingDir) && Directory.Exists(oldDir))
                Directory.Move(oldDir, _stagingDir);
            throw;
        }

        if (Directory.Exists(oldDir)) Directory.Delete(oldDir, recursive: true);
    }

    private async Task<(string? version, string? downloadUrl)> FetchLatestReleaseInfoAsync()
    {
        try
        {
            var json = await _etagCache.GetWithETagAsync(_http, LatestReleaseApiUrl).ConfigureAwait(false);
            if (json == null)
            {
                _crashReporter.Log("[Dlssg20_30Service.FetchLatestRelease] request failed");
                return (null, null);
            }

            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            var tag = root.TryGetProperty("tag_name", out var tagProp) ? tagProp.GetString() : null;
            if (string.IsNullOrEmpty(tag)) return (null, null);

            if (!root.TryGetProperty("assets", out var assets)) return (tag, null);

            var preferred = $"dlssg-release-{tag}-x64.zip";
            string? fallback = null;
            foreach (var asset in assets.EnumerateArray())
            {
                var name = asset.TryGetProperty("name", out var nameProp) ? nameProp.GetString() : null;
                if (string.IsNullOrEmpty(name)) continue;

                var url = asset.TryGetProperty("browser_download_url", out var urlProp) ? urlProp.GetString() : null;
                if (string.IsNullOrEmpty(url)) continue;

                if (name.Equals(preferred, StringComparison.OrdinalIgnoreCase))
                    return (tag, url);

                // The 310.9 alternative package is not the default runtime; only use it if nothing else matches.
                if (fallback == null
                    && name.StartsWith("dlssg-release-", StringComparison.OrdinalIgnoreCase)
                    && name.EndsWith("-x64.zip", StringComparison.OrdinalIgnoreCase)
                    && !name.Contains("-310.9", StringComparison.OrdinalIgnoreCase))
                    fallback = url;
            }

            return (tag, fallback);
        }
        catch (Exception ex)
        {
            _crashReporter.Log($"[Dlssg20_30Service.FetchLatestRelease] {ex.Message}");
            return (null, null);
        }
    }

    private async Task<bool> DownloadReleaseAsync(
        string url,
        string destPath,
        IProgress<(string message, double percent)>? progress)
    {
        try
        {
            using var resp = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
            {
                _crashReporter.Log($"[Dlssg20_30Service.DownloadRelease] HTTP {(int)resp.StatusCode} for {url}");
                return false;
            }

            var total = resp.Content.Headers.ContentLength ?? 0;
            await using var input  = await resp.Content.ReadAsStreamAsync().ConfigureAwait(false);
            await using var output = new FileStream(destPath, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true);

            var buffer = new byte[81920];
            long read = 0;
            int n;
            while ((n = await input.ReadAsync(buffer.AsMemory(0, buffer.Length)).ConfigureAwait(false)) > 0)
            {
                await output.WriteAsync(buffer.AsMemory(0, n)).ConfigureAwait(false);
                read += n;

                if (total > 0)
                {
                    var percent = 5 + (45.0 * read / total);
                    progress?.Report(($"Downloading 20/30 FG Unlock... {read / 1024 / 1024} / {total / 1024 / 1024} MB", percent));
                }
            }

            _crashReporter.Log($"[Dlssg20_30Service.DownloadRelease] Downloaded {Path.GetFileName(destPath)} ({read} bytes)");
            return true;
        }
        catch (Exception ex)
        {
            _crashReporter.Log($"[Dlssg20_30Service.DownloadRelease] Failed for {url} — {ex.Message}");
            return false;
        }
    }
}
