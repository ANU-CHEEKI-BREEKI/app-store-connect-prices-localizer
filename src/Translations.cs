/// <summary>one csv row: the key it was written under, and its value per locale</summary>
public class TranslationRow
{
    /// <summary>the whole key cell, '&lt;id&gt;.name' or '&lt;id&gt;.description' and so on</summary>
    public string Key { get; init; } = "";

    /// <summary>what the key points at, everything before the last dot</summary>
    public string Id { get; init; } = "";

    /// <summary>the part after the last dot, lowercased: 'name', 'description', 'before_earned_description'</summary>
    public string Field { get; init; } = "";

    /// <summary>locale code -> value. Only non empty cells are in here</summary>
    public Dictionary<string, string> Values { get; init; } = new(StringComparer.OrdinalIgnoreCase);
}

/// <summary>a parsed translations csv, the shape every 'locales export' subcommand writes</summary>
public class TranslationsCsv
{
    /// <summary>the language columns, in the order the file has them</summary>
    public List<string> Locales { get; init; } = new();

    public List<TranslationRow> Rows { get; init; } = new();

    /// <summary>rows grouped by what they translate, in first seen order</summary>
    public IEnumerable<IGrouping<string, TranslationRow>> ById
        => Rows.GroupBy(r => r.Id, StringComparer.Ordinal);
}

/// <summary>
/// Reads back the csv the 'locales export' subcommands write: a 'Key' column, the bookkeeping
/// columns next to it, and one column per language named 'English (United States)(en-US)'.
///
/// The same shape 'export-metadata' produces, so one translation pipeline covers all of them.
/// </summary>
public static class Translations
{
    public const string KeyColumn = AppMetadataCommandBase.KeyColumn;
    public const string IdColumn = AppMetadataCommandBase.IdColumn;
    public const string CommentsColumn = AppMetadataCommandBase.CommentsColumn;

    /// <summary>
    /// Loads the table. Throws <see cref="InvalidOperationException"/> when it has no key column
    /// at all, which is the one problem no caller can do anything sensible about.
    ///
    /// Empty cells are dropped here rather than later: an importer must be able to tell "not
    /// translated yet" from "translated to an empty string", and only the first of those exists.
    /// </summary>
    public static async Task<TranslationsCsv> LoadAsync(string path, bool verbose)
    {
        var table = await CommandLinesUtils.LoadCsvTable(path, path, verbose);

        var keyHeader = table.Headers.FirstOrDefault(h => string.Equals(h, KeyColumn, StringComparison.OrdinalIgnoreCase))
            ?? table.Headers.FirstOrDefault(h => string.Equals(h, IdColumn, StringComparison.OrdinalIgnoreCase));

        if (keyHeader is null)
            throw new InvalidOperationException($"the csv has no '{KeyColumn}' column, its headers are: {string.Join(", ", table.Headers)}");

        // a language column is one whose header ends with a locale code in parentheses.
        // that is what keeps 'Shared Comments' out of the data
        var localeColumns = table.Headers
            .Select(h => new { Header = h, Locale = LocaleColumns.Extract(h) })
            .Where(c => c.Locale is not null)
            .ToList();

        var rows = new List<TranslationRow>();

        foreach (var row in table.Rows)
        {
            var key = row.TryGetValue(keyHeader, out var cell) ? cell.Trim() : "";
            if (string.IsNullOrWhiteSpace(key))
                continue;

            // a product id may itself contain dots, the field is only ever the last segment
            var dot = key.LastIndexOf('.');
            if (dot <= 0 || dot == key.Length - 1)
            {
                Console.WriteLine($"Warning: key '{key}' has no '.field' suffix, skipped.");
                continue;
            }

            var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            foreach (var column in localeColumns)
            {
                if (!row.TryGetValue(column.Header, out var value) || string.IsNullOrWhiteSpace(value))
                    continue;

                values[column.Locale!] = Flatten(value);
            }

            rows.Add(new TranslationRow
            {
                Key = key,
                Id = key[..dot],
                Field = key[(dot + 1)..].ToLowerInvariant(),
                Values = values,
            });
        }

        return new TranslationsCsv
        {
            Locales = localeColumns.Select(c => c.Locale!).ToList(),
            Rows = rows,
        };
    }

    /// <summary>
    /// Puts a cell on one line.
    ///
    /// App Store Connect refuses any of these texts outright when it contains a control character:
    /// "cannot contain control characters (for example, null, new lines, carriage returns, escape)".
    /// A translator handed a paragraph will hand a paragraph back, so a newline in the csv is
    /// normal input, not a mistake - and there is nothing a store listing could do with it anyway.
    ///
    /// Done at load time rather than at send time so the value that gets compared against what
    /// Apple already holds is the same one that would be sent. Otherwise a cell with a newline would
    /// look changed on every single run.
    /// </summary>
    private static string Flatten(string value)
    {
        var builder = new System.Text.StringBuilder(value.Length);
        var lastWasSpace = false;

        foreach (var ch in value)
        {
            var isBlank = char.IsControl(ch) || ch == ' ';

            if (isBlank)
            {
                if (!lastWasSpace && builder.Length > 0)
                    builder.Append(' ');

                lastWasSpace = true;
                continue;
            }

            builder.Append(ch);
            lastWasSpace = false;
        }

        return builder.ToString().TrimEnd();
    }
}
