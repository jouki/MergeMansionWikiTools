using System.Reflection;
using Code.GameLogic.GameEvents.DailyChallenges.Data;
using Metaplay.Core.Model;
using Newtonsoft.Json;

namespace MergeMansionWikiTools.Dumper.Serializers;

/// <summary>
/// Serializer for the DailyChallenges (The Daily Scoop V2, 2026-06) config data types.
/// Several of them carry PRIVATE [MetaMember] fields that Newtonsoft's default
/// (public-only) serialization silently drops: day Rewards/RewardSegment, objective
/// _requirements, the reward-pool internals (RewardDefinitionsBySlotId,
/// RewardSlotsAmount, ForcedCatchUpPointsAmounts) and most of RewardDefinitionData
/// (RewardType/RewardAux0/RewardAux1/RewardAmounts).
///
/// Two details separate it from the shared <see cref="Core.MetaObjectWriter"/> and are why it stays
/// its own converter rather than folding into that one:
/// <list type="bullet">
/// <item><description>Member names are trimmed of underscores, so the private
/// <c>_requirements</c> field appears as <c>requirements</c> — which is exactly how golden spells
/// it.</description></item>
/// <item><description>Members are de-duplicated by that trimmed NAME across the whole inheritance
/// chain (reflection on a subtype does not return private members of its base types, so e.g.
/// <c>RewardDefinitionsBySlotId</c> on <c>BaseObjectiveRewardPoolData</c> would be missing from both
/// reward-pool subclasses), and no reference-skipping rule applies.</description></item>
/// </list>
/// </summary>
public sealed class DailyChallengesSerializer : JsonConverter
{
    private static readonly Type[] SupportedTypes =
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
        // DailyChallengesSpecialObjectiveRewardPoolData via IsAssignableFrom
        typeof(BaseObjectiveRewardPoolData),
        typeof(RewardDefinitionData),
    };

    public override bool CanConvert(Type objectType) => Array.Exists(SupportedTypes, t => t.IsAssignableFrom(objectType));

    public override bool CanRead => false;

    public override void WriteJson(JsonWriter writer, object? value, JsonSerializer serializer)
    {
        if (value is null) { writer.WriteNull(); return; }

        writer.WriteStartObject();

        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic
            | BindingFlags.Public | BindingFlags.DeclaredOnly;

        // Collect MetaMember-tagged members across the whole inheritance chain; a member redeclared
        // in a subtype wins over its base declaration.
        var members = new Dictionary<string, (int TagId, Func<object?> Getter)>(StringComparer.Ordinal);
        for (var t = value.GetType(); t != null && t != typeof(object); t = t.BaseType)
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

            writer.WritePropertyName(name);
            serializer.Serialize(writer, memberValue);
        }

        writer.WriteEndObject();
    }

    public override object? ReadJson(JsonReader reader, Type objectType, object? existingValue, JsonSerializer serializer)
        => throw new NotSupportedException($"{nameof(DailyChallengesSerializer)} is write-only (dumper never deserializes).");
}
