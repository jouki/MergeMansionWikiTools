using System.IO;
using System.IO.Compression;
using AddressablesTools;
using AddressablesTools.Catalog;

namespace MergeMansionWikiTools.Services;

internal static class CatalogParserService
{
    public record CatalogResult(List<string> Urls, int TotalLocations);

    /// <summary>
    /// Finds the first .xapk or .apk file in a version directory. Prefers .xapk.
    /// </summary>
    public static string? FindApkInFolder(string versionDir)
    {
        if (!Directory.Exists(versionDir)) return null;

        var xapk = Directory.EnumerateFiles(versionDir, "*.xapk").FirstOrDefault();
        if (xapk != null) return xapk;

        return Directory.EnumerateFiles(versionDir, "*.apk").FirstOrDefault();
    }

    /// <summary>
    /// Extracts the Addressables catalog from an APK/XAPK file.
    /// XAPK: outer ZIP → UnityDataAssetPack.apk → assets/aa/catalog.bin (or .json for older APKs)
    /// APK:  ZIP → assets/aa/catalog.bin (or .json)
    /// </summary>
    public static string ExtractCatalogFromApk(string apkPath, string outputDir)
    {
        var ext = Path.GetExtension(apkPath).ToLowerInvariant();
        Directory.CreateDirectory(outputDir);

        using var outerStream = File.OpenRead(apkPath);
        using var outerZip = new ZipArchive(outerStream, ZipArchiveMode.Read);

        if (ext == ".xapk")
        {
            // Find UnityDataAssetPack.apk inside the XAPK
            var innerEntry = outerZip.Entries
                .FirstOrDefault(e => e.Name.Equals("UnityDataAssetPack.apk", StringComparison.OrdinalIgnoreCase));

            if (innerEntry == null)
                throw new FileNotFoundException("UnityDataAssetPack.apk not found inside the XAPK.");

            using var innerStream = innerEntry.Open();
            using var innerMs = new MemoryStream();
            innerStream.CopyTo(innerMs);
            innerMs.Position = 0;

            using var innerZip = new ZipArchive(innerMs, ZipArchiveMode.Read);
            return ExtractCatalogEntry(innerZip, outputDir);
        }
        else
        {
            // Direct APK
            return ExtractCatalogEntry(outerZip, outputDir);
        }
    }

    private static string ExtractCatalogEntry(ZipArchive zip, string outputDir)
    {
        var catalogEntry = zip.Entries
            .FirstOrDefault(e => e.FullName.Equals("assets/aa/catalog.bin", StringComparison.OrdinalIgnoreCase))
            ?? zip.Entries
            .FirstOrDefault(e => e.FullName.Equals("assets/aa/catalog.json", StringComparison.OrdinalIgnoreCase));

        if (catalogEntry == null)
            throw new FileNotFoundException("assets/aa/catalog.bin (or .json) not found inside the APK.");

        var outputPath = Path.Combine(outputDir, Path.GetFileName(catalogEntry.FullName));
        using var entryStream = catalogEntry.Open();
        using var fileStream = File.Create(outputPath);
        entryStream.CopyTo(fileStream);
        return outputPath;
    }

    /// <summary>
    /// Parses a Unity Addressables catalog file (.bin or .json) and extracts all HTTP URLs.
    /// </summary>
    public static CatalogResult ExtractUrls(string catalogPath)
    {
        var bytes = File.ReadAllBytes(catalogPath);

        ContentCatalogData ccd;
        using (var ms = new MemoryStream(bytes))
        {
            var fileType = AddressablesCatalogFileParser.GetCatalogFileType(ms);
            ccd = fileType == CatalogFileType.Binary
                ? AddressablesCatalogFileParser.FromBinaryData(bytes)
                : AddressablesCatalogFileParser.FromJsonString(File.ReadAllText(catalogPath));
        }

        var urls = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        int totalLocations = 0;

        foreach (var key in ccd.Resources.Keys)
        {
            foreach (var rsrc in ccd.Resources[key])
            {
                totalLocations++;
                if (rsrc.InternalId.StartsWith("http", StringComparison.OrdinalIgnoreCase))
                    urls.Add(rsrc.InternalId);
            }
        }

        var sorted = urls.OrderBy(u => u, StringComparer.OrdinalIgnoreCase).ToList();
        return new CatalogResult(sorted, totalLocations);
    }

    private static readonly System.Text.RegularExpressions.Regex GuidKeyRx =
        new(@"^[0-9a-fA-F]{32}$", System.Text.RegularExpressions.RegexOptions.Compiled);

    /// <summary>
    /// Addressables asset GUID → asset name (InternalId file name without extension), e.g.
    /// "25fb1b52ef24d496d99e97c4a5a529c1" → "AdventCalendar2023_Calendar". Since 26.07.01 the
    /// PoolConfig stores prefabs as <c>AssetReferenceT</c> (GUID only) instead of a path, so the
    /// PoolTag → prefab mapping needs this lookup. A GUID key can carry several locations
    /// (prefab + sub-assets); the <c>.prefab</c> one wins, otherwise the first.
    /// </summary>
    public static Dictionary<string, string> BuildGuidToAssetNameMap(string catalogPath)
    {
        var bytes = File.ReadAllBytes(catalogPath);
        ContentCatalogData ccd;
        using (var ms = new MemoryStream(bytes))
        {
            var fileType = AddressablesCatalogFileParser.GetCatalogFileType(ms);
            ccd = fileType == CatalogFileType.Binary
                ? AddressablesCatalogFileParser.FromBinaryData(bytes)
                : AddressablesCatalogFileParser.FromJsonString(File.ReadAllText(catalogPath));
        }

        // Pass 1: InternalId → ADDRESS name. A location is reachable through several keys: its
        // GUID and its address (the "Assets/…/X.prefab" string). The address is what the game
        // (and the pre-26.07.01 prefabRef.Path) used, and it is the skeleton/texture name even
        // when the prefab FILE was renamed: Pantry → address ".../Pantry_Fruit/Pantry_Fruit.prefab",
        // InternalId ".../Pantry_Fruit/FirstFloorPantry_Fruit.prefab", texture "Pantry_Fruit.png".
        var addressByInternalId = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var key in ccd.Resources.Keys)
        {
            if (key is not string address || GuidKeyRx.IsMatch(address)
                || !address.EndsWith(".prefab", StringComparison.OrdinalIgnoreCase)) continue;
            var addrName = Path.GetFileNameWithoutExtension(address);
            if (string.IsNullOrEmpty(addrName)) continue;
            foreach (var rsrc in ccd.Resources[key])
                if (!string.IsNullOrEmpty(rsrc.InternalId))
                    addressByInternalId.TryAdd(rsrc.InternalId, addrName);
        }

        // Pass 2: GUID → name (address name preferred, InternalId file name as fallback)
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        int viaAddress = 0;
        foreach (var key in ccd.Resources.Keys)
        {
            if (key is not string guid || !GuidKeyRx.IsMatch(guid)) continue;
            string? best = null;
            bool bestIsPrefab = false;
            foreach (var rsrc in ccd.Resources[key])
            {
                var id = rsrc.InternalId;
                if (string.IsNullOrEmpty(id) || id.StartsWith("http", StringComparison.OrdinalIgnoreCase)) continue;
                bool isPrefab = id.EndsWith(".prefab", StringComparison.OrdinalIgnoreCase);
                if (best != null && (bestIsPrefab || !isPrefab)) continue;
                if (addressByInternalId.TryGetValue(id, out var addrName)) { best = addrName; viaAddress++; }
                else best = Path.GetFileNameWithoutExtension(id);
                bestIsPrefab = isPrefab;
                if (isPrefab) break;
            }
            if (!string.IsNullOrEmpty(best)) map[guid] = best;
        }
        AppLogger.Info($"[CATALOG] GUID → asset name map: {map.Count} entries ({viaAddress} via address key) from {Path.GetFileName(catalogPath)}");
        return map;
    }

}
