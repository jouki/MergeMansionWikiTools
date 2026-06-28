namespace MergeMansionWikiTools.Services;

/// <summary>Builds a green/red (added/removed) text preview of the two outcomes for an ambiguous
/// re-aired event: Variant A keeps both runs (adds the new one); Variant B treats it as the same
/// run moved (removes the existing run, adds the new one). Lines prefixed "+ " = added, "- " = removed,
/// "  " = unchanged. UI renders + green and - red.</summary>
public static class EventDriftPreview
{
    public static (string A, string B) BuildPreview(EventDriftDecision d)
    {
        string ex = d.ExistingStart.ToString("dd.MM.yyyy");
        string nw = d.NewStart.ToString("dd.MM.yyyy");
        string name = d.EventName;

        var a = string.Join("\n",
            $"  {name} run {ex}",
            $"+ {name} run {nw}   (new separate run)");

        var b = string.Join("\n",
            $"- {name} run {ex}   (superseded — moved)",
            $"+ {name} run {nw}");

        return (a, b);
    }
}
