using System.Globalization;
using AppStoreConnect.Net.Api;
using AppStoreConnect.Net.Client;
using AppStoreConnect.Net.Model;

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
        Console.WriteLine("export-iaps [--products <path-to-product-definitions.csv>] [--locale <locale>] [--region <region>] [--iap <id>] [-v]");
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
            "--iap <iap-id>",
            "Export only this one In-App Purchase."
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

            var singleIap = Config.Iap;
            if (!string.IsNullOrEmpty(singleIap))
                products = products.Where(p => p.Attributes?.ProductId == singleIap).ToList();

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
                rows.Add(await BuildRow(product, locale, region, verbose));

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

    private async Task<List<InAppPurchaseV2>> GetAllIapsAsync(bool verbose)
    {
        var api = new AppsApi(Service);

        var products = await FetchAllPagesAsync<InAppPurchasesV2Response, InAppPurchaseV2>(
            api.AsynchronousClient,
            api.Configuration,
            () => api.AppsInAppPurchasesV2GetToManyRelatedAsync(Config.AppId, limit: 200),
            r => r.Data,
            r => r.Links?.Next,
            verbose
        );

        return products
            .Where(p => !string.IsNullOrEmpty(p.Attributes?.ProductId))
            .ToList();
    }

    private async Task<List<string>> BuildRow(InAppPurchaseV2 product, string locale, string region, bool verbose)
    {
        var productId = product.Attributes?.ProductId ?? "";

        var localization = await GetLocalization(product, locale, verbose);

        if (!_localeWarned
            && localization is not null
            && !string.Equals(localization.Attributes?.Locale, locale, StringComparison.OrdinalIgnoreCase))
        {
            Console.WriteLine($"Warning: no '{locale}' localization, exporting '{localization.Attributes?.Locale}' instead. Set 'DefaultLocale' in your config.json, or pass --locale.");
            _localeWarned = true;
        }

        var price = await GetBasePrice(product, region, verbose);

        if (verbose)
            Console.WriteLine($"      {productId}: price '{price}', localization '{localization?.Attributes?.Locale ?? "none"}'");

        return
        [
            productId,
            product.Attributes?.Name ?? "",
            FormatType(product.Attributes?.InAppPurchaseType),
            price,
            localization?.Attributes?.Name ?? "",
            localization?.Attributes?.Description ?? "",
        ];
    }

    /// <summary>
    /// the localization in the wanted locale, or any other one, so a product that was never
    /// localized in it still exports its texts instead of two empty cells
    /// </summary>
    private async Task<InAppPurchaseLocalization?> GetLocalization(InAppPurchaseV2 product, string locale, bool verbose)
    {
        try
        {
            var api = new InAppPurchasesApi(Service);

            var localizations = await FetchAllPagesAsync<InAppPurchaseLocalizationsResponse, InAppPurchaseLocalization>(
                api.AsynchronousClient,
                api.Configuration,
                () => api.InAppPurchasesV2InAppPurchaseLocalizationsGetToManyRelatedAsync(product.Id, limit: 200),
                r => r.Data,
                r => r.Links?.Next,
                verbose
            );

            return localizations.FirstOrDefault(l => string.Equals(l.Attributes?.Locale, locale, StringComparison.OrdinalIgnoreCase))
                   ?? localizations.FirstOrDefault();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Warning: could not read localizations of {product.Attributes?.ProductId}: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// The manual price of the base territory, as a plain number for the csv.
    ///
    /// Unlike the 'list' command this must never throw: a product that has no price schedule yet,
    /// or is not sold in the default region, still has to produce a row, just with an empty price cell
    /// </summary>
    private async Task<string> GetBasePrice(InAppPurchaseV2 product, string region, bool verbose)
    {
        var productId = product.Attributes?.ProductId;

        try
        {
            var iapApi = new InAppPurchasesApi(Service);

            var scheduleResponse = await iapApi.InAppPurchasesV2IapPriceScheduleGetToOneRelatedAsync(product.Id);
            if (scheduleResponse?.Data is null)
            {
                Console.WriteLine($"Warning: {productId} has no price schedule, the default_price cell is left empty.");
                return "";
            }

            var pricesResponse = await new InAppPurchasePriceSchedulesApi(Service)
                .InAppPurchasePriceSchedulesManualPricesGetToManyRelatedAsync(
                    scheduleResponse.Data.Id,
                    filterTerritory: new List<string> { region },
                    include: new List<string> { "inAppPurchasePricePoint", "territory" }
                );

            var pricePoint = pricesResponse?.Included
                ?.Select(i => i.ActualInstance)
                .OfType<InAppPurchasePricePoint>()
                .FirstOrDefault();

            if (pricePoint?.Attributes?.CustomerPrice is null)
            {
                Console.WriteLine($"Warning: {productId} has no manual price for territory {region}, the default_price cell is left empty.");
                return "";
            }

            return FormatPrice(pricePoint.Attributes.CustomerPrice);
        }
        catch (ApiException ex)
        {
            Console.WriteLine($"Warning: could not read the price of {productId}: {ex.Message}");
            if (verbose)
                Console.WriteLine($"         {ex.ErrorContent}");
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
    /// the csv spelling 'create-iaps' expects, not the enum name
    /// </summary>
    private static string FormatType(InAppPurchaseType? type) => type switch
    {
        InAppPurchaseType.CONSUMABLE => "consumable",
        InAppPurchaseType.NONCONSUMABLE => "non-consumable",
        InAppPurchaseType.NONRENEWINGSUBSCRIPTION => "non-renewing-subscription",
        _ => "",
    };
}
