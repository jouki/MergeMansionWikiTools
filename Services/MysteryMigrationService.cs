using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using MergeMansionWikiTools.Models;

namespace MergeMansionWikiTools.Services;

// ── Operation kinds ──────────────────────────────────────────────

public enum MysteryMigrationOpKind
{
    MovePage,         // wiki page rename without redirect
    MoveFile,         // File:* rename
    PatchPage,        // generic search-replace OldName/OldTitle on a page
    PatchModule,      // p.mysteries entry in Module:Datatable/Various
    PatchMainPage,    // Merge Mansion Wiki — Latest Mystery Events
    PatchEventsTable, // Template:Events/Mystery Events
}

// ── Single operation ─────────────────────────────────────────────

public class MysteryMigrationOp
{
    public MysteryMigrationOpKind Kind { get; set; }
    public string Description { get; set; } = "";
    public string OldTarget { get; set; } = "";
    public string NewTarget { get; set; } = "";

    /// <summary>True when source resource exists on wiki and op is actionable.</summary>
    public bool? Exists { get; set; }

    /// <summary>True when user wants this op skipped during execute.</summary>
    public bool Skip { get; set; }

    /// <summary>Result after execute — null until run.</summary>
    public bool? Done { get; set; }
    public string? Error { get; set; }
}

// ── Per-mystery plan ─────────────────────────────────────────────

public class MysteryMigrationPlan
{
    public MysteryEvent Mystery { get; init; } = null!;

    /// <summary>Old display name (with "Season Pass - " prefix).</summary>
    public string OldName { get; init; } = "";

    /// <summary>New display name (stripped).</summary>
    public string NewName { get; init; } = "";

    /// <summary>Old wiki page title (may include "(Mystery YYYY)" disambig suffix).</summary>
    public string OldPageTitle { get; set; } = "";

    /// <summary>New wiki page title (may include "(Mystery YYYY)" disambig suffix).</summary>
    public string NewPageTitle { get; set; } = "";

    public List<MysteryMigrationOp> Ops { get; } = new();
}

// ── Service ──────────────────────────────────────────────────────

public static class MysteryMigrationService
{
    private const string BaseApiUrl = "https://merge-mansion.fandom.com/api.php";
    private const string MainPageTitle = "Merge Mansion Wiki";
    private const string EventsTableTitle = "Template:Events/Mystery Events";
    private const string ModuleTitle = "Module:Datatable/Various";

    // Neautentizované GETy (existence stránek, allimages discovery) jdou přes sdílený klient.
    // Autentizované edity používají per-session klienta z WikiMappingService.CreateAuthenticatedClientAsync.
    private static HttpClient Http => HttpClients.WikiApi;

    // ── Plan building ─────────────────────────────────────────────

    /// <summary>
    /// Builds a migration plan stripping the "Season Pass - " prefix from a single mystery.
    /// Disambiguation suffix (e.g. " (Mystery 2025)") is propagated from <paramref name="hasDisambig"/>.
    /// </summary>
    public static MysteryMigrationPlan BuildPlan(MysteryEvent mystery, bool hasDisambig)
    {
        var oldName = mystery.RawJsonName;
        var newName = mystery.Name;

        var disambigSuffix = hasDisambig && mystery.StartDate.HasValue
            ? $" (Mystery {mystery.StartDate.Value.Year})"
            : "";
        var oldPageTitle = oldName + disambigSuffix;
        var newPageTitle = newName + disambigSuffix;

        var plan = new MysteryMigrationPlan
        {
            Mystery = mystery,
            OldName = oldName,
            NewName = newName,
            OldPageTitle = oldPageTitle,
            NewPageTitle = newPageTitle,
        };

        // ── 1. Move event page ──
        plan.Ops.Add(new MysteryMigrationOp
        {
            Kind = MysteryMigrationOpKind.MovePage,
            Description = "Event page",
            OldTarget = oldPageTitle,
            NewTarget = newPageTitle,
        });

        // ── 2. Move reward + gallery templates ──
        plan.Ops.Add(new MysteryMigrationOp
        {
            Kind = MysteryMigrationOpKind.MovePage,
            Description = "Mystery Pass/Rewards/{name} template",
            OldTarget = $"Template:Mystery Pass/Rewards/{oldName}",
            NewTarget = $"Template:Mystery Pass/Rewards/{newName}",
        });
        plan.Ops.Add(new MysteryMigrationOp
        {
            Kind = MysteryMigrationOpKind.MovePage,
            Description = "Mystery Pass/Gallery/{name} template",
            OldTarget = $"Template:Mystery Pass/Gallery/{oldName}",
            NewTarget = $"Template:Mystery Pass/Gallery/{newName}",
        });

        // ── 3. Patch shared pages where this mystery is referenced ──
        plan.Ops.Add(new MysteryMigrationOp
        {
            Kind = MysteryMigrationOpKind.PatchModule,
            Description = "Module:Datatable/Various — p.mysteries entry",
            OldTarget = oldPageTitle,
            NewTarget = newPageTitle,
        });
        plan.Ops.Add(new MysteryMigrationOp
        {
            Kind = MysteryMigrationOpKind.PatchMainPage,
            Description = "Merge Mansion Wiki — Latest Mystery Events row",
            OldTarget = oldPageTitle,
            NewTarget = newPageTitle,
        });
        plan.Ops.Add(new MysteryMigrationOp
        {
            Kind = MysteryMigrationOpKind.PatchEventsTable,
            Description = "Template:Events/Mystery Events — table row",
            OldTarget = oldPageTitle,
            NewTarget = newPageTitle,
        });

        // ── 4. Event item page text references (when this mystery is the primary item) ──
        if (!string.IsNullOrEmpty(mystery.EventItemName))
        {
            plan.Ops.Add(new MysteryMigrationOp
            {
                Kind = MysteryMigrationOpKind.PatchPage,
                Description = $"Event item page — \"{mystery.EventItemName}\"",
                OldTarget = mystery.EventItemName,         // page title to fetch
                NewTarget = oldPageTitle + "|" + newPageTitle, // pipe-separated old|new for replacement
            });
        }

        // ── 5. New event page itself: vardefine EventDisplayName + DISPLAYTITLE ──
        plan.Ops.Add(new MysteryMigrationOp
        {
            Kind = MysteryMigrationOpKind.PatchPage,
            Description = $"Event page (post-move) — vardefine + DISPLAYTITLE",
            OldTarget = newPageTitle, // after move, fetch by NEW title
            NewTarget = oldName + "|" + newName,
        });

        // Files are discovered live in CheckExistenceAsync (cannot enumerate without API).
        return plan;
    }

    // ── Existence checking + file discovery ───────────────────────

    public static async Task CheckExistenceAsync(MysteryMigrationPlan plan, CancellationToken ct = default)
    {
        // Pages — batch existence check
        var pagesToCheck = plan.Ops
            .Where(o => o.Kind == MysteryMigrationOpKind.MovePage)
            .Select(o => o.OldTarget)
            .Distinct()
            .ToList();
        var pageExistence = await CheckPagesExistAsync(pagesToCheck, ct);
        foreach (var op in plan.Ops.Where(o => o.Kind == MysteryMigrationOpKind.MovePage))
        {
            op.Exists = pageExistence.TryGetValue(op.OldTarget, out var ex) && ex;
            if (op.Exists != true) op.Skip = true;
        }

        // Patch ops — fetch the page once each, mark exists if it contains the old reference
        foreach (var op in plan.Ops.Where(o => o.Kind != MysteryMigrationOpKind.MovePage
                                            && o.Kind != MysteryMigrationOpKind.MoveFile))
        {
            ct.ThrowIfCancellationRequested();
            var pageTitle = op.Kind switch
            {
                MysteryMigrationOpKind.PatchModule => ModuleTitle,
                MysteryMigrationOpKind.PatchMainPage => MainPageTitle,
                MysteryMigrationOpKind.PatchEventsTable => EventsTableTitle,
                _ => op.OldTarget, // PatchPage stores page title in OldTarget
            };
            var content = await MysteryWikiService.FetchPageContentAsync(pageTitle);
            if (string.IsNullOrEmpty(content))
            {
                op.Exists = false; op.Skip = true;
                continue;
            }
            var (search, _) = SplitPair(op);
            op.Exists = content.Contains(search, StringComparison.Ordinal);
            if (op.Exists != true) op.Skip = true;
        }

        // Files — discover via allimages prefix queries
        await DiscoverFilesAsync(plan, ct);
    }

    private static async Task DiscoverFilesAsync(MysteryMigrationPlan plan, CancellationToken ct)
    {
        var camelOld = MysteryWikiService.FormatFileName(plan.OldName, 0, suppressLevel: true)
            .Replace(".png", "", StringComparison.OrdinalIgnoreCase);
        var underscoreOld = plan.OldName.Replace(' ', '_');
        var camelNew = MysteryWikiService.FormatFileName(plan.NewName, 0, suppressLevel: true)
            .Replace(".png", "", StringComparison.OrdinalIgnoreCase);
        var underscoreNew = plan.NewName.Replace(' ', '_');

        if (camelOld == camelNew && underscoreOld == underscoreNew) return; // nothing to migrate

        var candidates = new HashSet<string>(StringComparer.Ordinal);
        if (!string.IsNullOrEmpty(camelOld))      candidates.UnionWith(await ListImagesByPrefixAsync(camelOld, ct));
        if (!string.IsNullOrEmpty(underscoreOld) && underscoreOld != camelOld)
            candidates.UnionWith(await ListImagesByPrefixAsync(underscoreOld, ct));

        foreach (var oldFile in candidates.OrderBy(s => s, StringComparer.Ordinal))
        {
            var newFile = oldFile;
            if (oldFile.StartsWith(camelOld, StringComparison.Ordinal))
                newFile = camelNew + oldFile[camelOld.Length..];
            else if (oldFile.StartsWith(underscoreOld, StringComparison.Ordinal))
                newFile = underscoreNew + oldFile[underscoreOld.Length..];

            if (newFile == oldFile) continue;

            plan.Ops.Add(new MysteryMigrationOp
            {
                Kind = MysteryMigrationOpKind.MoveFile,
                Description = $"File:{oldFile}",
                OldTarget = $"File:{oldFile}",
                NewTarget = $"File:{newFile}",
                Exists = true,
            });
        }
    }

    private static async Task<List<string>> ListImagesByPrefixAsync(string prefix, CancellationToken ct)
    {
        var results = new List<string>();
        string? continueToken = null;
        do
        {
            ct.ThrowIfCancellationRequested();
            var url = $"{BaseApiUrl}?action=query&list=allimages&aiprefix={Uri.EscapeDataString(prefix)}&ailimit=500&format=json";
            if (continueToken != null) url += $"&aicontinue={Uri.EscapeDataString(continueToken)}";

            string raw;
            try { raw = await Http.GetStringAsync(url, ct); }
            catch { return results; }

            using var doc = JsonDocument.Parse(raw);
            if (doc.RootElement.TryGetProperty("query", out var query)
                && query.TryGetProperty("allimages", out var imgs))
            {
                foreach (var img in imgs.EnumerateArray())
                {
                    var name = img.GetProperty("name").GetString();
                    if (!string.IsNullOrEmpty(name)) results.Add(name);
                }
            }

            continueToken = null;
            if (doc.RootElement.TryGetProperty("continue", out var cont)
                && cont.TryGetProperty("aicontinue", out var ac))
            {
                continueToken = ac.GetString();
            }
        } while (continueToken != null);
        return results;
    }

    private static async Task<Dictionary<string, bool>> CheckPagesExistAsync(
        List<string> titles, CancellationToken ct)
    {
        var result = new Dictionary<string, bool>(StringComparer.Ordinal);
        if (titles.Count == 0) return result;

        // MediaWiki query=titles supports up to 50 per call
        for (int i = 0; i < titles.Count; i += 50)
        {
            ct.ThrowIfCancellationRequested();
            var batch = titles.Skip(i).Take(50).ToList();
            var url = $"{BaseApiUrl}?action=query&titles={Uri.EscapeDataString(string.Join("|", batch))}&format=json";
            string raw;
            try { raw = await Http.GetStringAsync(url, ct); }
            catch { foreach (var t in batch) result[t] = false; continue; }
            using var doc = JsonDocument.Parse(raw);
            if (!doc.RootElement.TryGetProperty("query", out var query)
                || !query.TryGetProperty("pages", out var pages)) { foreach (var t in batch) result[t] = false; continue; }

            // Index by title (could differ from input if normalized — handle via "normalized" mapping)
            var titleNormalization = new Dictionary<string, string>(StringComparer.Ordinal);
            if (query.TryGetProperty("normalized", out var norm))
            {
                foreach (var n in norm.EnumerateArray())
                    titleNormalization[n.GetProperty("from").GetString() ?? ""] = n.GetProperty("to").GetString() ?? "";
            }

            foreach (var page in pages.EnumerateObject())
            {
                var actualTitle = page.Value.GetProperty("title").GetString() ?? "";
                bool missing = page.Value.TryGetProperty("missing", out _);
                // Map normalized → original input title
                var inputTitle = batch.FirstOrDefault(t =>
                    string.Equals(t, actualTitle, StringComparison.Ordinal)
                    || (titleNormalization.TryGetValue(t, out var nt) && nt == actualTitle));
                if (inputTitle != null) result[inputTitle] = !missing;
            }
            foreach (var t in batch) if (!result.ContainsKey(t)) result[t] = false;
        }
        return result;
    }

    // ── Execute ───────────────────────────────────────────────────

    public static async Task ExecuteAsync(MysteryMigrationPlan plan, string username, string password,
                                          IProgress<string>? log = null, CancellationToken ct = default)
    {
        using var client = await WikiMappingService.CreateAuthenticatedClientAsync(username, password);
        var csrfToken = await GetCsrfAsync(client);

        foreach (var op in plan.Ops)
        {
            ct.ThrowIfCancellationRequested();
            if (op.Skip) { op.Done = false; continue; }
            try
            {
                switch (op.Kind)
                {
                    case MysteryMigrationOpKind.MovePage:
                        log?.Report($"Move page: {op.OldTarget} → {op.NewTarget}");
                        await WikiMappingService.MoveWikiPageAsync(client, csrfToken, op.OldTarget, op.NewTarget,
                            $"Strip Season Pass prefix (via MergeMansionWikiTools)");
                        op.Done = true;
                        break;
                    case MysteryMigrationOpKind.MoveFile:
                        log?.Report($"Move file: {op.OldTarget} → {op.NewTarget}");
                        await WikiMappingService.MoveFileAsync(client, csrfToken, op.OldTarget, op.NewTarget,
                            $"Strip Season Pass prefix (via MergeMansionWikiTools)");
                        op.Done = true;
                        break;
                    case MysteryMigrationOpKind.PatchModule:
                        log?.Report($"Patch module: {op.OldTarget} → {op.NewTarget}");
                        await PatchModuleEntryAsync(client, csrfToken, plan, op);
                        op.Done = true;
                        break;
                    case MysteryMigrationOpKind.PatchMainPage:
                        log?.Report($"Patch main page: {op.OldTarget} → {op.NewTarget}");
                        await PatchSimpleReferencesAsync(client, csrfToken, MainPageTitle, plan, op);
                        op.Done = true;
                        break;
                    case MysteryMigrationOpKind.PatchEventsTable:
                        log?.Report($"Patch events table: {op.OldTarget} → {op.NewTarget}");
                        await PatchSimpleReferencesAsync(client, csrfToken, EventsTableTitle, plan, op);
                        op.Done = true;
                        break;
                    case MysteryMigrationOpKind.PatchPage:
                        log?.Report($"Patch page: {op.OldTarget}");
                        await PatchPageReferencesAsync(client, csrfToken, op.OldTarget, plan, op);
                        op.Done = true;
                        break;
                }
            }
            catch (Exception ex)
            {
                op.Done = false;
                op.Error = ex.Message;
                log?.Report($"  ERROR: {ex.Message}");
            }
        }
    }

    private static async Task<string> GetCsrfAsync(HttpClient client)
    {
        var raw = await client.GetStringAsync($"{BaseApiUrl}?action=query&meta=tokens&format=json");
        using var doc = JsonDocument.Parse(raw);
        return doc.RootElement.GetProperty("query").GetProperty("tokens")
            .GetProperty("csrftoken").GetString() ?? "";
    }

    // ── Patch helpers ─────────────────────────────────────────────

    private static async Task PatchSimpleReferencesAsync(HttpClient client, string csrfToken,
        string pageTitle, MysteryMigrationPlan plan, MysteryMigrationOp op)
    {
        var content = await MysteryWikiService.FetchPageContentAsync(pageTitle);
        if (string.IsNullOrEmpty(content))
            throw new Exception($"Page '{pageTitle}' is empty or missing");

        var updated = ReplaceMysteryReferences(content, plan.OldPageTitle, plan.NewPageTitle, plan.OldName, plan.NewName);
        if (updated == content) return; // nothing to do

        await PublishViaApiAsync(client, csrfToken, pageTitle, updated,
            $"Strip Season Pass prefix on {plan.OldName} (via MergeMansionWikiTools)");
    }

    private static async Task PatchModuleEntryAsync(HttpClient client, string csrfToken,
        MysteryMigrationPlan plan, MysteryMigrationOp op)
    {
        var content = await MysteryWikiService.FetchPageContentAsync(ModuleTitle);
        if (string.IsNullOrEmpty(content))
            throw new Exception("Module:Datatable/Various is empty or missing");

        var oldTitleEsc = Regex.Escape(plan.OldPageTitle);
        var oldNameEsc = Regex.Escape(plan.OldName);

        // p.mysteries entry: { name = "OldTitle", displayName = "OldName", ... }
        // Patch both `name = "..."` and `displayName = "..."` fields when they reference the old form.
        var nameRx = new Regex(@"(name\s*=\s*"")" + oldTitleEsc + @"(""\s*,)");
        var dispRx = new Regex(@"(displayName\s*=\s*"")" + oldNameEsc + @"(""\s*,?)");
        var updated = nameRx.Replace(content, "$1" + plan.NewPageTitle + "$2");
        updated = dispRx.Replace(updated, "$1" + plan.NewName + "$2");

        if (updated == content) return;

        await PublishViaApiAsync(client, csrfToken, ModuleTitle, updated,
            $"Strip Season Pass prefix on {plan.OldName} (via MergeMansionWikiTools)");
    }

    private static async Task PatchPageReferencesAsync(HttpClient client, string csrfToken,
        string pageTitle, MysteryMigrationPlan plan, MysteryMigrationOp op)
    {
        var content = await MysteryWikiService.FetchPageContentAsync(pageTitle);
        if (string.IsNullOrEmpty(content))
            throw new Exception($"Page '{pageTitle}' is empty or missing");

        var (oldVal, newVal) = SplitPair(op);
        // PatchPage carries 2 different payload shapes:
        //   - Event item page: oldVal = OldPageTitle, newVal = NewPageTitle
        //   - Event page itself: oldVal = OldName, newVal = NewName
        // Both are handled by the same helper which patches all reasonable patterns.
        var updated = ReplaceMysteryReferences(content, plan.OldPageTitle, plan.NewPageTitle, plan.OldName, plan.NewName);
        if (updated == content) return;

        await PublishViaApiAsync(client, csrfToken, pageTitle, updated,
            $"Strip Season Pass prefix on {plan.OldName} (via MergeMansionWikiTools)");
    }

    /// <summary>
    /// Replaces all wiki references for a Season Pass mystery rename.
    /// Patterns covered: [[OldTitle]], [[OldTitle|...]], {{Item|OldTitle|...}}, {{Item/Group|OldTitle|...}},
    /// {{Item/Icon|OldTitle|...}}, {{#vardefine:any|OldName}}, displayName=OldName, name="OldTitle",
    /// {{Mystery Pass/Rewards/OldName}}, {{Mystery Pass/Gallery/OldName}}.
    /// </summary>
    internal static string ReplaceMysteryReferences(string text, string oldTitle, string newTitle,
                                                    string oldName, string newName)
    {
        var oldTitleEsc = Regex.Escape(oldTitle);
        var oldNameEsc = Regex.Escape(oldName);

        // [[OldTitle]] and [[OldTitle|…]]
        text = Regex.Replace(text, @"\[\[" + oldTitleEsc + @"\]\]", $"[[{newTitle}]]");
        text = Regex.Replace(text, @"\[\[" + oldTitleEsc + @"\|", $"[[{newTitle}|");

        // {{Item|OldTitle|…}}, {{Item/Group|OldTitle|…}}, {{Item/Icon|OldTitle|…}}, {{Item/nolevel|OldTitle…}}
        text = Regex.Replace(text, @"(\{\{Item\|)" + oldTitleEsc + @"(\|)", "$1" + newTitle + "$2");
        text = Regex.Replace(text, @"(\{\{Item/Group\|)" + oldTitleEsc + @"(\|)", "$1" + newTitle + "$2");
        text = Regex.Replace(text, @"(\{\{Item/Icon\|)" + oldTitleEsc + @"(\|)", "$1" + newTitle + "$2");
        text = Regex.Replace(text, @"(\{\{Item/nolevel\|)" + oldTitleEsc + @"([|}])", "$1" + newTitle + "$2");

        // displayName=OldName parameter on Item/Group, Item/Icon templates
        text = Regex.Replace(text, @"(\bdisplayName\s*=\s*)" + oldNameEsc + @"(\}|\|)", "$1" + newName + "$2");

        // {{#vardefine:Anything|OldName}} (most commonly EventDisplayName)
        text = Regex.Replace(text, @"(\{\{#vardefine:[^|}]*\|)" + oldNameEsc + @"(\}\})", "$1" + newName + "$2");

        // {{DISPLAYTITLE:OldName}} or DISPLAYTITLE:OldTitle
        text = Regex.Replace(text, @"(\{\{DISPLAYTITLE:\s*)" + oldNameEsc + @"(\s*\}\})", "$1" + newName + "$2", RegexOptions.IgnoreCase);

        // {{Mystery Pass/Rewards/OldName}} and {{Mystery Pass/Rewards/OldName|…}}
        text = Regex.Replace(text, @"(\{\{Mystery Pass/Rewards/)" + oldNameEsc + @"(\}\}|\|)", "$1" + newName + "$2");
        text = Regex.Replace(text, @"(\{\{Mystery Pass/Gallery/)" + oldNameEsc + @"(\}\}|\|)", "$1" + newName + "$2");

        return text;
    }

    private static (string Old, string New) SplitPair(MysteryMigrationOp op)
    {
        // PatchPage encodes "old|new" in NewTarget; other ops use OldTarget/NewTarget pair directly.
        if (op.Kind == MysteryMigrationOpKind.PatchPage)
        {
            var idx = op.NewTarget.IndexOf('|');
            if (idx > 0) return (op.NewTarget[..idx], op.NewTarget[(idx + 1)..]);
        }
        return (op.OldTarget, op.NewTarget);
    }

    private static async Task PublishViaApiAsync(HttpClient client, string csrfToken,
        string pageTitle, string content, string editSummary)
    {
        var form = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["action"] = "edit",
            ["title"] = pageTitle,
            ["text"] = content,
            ["token"] = csrfToken,
            ["summary"] = editSummary,
            ["bot"] = "1",
            ["format"] = "json",
        });
        var resp = await client.PostAsync(BaseApiUrl, form);
        var raw = await resp.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(raw);
        if (doc.RootElement.TryGetProperty("error", out var error))
            throw new Exception($"Edit failed on '{pageTitle}': {error.GetProperty("info").GetString()}");
    }
}
