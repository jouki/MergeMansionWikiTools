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

                        // Config (1 file)
                        string? configFilePath = null;
                        foreach (var remoteFile in configFiles)
                        {
                            ct.ThrowIfCancellationRequested();
                            var fileName = remoteFile.Split('\\').Last();
                            var localPath = Path.Combine(cDir, fileName);
                            DownloadFile(device, remoteFile, localPath);
                            var size = new FileInfo(localPath).Length;
                            progress?.Report($"  Config: {fileName} ({FormatSize(size)})");
                            configFilePath ??= localPath;
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
