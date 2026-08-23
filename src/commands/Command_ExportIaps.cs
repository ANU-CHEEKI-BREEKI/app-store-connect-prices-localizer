using System.Globalization;
using System.Text.Json.Nodes;

/// <summary>
/// Dumps the existing In-App Purchases into the same csv 'create-iaps' reads, so a new product
/// can be added by copying a line in a spreadsheet instead of writing the whole file by hand.
/// </summary>
public class Command_ExportIaps : CommandBase
{
    public static readonly List<string> Headers =
    [
        "product_id",
        "reference_name",
        "type",
        "default_price",
        "localized_title",
        "localized_description",
    ];

    /// <summary>the fallback locale warning is worth printing once, not once per product</summary>
    private bool _localeWarned;

    public override string Name => "export-iaps";
    public override string Description => "Exports all In-App Purchases into a product definitions csv, ready to be edited in a spreadsheet and fed back to 'create-iaps'.";

    public override void PrintHelp()
    {
        Console.WriteLine("export-iaps [--products <path-to-product-definitions.csv>] [--locale <locale>] [--region <region>] [--iap <id[,id...]>] [-v]");
        Console.WriteLine();
        Console.WriteLine();

        Console.WriteLine("description:");
        CommandLinesUtils.PrintDescription(Description);
        CommandLinesUtils.PrintDescription($"Columns: {string.Join(", ", Headers)}.");
        CommandLinesUtils.PrintDescription("The price column is the base price in the default region. Prices for all the other territories are not exported, 'localize' recalculates them from the percentage template.");
        CommandLinesUtils.PrintDescription("The title and the description are the In-App Purchase localization in the configured locale, the rest of the locales are not exported. One row per product.");
        CommandLinesUtils.PrintDescription("An existing csv at the target path is overwritten.");

        Console.WriteLine();
        Console.WriteLine("options:");

        CommandLinesUtils.PrintOption(
            "--products <path>",
            "Specifies path to the csv to write. If not specified, used path from global config json ('ProductDefinitionsFilePath')."
        );
        CommandLinesUtils.PrintOption(
            "--locale <locale>",
            "Locale of the exported localization. Default is en-US, or locale specified in global config.json."
        );
        CommandLinesUtils.PrintOption(
            "--region <region>",
            "Territory the exported base price is read from. Default is USA, or region specified in global config.json."
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
        var verbose = Args.HasFlag("-v");

        try
        {
            if (string.IsNullOrWhiteSpace(Config.AppId))
            {
                Console.WriteLine("[ERROR] no app id. specify it in config.json or with --app-id");
                return;
            }

            var path = Config.ProductDefinitionsFilePath;
            if (string.IsNullOrWhiteSpace(path) || Directory.Exists(path))
            {
                Console.WriteLine($"[ERROR] '{path}' is not a file to write the csv into.");
                Console.WriteLine("        set 'ProductDefinitionsFilePath' in your config.json, or pass --products <path>");
                return;
            }

            Console.WriteLine("   -> Receiving IAP list...");

            var products = await GetAllIapsAsync(verbose);

            products = FilterByIap(products, p => (string?)p?["attributes"]?["productId"]);

            if (products.Count == 0)
            {
                Console.WriteLine("   -> nothing to export, no In-App Purchases matched.");
                return;
            }

            var locale = string.IsNullOrWhiteSpace(Config.DefaultLocale) ? "en-US" : Config.DefaultLocale;
            var region = string.IsNullOrWhiteSpace(Config.DefaultRegion) ? "USA" : Config.DefaultRegion;

            Console.WriteLine($"   -> Exporting {products.Count} product(s) into {Path.GetFullPath(path)}...");

            var rows = new List<List<string>>();

            foreach (var product in products)
                rows.Add(await BuildRow(product!, locale, region, verbose));

            await CommandLinesUtils.SaveCsv(path, Headers, rows);

            Console.WriteLine();
            Console.WriteLine($"written: {Path.GetFullPath(path)}");
            Console.WriteLine($"{products.Count} product(s), {rows.Count} row(s).");
        }
        catch (Exception ex)
        {
            Console.WriteLine(ex);
        }
    }

    private async Task<List<JsonNode?>> GetAllIapsAsync(bool verbose)
    {
        var page = await Http.GetPagedAsync($"/v1/apps/{Config.AppId}/inAppPurchasesV2?limit=200");

        return page.Data
            .Where(p => !string.IsNullOrEmpty((string?)p?["attributes"]?["productId"]))
            .ToList();
    }

    private async Task<List<string>> BuildRow(JsonNode product, string locale, string region, bool verbose)
    {
        var productId = (string?)product["attributes"]?["productId"] ?? "";

        var localization = await GetLocalization(product, locale, verbose);

        if (!_localeWarned
            && localization is not null
            && !string.Equals((string?)localization["attributes"]?["locale"], locale, StringComparison.OrdinalIgnoreCase))
        {
            Console.WriteLine($"Warning: no '{locale}' localization, exporting '{(string?)localization["attributes"]?["locale"]}' instead. Set 'DefaultLocale' in your config.json, or pass --locale.");
            _localeWarned = true;
        }

        var price = await GetBasePrice(product, region, verbose);

        if (verbose)
            Console.WriteLine($"      {productId}: price '{price}', localization '{(string?)localization?["attributes"]?["locale"] ?? "none"}'");

        return
        [
            productId,
            (string?)product["attributes"]?["name"] ?? "",
            FormatType((string?)product["attributes"]?["inAppPurchaseType"]),
            price,
            (string?)localization?["attributes"]?["name"] ?? "",
            (string?)localization?["attributes"]?["description"] ?? "",
        ];
    }

    /// <summary>
    /// the localization in the wanted locale, or any other one, so a product that was never
    /// localized in it still exports its texts instead of two empty cells
    /// </summary>
    private async Task<JsonNode?> GetLocalization(JsonNode product, string locale, bool verbose)
    {
        try
        {
            var page = await Http.GetPagedAsync($"/v2/inAppPurchases/{(string?)product["id"]}/inAppPurchaseLocalizations?limit=200");

            var localizations = page.Data;

            return localizations.FirstOrDefault(l => string.Equals((string?)l?["attributes"]?["locale"], locale, StringComparison.OrdinalIgnoreCase))
                   ?? localizations.FirstOrDefault();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Warning: could not read localizations of {(string?)product["attributes"]?["productId"]}: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// The manual price of the base territory, as a plain number for the csv.
    ///
    /// Unlike the 'list' command this must never throw: a product that has no price schedule yet,
    /// or is not sold in the default region, still has to produce a row, just with an empty price cell
    /// </summary>
    private async Task<string> GetBasePrice(JsonNode product, string region, bool verbose)
    {
        var productId = (string?)product["attributes"]?["productId"];

        try
        {
            var scheduleResponse = await Http.GetAsync($"/v2/inAppPurchases/{(string?)product["id"]}/iapPriceSchedule");
            if (scheduleResponse["data"] is null)
            {
                Console.WriteLine($"Warning: {productId} has no price schedule, the default_price cell is left empty.");
                return "";
            }

            var pricesResponse = await Http.GetAsync(
                $"/v1/inAppPurchasePriceSchedules/{(string?)scheduleResponse["data"]?["id"]}/manualPrices?filter[territory]={region}&include=inAppPurchasePricePoint,territory"
            );

            var pricePoint = (pricesResponse["included"] as JsonArray ?? new JsonArray())
                .FirstOrDefault(i => (string?)i?["type"] == "inAppPurchasePricePoints");

            var customerPrice = (string?)pricePoint?["attributes"]?["customerPrice"];

            if (customerPrice is null)
            {
                Console.WriteLine($"Warning: {productId} has no manual price for territory {region}, the default_price cell is left empty.");
                return "";
            }

            return FormatPrice(customerPrice);
        }
        catch (AscApiException ex)
        {
            Console.WriteLine($"Warning: could not read the price of {productId}: {ex.Message}");
            if (verbose)
                Console.WriteLine($"         {ex.ResponseBody}");
            return "";
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Warning: could not read the price of {productId}: {ex.Message}");
            return "";
        }
    }

    /// <summary>
    /// the api hands prices back as strings, normalize them so 'create-iaps' parses them back
    /// no matter what the machine's locale is
    /// </summary>
    private static string FormatPrice(string customerPrice)
        => decimal.TryParse(customerPrice, NumberStyles.Any, CultureInfo.InvariantCulture, out var value)
            ? value.ToString("0.####", CultureInfo.InvariantCulture)
            : customerPrice;

    /// <summary>
    /// the csv spelling 'create-iaps' expects, not the api's underscored name
    /// </summary>
    private static string FormatType(string? type) => type switch
    {
        "CONSUMABLE" => "consumable",
        "NON_CONSUMABLE" => "non-consumable",
        "NON_RENEWING_SUBSCRIPTION" => "non-renewing-subscription",
        _ => "",
    };
}
