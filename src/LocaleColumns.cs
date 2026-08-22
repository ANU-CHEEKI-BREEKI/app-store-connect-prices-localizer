using System.Globalization;
using System.Text.RegularExpressions;

/// <summary>
/// The translation tooling identifies a language by the code in the trailing parentheses of its
/// column header, 'English (United States)(en-US)'. Every export in this tool writes that shape and
/// every import reads it back, so the two halves live here instead of next to one of the commands.
/// </summary>
public static class LocaleColumns
{
    /// <summary>
    /// builds the language column header the translation tooling expects: 'English (United States)(en-US)'.
    /// the locale code in the trailing parentheses is what makes the import exact
    /// </summary>
    public static string ColumnName(string locale)
    {
        var name = locale;

        try
        {
            var culture = CultureInfo.GetCultureInfo(locale);
            if (!culture.EnglishName.StartsWith("Unknown", StringComparison.OrdinalIgnoreCase))
                name = culture.EnglishName;
        }
        catch (CultureNotFoundException)
        {
            // an App Store locale .NET does not know, the raw code is a good enough column title
        }

        return $"{name}({locale})";
    }

    /// <summary>
    /// reads the locale code back out of a column header.
    /// returns null for the 'Key' / 'Id' / 'Shared Comments' columns and for anything that is not a locale
    /// </summary>
    public static string? Extract(string header)
    {
        var close = header.LastIndexOf(')');
        if (close < 0)
            return null;

        var open = header.LastIndexOf('(', close - 1);
        if (open < 0)
            return null;

        var code = header.Substring(open + 1, close - open - 1).Trim();

        // 'en', 'en-US', 'zh-Hans'. keeps a plain 'Portuguese (Brazil)' header from looking like a locale
        return Regex.IsMatch(code, "^[a-zA-Z]{2,3}(-[a-zA-Z0-9]{2,8})?$") ? code : null;
    }
}
