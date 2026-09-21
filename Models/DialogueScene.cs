namespace MergeMansionWikiTools.Models;

/// <summary>Which side of the screen is speaking. `None` = a line with no speaker (narration, beat).</summary>
public enum DialogueSide { None, Left, Right }

/// <summary>
/// One line as the game renders it: who speaks, who stands on which side, with which expression
/// and outfit variant. `Variant` is not in the config — it is derived from the atlas (see Task 4).
/// </summary>
public class DialogueLineInfo
{
    public string Id { get; set; } = "";
    public string Text { get; set; } = "";
    public string? Speaker { get; set; }
    /// <summary>Raw in-game character identifier behind <see cref="Speaker"/> (e.g. "AntiqueDealer"
    /// for the wiki name "Julius"). The portrait atlas sometimes only has sprites under this id, or
    /// under a name that does not match the wiki display name at all — see <c>PortraitVariantResolver</c>.</summary>
    public string? SpeakerId { get; set; }
    public DialogueSide Side { get; set; } = DialogueSide.None;
    public string? Left { get; set; }
    public string? Right { get; set; }
    public string Expression { get; set; } = "Default";
    public string Variant { get; set; } = "Default";
    public int Order { get; set; }
}

/// <summary>A scene is a StoryDefinitionId plus whatever triggers it in the game.</summary>
public class DialogueScene
{
    public string Id { get; set; } = "";
    public string? Area { get; set; }
    public string? Event { get; set; }
    public string? Trigger { get; set; }        // "task completed", "event start", …
    public string? Task { get; set; }           // popis hotspotu, když ho dump zná
    public string? Hotspot { get; set; }
    public List<DialogueLineInfo> Lines { get; set; } = new();

    public bool IsAreaStory => !string.IsNullOrEmpty(Area);
    public bool IsEventStory => !string.IsNullOrEmpty(Event);
}
