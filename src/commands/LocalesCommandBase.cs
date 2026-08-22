/// <summary>
/// Shared plumbing for the 'locales' subcommands: the ones that pull translatable text out of
/// App Store Connect into a csv, and the ones that write a translated csv back.
///
/// They all produce and read the same table: a 'Key' column, the bookkeeping columns next to it,
/// and one column per language named 'English (United States)(en-US)'. The same shape
/// 'export-metadata' writes, so a single translation pipeline covers every text of the app.
/// </summary>
public abstract class LocalesCommandBase : CommandBase
{
    /// <summary>one translatable field, one csv row per item</summary>
    public record TextField(string Key, string Title, int MaxLength);

    public const string KeyColumn = AppMetadataCommandBase.KeyColumn;
    public const string IdColumn = AppMetadataCommandBase.IdColumn;
    public const string CommentsColumn = AppMetadataCommandBase.CommentsColumn;

    public static readonly List<string> LeadingColumns = new() { KeyColumn, IdColumn, CommentsColumn };

    /// <summary>the fields this subcommand exports, in the order App Store Connect shows them</summary>
    protected abstract TextField[] Fields { get; }

    protected bool Verbose => Args.HasFlag("-v");
    protected bool DryRun => Args.HasFlag("-n") || Args.HasFlag("--dry-run");

    /// <summary>
    /// Which languages get a column, and in what order.
    ///
    /// '--locales' pins the set for one run. Otherwise the source locales lead - they are what a
    /// translation service reads as its context - and everything already translated follows.
    /// A source locale that nothing is translated into yet still gets its (empty) column, because
    /// an empty column is exactly the work that has to be done.
    /// </summary>
    protected List<string> ResolveLocales(IEnumerable<string> found)
    {
        var pinned = ParseList("--locales");
        if (pinned.Count > 0)
            return pinned;

        var leading = Config.SourceLocales is { Count: > 0 }
            ? Config.SourceLocales
            : new List<string> { string.IsNullOrWhiteSpace(Config.DefaultLocale) ? "en-US" : Config.DefaultLocale };

        var translated = found
            .Where(l => !string.IsNullOrWhiteSpace(l))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(l => l, StringComparer.Ordinal);

        var locales = new List<string>();

        foreach (var locale in leading.Concat(translated))
        {
            if (string.IsNullOrWhiteSpace(locale))
                continue;

            if (!locales.Contains(locale, StringComparer.OrdinalIgnoreCase))
                locales.Add(locale);
        }

        return locales;
    }

    /// <summary>a comma or space separated option value, empty when the option was not given</summary>
    protected List<string> ParseList(string option)
        => Args.TryGetOption(option, "")
            .Split(new[] { ',', ' ' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToList();

    /// <summary>
    /// resolves the csv path the way the rest of the tool does:
    /// an explicit argument wins, then the config value, then a file next to config.json, then the desktop
    /// </summary>
    protected string ResolveCsvPath(string configuredPath, string defaultFileName)
    {
        var explicitPath = Args.TryGetOption("--csv", "");

        if (!string.IsNullOrWhiteSpace(explicitPath))
        {
            return Directory.Exists(explicitPath)
                ? Path.Combine(explicitPath, defaultFileName)
                : explicitPath;
        }

        if (!string.IsNullOrWhiteSpace(configuredPath) && !Directory.Exists(configuredPath))
            return configuredPath;

        if (!string.IsNullOrWhiteSpace(Config.ConfigDirectory) && Directory.Exists(Config.ConfigDirectory))
            return Path.Combine(Config.ConfigDirectory, defaultFileName);

        // ask the system where the desktop is: on Windows it can live under OneDrive or carry a localized name
        var desktop = Environment.GetFolderPath(Environment.SpecialFolder.Desktop);
        return Path.Combine(desktop, defaultFileName);
    }

    /// <summary>the header row: the bookkeeping columns, then one per language</summary>
    protected static List<string> BuildHeaders(List<string> locales)
    {
        var headers = new List<string>(LeadingColumns);
        headers.AddRange(locales.Select(LocaleColumns.ColumnName));
        return headers;
    }

    /// <summary>
    /// How much of each language is actually filled in. Without this the csv looks complete the
    /// moment it has columns, and a half translated language is exactly the thing worth seeing.
    /// </summary>
    protected static void PrintCoverage(List<List<string>> rows, List<string> locales)
    {
        Console.WriteLine();
        Console.WriteLine("filled in:");

        for (int i = 0; i < locales.Count; i++)
        {
            var column = LeadingColumns.Count + i;
            var filled = rows.Count(r => column < r.Count && !string.IsNullOrWhiteSpace(r[column]));
            var note = filled == 0 ? "  <- empty, ready to translate" : "";

            Console.WriteLine($"        {locales[i],-12} {filled,4} of {rows.Count} key(s){note}");
        }
    }

    /// <summary>
    /// Values App Store Connect would reject, reported at the end of an export. A translation is
    /// routinely longer than the english it came from, and this is worth knowing before the csv
    /// goes out to a translator, not after it comes back.
    /// </summary>
    protected void PrintLimits(List<List<string>> rows, List<string> locales)
    {
        var over = new List<string>();

        foreach (var row in rows)
        {
            var field = FieldOf(row[0]);
            if (field is null)
                continue;

            for (int i = 0; i < locales.Count; i++)
            {
                var column = LeadingColumns.Count + i;
                if (column >= row.Count)
                    continue;

                var value = row[column];
                if (value.Length > field.MaxLength)
                    over.Add($"        {row[0]} [{locales[i]}] is {value.Length}, the limit is {field.MaxLength}");
            }
        }

        if (over.Count == 0)
            return;

        Console.WriteLine();
        Console.WriteLine("too long for App Store Connect:");
        foreach (var line in over)
            Console.WriteLine(line);
    }

    /// <summary>the field a '&lt;id&gt;.&lt;field&gt;' key names, null when it names none of them</summary>
    protected TextField? FieldOf(string key)
    {
        var dot = key.LastIndexOf('.');
        if (dot < 0)
            return null;

        var name = key[(dot + 1)..];
        return Fields.FirstOrDefault(f => string.Equals(f.Key, name, StringComparison.OrdinalIgnoreCase));
    }

    protected static string Preview(string value)
    {
        var single = value.Replace("\n", "\\n").Replace("\r", "");
        return single.Length <= 60 ? single : single.Substring(0, 60) + "...";
    }

    /// <summary>
    /// Whether a csv value is really different from what App Store Connect holds.
    ///
    /// Compared trimmed on purpose. Reading a csv trims every cell, and a text that was typed into
    /// the console with a stray leading space would otherwise come back looking edited on every
    /// single round trip - a change nobody made, sent to Apple forever.
    /// </summary>
    protected static bool IsChanged(string? value, string? current)
        => value is not null && !string.Equals(value.Trim(), (current ?? "").Trim(), StringComparison.Ordinal);

    protected static void PrintApiError(string what, Exception ex)
    {
        if (ex is AppStoreConnect.Net.Client.ApiException api)
        {
            Console.WriteLine($"[API ERROR] {what}: {api.Message}");
            Console.WriteLine($"Status: {api.ErrorCode}");
            Console.WriteLine($"Response Body: {api.ErrorContent}");
            return;
        }

        Console.WriteLine($"[ERROR] {what}: {ex.Message}");
    }

    protected static void PrintSummary(List<string> updated, List<string> created, List<string> skipped, List<string> failed)
    {
        Console.WriteLine();
        Console.WriteLine("summary:");

        Print("updated", updated);
        Print("created", created);
        Print("skipped", skipped);
        Print("failed ", failed);

        static void Print(string label, List<string> items)
        {
            Console.WriteLine($"   {label}: {items.Count}");
            foreach (var item in items)
                Console.WriteLine($"      -> {item}");
        }
    }
}
