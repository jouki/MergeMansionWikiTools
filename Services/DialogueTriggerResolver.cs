using System.IO;
using System.Text.Json;
using MergeMansionWikiTools.Models;

namespace MergeMansionWikiTools.Services;

/// <summary>
/// Fills in what triggers each scene. A hotspot starts them two ways: `TriggerDialogue` carries inline
/// DialogItems, `TriggerCutscene` only an id — and that id IS the scene id (Attic15 on "Clean dirt").
/// </summary>
public static class DialogueTriggerResolver
{
    private static readonly (string Key, string Phase)[] Phases =
    {
        ("AppearActions", "task appears"),
        ("CompletionActions", "task completed"),
        ("FinalizationActions", "task finalized"),
    };

    public static void Apply(List<DialogueScene> scenes, string? areasJsonPath, string? eventsJsonPath)
    {
        var byId = scenes.ToDictionary(s => s.Id, StringComparer.Ordinal);
        var areaCandidates = new List<(string AreaId, string AreaName)>();
        if (!string.IsNullOrEmpty(areasJsonPath) && File.Exists(areasJsonPath))
            areaCandidates = ApplyAreas(byId, areasJsonPath);
        if (!string.IsNullOrEmpty(eventsJsonPath) && File.Exists(eventsJsonPath))
            ApplyEvents(byId, eventsJsonPath);
        // last resort, AFTER real hotspot triggers and events — a scene that fits neither must not be
        // stolen away from a legitimate event match by a guess
        ApplyAreaIdFallback(byId, areaCandidates);
    }

    private static List<(string AreaId, string AreaName)> ApplyAreas(Dictionary<string, DialogueScene> byId, string path)
    {
        var areaCandidates = new List<(string, string)>();
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        var root = doc.RootElement;
        if (root.TryGetProperty("Data", out var d)) root = d;
        if (root.ValueKind != JsonValueKind.Array) return areaCandidates;

        foreach (var area in root.EnumerateArray())
        {
            var areaId = Str(area, "AreaId");
            // AreaOrderingService.BuildDisplayName strips the "HotspotTitle_" prefix + camelCase-splits it,
            // and trims stray leading/trailing whitespace (" Walk-in Closet") - same cleanup the Lua area
            // mapping already relies on, reused here so a dialogue page never gets a raw/unusable area name
            var rawAreaName = Str(area, "Name") ?? areaId;
            var areaName = rawAreaName == null ? null : AreaOrderingService.BuildDisplayName(rawAreaName);
            if (!string.IsNullOrEmpty(areaId) && !string.IsNullOrEmpty(areaName))
                areaCandidates.Add((areaId, areaName));
            if (!area.TryGetProperty("HotspotsRefs", out var hotspots) || hotspots.ValueKind != JsonValueKind.Array) continue;

            foreach (var h in hotspots.EnumerateArray())
            {
                if (h.ValueKind != JsonValueKind.Object) continue;
                var task = Str(h, "Description");
                var hotspotId = Str(h, "Id");
                foreach (var (key, phase) in Phases)
                {
                    if (!h.TryGetProperty(key, out var actions) || actions.ValueKind != JsonValueKind.Array) continue;
                    foreach (var a in actions.EnumerateArray())
                        foreach (var sceneId in SceneIds(a))
                            if (byId.TryGetValue(sceneId, out var scene) && scene.Area == null)
                            {
                                scene.Area = areaName; scene.Task = task;
                                scene.Hotspot = hotspotId; scene.Trigger = phase;
                            }
                }
            }
        }
        return areaCandidates;
    }

    /// <summary>
    /// Last-resort HEURISTIC for a scene that still has neither a real hotspot trigger nor an event
    /// match: if the scene's own id starts with an area's AreaId, assume it belongs to that area anyway
    /// (a hotspot the dump no longer references, but the scene id still carries the area's naming). Same
    /// tie-break as <see cref="ApplyEvents"/> — longest AreaId wins, alphabetical on a length tie — and an
    /// AreaId shorter than 4 chars is skipped so a short/generic id can't produce an accidental match.
    /// 4 (not the events' 6) because an AreaId is a real, distinct area name straight out of the dump —
    /// the shortest ones in a live dump are "Maze"/"Tomb" (4) and "Study"/"Attic" (5), all of them
    /// unambiguous; a 6-char floor would silently drop those areas from the fallback entirely.
    /// This is a GUESS, not data read from the dump: no Task/Hotspot is set (there isn't one), and
    /// Trigger is stamped with a distinct marker so it never reads like a real trigger phase.
    ///
    /// The prefix match is case-INSENSITIVE, unlike <see cref="ApplyEvents"/>'s prefix matching — the
    /// dump is not consistent about casing between a scene id and the AreaId it belongs to (e.g. the
    /// Music Room area's AreaId is "MusicianRoom" but its scenes use "Musicianroom_..."/"musicianroom...";
    /// the Dance Floor area's AreaId "DanceFloor" pairs with scenes like "Dancefloor_02" — lower-case f).
    /// A case-sensitive match silently drops those areas from the fallback entirely (measured on a real
    /// dump: 665 matches case-sensitive vs. 825 case-insensitive, all of the extra 160 correct on manual
    /// spot-check) — so do NOT "fix" this back to Ordinal, that would reintroduce the miss.
    /// </summary>
    private static void ApplyAreaIdFallback(
        Dictionary<string, DialogueScene> byId, List<(string AreaId, string AreaName)> areaCandidates)
    {
        var ordered = areaCandidates
            .Where(c => c.AreaId.Length >= 4)
            .OrderByDescending(c => c.AreaId.Length)
            .ThenBy(c => c.AreaId, StringComparer.Ordinal)
            .ToList();

        foreach (var (areaId, areaName) in ordered)
            foreach (var scene in byId.Values)
                if (scene.Area == null && scene.Event == null
                    && scene.Id.StartsWith(areaId, StringComparison.OrdinalIgnoreCase))
                {
                    scene.Area = areaName;
                    scene.Trigger = "inferred from scene id (no hotspot trigger in the dump)";
                }
    }

    /// <summary>Scene ids this action triggers — inline dialogue as well as a cutscene.</summary>
    private static IEnumerable<string> SceneIds(JsonElement action)
    {
        // reálný dump má v polích akcí i JSON null (prázdný slot akce, nebo nevyplněný TriggerDialogue/
        // TriggerCutscene) — TryGetProperty na non-objektu vyhazuje, takže se všude ověřuje ValueKind
        if (action.ValueKind != JsonValueKind.Object) yield break;
        if (action.TryGetProperty("TriggerDialogue", out var td) && td.ValueKind == JsonValueKind.Object &&
            td.TryGetProperty("StoryDefinitionId", out var sid) && sid.GetString() is { } s1)
            yield return s1;
        if (action.TryGetProperty("TriggerCutscene", out var tc) && tc.ValueKind == JsonValueKind.Object &&
            tc.TryGetProperty("CutsceneId", out var cid) && cid.GetString() is { } s2)
            yield return s2;
    }

    private static void ApplyEvents(Dictionary<string, DialogueScene> byId, string path)
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        var root = doc.RootElement;
        if (root.TryGetProperty("Data", out var d)) root = d;
        if (root.ValueKind != JsonValueKind.Object) return;

        // nejdřív se posbírají VŠECHNY kandidáti ze všech knihoven a teprve pak se přiřazují scény —
        // kdyby se přiřazovalo hned při čtení každého eventu, vyhrávalo by pořadí v souboru místo
        // délky prefixu (viz komentář u `ordered` níže)
        var wrapperMatches = new List<(string SceneId, string EventName)>();
        var candidates = new List<(string Prefix, string EventName)>();

        foreach (var lib in root.EnumerateObject())
        {
            if (lib.Value.ValueKind != JsonValueKind.Array) continue;
            foreach (var ev in lib.Value.EnumerateArray())
            {
                if (ev.ValueKind != JsonValueKind.Object) continue;
                var name = Str(ev, "Name");
                if (string.IsNullOrEmpty(name) || name.Contains('_')) continue;   // nelokalizovaný raw název

                // vzácně eventy zabalují triggery stejně jako hotspoty (TriggerDialogue/TriggerCutscene) —
                // ověřeno na živém dumpu 26.07.01, kde se to týká jen 2 výskytů, ale je to levné pokrytí
                foreach (var sceneId in AllSceneIds(ev))
                    wrapperMatches.Add((sceneId, name));

                // hlavní cesta: eventy dialogové scény NEobalují do akčního objektu jako hotspoty, obsahují
                // rovnou string s ID scény v polích jako IntroDialogue/EndDialogue/EnterBoardDialogue
                // (např. "SP_XmasMystery2024_IntroDialogue_Dialogue"). Klíč scény je vždy "{EventId}..." —
                // ne vždy s podtržítkem za ID (viz "LBE_BushBonanza" + "BushChain1"), proto holé StartsWith.
                foreach (var prefix in EventIdPrefixes(ev))
                    candidates.Add((prefix, name));
            }
        }

        // obalová detekce je jednoznačná (id scény je přímo v akčním objektu), takže jde první
        foreach (var (sceneId, name) in wrapperMatches)
            Assign(byId, sceneId, name);

        // Když je jedno id eventu prefixem druhého (např. "CBE_MaddieInParis" vs.
        // "CBE_MaddieInParis2025"), musí vyhrát ten delší/specifičtější — jinak skončí scény z novějšího
        // ročníku na stránce staršího eventu. Řazením od nejdelšího k nejkratšímu (a abecedně při shodné
        // délce, aby výsledek nezávisel na pořadí v souboru) se zajistí, že se první match pro danou scénu
        // vždy najde u nejkonkrétnějšího kandidáta.
        var ordered = candidates
            .OrderByDescending(c => c.Prefix.Length)
            .ThenBy(c => c.Prefix, StringComparer.Ordinal)
            .ToList();

        foreach (var (prefix, name) in ordered)
            foreach (var scene in byId.Values)
                if (scene.Area == null && scene.Event == null &&
                    scene.Id.StartsWith(prefix, StringComparison.Ordinal))
                    Assign(byId, scene.Id, name);
    }

    private static void Assign(Dictionary<string, DialogueScene> byId, string sceneId, string eventName)
    {
        if (byId.TryGetValue(sceneId, out var scene) && scene.Area == null && scene.Event == null)
        {
            scene.Event = eventName;
            scene.Trigger ??= "part of the event";
        }
    }

    /// <summary>
    /// Candidate event-id prefixes for this event: every top-level string field whose name ends in
    /// `Id` or `Key` (ProgressionEventId, CollectibleBoardEventId, ConfigKey, ...), at least 6 chars long
    /// to avoid generic short values matching scenes by accident.
    /// </summary>
    private static IEnumerable<string> EventIdPrefixes(JsonElement ev)
    {
        foreach (var p in ev.EnumerateObject())
        {
            if (p.Value.ValueKind != JsonValueKind.String) continue;
            if (!p.Name.EndsWith("Id", StringComparison.Ordinal) && !p.Name.EndsWith("Key", StringComparison.Ordinal))
                continue;
            var v = p.Value.GetString();
            if (!string.IsNullOrEmpty(v) && v.Length >= 6) yield return v;
        }
    }

    /// <summary>Walks arbitrarily nested event actions.</summary>
    private static IEnumerable<string> AllSceneIds(JsonElement node)
    {
        if (node.ValueKind == JsonValueKind.Object)
        {
            foreach (var id in SceneIds(node)) yield return id;
            foreach (var p in node.EnumerateObject())
                foreach (var id in AllSceneIds(p.Value)) yield return id;
        }
        else if (node.ValueKind == JsonValueKind.Array)
            foreach (var item in node.EnumerateArray())
                foreach (var id in AllSceneIds(item)) yield return id;
    }

    private static string? Str(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString() : null;
}
