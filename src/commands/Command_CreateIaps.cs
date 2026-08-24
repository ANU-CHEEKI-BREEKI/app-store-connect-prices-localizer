using System.Globalization;
using System.Text.Json.Nodes;

public class Command_CreateIaps : CommandBase
{
    public override string Name => "create-iaps";
    public override string Description => "Creates In-App Purchases in App Store Connect from the product definitions csv. Products that already exist are not re-created.";

    public override void PrintHelp()
    {
        Console.WriteLine("create-iaps [--products <path-to-product-definitions.csv>] [--locale <locale>] [--iap <id[,id...]>] [-v]");
        Console.WriteLine();
        Console.WriteLine();

        Console.WriteLine("description:");
        CommandLinesUtils.PrintDescription(Description);
        CommandLinesUtils.PrintDescription("Every created product gets a localization (en-US by default), is made available in all territories, and receives its base price from the 'default_price' column.");
        CommandLinesUtils.PrintDescription("If a product with the same product id already exists, it is skipped: it is never re-created and its prices are never touched. Only a missing localization or a missing availability is added to it.");
        CommandLinesUtils.PrintDescription("The csv separator is detected automatically, both ';' and ',' are supported.");

        Console.WriteLine();
        Console.WriteLine("options:");

        CommandLinesUtils.PrintOption(
            "--products <path>",
            "Specifies path to csv with product definitions. If not specified, used path from global config json."
        );
        CommandLinesUtils.PrintOption(
            "--locale <locale>",
            "Locale of the created localization. Default is en-US, or locale specified in global config.json"
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
            Console.WriteLine("   -> Creating IAPs...");

            var definitions = await LoadDefinitions(verbose);
            if (definitions is null)
                return;

            definitions = FilterByIap(definitions, d => d.ProductId);

            if (definitions.Count == 0)
            {
                Console.WriteLine("   -> nothing to create, no product definitions matched.");
                return;
            }

            Console.WriteLine("   -> Receiving IAP list...");
            var page = await Http.GetPagedAsync($"/v1/apps/{Config.AppId}/inAppPurchasesV2?limit=200");

            var existing = page.Data
                .Where(p => (string?)p?["attributes"]?["productId"] != null)
                .ToDictionary(p => (string)p!["attributes"]!["productId"]!, p => p!);

            var territories = await GetAllTerritoriesAsync();

            var created = new List<string>();
            var skipped = new List<string>();
            var failed = new List<string>();
            var pricesSetup = new List<IapPriceSetup>();

            foreach (var definition in definitions)
            {
                if (existing.TryGetValue(definition.ProductId, out var alreadyExisting))
                {
                    Console.WriteLine($"   -> [SKIP] {definition.ProductId} already exists in App Store Connect.");
                    skipped.Add(definition.ProductId);

                    // it may be a leftover of a previously failed run, so top up what is missing.
                    // prices are intentionally not touched here, that is what the 'restore' command is for
                    await EnsureLocalization(alreadyExisting, definition, verbose);
                    await EnsureAvailability(alreadyExisting, territories, verbose);
                    continue;
                }

                var iap = await CreateIap(definition, verbose);
                if (iap is null)
                {
                    failed.Add(definition.ProductId);
                    continue;
                }

                created.Add(definition.ProductId);

                await EnsureLocalization(iap, definition, verbose);
                await EnsureAvailability(iap, territories, verbose);

                var basePrice = definition.DefaultPrice;

                // forcibly adjust price if it is a whole number
                // to make sure we have marketable price
                // exactly like the 'restore' command does
                if (basePrice == Math.Truncate(basePrice))
                    basePrice -= 0.01m;

                pricesSetup.Add(new IapPriceSetup
                {
                    Iap = iap,
                    BasePrice = (double)basePrice,
                    BaseTerritoryCode = Config.DefaultRegion,
                });
            }

            if (pricesSetup.Count > 0)
            {
                var restorer = new Command_Restore();
                restorer.Initialize(Auth, Config, Args);
                await restorer.SetPrices(pricesSetup, verbose);
            }

            PrintSummary(created, skipped, failed);
        }
        catch (Exception ex)
        {
            Console.WriteLine(ex);
        }
    }

    private async Task<List<ProductDefinition>?> LoadDefinitions(bool verbose)
    {
        var path = Config.ProductDefinitionsFilePath;

        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            Console.WriteLine($"[ERROR] product definitions csv not found: '{path}'");
            Console.WriteLine("        set 'ProductDefinitionsFilePath' in your config.json, or pass --products <path>");
            return null;
        }

        var rows = await CommandLinesUtils.LoadCsv(path, path, verbose);

        var definitions = new List<ProductDefinition>();
        foreach (var row in rows)
        {
            var productId = Get(row, "product_id");
            if (string.IsNullOrWhiteSpace(productId))
                continue;

            var rawType = Get(row, "type");
            if (!TryParseType(rawType, out var type))
            {
                Console.WriteLine($"[ERROR] {productId}: unknown product type '{rawType}'. Expected 'consumable', 'non-consumable' or 'non-renewing-subscription'. Skipped.");
                continue;
            }

            var rawPrice = Get(row, "default_price");
            if (!decimal.TryParse(rawPrice.Replace(",", "."), NumberStyles.Any, CultureInfo.InvariantCulture, out var price))
            {
                Console.WriteLine($"[ERROR] {productId}: can not parse default_price '{rawPrice}'. Skipped.");
                continue;
            }

            definitions.Add(new ProductDefinition
            {
                ProductId = productId,
                ReferenceName = Get(row, "reference_name"),
                Type = type,
                DefaultPrice = price,
                LocalizedTitle = Get(row, "localized_title"),
                LocalizedDescription = Get(row, "localized_description"),
            });
        }

        Console.WriteLine($"   -> loaded {definitions.Count} product definitions.");

        if (verbose)
        {
            foreach (var d in definitions)
                Console.WriteLine($"      {d.ProductId} | {d.ReferenceName} | {d.Type} | {d.DefaultPrice} | {d.LocalizedTitle}");
        }

        return definitions;
    }

    private static string Get(Dictionary<string, string> row, string column)
        => row.TryGetValue(column, out var value) ? value : "";

    // ProductDefinition (src/Configs.cs) still carries the generated client's enum, so the type is
    private static bool TryParseType(string raw, out IapType type)
    {
        // 'non-consumable', 'NON_CONSUMABLE', 'nonConsumable' all mean the same thing
        var normalized = new string(raw.Where(char.IsLetter).ToArray()).ToLowerInvariant();

        switch (normalized)
        {
            case "consumable":
                type = IapType.CONSUMABLE;
                return true;
            case "nonconsumable":
                type = IapType.NONCONSUMABLE;
                return true;
            case "nonrenewingsubscription":
                type = IapType.NONRENEWINGSUBSCRIPTION;
                return true;
            default:
                type = default;
                return false;
        }
    }

    /// <summary>the value the api takes: the enum's serialized name, underscores and all</summary>
    private static string ApiTypeName(IapType type) => type switch
    {
        IapType.CONSUMABLE => "CONSUMABLE",
        IapType.NONCONSUMABLE => "NON_CONSUMABLE",
        IapType.NONRENEWINGSUBSCRIPTION => "NON_RENEWING_SUBSCRIPTION",
        _ => "",
    };

    private async Task<JsonNode?> CreateIap(ProductDefinition definition, bool verbose)
    {
        Console.WriteLine($"   -> Creating IAP: {definition.ProductId} ({definition.Type})...");

        var request = AscHttp.Body("inAppPurchases",
            new JsonObject
            {
                ["app"] = AscHttp.Link("apps", Config.AppId),
            },
            new JsonObject
            {
                ["name"] = definition.ReferenceName,
                ["productId"] = definition.ProductId,
                ["inAppPurchaseType"] = ApiTypeName(definition.Type),
                ["familySharable"] = false,
            }
        );

        try
        {
            var response = await Http.PostAsync("/v2/inAppPurchases", request);

            if (verbose)
                Console.WriteLine($"[SUCCESS] created {definition.ProductId} (ID: {(string?)response["data"]?["id"]})");

            return response["data"];
        }
        catch (AscApiException ex)
        {
            Console.WriteLine($"[API ERROR] failed to create {definition.ProductId}: {ex.Message}");
            Console.WriteLine($"Status: {ex.StatusCode}");
            Console.WriteLine($"Response Body: {ex.ResponseBody}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[ERROR] failed to create {definition.ProductId}: {ex.Message}");
        }

        return null;
    }

    private async Task EnsureLocalization(JsonNode iap, ProductDefinition definition, bool verbose)
    {
        var locale = string.IsNullOrWhiteSpace(Config.DefaultLocale) ? "en-US" : Config.DefaultLocale;

        try
        {
            var localizations = await Http.GetAsync($"/v2/inAppPurchases/{(string?)iap["id"]}/inAppPurchaseLocalizations?limit=200");

            var already = (localizations["data"] as JsonArray ?? new JsonArray()).Any(
                l => string.Equals((string?)l?["attributes"]?["locale"], locale, StringComparison.OrdinalIgnoreCase)
            );

            if (already)
            {
                if (verbose)
                    Console.WriteLine($"   -> [SKIP] {definition.ProductId} already has a '{locale}' localization.");
                return;
            }

            Console.WriteLine($"   -> Adding '{locale}' localization for {definition.ProductId}...");

            var request = AscHttp.Body("inAppPurchaseLocalizations",
                new JsonObject
                {
                    ["inAppPurchaseV2"] = AscHttp.Link("inAppPurchases", (string)iap["id"]!),
                },
                new JsonObject
                {
                    ["name"] = definition.LocalizedTitle,
                    ["locale"] = locale,
                    ["description"] = definition.LocalizedDescription,
                }
            );

            var response = await Http.PostAsync("/v1/inAppPurchaseLocalizations", request);

            if (verbose)
                Console.WriteLine($"[SUCCESS] localization created (ID: {(string?)response["data"]?["id"]})");
        }
        catch (AscApiException ex)
        {
            Console.WriteLine($"[API ERROR] failed to localize {definition.ProductId}: {ex.Message}");
            Console.WriteLine($"Status: {ex.StatusCode}");
            Console.WriteLine($"Response Body: {ex.ResponseBody}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[ERROR] failed to localize {definition.ProductId}: {ex.Message}");
        }
    }

    private async Task EnsureAvailability(JsonNode iap, List<Command_Localize.StoreTerritory> territories, bool verbose)
    {
        var productId = (string?)iap["attributes"]?["productId"];

        try
        {
            if (await HasAvailability(iap))
            {
                if (verbose)
                    Console.WriteLine($"   -> [SKIP] {productId} availability is already set.");
                return;
            }

            Console.WriteLine($"   -> Making {productId} available in all {territories.Count} territories...");

            var request = AscHttp.Body("inAppPurchaseAvailabilities",
                new JsonObject
                {
                    ["inAppPurchase"] = AscHttp.Link("inAppPurchases", (string)iap["id"]!),
                    ["availableTerritories"] = AscHttp.LinkMany("territories", territories.Select(t => t.Code)),
                },
                new JsonObject
                {
                    // so the product is automatically available in territories apple adds later
                    ["availableInNewTerritories"] = true,
                }
            );

            var response = await Http.PostAsync("/v1/inAppPurchaseAvailabilities", request);

            if (verbose)
                Console.WriteLine($"[SUCCESS] availability created (ID: {(string?)response["data"]?["id"]})");
        }
        catch (AscApiException ex)
        {
            Console.WriteLine($"[API ERROR] failed to set availability for {productId}: {ex.Message}");
            Console.WriteLine($"Status: {ex.StatusCode}");
            Console.WriteLine($"Response Body: {ex.ResponseBody}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[ERROR] failed to set availability for {productId}: {ex.Message}");
        }
    }

    private async Task<bool> HasAvailability(JsonNode iap)
    {
        try
        {
            var response = await Http.GetAsync($"/v2/inAppPurchases/{(string?)iap["id"]}/inAppPurchaseAvailability");

            return response["data"] is not null;
        }
        catch (AscApiException ex) when (ex.StatusCode == 404)
        {
            // no availability relationship yet
            return false;
        }
    }

    /// <summary>
    /// all territories the store supports, to make a product available everywhere
    /// </summary>
    private async Task<List<Command_Localize.StoreTerritory>> GetAllTerritoriesAsync()
    {
        var localizeCommand = new Command_Localize();
        localizeCommand.Initialize(Auth, Config, Args);
        return await localizeCommand.GetAllTerritoriesAsync();
    }

    private void PrintSummary(List<string> created, List<string> skipped, List<string> failed)
    {
        Console.WriteLine();
        Console.WriteLine("summary:");
        Console.WriteLine($"   created: {created.Count}");
        foreach (var item in created)
            Console.WriteLine($"      -> {item}");

        Console.WriteLine($"   skipped: {skipped.Count} (already exist in App Store Connect)");
        foreach (var item in skipped)
            Console.WriteLine($"      -> {item}");

        Console.WriteLine($"   failed:  {failed.Count}");
        foreach (var item in failed)
            Console.WriteLine($"      -> {item}");
    }
}
