using GameLogic.Config;
using GameLogic.Player.Director.Config;
using MergeMansionWikiTools.Dumper.Core;
using Newtonsoft.Json;

namespace MergeMansionWikiTools.Dumper.Serializers;

/// <summary>
/// The <c>IDirectorAction</c> union, shared by <c>areas.json</c> (a hotspot's
/// completion/appear/finalization actions) and <c>events.json</c> (an event level's
/// <c>OnLevelClaimAction</c>/<c>Actions</c>) — both files write it identically, so it is one
/// converter rather than a copy in each dump's serializer.
/// <para>
/// The shape is a single-key object keyed by the action's exact type name —
/// <c>{"TriggerCutscene": {"CutsceneId": "Study07_BurnIt"}}</c>, down to <c>{"NoAction": {}}</c> for
/// the member-less one. Two payloads are not the action's own members:
/// </para>
/// <list type="bullet">
/// <item><description><see cref="TriggerDialogue"/> is replaced by the story it points at (the
/// whole <c>StoryElementInfo</c>: <c>StoryDefinitionId</c>, <c>StealAllSteps</c>,
/// <c>DialogItems</c>, ...), or by <c>null</c> when the story is gone — 540 of the area corpus's
/// 75 528 dialogue actions.</description></item>
/// <item><description><see cref="GameLogic.Hotspots.Actions.ReplaceItemsOnBoard"/>'s
/// <c>ReplacementItem</c> expands from an item key to the full item definition, which is why the
/// caller must also register <see cref="ChainSerializer"/> when its file can contain that
/// action.</description></item>
/// </list>
/// </summary>
public sealed class DirectorActionSerializer : JsonConverter
{
    private readonly SharedGameConfig _config;
    private readonly IDumpLog _log;

    public DirectorActionSerializer(SharedGameConfig config, IDumpLog log)
    {
        _config = config ?? throw new ArgumentNullException(nameof(config));
        _log = log ?? ConsoleDumpLog.Instance;
    }

    public override bool CanRead => false;

    public override bool CanConvert(Type objectType) => typeof(IDirectorAction).IsAssignableFrom(objectType);

    public override void WriteJson(JsonWriter w, object? value, JsonSerializer s)
    {
        if (value is not IDirectorAction action) { w.WriteNull(); return; }

        w.WriteStartObject();
        w.WritePropertyName(action.GetType().Name);

        if (action is TriggerDialogue dialogue)
        {
            var story = ResolveStory(dialogue);
            if (story == null) w.WriteNull();
            else MetaObjectWriter.WriteObject(w, story, s, _log, refSkip: MetaRefSkip.Unresolved);
        }
        else
        {
            MetaObjectWriter.WriteObject(w, action, s, _log, (name, member) =>
            {
                if (name != "ReplacementItem" || member is not ItemDef itemDef) return false;
                if (_config.Items == null || !_config.Items.TryGetValue(itemDef.ConfigKey, out var item) || item == null) return false;
                w.WritePropertyName(name);
                s.Serialize(w, item);
                return true;
            }, MetaRefSkip.Unresolved);
        }

        w.WriteEndObject();
    }

    private GameLogic.Story.StoryElementInfo? ResolveStory(TriggerDialogue dialogue)
    {
        if (_config.StoryElements == null) return null;
        if (MetaObjectWriter.GetMember(dialogue, "DialogueId", _log) is not GameLogic.Story.StoryDefinitionId id || id.Value == null) return null;
        return _config.StoryElements.TryGetValue(id, out var story) ? story : null;
    }

    public override object? ReadJson(JsonReader reader, Type objectType, object? existingValue, JsonSerializer serializer)
        => throw new NotSupportedException($"{nameof(DirectorActionSerializer)} is write-only (dumper never deserializes).");
}
