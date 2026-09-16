using Metaplay.Core.Localization;
using Metaplay.Unity;

namespace MergeMansionWikiTools.Dumper.Core;

/// <summary>
/// Localization helpers shared by the serializers. The game's <c>LocMan</c> is not defensive —
/// <c>GetItemName</c>/<c>GetDescription</c> throw on an item type that has no <c>Name_Level</c>
/// underscore, and a config can always contain one — so every dumper lookup goes through
/// <see cref="SafeLoc"/> rather than being wrapped ad hoc at each call site.
/// </summary>
public static class Loc
{
    /// <summary>
    /// Runs a localization lookup that is allowed to throw and falls back to
    /// <paramref name="fallback"/> (which is also used for a null result — <c>LocMan.Get</c> returns
    /// the key itself for a missing translation, so null only happens if the language file is
    /// malformed). The failure is logged, never rethrown: one bad item must not abort a whole dump.
    /// </summary>
    /// <param name="what">What was being looked up, for the warning line (e.g. the item type).</param>
    public static string SafeLoc(Func<string> get, string fallback, IDumpLog log, string what)
    {
        try
        {
            return get() ?? fallback;
        }
        catch (Exception ex)
        {
            log.Warn($"Localization failed for {what} ({ex.GetType().Name}: {ex.Message}) — using '{fallback}'");
            return fallback;
        }
    }

    /// <summary>
    /// Raw translation lookup: the active language's table, straight, with <c>null</c> for "no such
    /// key" (and for a null/empty key or a language file that never loaded). Used by the card
    /// collection, dialogue and experimental dumps, whose golden output carries a JSON <c>null</c>
    /// wherever a loc id does not resolve, rather than <see cref="SafeLoc"/>'s echo-the-key result.
    /// <para>
    /// Deliberately NOT <c>LocMan.TryGet</c>, which additionally runs the game's
    /// <c>FillInParameters</c> pass over the hit. Golden keeps the raw placeholders — a daily
    /// challenge's title really is written as <c>"Complete {0} daily trades"</c> — so this reads the
    /// table directly.
    /// </para>
    /// </summary>
    public static string? Translate(string? locId)
    {
        if (string.IsNullOrEmpty(locId)) return null;

        var lang = MetaplaySDK.ActiveLanguage;
        if (lang?.Translations == null) return null;

        return lang.Translations.TryGetValue(TranslationId.FromString(locId), out var translation)
            ? translation
            : null;
    }
}
