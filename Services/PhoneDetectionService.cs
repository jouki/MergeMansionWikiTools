using System.IO;
using MediaDevices;

namespace MergeMansionWikiTools.Services;

internal static class PhoneDetectionService
{
    public record ExtractionResult(
        string? ConfigFilePath,
        string? PatchFilePath,
        int PatchFileCount,
        string? LanguageFilePath,
        string DeviceName,
        List<string> Warnings,
        string? Error = null
    );

    private static readonly string[] KnownPackages = ["com.everywear.game5"];
    private static readonly string[] FallbackPrefixes = ["com.metacore."];

    /// <summary>Local file (under _DATA) the pull writes the game's client version (app_ver) to.</summary>
    public const string GameVersionFileName = "game_version.txt";

    /// <summary>Local file (under _DATA) the pull writes the Unity engine version (engine_ver) to.</summary>
    public const string UnityVersionFileName = "unity_version.txt";

    public static async Task<ExtractionResult> ExtractGameDataAsync(
        string dataDir,
        IProgress<string>? progress = null,
        CancellationToken ct = default)
    {
        return await Task.Run(() =>
        {
            var warnings = new List<string>();

            // 1. Enumerate MTP devices
            progress?.Report("Scanning for MTP devices...");
            var devices = MediaDevice.GetDevices().ToList();
            if (devices.Count == 0)
                return new ExtractionResult(null, null, 0, null, "", warnings,
                    "No MTP devices detected. Connect phone via USB in File Transfer mode.");

            progress?.Report($"Found {devices.Count} device(s): {string.Join(", ", devices.Select(d => d.FriendlyName))}");

            // 2. Try each device
            foreach (var device in devices)
            {
                ct.ThrowIfCancellationRequested();
                var deviceName = device.FriendlyName;
                progress?.Report($"Connecting to {deviceName}...");

                try
                {
                    device.Connect();
                }
                catch (Exception ex)
                {
                    warnings.Add($"Could not connect to {deviceName}: {ex.Message}");
                    progress?.Report($"[WARN] Could not access {deviceName}. Unlock phone and allow file transfer.");
                    continue;
                }

                try
                {
                    // Enumerate actual storage roots from device (handles localized names)
                    string[] storageRoots;
                    try
                    {
                        storageRoots = device.GetDirectories(@"\");
                    }
                    catch (Exception ex)
                    {
                        warnings.Add($"Could not list storage on {deviceName}: {ex.Message}");
                        progress?.Report($"[ERROR] Could not browse {deviceName}. Unlock phone and allow file transfer.");
                        continue;
                    }

                    if (storageRoots.Length == 0)
                    {
                        warnings.Add($"{deviceName}: no storage accessible. Make sure the phone is in File Transfer (MTP) mode.");
                        progress?.Report($"[ERROR] {deviceName} has no accessible storage. Check USB mode — select \"File Transfer\" in the USB notification on the phone.");
                        continue;
                    }

                    progress?.Report($"Storage roots: {string.Join(", ", storageRoots.Select(r => r.TrimStart('\\')))}");

                    // Try each storage root
                    foreach (var root in storageRoots)
                    {
                        ct.ThrowIfCancellationRequested();

                        var androidData = root + @"\Android\data";
                        if (!DirectoryExistsSafe(device, androidData))
                            continue;

                        // Try known packages first
                        string? packagePath = null;
                        foreach (var pkg in KnownPackages)
                        {
                            var candidate = androidData + @"\" + pkg;
                            if (DirectoryExistsSafe(device, candidate))
                            {
                                packagePath = candidate;
                                break;
                            }
                        }

                        // Fallback: search for com.metacore.*
                        if (packagePath == null)
                        {
                            try
                            {
                                var dirs = device.GetDirectories(androidData);
                                foreach (var dir in dirs)
                                {
                                    var dirName = dir.Split('\\').Last();
                                    if (FallbackPrefixes.Any(p => dirName.StartsWith(p, StringComparison.OrdinalIgnoreCase)))
                                    {
                                        packagePath = dir;
                                        progress?.Report($"Found game package via fallback: {dirName}");
                                        break;
                                    }
                                }
                            }
                            catch { /* directory listing may fail */ }
                        }

                        if (packagePath == null)
                            continue;

                        progress?.Report($"Found game data at {packagePath}");

                        var cachePath = packagePath + @"\cache";
                        if (!DirectoryExistsSafe(device, cachePath))
                        {
                            warnings.Add($"Cache folder not found in {packagePath}");
                            continue;
                        }

                        // Check for SharedGameConfig
                        var configDir = cachePath + @"\SharedGameConfig";
                        if (!DirectoryExistsSafe(device, configDir))
                        {
                            progress?.Report("[WARN] Config folder not found. Open the game first to generate data.");
                            continue;
                        }

                        var configFiles = GetFilesSafe(device, configDir);
                        if (configFiles.Length == 0)
                        {
                            progress?.Report("[WARN] Config folder on device is empty. Open the game first.");
                            continue;
                        }

                        // We found valid game data — extract everything
                        progress?.Report("Extracting game data...");

                        // Clear & prepare local directories
                        var cDir = Path.Combine(dataDir, "C");
                        var pDir = Path.Combine(dataDir, "P");
                        var lDir = Path.Combine(dataDir, "L");
                        ClearAndCreateDir(cDir);
                        ClearAndCreateDir(pDir);
                        ClearAndCreateDir(lDir);

                        // Config: the game caches SEVERAL config versions under this folder — download
                        // all of them, then pick the one with the newest embedded archive CreatedAt.
                        // The device enumeration order is arbitrary, so "first file" routinely selects a
                        // stale build and the dump silently uses outdated config (e.g. an event whose
                        // Start was moved to today only in the newest archive). CreatedAt is read straight
                        // from the archive header — no MetaplayCore.Initialize needed (verified).
                        foreach (var remoteFile in configFiles)
                        {
                            ct.ThrowIfCancellationRequested();
                            var fileName = remoteFile.Split('\\').Last();
                            var localPath = Path.Combine(cDir, fileName);
                            DownloadFile(device, remoteFile, localPath);
                            var size = new FileInfo(localPath).Length;
                            progress?.Report($"  Config: {fileName} ({FormatSize(size)})");
                        }
                        string? configFilePath = DumperService.SelectNewestConfigArchive(cDir)
                            ?? Directory.GetFiles(cDir).FirstOrDefault();
                        if (configFilePath != null)
                        {
                            var createdAt = DumperService.ReadConfigCreatedAt(configFilePath);
                            progress?.Report($"  → newest config: {Path.GetFileName(configFilePath)} (CreatedAt {createdAt:yyyy-MM-dd HH:mm}Z) of {configFiles.Length}");
                        }

                        // Patches (multiple files — newest selected)
                        string? patchFilePath = null;
                        int patchFileCount = 0;
                        var patchDir = cachePath + @"\SharedGameConfigPatches";
                        if (DirectoryExistsSafe(device, patchDir))
                        {
                            var patchFiles = GetFilesSafe(device, patchDir);
                            DateTime newestTime = DateTime.MinValue;
                            foreach (var remoteFile in patchFiles)
                            {
                                ct.ThrowIfCancellationRequested();
                                var fileName = remoteFile.Split('\\').Last();
                                var localPath = Path.Combine(pDir, fileName);
                                DownloadFile(device, remoteFile, localPath);
                                var size = new FileInfo(localPath).Length;
                                progress?.Report($"  Patch: {fileName} ({FormatSize(size)})");

                                // Select newest patch by local write time (preserved from download)
                                var writeTime = new FileInfo(localPath).LastWriteTimeUtc;
                                if (writeTime > newestTime)
                                {
                                    newestTime = writeTime;
                                    patchFilePath = localPath;
                                }
                                patchFileCount++;
                            }

                            if (patchFileCount > 1)
                                progress?.Report($"  {patchFileCount} patch files found, newest selected: {Path.GetFileName(patchFilePath)}");
                        }
                        else
                        {
                            progress?.Report("  No patch files found (optional).");
                        }

                        // Language (en.mpc directory contains a single file)
                        string? languageFilePath = null;
                        var locDir = cachePath + @"\Localizations";
                        if (DirectoryExistsSafe(device, locDir))
                        {
                            // Try en.mpc subfolder first
                            var enDir = locDir + @"\en.mpc";
                            string[]? langFiles = null;

                            if (DirectoryExistsSafe(device, enDir))
                                langFiles = GetFilesSafe(device, enDir);

                            // Fallback: try any .mpc subfolder
                            if (langFiles == null || langFiles.Length == 0)
                            {
                                try
                                {
                                    var subDirs = device.GetDirectories(locDir);
                                    foreach (var sub in subDirs)
                                    {
                                        var files = GetFilesSafe(device, sub);
                                        if (files.Length > 0)
                                        {
                                            langFiles = files;
                                            var subName = sub.Split('\\').Last();
                                            progress?.Report($"  Using language folder: {subName}");
                                            break;
                                        }
                                    }
                                }
                                catch { /* listing may fail */ }
                            }

                            if (langFiles != null)
                            {
                                foreach (var remoteFile in langFiles)
                                {
                                    ct.ThrowIfCancellationRequested();
                                    var fileName = remoteFile.Split('\\').Last();
                                    var localPath = Path.Combine(lDir, fileName);
                                    DownloadFile(device, remoteFile, localPath);
                                    var size = new FileInfo(localPath).Length;
                                    progress?.Report($"  Language: {fileName} ({FormatSize(size)})");
                                    languageFilePath ??= localPath;
                                }
                            }
                        }
                        else
                        {
                            progress?.Report("  No language files found (optional).");
                        }

                        // Last-session game config — lists the account's AB-experiment memberships.
                        // Not part of the standard config/patch/lang set; pulled so the dump can emit
                        // "AB Groups.txt". Saved next to C/P/L as _DATA\LastSessionGameConfig.dat.
                        var localDat = Path.Combine(dataDir, AbGroupsService.LastSessionFileName);
                        try
                        {
                            var remoteDat = cachePath + @"\" + AbGroupsService.RemoteFileName;
                            if (FileExistsSafe(device, remoteDat))
                            {
                                DownloadFile(device, remoteDat, localDat);
                                progress?.Report($"  AB groups: {AbGroupsService.LastSessionFileName} ({FormatSize(new FileInfo(localDat).Length)})");
                            }
                            else
                            {
                                // No file on device → drop any stale local copy so AB Groups.txt isn't misleading.
                                if (File.Exists(localDat)) File.Delete(localDat);
                                progress?.Report("  AB groups file not found on device (AB Groups.txt will list the patch catalog only).");
                            }
                        }
                        catch (Exception ex)
                        {
                            warnings.Add($"Could not pull {AbGroupsService.RemoteFileName}: {ex.Message}");
                        }

                        // Game version — Unity Analytics persists "app_ver" (e.g. "26.05.01") to
                        // files\Unity\<projectGuid>\Analytics\values. This is the reliable client
                        // version (the pulled config/patch blobs are server data and carry no client
                        // version). Captured at pull time → _DATA\game_version.txt so the dump/Discord
                        // publish can read it even when the phone is disconnected.
                        var gvPath = Path.Combine(dataDir, GameVersionFileName);
                        var uvPath = Path.Combine(dataDir, UnityVersionFileName);
                        try
                        {
                            var (appVer, engineVer) = TryExtractGameVersion(device, packagePath);
                            if (!string.IsNullOrEmpty(appVer))
                            {
                                File.WriteAllText(gvPath, appVer);
                                progress?.Report($"  Game version: {appVer}" +
                                    (string.IsNullOrEmpty(engineVer) ? "" : $" (Unity {engineVer})"));
                            }
                            else if (File.Exists(gvPath)) File.Delete(gvPath); // drop stale

                            if (!string.IsNullOrEmpty(engineVer)) File.WriteAllText(uvPath, engineVer);
                            else if (File.Exists(uvPath)) File.Delete(uvPath);

                            if (string.IsNullOrEmpty(appVer) && string.IsNullOrEmpty(engineVer))
                                progress?.Report("  Game/Unity version not found in phone files.");
                        }
                        catch (Exception ex)
                        {
                            warnings.Add($"Could not read game version: {ex.Message}");
                        }

                        progress?.Report("Extraction complete.");
                        return new ExtractionResult(configFilePath, patchFilePath, patchFileCount, languageFilePath, deviceName, warnings);
                    }

                    // Tried all roots on this device
                    warnings.Add($"Merge Mansion not found on {deviceName}.");
                }
                finally
                {
                    try { device.Disconnect(); } catch { }
                }
            }

            // No device had game data — build a helpful message
            var deviceNames = string.Join(", ", devices.Select(d => d.FriendlyName));
            var hint = warnings.Any(w => w.Contains("no storage accessible"))
                ? " Make sure the phone is connected in File Transfer (MTP) mode."
                : " Ensure Merge Mansion is installed and has been opened at least once.";
            return new ExtractionResult(null, null, 0, null, deviceNames, warnings,
                $"Merge Mansion not found on any connected device ({deviceNames}).{hint}");
        }, ct);
    }

    private static bool DirectoryExistsSafe(MediaDevice device, string path)
    {
        try { return device.DirectoryExists(path); }
        catch { return false; }
    }

    private static bool FileExistsSafe(MediaDevice device, string path)
    {
        try { return device.FileExists(path); }
        catch { return false; }
    }

    /// <summary>
    /// Reads the game's client version ("app_ver", e.g. "26.05.01") and Unity engine version
    /// ("engine_ver", e.g. "6000.3.6f1") from the Unity Analytics values file:
    /// files\Unity\&lt;projectGuid&gt;\Analytics\values (small JSON). The project GUID subfolder is
    /// enumerated so we don't hardcode it. Returns (null, null) if not found.
    /// </summary>
    private static (string? AppVer, string? EngineVer) TryExtractGameVersion(MediaDevice device, string packagePath)
    {
        var unityDir = packagePath + @"\files\Unity";
        if (!DirectoryExistsSafe(device, unityDir)) return (null, null);

        string[] subs;
        try { subs = device.GetDirectories(unityDir); }
        catch { return (null, null); }

        foreach (var sub in subs)
        {
            var valuesPath = sub + @"\Analytics\values";
            if (!FileExistsSafe(device, valuesPath)) continue;
            try
            {
                using var ms = new MemoryStream();
                device.DownloadFile(valuesPath, ms);
                var json = System.Text.Encoding.UTF8.GetString(ms.ToArray());
                var app = System.Text.RegularExpressions.Regex.Match(json, "\"app_ver\"\\s*:\\s*\"([^\"]+)\"");
                var eng = System.Text.RegularExpressions.Regex.Match(json, "\"engine_ver\"\\s*:\\s*\"([^\"]+)\"");
                if (app.Success || eng.Success)
                    return (app.Success ? app.Groups[1].Value.Trim() : null,
                            eng.Success ? eng.Groups[1].Value.Trim() : null);
            }
            catch { /* try next */ }
        }
        return (null, null);
    }

    private static string[] GetFilesSafe(MediaDevice device, string path)
    {
        try { return device.GetFiles(path); }
        catch { return []; }
    }

    private static void DownloadFile(MediaDevice device, string remotePath, string localPath)
    {
        using var fs = File.Create(localPath);
        device.DownloadFile(remotePath, fs);
    }

    private static void ClearAndCreateDir(string path)
    {
        if (Directory.Exists(path))
            Directory.Delete(path, true);
        Directory.CreateDirectory(path);
    }

    private static string FormatSize(long bytes)
    {
        if (bytes < 1024) return $"{bytes} B";
        if (bytes < 1024 * 1024) return $"{bytes / 1024.0:F1} KB";
        return $"{bytes / (1024.0 * 1024.0):F1} MB";
    }
}
