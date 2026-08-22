using AppStoreConnect.Net.Api;
using AppStoreConnect.Net.Model;

/// <summary>
/// Exports the display name and description of every In-App Purchase into a csv laid out the way
/// translation tooling expects: one row per key, one column per language.
///
/// Not to be confused with the top level 'export-iaps', which writes the product definitions csv
/// 'create-iaps' reads back: prices, one language, one row per product. This one is only about text,
/// and that text is what the payment sheet shows at the moment somebody pays.
/// </summary>
public class Command_LocalesExportIaps : IapLocalesCommandBase
{
    public const string DefaultFileName = "IapTranslations.csv";

    public override string Name => "locales export iaps";

    public override string Description
        => "Exports the display name and description of every In-App Purchase into a csv, one row per key and one column per language, ready to be fed to a translation service.";

    public override void PrintHelp()
    {
        Console.WriteLine("locales export iaps [--csv <path>] [--locales <code[,code...]>] [--iap <id[,id...]>] [-v]");
        Console.WriteLine();
        Console.WriteLine();

        Console.WriteLine("description:");
        CommandLinesUtils.PrintDescription(Description);
        CommandLinesUtils.PrintDescription($"Columns: {string.Join(", ", LeadingColumns)}, then one column per language named like 'English (United States)(en-US)'. The locale code in the trailing parentheses is what the import reads, so adding a language is adding a column.");
        CommandLinesUtils.PrintDescription($"Every product contributes two rows, '<product_id>.{NameField}' and '<product_id>.{DescriptionField}', because a translation service wants one string per row.");
        CommandLinesUtils.PrintDescription("Not to be confused with 'export-iaps', the top level command that writes the product definitions csv 'create-iaps' reads back: prices, one language, one row per product. This command is only about the text.");
        CommandLinesUtils.PrintDescription("Every language a product already has gets a column, and the source locales lead. They only decide what comes first, they never narrow anything down.");
        CommandLinesUtils.PrintDescription($"A display name over {IapFields[0].MaxLength} characters or a description over {IapFields[1].MaxLength} is reported at the end, because App Store Connect rejects those and a translation is routinely longer than its english.");
        CommandLinesUtils.PrintDescription($"If no path is given, the table is written next to your config.json as '{DefaultFileName}', or to the Desktop when there is no config directory. An existing csv is overwritten.");

        Console.WriteLine();
        Console.WriteLine("options:");

        CommandLinesUtils.PrintOption(
            "--csv <path>",
            $"Where to write the table. If not specified, the path from global config json ('IapTranslationsFilePath') is used. A directory is also accepted, then '{DefaultFileName}' is created inside it."
        );
        CommandLinesUtils.PrintOption(
            "--locales <code[,code...]>",
            "Produce columns for exactly these locales, for this run only. Default is the source locales first, then every language already translated."
        );
        CommandLinesUtils.PrintOption(
            CommandLinesUtils.IapOptionName,
            CommandLinesUtils.IapOptionDescription
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
            if (string.IsNullOrWhiteSpace(Config.AppId))
            {
                Console.WriteLine("[ERROR] no app id. specify it in config.json or with --app-id");
                return;
            }

            Console.WriteLine("   -> Exporting In-App Purchase texts...");

            var path = ResolveCsvPath(Config.IapTranslationsFilePath, DefaultFileName);

            var products = await GetProductsAsync(Verbose);
            products = FilterByIap(products, p => p.Product.Attributes?.ProductId);

            if (products.Count == 0)
            {
                Console.WriteLine("   -> nothing to export, no In-App Purchases matched.");
                return;
            }

            var locales = ResolveLocales(products.SelectMany(p => p.Locales));

            if (locales.Count == 0)
            {
                Console.WriteLine("   -> no languages at all, and none configured. Nothing to put in the columns.");
                Console.WriteLine("      set 'SourceLocales' in your config.json, or pass --locales <code[,code...]>");
                return;
            }

            Console.WriteLine($"   -> {locales.Count} language(s): {string.Join(", ", locales)}");

            var rows = new List<List<string>>();

            foreach (var product in products)
            {
                foreach (var field in Fields)
                    rows.Add(BuildRow(product, field, locales));
            }

            var headers = BuildHeaders(locales);
            await CommandLinesUtils.SaveCsv(path, headers, rows);

            Console.WriteLine();
            Console.WriteLine($"written: {Path.GetFullPath(path)}");
            Console.WriteLine($"{rows.Count} key(s) from {products.Count} product(s), {locales.Count} language(s).");

            PrintCoverage(rows, locales);
            PrintLimits(rows, locales);
        }
        catch (Exception ex)
        {
            PrintApiError("failed to export In-App Purchase texts", ex);
        }
    }

    private static List<string> BuildRow(IapTexts product, TextField field, List<string> locales)
    {
        var key = $"{product.ProductId}.{field.Key}";
        var comment = $"In-App Purchase '{product.ReferenceName}' > {field.Title}. Max {field.MaxLength} characters.";

        var row = new List<string> { key, key, comment };

        foreach (var locale in locales)
            row.Add(ValueOf(product.Find(locale), field.Key) ?? "");

        return row;
    }
}
