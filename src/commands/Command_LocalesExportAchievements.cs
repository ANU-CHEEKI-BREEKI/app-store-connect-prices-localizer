/// <summary>
/// Exports every Game Center achievement into a csv laid out the way translation tooling expects:
/// one row per key, one column per language.
///
/// App Store Connect machine translates nothing here. A game that ships seventy achievements in
/// english ships them in english everywhere, and the console can only be clicked through one
/// achievement and one language at a time.
/// </summary>
public class Command_LocalesExportAchievements : GameCenterCommandBase
{
    public const string DefaultFileName = "AchievementTranslations.csv";

    /// <summary>
    /// key suffixes. A vendor identifier is normally a plain word, but the split happens at the
    /// LAST dot anyway, so one containing dots still reads back correctly
    /// </summary>
    public const string NameField = "name";
    public const string BeforeEarnedField = "before_earned_description";
    public const string AfterEarnedField = "after_earned_description";

    /// <summary>
    /// the csv rows of one achievement, with the limits App Store Connect enforces.
    /// The numbers are the ones its own "Add Achievement Localization" dialog counts down from
    /// </summary>
    public static readonly TextField[] AchievementFields =
    {
        new(NameField, "Display Name", 30),
        new(BeforeEarnedField, "Pre-earned Description", 120),
        new(AfterEarnedField, "Earned Description", 120),
    };

    protected override TextField[] Fields => AchievementFields;

    public override string Name => "locales export achievements";

    public override string Description
        => "Exports every Game Center achievement title and both descriptions into a csv, one row per key and one column per language, ready to be fed to a translation service.";

    public override void PrintHelp()
    {
        Console.WriteLine("locales export achievements [--csv <path>] [--all-locales] [--locales <code[,code...]>] [-v]");
        Console.WriteLine();
        Console.WriteLine();

        Console.WriteLine("description:");
        CommandLinesUtils.PrintDescription(Description);
        CommandLinesUtils.PrintDescription($"Columns: {string.Join(", ", LeadingColumns)}, then one column per language named like 'English (United States)(en-US)'. The locale code in the trailing parentheses is what the import reads, so adding a language is adding a column.");
        CommandLinesUtils.PrintDescription($"Every achievement contributes three rows, '<vendor_id>.{NameField}', '<vendor_id>.{BeforeEarnedField}' and '<vendor_id>.{AfterEarnedField}', because a translation service wants one string per row.");
        CommandLinesUtils.PrintDescription("Points, type, images and the reference name are not exported and never change.");
        CommandLinesUtils.PrintDescription("Every language an achievement already has gets a column, and the source locales lead. They only decide what comes first, they never narrow anything down: a source locale nothing is translated into yet still gets its empty column, because that column is the work.");
        CommandLinesUtils.PrintDescription($"Pass {AllLocalesOption} to get a column for every one of the {AppStoreLocales.Supported.Length} languages the App Store supports, whether or not anything is translated into it yet. Without it only the languages that already have text get a column.");
        CommandLinesUtils.PrintDescription($"If no path is given, the table is written next to your config.json as '{DefaultFileName}', or to the Desktop when there is no config directory.");
        CommandLinesUtils.PrintDescription("An existing csv at the target path is overwritten.");

        Console.WriteLine();
        Console.WriteLine("options:");

        CommandLinesUtils.PrintOption(
            "--csv <path>",
            $"Where to write the table. If not specified, the path from global config json ('AchievementTranslationsFilePath') is used. A directory is also accepted, then '{DefaultFileName}' is created inside it."
        );
        CommandLinesUtils.PrintOption(
            AllLocalesOption,
            AllLocalesDescription
        );
        CommandLinesUtils.PrintOption(
            "--locales <code[,code...]>",
            "Produce columns for exactly these locales, for this run only. Default is the source locales first, then every language already translated."
        );
        CommandLinesUtils.PrintOption(
            "-v",
            "Include additional verbose output"
        );
    }

    protected override async Task InternalExecuteAsync()
    {
        try
        {
            Console.WriteLine("   -> Exporting Game Center achievements...");

            var path = ResolveCsvPath(Config.AchievementTranslationsFilePath, DefaultFileName);

            var achievements = await GetAchievementsAsync(Verbose);
            if (achievements is null)
                return;

            if (achievements.Count == 0)
            {
                Console.WriteLine("   -> nothing to export, this app has no achievements.");
                return;
            }

            var locales = ResolveLocales(achievements.SelectMany(a => a.Locales));

            if (locales.Count == 0)
            {
                Console.WriteLine("   -> no languages at all, and none configured. Nothing to put in the columns.");
                Console.WriteLine("      set 'SourceLocales' in your config.json, or pass --locales <code[,code...]>");
                return;
            }

            Console.WriteLine($"   -> {locales.Count} language(s): {string.Join(", ", locales)}");

            var rows = new List<List<string>>();

            foreach (var achievement in achievements)
            {
                foreach (var field in Fields)
                    rows.Add(BuildRow(achievement, field, locales));
            }

            var headers = BuildHeaders(locales);
            await CommandLinesUtils.SaveCsv(path, headers, rows);

            Console.WriteLine();
            Console.WriteLine($"written: {Path.GetFullPath(path)}");
            Console.WriteLine($"{rows.Count} key(s) from {achievements.Count} achievement(s), {locales.Count} language(s).");

            PrintCoverage(rows, locales);
            PrintLimits(rows, locales);
            PrintMissingImages(achievements);
        }
        catch (Exception ex)
        {
            PrintApiError("failed to export achievements", ex);
        }
    }

    private List<string> BuildRow(Achievement achievement, TextField field, List<string> locales)
    {
        var key = $"{achievement.VendorIdentifier}.{field.Key}";
        var comment = $"Game Center > '{achievement.ReferenceName}' > {field.Title}. Max {field.MaxLength} characters.";

        var row = new List<string> { key, key, comment };

        foreach (var locale in locales)
            row.Add(ValueOf(achievement.Find(locale), field.Key) ?? "");

        return row;
    }

    public static string? ValueOf(Localization? localization, string field)
        => field switch
        {
            NameField => localization?.Attributes?.Name,
            BeforeEarnedField => localization?.Attributes?.BeforeEarnedDescription,
            AfterEarnedField => localization?.Attributes?.AfterEarnedDescription,
            _ => null,
        };

    /// <summary>
    /// A language without an image can hold text but never goes live, so it is worth naming here
    /// rather than being discovered when the review refuses to start.
    /// </summary>
    private static void PrintMissingImages(List<Achievement> achievements)
    {
        var missing = achievements
            .SelectMany(a => a.Localizations
                .Where(l => a.ImageOf(l) is null)
                .Select(l => $"{a.VendorIdentifier} [{l.Attributes?.Locale}]"))
            .ToList();

        if (missing.Count == 0)
            return;

        Console.WriteLine();
        Console.WriteLine($"{missing.Count} language(s) have no image and can not go live:");
        foreach (var item in missing.Take(20))
            Console.WriteLine($"        {item}");

        if (missing.Count > 20)
            Console.WriteLine($"        ... and {missing.Count - 20} more");

        Console.WriteLine();
        Console.WriteLine("run 'locales sync achievement-images' to give them the image of the primary language.");
    }
}
