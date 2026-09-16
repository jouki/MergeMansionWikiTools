using System.Reflection;
using Metaplay.Core.Config;
using Code.GameLogic.GameEvents;
using Code.GameLogic.GameEvents.DailyChallenges.Data;
using GameLogic.Config;
using GameLogic.Player.Items;
using GameLogic.Player.Items.Activation;
using GameLogic.Player.Items.Production;
using GameLogic.Player.Requirements;
using GameLogic.Player.Rewards;
using MergeMansionWikiTools.Dumper.Core;
using MergeMansionWikiTools.Dumper.Serializers;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace MergeMansionWikiTools.Dumper.Dumpers;

/// <summary>
/// Produces the <c>Experimental/</c> section files and the standalone <c>Pets.json</c> — the parts
/// of the config that have no wiki generator yet and are dumped for research: the global tuning
/// values, dig events, pets, the whole offer/IAP catalogue, the Daily Challenges ladder, and a
/// speed-up cost analysis over every generator in the game.
/// <para>
/// The two outputs differ in FORMAT, not just in content: section files are
/// <see cref="DumpFileKind.Section"/> (no BOM, "T"-separated CreatedAt) because they go through the
/// shared converter stack, while <c>Pets.json</c> is <see cref="DumpFileKind.Aux"/> (no BOM,
/// <c>MetaTime.ToString()</c>'s " Z" CreatedAt) because it is written with NO converters at all —
/// see <see cref="WritePetsJson"/>.
/// </para>
/// </summary>
public sealed class ExperimentalDumper
{
    private readonly IDumpLog _log;

    public ExperimentalDumper(IDumpLog? log = null) => _log = log ?? ConsoleDumpLog.Instance;

    /// <summary>
    /// Writes one JSON file per section into <paramref name="outputDir"/> and returns
    /// <c>(section, path)</c> for each, in section order — the caller (DumperService) reports the
    /// per-section sizes from that list.
    /// </summary>
    public IReadOnlyList<(string Section, string Path)> WriteIndividualFiles(string outputDir, SharedGameConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        Directory.CreateDirectory(outputDir);

        var sections = Dump(config);
        var settings = Settings(config);
        var written = new List<(string, string)>();

        foreach (var (key, value) in sections)
        {
            var filePath = Path.Combine(outputDir, $"{key}.json");
            var json = DumpJson.Serialize(value, config.ArchiveCreatedAt, DumpFileKind.Section, settings);
            DumpJson.Write(filePath, json, DumpFileKind.Section);
            written.Add((key, filePath));
        }

        DumpConverters.LogRequirementSummary(settings, _log, "Experimental/*");
        return written;
    }

    /// <summary>The six sections, keyed by their file name.</summary>
    public IDictionary<string, object?> Dump(SharedGameConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);

        return new Dictionary<string, object?>
        {
            ["SharedGlobals"] = BuildSharedGlobals(config),
            ["DigEvent"] = BuildDigEvent(config),
            ["Pets"] = BuildPets(config),
            ["Offers"] = BuildOffers(config),
            ["DailyChallenges"] = BuildDailyChallenges(config),
            ["SpeedUpAnalysis"] = BuildSpeedUpAnalysis(config),
        };
    }

    private JsonSerializerSettings Settings(SharedGameConfig config)
        // SharedGlobals.StartupActions is an IDirectorAction list ({"TriggerVideo": {...}}), the one
        // file-specific shape here; everything else is default reflection over the shared stack.
        => DumpConverters.MainFileSettings(config, _log, MetaRefSkip.EmptyKey,
            new DirectorActionSerializer(config, _log));

    // ── 0. SharedGlobals ────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Every public instance property of <c>config.SharedGlobals</c>, keyed <c>"Name (TypeName)"</c>
    /// — the CLR type name verbatim, so a list shows up as <c>List`1</c>.
    /// <para>
    /// Each value is serialized on its own and re-parsed into a <see cref="JToken"/> rather than
    /// being embedded directly: that is what isolates <c>ReferenceLoopHandling.Ignore</c> (needed
    /// because <c>GameConfigKeyValue&lt;T&gt;.Member</c> returns <c>this</c>) and what turns a
    /// property whose getter throws into an inline <c>&lt;error: ...&gt;</c> string instead of
    /// aborting the section. <c>Member</c> and <c>ConfigKey</c> are skipped outright.
    /// </para>
    /// </summary>
    private Dictionary<string, object?>? BuildSharedGlobals(SharedGameConfig config)
    {
        var globals = config.SharedGlobals;
        if (globals == null)
        {
            _log.Warn("[SharedGlobals] config.SharedGlobals is null — entire entry missing from import.");
            return null;
        }

        var globalsData = new Dictionary<string, object?>();
        var globalsSettings = Settings(config);
        globalsSettings.ReferenceLoopHandling = ReferenceLoopHandling.Ignore;

        foreach (var p in globals.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (p.Name is "Member" or "ConfigKey") continue;

            var key = GlobalsKey(p.Name, p.PropertyType);
            try
            {
                var val = p.GetValue(globals);
                if (val == null)
                {
                    globalsData[key] = null;
                    _log.Warn($"[SharedGlobals] {p.Name} ({p.PropertyType.Name}) = null");
                }
                else
                {
                    globalsData[key] = JToken.Parse(JsonConvert.SerializeObject(val, globalsSettings));
                }
            }
            catch (Exception ex)
            {
                globalsData[key] = $"<error: {ex.GetType().Name}: {ex.Message}>";
                _log.Error($"[SharedGlobals] Failed to serialize {p.Name} ({p.PropertyType.Name}): {ex.Message}");
            }
        }

        return globalsData;
    }

    /// <summary>
    /// One SharedGlobals key: <c>"Name (TypeName)"</c> with the CLR type name verbatim, so a
    /// <c>List&lt;string&gt;</c> property reads <c>"AdsSoftLaunchSegments (List`1)"</c> — the
    /// backtick arity suffix and all. Public so the formatting can be unit-tested.
    /// </summary>
    public static string GlobalsKey(string name, Type type) => $"{name} ({type.Name})";

    // ── 1. DigEvent (Re-Archaeology) ────────────────────────────────────────────────────────────

    private static Dictionary<string, object?> BuildDigEvent(SharedGameConfig config) => new()
    {
        ["Events"] = config.DigEvents?.EnumerateAll().Select(x => x.Value).ToArray() ?? Array.Empty<object>(),

        ["Boards"] = config.DigEventBoards?.EnumerateAll().Select(x =>
        {
            var b = (DigEventBoards)x.Value;
            return new Dictionary<string, object?>
            {
                ["BoardId"] = b.BoardId?.ToString(),
                ["BoardWidth"] = b.BoardWidth,
                ["BoardHeight"] = b.BoardHeight,
                ["CellSize"] = b.CellSize,
                ["Treasures"] = b.Treasures?.Select(t => t?.ToString()).ToArray(),
                ["BoardReward"] = b.BoardReward,
                ["CompensationChance"] = b.CompensationChance,
            };
        }).ToArray() ?? Array.Empty<object>(),

        ["Items"] = config.DigEventItemInfos?.EnumerateAll().Select(x =>
        {
            var item = (DigEventItemInfo)x.Value;
            return new Dictionary<string, object?>
            {
                ["ItemId"] = item.ItemId?.ToString(),
                ["AssetId"] = item.AssetId,
                ["GoesMuseum"] = item.GoesMuseum,
                ["CanBeShiny"] = item.CanBeShiny,
                ["Shape"] = item.Coordinates?.Select(c => new[] { c.Item1, c.Item2 }).ToArray(),
                ["Weight"] = item.Weight,
                ["MuseumSize"] = $"{item.MuseumItemWidth}x{item.MuseumItemHeight}",
                ["MuseumItemRotation"] = item.MuseumItemRotation.ToString(),
            };
        }).ToArray() ?? Array.Empty<object>(),

        ["ShinyProgression"] = config.DigEventShinyProgression?.EnumerateAll().Select(x => x.Value).ToArray() ?? Array.Empty<object>(),
    };

    // ── 2. Pets ─────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// The pet table: each id/text pair as the raw loc id plus its translation. Shared by the
    /// <c>Experimental/Pets.json</c> section and the standalone <c>Pets.json</c>, which differ only
    /// in how they are written (see <see cref="WritePetsJson"/>).
    /// </summary>
    private static object[] BuildPets(SharedGameConfig config)
        => config.PetInfos?.EnumerateAll().Select(x =>
        {
            var pet = (PetInfo)x.Value;
            return (object)new Dictionary<string, object?>
            {
                ["PetId"] = pet.ConfigKey?.ToString(),
                ["UnlockHeaderLocId"] = pet.UnlockHeaderLocId,
                ["UnlockHeader"] = Loc.Translate(pet.UnlockHeaderLocId),
                ["UnlockDescLocId"] = pet.UnlockDescLocId,
                ["UnlockDesc"] = Loc.Translate(pet.UnlockDescLocId),
                ["InfoHeaderLocId"] = pet.InfoHeaderLocId,
                ["InfoHeader"] = Loc.Translate(pet.InfoHeaderLocId),
                ["InfoDescLocId"] = pet.InfoDescLocId,
                ["InfoDesc"] = Loc.Translate(pet.InfoDescLocId),
                ["SelectionHeaderLocId"] = pet.SelectionHeaderLocId,
                ["SelectionHeader"] = Loc.Translate(pet.SelectionHeaderLocId),
                ["SelectionDescriptionLocId"] = pet.SelectionDescriptionLocId,
                ["SelectionDescription"] = Loc.Translate(pet.SelectionDescriptionLocId),
                ["Decoration"] = pet.Decoration?.ToString(),
                ["AssetPackId"] = pet.AssetPackId?.ToString(),
            };
        }).ToArray() ?? Array.Empty<object>();

    /// <summary>
    /// Serializes the standalone <c>Pets.json</c> (the app's <c>DumpMode.Pets</c>) and returns the
    /// JSON text.
    /// <para>
    /// It uses DEFAULT Newtonsoft settings on purpose — no converters, no
    /// <c>IgnoreDataMemberContractResolver</c>, <c>NullValueHandling.Include</c> — because that is
    /// what legacy does here, and it is the whole reason this one file's timestamp looks different:
    /// with no MetaTime converter in the chain the envelope's <c>MetaTime</c> falls through to its
    /// own <c>TypeConverter</c> and prints <c>"… 12:12:19.371 Z"</c> rather than the "T" form.
    /// <see cref="DumpFileKind.Aux"/> is what reproduces that here (the CreatedAt is formatted
    /// up-front, not re-derived from the settings). The payload itself is nothing but strings, so
    /// the settings have no other observable effect — golden's two <c>"InfoHeader": null</c> lines
    /// survive either way, since <c>NullValueHandling</c> never reaches dictionary VALUES.
    /// </para>
    /// </summary>
    public static string WritePetsJson(string filePath, SharedGameConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        var json = DumpJson.Serialize(BuildPets(config), config.ArchiveCreatedAt, DumpFileKind.Aux, new JsonSerializerSettings());
        DumpJson.Write(filePath, json, DumpFileKind.Aux);
        return json;
    }

    // ── 3. Offers ───────────────────────────────────────────────────────────────────────────────

    private static Dictionary<string, object?> BuildOffers(SharedGameConfig config) => new()
    {
        ["Offers"] = config.Offers?.EnumerateAll().Select(x => x.Value).ToArray() ?? Array.Empty<object>(),
        ["InAppProducts"] = config.InAppProducts?.EnumerateAll().Select(x => x.Value).ToArray() ?? Array.Empty<object>(),
        ["OfferGroups"] = config.OfferGroups?.EnumerateAll().Select(x => x.Value).ToArray() ?? Array.Empty<object>(),

        ["OfferPopupTriggers"] = config.OfferPopupTriggers?.EnumerateAll().Select(x =>
        {
            var t = (OfferPopupTrigger)x.Value;
            return new Dictionary<string, object?>
            {
                ["TriggerId"] = t.ConfigKey?.ToString(),
                ["MaxTriggersPerSession"] = t.MaxTriggersPerSession,
                ["MaxTriggersTotal"] = t.MaxTriggersTotal,
                ["TriggerRequirements"] = t.TriggerRequirements?.Select(SerializeRequirement).ToArray(),
                ["TriggerPlacements"] = t.TriggerPlacements?.ToDictionary(kv => kv.Key.ToString(), kv => (object?)kv.Value),
                ["ActivatesOfferGroup"] = t.ActivatesOfferGroup,
                ["MaxWaitTimerToPrompt"] = t.MaxWaitTimerToPrompt,
            };
        }).ToArray() ?? Array.Empty<object>(),
    };

    /// <summary>
    /// A trigger requirement, written as its own flat reflection dump — <c>$type</c> followed by
    /// every DECLARED property and then every declared field (compiler backing fields excluded) —
    /// and NOT through the shared <c>PlayerRequirementConverter</c>'s <c>{Kind: payload}</c> union.
    /// Golden confirms the flat form:
    /// <c>{"$type": "PlayerCurrencySpentRequirement", "Currency": "Energy", "Amount": 25}</c>.
    /// Unreadable members are dropped rather than failing the section — this is a research dump over
    /// requirement kinds no wiki generator consumes.
    /// </summary>
    private static object? SerializeRequirement(PlayerRequirement? req)
    {
        if (req == null) return null;

        var type = req.GetType();
        var dict = new Dictionary<string, object?> { ["$type"] = type.Name };
        const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly;

        foreach (var prop in type.GetProperties(flags))
        {
            if (prop.GetIndexParameters().Length > 0) continue;
            try { dict[prop.Name] = prop.GetValue(req); }
            catch { /* an unreadable derived getter is simply left out */ }
        }

        foreach (var field in type.GetFields(flags))
        {
            if (field.Name.Contains("BackingField")) continue;
            try { dict[field.Name] = field.GetValue(req); }
            catch { /* ditto */ }
        }

        return dict;
    }

    // ── 4. DailyChallenges ──────────────────────────────────────────────────────────────────────

    /// <summary>
    /// The Daily Challenges ladder, flattened week → day → task and week → milestone, with the day /
    /// objective / milestone libraries joined in by id (the week only holds references).
    /// </summary>
    private static object[] BuildDailyChallenges(SharedGameConfig config)
    {
        var objectiveLookup = Lookup(config.DailyChallengesStandardObjectives);
        var milestoneLookup = Lookup(config.DailyChallengesMilestones);
        var dayLookup = Lookup(config.DailyChallengesDays);

        return config.DailyChallengesWeeks?.EnumerateAll().Select(wkv =>
        {
            var week = (DailyChallengesWeekData)wkv.Value;
            return (object)new Dictionary<string, object?>
            {
                ["WeekId"] = week.ConfigKey?.ToString(),
                ["Difficulty"] = week.WeekDifficulty,
                ["Days"] = week.Days?.Select((dayRef, dayIndex) =>
                {
                    var dayId = dayRef?.ConfigKey?.ToString();
                    dayLookup.TryGetValue(dayId ?? "", out var day);
                    return new Dictionary<string, object?>
                    {
                        ["Day"] = dayIndex + 1,
                        ["DayId"] = dayId,
                        ["RequiredTasksForReward"] = day?.RequiredCompletedObjectivesForDayReward,
                        ["TargetMilestoneIndex"] = day?.TargetMilestoneIndex,
                        ["Tasks"] = day?.StandardObjectives?.Select(objRef =>
                        {
                            var objId = objRef?.ConfigKey?.ToString();
                            objectiveLookup.TryGetValue(objId ?? "", out var obj);
                            if (obj == null) return new Dictionary<string, object?> { ["TaskId"] = objId };
                            return new Dictionary<string, object?>
                            {
                                ["TaskId"] = objId,
                                ["DisplayName"] = Loc.Translate(obj.LocId),
                                ["ObjectiveType"] = obj.ObjectiveType.ToString(),
                                ["Requirement"] = obj.ObjectiveRequirement,
                                ["Parameter"] = obj.ObjectiveParameter,
                                ["Priority"] = obj.OrderPriority,
                            };
                        }).ToArray(),
                    };
                }).ToArray(),
                ["Milestones"] = week.Milestones?.Select(msRef =>
                {
                    var msId = msRef?.ConfigKey?.ToString();
                    milestoneLookup.TryGetValue(msId ?? "", out var ms);
                    if (ms == null) return new Dictionary<string, object?> { ["MilestoneId"] = msId };
                    return new Dictionary<string, object?>
                    {
                        ["MilestoneId"] = msId,
                        ["RequiredPoints"] = ms.RequiredPoints,
                        ["Rewards"] = ms.Rewards,
                    };
                }).ToArray(),
            };
        }).ToArray() ?? Array.Empty<object>();
    }

    /// <summary>Library keyed by its config key's string form, for the id-based joins above.</summary>
    private static Dictionary<string, TValue> Lookup<TKey, TValue>(GameConfigLibrary<TKey, TValue>? library)
        where TKey : notnull
    {
        var map = new Dictionary<string, TValue>();
        if (library == null) return map;
        foreach (var kv in library.EnumerateAll())
            map[kv.Key?.ToString() ?? ""] = (TValue)kv.Value;
        return map;
    }

    // ── 5. Speed-up cost analysis ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Every generator in the game with its cycle timing, its drops' time-skip prices and the
    /// resulting average. The <c>_debug</c> block records how many items survived each filter step,
    /// which is how a config change that quietly removes generators becomes visible.
    /// </summary>
    private static Dictionary<string, object?> BuildSpeedUpAnalysis(SharedGameConfig config)
    {
        var speedUpData = new List<object>();
        int dbgTotal = 0, dbgHasAct = 0, dbgHasSpawn = 0, dbgHasCycle = 0;

        if (config.Items != null)
        {
            foreach (var kv in config.Items.EnumerateAll())
            {
                dbgTotal++;
                var item = (ItemDefinition)kv.Value;

                var actFeatures = item.ActivationFeatures as ActivationFeatures
                    ?? item.GetType().GetField("_ActivationFeatures", BindingFlags.NonPublic | BindingFlags.Instance)
                        ?.GetValue(item) as ActivationFeatures;
                if (actFeatures == null) continue;
                dbgHasAct++;

                if (actFeatures.ActivationSpawn == null || actFeatures.ActivationSpawn is EmptyProducer) continue;
                dbgHasSpawn++;

                // IActivationCycle -> ActivationCycle -> DailyActivationCyclesData -> ActivationCycleData
                if (actFeatures.ActivationCycle is not ActivationCycle activationCycle) continue;

                long delayMs;
                var amount = 0;
                double timerSkipMult = 0;

                var cycleData = activationCycle.DailyActivationCyclesData as ActivationCycleData;
                if (cycleData?.DelaysBetweenCycles != null && cycleData.DelaysBetweenCycles.Count > 0)
                {
                    delayMs = cycleData.DelaysBetweenCycles.First().Milliseconds;
                    amount = cycleData.ActivationAmountInCycle?.FirstOrDefault() ?? 0;
                    timerSkipMult = cycleData.TimerSkipMultiplier?.FirstOrDefault().Double ?? 0;
                }
                else
                {
                    delayMs = activationCycle.ActivationDelay.Milliseconds; // simple single-cycle generator
                }

                if (delayMs <= 0) continue;
                dbgHasCycle++;

                var producer = actFeatures.ActivationSpawn;
                var (avgTSP, drops) = GetProducerDropsTSP(producer, config);

                speedUpData.Add(new Dictionary<string, object?>
                {
                    ["ItemType"] = kv.Key,
                    ["RawItemTSP"] = item.TimeSkipPriceGems.Double,
                    ["ProducerType"] = GetBaseProducer(producer).GetType().Name,
                    ["ProducerTSP"] = Math.Round(avgTSP, 6),
                    ["Delay_ms"] = delayMs,
                    ["Amount"] = amount,
                    ["TimerSkipMult"] = timerSkipMult,
                    ["StorageMax"] = actFeatures.StorageMax,
                    ["HowManyCycles"] = activationCycle.HowManyCycles,
                    ["HasCycleData"] = cycleData != null,
                    ["Drops"] = drops,
                });
            }
        }

        return new Dictionary<string, object?>
        {
            ["_debug"] = new Dictionary<string, object?>
            {
                ["TotalItems"] = dbgTotal,
                ["HasActivationFeatures"] = dbgHasAct,
                ["HasNonEmptySpawn"] = dbgHasSpawn,
                ["HasCycleWithDelay"] = dbgHasCycle,
            },
            ["Generators"] = speedUpData,
        };
    }

    /// <summary>Resolves a producer's drops and their average time-skip price.</summary>
    private static (double AvgTsp, List<object> Drops) GetProducerDropsTSP(IItemSpawner producer, SharedGameConfig config)
    {
        var drops = new List<object>();

        if (producer is PrefixProducer prefix && prefix.BaseProducer is IItemSpawner baseProd)
            return GetProducerDropsTSP(baseProd, config);

        var itemEntries = new List<(ItemDef? ItemRef, int Weight)>();
        switch (producer)
        {
            case ConstantProducer cp when cp.Products != null:
                itemEntries.AddRange(cp.Products.Select(p => ((ItemDef?)p, 1)));
                break;
            case RandomProducer rp when rp.OddsList != null:
                itemEntries.AddRange(rp.OddsList.Select(o => ((ItemDef?)o.Type, o.Weight)));
                break;
            case ControlledRandomProducer crp when crp.GenerationOdds != null:
                itemEntries.AddRange(crp.GenerationOdds.Select(o => ((ItemDef?)o.Type, o.Weight)));
                break;
            default:
                // Sequence producers expose their odds under the same name but no shared interface.
                var oddsListProp = producer.GetType().GetProperty("OddsList",
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                if (oddsListProp?.GetValue(producer) is IEnumerable<ItemOdds> oddsList)
                    itemEntries.AddRange(oddsList.Select(o => ((ItemDef?)o.Type, o.Weight)));
                break;
        }

        foreach (var (itemRef, weight) in itemEntries)
        {
            try
            {
                var def = itemRef?.GetDef(config);
                drops.Add(new Dictionary<string, object?>
                {
                    ["ItemType"] = itemRef?.ConfigKey ?? 0,
                    ["TSP"] = Math.Round(def?.TimeSkipPriceGems.Double ?? 0, 6),
                    ["Weight"] = weight,
                });
            }
            catch
            {
                drops.Add(new Dictionary<string, object?>
                {
                    ["ItemType"] = itemRef?.ConfigKey ?? 0,
                    ["TSP"] = "<error>",
                    ["Weight"] = weight,
                });
            }
        }

        var avg = drops.Count > 0
            ? drops.OfType<Dictionary<string, object?>>()
                .Where(d => d["TSP"] is double)
                .Select(d => (double)d["TSP"]!)
                .DefaultIfEmpty(0)
                .Average()
            : 0;

        return (avg, drops);
    }

    private static IItemSpawner GetBaseProducer(IItemSpawner producer)
    {
        while (producer is PrefixProducer prefix && prefix.BaseProducer is IItemSpawner baseProd)
            producer = baseProd;
        return producer;
    }
}
