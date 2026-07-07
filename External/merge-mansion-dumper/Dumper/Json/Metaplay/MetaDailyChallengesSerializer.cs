using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Code.GameLogic.GameEvents.DailyChallenges.Data;
using Metaplay.Core.Model;
using Newtonsoft.Json;

namespace merge_mansion_dumper.Dumper.Json.Metaplay
{
    /// <summary>
    /// Serializer for the DailyChallenges (The Daily Scoop V2, 2026-06) config data types.
    /// Several of them carry PRIVATE [MetaMember] fields that Newtonsoft's default
    /// (public-only) serialization silently drops: day Rewards/RewardSegment, objective
    /// _requirements, the reward-pool internals (RewardDefinitionsBySlotId,
    /// RewardSlotsAmount, ForcedCatchUpPointsAmounts) and most of RewardDefinitionData
    /// (RewardType/RewardAux0/RewardAux1/RewardAmounts).
    ///
    /// Unlike BaseMetaJsonSerializer.WriteObject, the member scan here walks the WHOLE
    /// inheritance chain: reflection on a subtype does not return private members of its
    /// base types, so e.g. RewardDefinitionsBySlotId (private on BaseObjectiveRewardPoolData)
    /// would be missing from the two reward-pool subclasses.
    /// </summary>
    class MetaDailyChallengesSerializer : BaseMetaJsonSerializer
    {
        private readonly Type[] _supportedTypes =
        {
            typeof(DailyChallengesMinigameData),
            typeof(DailyChallengesWeekByMinigameIdData),
            typeof(DailyChallengeWeekByPreviousCompletionData),
            typeof(DailyChallengesEventSettings),
            typeof(DailyChallengesWeekData),
            typeof(DailyChallengesDayData),
            typeof(DailyChallengesStandardObjectiveData),
            typeof(DailyChallengesSpecialObjectiveData),
            typeof(DailyChallengesMilestoneData),
            // base type covers DailyChallengesStandardObjectiveRewardPoolData and
            // DailyChallengesSpecialObjectiveRewardPoolData via IsAssignableTo
            typeof(BaseObjectiveRewardPoolData),
            typeof(RewardDefinitionData),
        };

        protected override Type[] GetTypes() => _supportedTypes;

        public override void WriteJson(JsonWriter writer, object? value, JsonSerializer serializer)
        {
            writer.WriteStartObject();

            const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic
                | BindingFlags.Public | BindingFlags.DeclaredOnly;

            // collect MetaMember-tagged members across the whole inheritance chain;
            // a member redeclared in a subtype wins over its base declaration
            var members = new Dictionary<string, (int TagId, Func<object?> Getter)>();
            for (var t = value!.GetType(); t != null && t != typeof(object); t = t.BaseType)
            {
                foreach (var p in t.GetProperties(flags))
                {
                    var tag = p.GetCustomAttribute<MetaMemberAttribute>()?.TagId ?? -1;
                    var name = p.Name.Trim('_');
                    if (tag > 0 && !members.ContainsKey(name))
                        members[name] = (tag, () => p.GetValue(value));
                }
                foreach (var f in t.GetFields(flags))
                {
                    var tag = f.GetCustomAttribute<MetaMemberAttribute>()?.TagId ?? -1;
                    var name = f.Name.Trim('_');
                    if (tag > 0 && !members.ContainsKey(name))
                        members[name] = (tag, () => f.GetValue(value));
                }
            }

            foreach (var (name, member) in members.OrderBy(x => x.Value.TagId))
            {
                object? memberValue;
                try
                {
                    memberValue = member.Getter();
                }
                catch (TargetInvocationException tie) when (tie.InnerException is InvalidOperationException)
                {
                    continue; // unresolved MetaRef behind a derived getter — skip, don't crash the dump
                }

                if (memberValue == null && serializer.NullValueHandling == NullValueHandling.Ignore)
                    continue;

                WriteObjectMember(writer, name, value.GetType(), memberValue, serializer);
            }

            writer.WriteEndObject();
        }
    }
}
