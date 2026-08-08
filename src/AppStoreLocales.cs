/// <summary>
/// The App Store accepts only its own locale codes, and only for the languages a product page can
/// actually be shown in. A translation table normally carries plain language codes ('pt', 'de', 'zh'),
/// so they have to be mapped before anything is sent: an unmapped code is not a failed write,
/// it is two failed writes and a language silently missing from the store page.
/// </summary>
public static class AppStoreLocales
{
    /// <summary>every locale an app store product page can be localized into</summary>
    public static readonly string[] Supported =
    {
        "ar-SA", "ca", "cs", "da", "de-DE", "el", "en-AU", "en-CA", "en-GB", "en-US",
        "es-ES", "es-MX", "fi", "fr-CA", "fr-FR", "he", "hi", "hr", "hu", "id",
        "it", "ja", "ko", "ms", "nl-NL", "no", "pl", "pt-BR", "pt-PT", "ro",
        "ru", "sk", "sv", "th", "tr", "uk", "vi", "zh-Hans", "zh-Hant",
    };

    /// <summary>
    /// what a bare language code means when the app itself does not already answer the question.
    /// only languages the App Store splits into regional variants need an entry here
    /// </summary>
    private static readonly Dictionary<string, string> Defaults = new(StringComparer.OrdinalIgnoreCase)
    {
        ["ar"] = "ar-SA",
        ["de"] = "de-DE",
        ["en"] = "en-US",
        ["es"] = "es-ES",
        ["fr"] = "fr-FR",
        ["nl"] = "nl-NL",
        ["pt"] = "pt-BR",
        ["zh"] = "zh-Hans",
        ["zh-CN"] = "zh-Hans",
        ["zh-TW"] = "zh-Hant",
        ["zh-Hant-TW"] = "zh-Hant",

        // codes other tooling likes to emit for a language the App Store spells differently
        ["nb"] = "no",
        ["nb-NO"] = "no",
        ["iw"] = "he",
        ["in"] = "id",
    };

    /// <summary>
    /// turns a locale code from a translation table into the one App Store Connect expects.
    /// returns false for a language the App Store can not show a product page in at all
    /// </summary>
    public static bool TryResolve(string locale, IReadOnlyCollection<string> existingOnApp, out string resolved, out string note)
    {
        resolved = "";
        note = "";

        var exact = Supported.FirstOrDefault(s => string.Equals(s, locale, StringComparison.OrdinalIgnoreCase));
        if (exact is not null)
        {
            resolved = exact;
            return true;
        }

        var language = Language(locale);

        // the app is the best answer to "which Portuguese did you mean", it already made that choice
        var onApp = existingOnApp
            .Where(e => string.Equals(Language(e), language, StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (onApp.Count == 1)
        {
            resolved = onApp[0];
            note = "already on the app";
            return true;
        }

        if (Defaults.TryGetValue(locale, out var mapped))
        {
            resolved = mapped;
            note = "App Store default for this language";
            return true;
        }

        var candidates = Supported
            .Where(s => string.Equals(Language(s), language, StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (candidates.Count == 1)
        {
            resolved = candidates[0];
            note = "the only App Store locale for this language";
            return true;
        }

        return false;
    }

    private static string Language(string locale)
    {
        var dash = locale.IndexOf('-');
        return dash < 0 ? locale : locale.Substring(0, dash);
    }
}
