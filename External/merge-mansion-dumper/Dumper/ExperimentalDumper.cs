using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Code.GameLogic.GameEvents;
using Code.GameLogic.GameEvents.DailyChallenges.Data;
using GameLogic.Config;
using GameLogic.Player.Items;
using GameLogic.Player.Items.Activation;
using GameLogic.Player.Items.Production;
using GameLogic.Player.Requirements;
using GameLogic.Player.Rewards;
using merge_mansion_dumper.Dumper.Base;
using merge_mansion_dumper.Dumper.Json;
using Metaplay.Core.Localization;
using Metaplay.Unity;
using Newtonsoft.Json;
using Newtonsoft.Json.Converters;

namespace merge_mansion_dumper.Dumper
{
    public class ExperimentalDumper : JsonDumper<IDictionary<string, object>>
    {
        /// <summary>
        /// Writes each section as a separate JSON file into the given directory.
        /// Returns list of (sectionName, filePath) for successfully written files.
        /// </summary>
        public List<(string Section, string Path)> WriteIndividualFiles(string outputDir, SharedGameConfig config)
        {
            Directory.CreateDirectory(outputDir);

            var sections = Dump(config);
            var settings = CreateSettings(config);
            var written = new List<(string, string)>();

            foreach (var (key, value) in sections)
            {
                var data = new { CreatedAt = config.ArchiveCreatedAt, Data = value };
                var filePath = System.IO.Path.Combine(outputDir, $"{key}.json");
                File.WriteAllText(filePath, JsonConvert.SerializeObject(data, Formatting.Indented, settings));
                written.Add((key, filePath));
            }

            return written;
        }

        public override IDictionary<string, object> Dump(SharedGameConfig config)
        {
            var result = new Dictionary<string, object>();

            // ── 0. SharedGlobals ──────────────────────────────────────────
            var globals = config.SharedGlobals;
            if (globals != null)
            {
                var globalsData = new Dictionary<string, object>();

                // Dump all properties via reflection
                var props = globals.GetType().GetProperties(
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                foreach (var p in props)
                {
                    try
                    {
                        var val = p.GetValue(globals);
                        if (val != null)
                            globalsData[$"{p.Name} ({p.PropertyType.Name})"] = val.ToString();
                        else
                            globalsData[$"{p.Name} ({p.PropertyType.Name})"] = null;
                    }
                    catch { globalsData[p.Name] = "<error>"; }
                }

                result["SharedGlobals"] = globalsData;
            }

            // ── 1. DigEvent (Archaeology — combined) ─────────────────────

            result["DigEvent"] = new Dictionary<string, object>
            {
                ["Events"] = config.DigEvents?.EnumerateAll()
                    .Select(x => x.Value).ToArray() ?? Array.Empty<object>(),

                ["Boards"] = config.DigEventBoards?.EnumerateAll().Select(x =>
                {
                    var b = (DigEventBoards)x.Value;
                    return new Dictionary<string, object>
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
                    return new Dictionary<string, object>
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

                ["ShinyProgression"] = config.DigEventShinyProgression?.EnumerateAll()
                    .Select(x => x.Value).ToArray() ?? Array.Empty<object>(),
            };

            // ── 2. Pets ──────────────────────────────────────────────────

            result["Pets"] = config.PetInfos?.EnumerateAll().Select(x =>
            {
                var pet = (PetInfo)x.Value;
                return new Dictionary<string, object>
                {
                    ["PetId"] = pet.ConfigKey?.ToString(),
                    ["UnlockHeaderLocId"] = pet.UnlockHeaderLocId,
                    ["UnlockHeader"] = Localize(pet.UnlockHeaderLocId),
                    ["UnlockDescLocId"] = pet.UnlockDescLocId,
                    ["UnlockDesc"] = Localize(pet.UnlockDescLocId),
                    ["InfoHeaderLocId"] = pet.InfoHeaderLocId,
                    ["InfoHeader"] = Localize(pet.InfoHeaderLocId),
                    ["InfoDescLocId"] = pet.InfoDescLocId,
                    ["InfoDesc"] = Localize(pet.InfoDescLocId),
                    ["SelectionHeaderLocId"] = pet.SelectionHeaderLocId,
                    ["SelectionHeader"] = Localize(pet.SelectionHeaderLocId),
                    ["SelectionDescriptionLocId"] = pet.SelectionDescriptionLocId,
                    ["SelectionDescription"] = Localize(pet.SelectionDescriptionLocId),
                    ["Decoration"] = pet.Decoration?.ToString(),
                    ["AssetPackId"] = pet.AssetPackId?.ToString(),
                };
            }).ToArray() ?? Array.Empty<object>();

            // ── 3. Offers (combined) ───────────────────────────────────

            result["Offers"] = new Dictionary<string, object>
            {
                ["Offers"] = config.Offers?.EnumerateAll()
                    .Select(x => x.Value).ToArray() ?? Array.Empty<object>(),

                ["InAppProducts"] = config.InAppProducts?.EnumerateAll()
                    .Select(x => x.Value).ToArray() ?? Array.Empty<object>(),

                ["OfferGroups"] = config.OfferGroups?.EnumerateAll()
                    .Select(x => x.Value).ToArray() ?? Array.Empty<object>(),

                ["OfferPopupTriggers"] = config.OfferPopupTriggers?.EnumerateAll().Select(x =>
                {
                    var t = (OfferPopupTrigger)x.Value;
                    return new Dictionary<string, object>
                    {
                        ["TriggerId"] = t.ConfigKey?.ToString(),
                        ["MaxTriggersPerSession"] = t.MaxTriggersPerSession,
                        ["MaxTriggersTotal"] = t.MaxTriggersTotal,
                        ["TriggerRequirements"] = t.TriggerRequirements?.Select(SerializeRequirement).ToArray(),
                        ["TriggerPlacements"] = t.TriggerPlacements?.ToDictionary(
                            kv => kv.Key.ToString(),
                            kv => (object)kv.Value),
                        ["ActivatesOfferGroup"] = t.ActivatesOfferGroup,
                        ["MaxWaitTimerToPrompt"] = t.MaxWaitTimerToPrompt,
                    };
                }).ToArray() ?? Array.Empty<object>(),
            };

            // ── 4. DailyChallenges (combined) ─────────────────────────────

            // Build lookup tables
            var objectiveLookup = new Dictionary<string, DailyChallengesStandardObjectiveData>();
            if (config.DailyChallengesStandardObjectives != null)
                foreach (var kv in config.DailyChallengesStandardObjectives.EnumerateAll())
                    objectiveLookup[kv.Key.ToString()] = (DailyChallengesStandardObjectiveData)kv.Value;

            var milestoneLookup = new Dictionary<string, DailyChallengesMilestoneData>();
            if (config.DailyChallengesMilestones != null)
                foreach (var kv in config.DailyChallengesMilestones.EnumerateAll())
                    milestoneLookup[kv.Key.ToString()] = (DailyChallengesMilestoneData)kv.Value;

            var dayLookup = new Dictionary<string, DailyChallengesDayData>();
            if (config.DailyChallengesDays != null)
                foreach (var kv in config.DailyChallengesDays.EnumerateAll())
                    dayLookup[kv.Key.ToString()] = (DailyChallengesDayData)kv.Value;

            result["DailyChallenges"] = config.DailyChallengesWeeks?.EnumerateAll().Select(wkv =>
            {
                var week = (DailyChallengesWeekData)wkv.Value;
                return new Dictionary<string, object>
                {
                    ["WeekId"] = week.ConfigKey?.ToString(),
                    ["Difficulty"] = week.WeekDifficulty,
                    ["Days"] = week.Days?.Select((dayRef, dayIndex) =>
                    {
                        var dayId = dayRef?.ConfigKey?.ToString();
                        dayLookup.TryGetValue(dayId ?? "", out var day);
                        return new Dictionary<string, object>
                        {
                            ["Day"] = dayIndex + 1,
                            ["DayId"] = dayId,
                            ["RequiredTasksForReward"] = day?.RequiredCompletedObjectivesForDayReward,
                            ["TargetMilestoneIndex"] = day?.TargetMilestoneIndex,
                            ["Tasks"] = day?.StandardObjectives?.Select(objRef =>
                            {
                                var objId = objRef?.ConfigKey?.ToString();
                                objectiveLookup.TryGetValue(objId ?? "", out var obj);
                                if (obj == null) return new Dictionary<string, object> { ["TaskId"] = objId };
                                return new Dictionary<string, object>
                                {
                                    ["TaskId"] = objId,
                                    ["DisplayName"] = Localize(obj.LocId),
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
                        if (ms == null) return new Dictionary<string, object> { ["MilestoneId"] = msId };
                        return (object)new Dictionary<string, object>
                        {
                            ["MilestoneId"] = msId,
                            ["RequiredPoints"] = ms.RequiredPoints,
                            ["Rewards"] = ms.Rewards,
                        };
                    }).ToArray(),
                };
            }).ToArray() ?? Array.Empty<object>();

            // ── 5. Speed-Up Cost Analysis ────────────────────────────────
            var speedUpData = new List<object>();
            int dbgTotal = 0, dbgHasAct = 0, dbgHasSpawn = 0, dbgHasCycle = 0;
            if (config.Items != null)
            {
                foreach (var kv in config.Items.EnumerateAll())
                {
                    dbgTotal++;
                    var item = (ItemDefinition)kv.Value;

                    // Debug: try multiple access paths
                    var actFeatures = item.ActivationFeatures as ActivationFeatures;
                    if (actFeatures == null)
                    {
                        // Try reflection for private field
                        var field = item.GetType().GetField("_ActivationFeatures",
                            BindingFlags.NonPublic | BindingFlags.Instance);
                        actFeatures = field?.GetValue(item) as ActivationFeatures;
                    }

                    if (actFeatures == null) continue;
                    dbgHasAct++;

                    if (actFeatures.ActivationSpawn == null || actFeatures.ActivationSpawn is EmptyProducer)
                        continue;
                    dbgHasSpawn++;

                    // Navigate: IActivationCycle → ActivationCycle → DailyActivationCyclesData → ActivationCycleData
                    var activationCycle = actFeatures.ActivationCycle as ActivationCycle;
                    if (activationCycle == null) continue;

                    long delayMs;
                    int amount = 0;
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
                        // Fallback: simple single-cycle generator
                        delayMs = activationCycle.ActivationDelay.Milliseconds;
                    }

                    if (delayMs <= 0) continue;
                    dbgHasCycle++;

                    var producer = actFeatures.ActivationSpawn;
                    var (avgTSP, drops) = GetProducerDropsTSP(producer, config);
                    var baseProducer = GetBaseProducer(producer);

                    speedUpData.Add(new Dictionary<string, object>
                    {
                        ["ItemType"] = kv.Key,
                        ["RawItemTSP"] = item.TimeSkipPriceGems.Double,
                        ["ProducerType"] = baseProducer.GetType().Name,
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
            result["SpeedUpAnalysis"] = new Dictionary<string, object>
            {
                ["_debug"] = new Dictionary<string, object>
                {
                    ["TotalItems"] = dbgTotal,
                    ["HasActivationFeatures"] = dbgHasAct,
                    ["HasNonEmptySpawn"] = dbgHasSpawn,
                    ["HasCycleWithDelay"] = dbgHasCycle,
                },
                ["Generators"] = speedUpData,
            };

            return result;
        }

        private static object SerializeRequirement(PlayerRequirement req)
        {
            if (req == null) return null;

            var type = req.GetType();
            var dict = new Dictionary<string, object>
            {
                ["$type"] = type.Name
            };

            var flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly;

            foreach (var prop in type.GetProperties(flags))
            {
                if (prop.GetIndexParameters().Length > 0) continue;
                try { dict[prop.Name] = prop.GetValue(req); }
                catch { /* skip unreadable */ }
            }

            foreach (var field in type.GetFields(flags))
            {
                if (field.Name.Contains("BackingField")) continue;
                try { dict[field.Name] = field.GetValue(req); }
                catch { /* skip unreadable */ }
            }

            return dict;
        }

        /// <summary>Writes Pets.json as a standalone file (used by main dump).</summary>
        public static void WritePetsJson(string filePath, SharedGameConfig config)
        {
            var pets = config.PetInfos?.EnumerateAll().Select(x =>
            {
                var pet = (GameLogic.Player.Rewards.PetInfo)x.Value;
                return new Dictionary<string, object>
                {
                    ["PetId"] = pet.ConfigKey?.ToString(),
                    ["UnlockHeaderLocId"] = pet.UnlockHeaderLocId,
                    ["UnlockHeader"] = Localize(pet.UnlockHeaderLocId),
                    ["UnlockDescLocId"] = pet.UnlockDescLocId,
                    ["UnlockDesc"] = Localize(pet.UnlockDescLocId),
                    ["InfoHeaderLocId"] = pet.InfoHeaderLocId,
                    ["InfoHeader"] = Localize(pet.InfoHeaderLocId),
                    ["InfoDescLocId"] = pet.InfoDescLocId,
                    ["InfoDesc"] = Localize(pet.InfoDescLocId),
                    ["SelectionHeaderLocId"] = pet.SelectionHeaderLocId,
                    ["SelectionHeader"] = Localize(pet.SelectionHeaderLocId),
                    ["SelectionDescriptionLocId"] = pet.SelectionDescriptionLocId,
                    ["SelectionDescription"] = Localize(pet.SelectionDescriptionLocId),
                    ["Decoration"] = pet.Decoration?.ToString(),
                    ["AssetPackId"] = pet.AssetPackId?.ToString(),
                };
            }).ToArray() ?? Array.Empty<object>();

            var data = new { CreatedAt = config.ArchiveCreatedAt, Data = pets };
            var dir = System.IO.Path.GetDirectoryName(filePath);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);
            File.WriteAllText(filePath, JsonConvert.SerializeObject(data, Formatting.Indented));
        }

        private static string Localize(string locId)
        {
            if (string.IsNullOrEmpty(locId))
                return null;

            var lang = MetaplaySDK.ActiveLanguage;
            if (lang?.Translations == null)
                return null;

            return lang.Translations.TryGetValue(TranslationId.FromString(locId), out var translation)
                ? translation
                : null;
        }

        /// <summary>
        /// Resolves all drop items from a producer and returns (avgTSP, list of drop details).
        /// </summary>
        private static (double avgTSP, List<object> drops) GetProducerDropsTSP(
            IItemSpawner producer, SharedGameConfig config)
        {
            var drops = new List<object>();

            // Unwrap PrefixProducer
            if (producer is PrefixProducer pp && pp.BaseProducer is IItemSpawner baseProd)
                return GetProducerDropsTSP(baseProd, config);

            // Collect product references with weights based on producer type
            var itemEntries = new List<(ItemDef itemRef, int weight)>();
            switch (producer)
            {
                case ConstantProducer cp when cp.Products != null:
                    itemEntries.AddRange(cp.Products.Select(p => (p, 1)));
                    break;
                case RandomProducer rp when rp.OddsList != null:
                    itemEntries.AddRange(rp.OddsList.Select(o => (o.Type, o.Weight)));
                    break;
                case ControlledRandomProducer crp when crp.GenerationOdds != null:
                    itemEntries.AddRange(crp.GenerationOdds.Select(o => (o.Type, o.Weight)));
                    break;
                default:
                    // Try reflection for OddsList (sequence producers)
                    var oddsListProp = producer.GetType().GetProperty("OddsList",
                        BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                    if (oddsListProp?.GetValue(producer) is IEnumerable<ItemOdds> oddsList)
                        itemEntries.AddRange(oddsList.Select(o => (o.Type, o.Weight)));
                    break;
            }

            foreach (var (itemRef, weight) in itemEntries)
            {
                try
                {
                    var def = itemRef?.GetDef(config);
                    var tsp = def?.TimeSkipPriceGems.Double ?? 0;
                    drops.Add(new Dictionary<string, object>
                    {
                        ["ItemType"] = itemRef?.ConfigKey ?? 0,
                        ["TSP"] = Math.Round(tsp, 6),
                        ["Weight"] = weight,
                    });
                }
                catch
                {
                    drops.Add(new Dictionary<string, object>
                    {
                        ["ItemType"] = itemRef?.ConfigKey ?? 0,
                        ["TSP"] = "<error>",
                        ["Weight"] = weight,
                    });
                }
            }

            var avg = drops.Count > 0
                ? drops.OfType<Dictionary<string, object>>()
                    .Where(d => d["TSP"] is double)
                    .Select(d => (double)d["TSP"])
                    .DefaultIfEmpty(0)
                    .Average()
                : 0;

            return (avg, drops);
        }

        private static IItemSpawner GetBaseProducer(IItemSpawner producer)
        {
            while (producer is PrefixProducer pp && pp.BaseProducer is IItemSpawner baseProd)
                producer = baseProd;
            return producer;
        }

        protected override JsonSerializerSettings CreateSettings(SharedGameConfig config)
        {
            return new JsonSerializerSettings(base.CreateSettings(config))
            {
                Converters =
                {
                    new MergeMansionJsonConverter(config, Output, false),
                    new StringEnumConverter()
                }
            };
        }
    }
}
