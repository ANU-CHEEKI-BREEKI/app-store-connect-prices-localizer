using System.Globalization;
using AppStoreConnect.Net.Api;
using AppStoreConnect.Net.Client;
using AppStoreConnect.Net.Model;

public class Command_CreateIaps : CommandBase
{
    public override string Name => "create-iaps";
    public override string Description => "Creates In-App Purchases in App Store Connect from the product definitions csv. Products that already exist are not re-created.";

    public override void PrintHelp()
    {
        Console.WriteLine("create-iaps [--products <path-to-product-definitions.csv>] [--locale <locale>] [-v]");
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

            var singleIap = Config.Iap;
            if (!string.IsNullOrEmpty(singleIap))
                definitions = definitions.Where(d => d.ProductId == singleIap).ToList();

            if (definitions.Count == 0)
            {
                Console.WriteLine("   -> nothing to create, no product definitions matched.");
                return;
            }

            Console.WriteLine("   -> Receiving IAP list...");
            var appApi = new AppsApi(Service);
            var iaps = await appApi.AppsInAppPurchasesV2GetToManyRelatedAsync(Config.AppId, limit: 200);

            var existing = iaps.Data
                .Where(p => p.Attributes?.ProductId != null)
                .ToDictionary(p => p.Attributes.ProductId, p => p);

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
                restorer.Initialize(Service, Config, Args);
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

    private static bool TryParseType(string raw, out InAppPurchaseType type)
    {
        // 'non-consumable', 'NON_CONSUMABLE', 'nonConsumable' all mean the same thing
        var normalized = new string(raw.Where(char.IsLetter).ToArray()).ToLowerInvariant();

        switch (normalized)
        {
            case "consumable":
                type = InAppPurchaseType.CONSUMABLE;
                return true;
            case "nonconsumable":
                type = InAppPurchaseType.NONCONSUMABLE;
                return true;
            case "nonrenewingsubscription":
                type = InAppPurchaseType.NONRENEWINGSUBSCRIPTION;
                return true;
            default:
                type = default;
                return false;
        }
    }

    private async Task<InAppPurchaseV2?> CreateIap(ProductDefinition definition, bool verbose)
    {
        Console.WriteLine($"   -> Creating IAP: {definition.ProductId} ({definition.Type})...");

        var request = new InAppPurchaseV2CreateRequest(
            data: new InAppPurchaseV2CreateRequestData(
                type: InAppPurchaseV2CreateRequestData.TypeEnum.InAppPurchases,
                attributes: new InAppPurchaseV2CreateRequestDataAttributes(
                    name: definition.ReferenceName,
                    productId: definition.ProductId,
                    inAppPurchaseType: definition.Type,
                    familySharable: false
                ),
                relationships: new AccessibilityDeclarationCreateRequestDataRelationships(
                    app: new AccessibilityDeclarationCreateRequestDataRelationshipsApp(
                        data: new AccessibilityDeclarationCreateRequestDataRelationshipsAppData(
                            type: AccessibilityDeclarationCreateRequestDataRelationshipsAppData.TypeEnum.Apps,
                            id: Config.AppId
                        )
                    )
                )
            )
        );

        try
        {
            var response = await new InAppPurchasesApi(Service).InAppPurchasesV2CreateInstanceAsync(request);

            if (verbose)
                Console.WriteLine($"[SUCCESS] created {definition.ProductId} (ID: {response.Data.Id})");

            return response.Data;
        }
        catch (ApiException ex)
        {
            Console.WriteLine($"[API ERROR] failed to create {definition.ProductId}: {ex.Message}");
            Console.WriteLine($"Status: {ex.ErrorCode}");
            Console.WriteLine($"Response Body: {ex.ErrorContent}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[ERROR] failed to create {definition.ProductId}: {ex.Message}");
        }

        return null;
    }

    private async Task EnsureLocalization(InAppPurchaseV2 iap, ProductDefinition definition, bool verbose)
    {
        var locale = string.IsNullOrWhiteSpace(Config.DefaultLocale) ? "en-US" : Config.DefaultLocale;
        var iapApi = new InAppPurchasesApi(Service);

        try
        {
            var localizations = await iapApi.InAppPurchasesV2InAppPurchaseLocalizationsGetToManyRelatedAsync(iap.Id, limit: 200);

            var already = localizations.Data?.Any(
                l => string.Equals(l.Attributes?.Locale, locale, StringComparison.OrdinalIgnoreCase)
            ) ?? false;

            if (already)
            {
                if (verbose)
                    Console.WriteLine($"   -> [SKIP] {definition.ProductId} already has a '{locale}' localization.");
                return;
            }

            Console.WriteLine($"   -> Adding '{locale}' localization for {definition.ProductId}...");

            var request = new InAppPurchaseLocalizationCreateRequest(
                data: new InAppPurchaseLocalizationCreateRequestData(
                    type: InAppPurchaseLocalizationCreateRequestData.TypeEnum.InAppPurchaseLocalizations,
                    attributes: new InAppPurchaseLocalizationCreateRequestDataAttributes(
                        name: definition.LocalizedTitle,
                        locale: locale,
                        description: definition.LocalizedDescription
                    ),
                    relationships: new InAppPurchaseAppStoreReviewScreenshotCreateRequestDataRelationships(
                        inAppPurchaseV2: new InAppPurchaseAppStoreReviewScreenshotCreateRequestDataRelationshipsInAppPurchaseV2(
                            data: new(
                                id: iap.Id,
                                type: AppRelationshipsInAppPurchasesDataInner.TypeEnum.InAppPurchases
                            )
                        )
                    )
                )
            );

            var response = await new InAppPurchaseLocalizationsApi(Service).InAppPurchaseLocalizationsCreateInstanceAsync(request);

            if (verbose)
                Console.WriteLine($"[SUCCESS] localization created (ID: {response.Data.Id})");
        }
        catch (ApiException ex)
        {
            Console.WriteLine($"[API ERROR] failed to localize {definition.ProductId}: {ex.Message}");
            Console.WriteLine($"Status: {ex.ErrorCode}");
            Console.WriteLine($"Response Body: {ex.ErrorContent}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[ERROR] failed to localize {definition.ProductId}: {ex.Message}");
        }
    }

    private async Task EnsureAvailability(InAppPurchaseV2 iap, List<Command_Localize.StoreTerritory> territories, bool verbose)
    {
        var productId = iap.Attributes?.ProductId;

        try
        {
            if (await HasAvailability(iap))
            {
                if (verbose)
                    Console.WriteLine($"   -> [SKIP] {productId} availability is already set.");
                return;
            }

            Console.WriteLine($"   -> Making {productId} available in all {territories.Count} territories...");

            var request = new InAppPurchaseAvailabilityCreateRequest(
                data: new InAppPurchaseAvailabilityCreateRequestData(
                    type: InAppPurchaseAvailabilityCreateRequestData.TypeEnum.InAppPurchaseAvailabilities,
                    attributes: new AppAvailabilityV2CreateRequestDataAttributes(
                        // so the product is automatically available in territories apple adds later
                        availableInNewTerritories: true
                    ),
                    relationships: new InAppPurchaseAvailabilityCreateRequestDataRelationships(
                        inAppPurchase: new InAppPurchaseAppStoreReviewScreenshotCreateRequestDataRelationshipsInAppPurchaseV2(
                            data: new(
                                id: iap.Id,
                                type: AppRelationshipsInAppPurchasesDataInner.TypeEnum.InAppPurchases
                            )
                        ),
                        availableTerritories: new EndUserLicenseAgreementCreateRequestDataRelationshipsTerritories(
                            data: territories.Select(t => new AppPricePointV3RelationshipsTerritoryData(
                                type: AppPricePointV3RelationshipsTerritoryData.TypeEnum.Territories,
                                id: t.Code
                            )).ToList()
                        )
                    )
                )
            );

            var response = await new InAppPurchaseAvailabilitiesApi(Service).InAppPurchaseAvailabilitiesCreateInstanceAsync(request);

            if (verbose)
                Console.WriteLine($"[SUCCESS] availability created (ID: {response.Data.Id})");
        }
        catch (ApiException ex)
        {
            Console.WriteLine($"[API ERROR] failed to set availability for {productId}: {ex.Message}");
            Console.WriteLine($"Status: {ex.ErrorCode}");
            Console.WriteLine($"Response Body: {ex.ErrorContent}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[ERROR] failed to set availability for {productId}: {ex.Message}");
        }
    }

    private async Task<bool> HasAvailability(InAppPurchaseV2 iap)
    {
        try
        {
            var response = await new InAppPurchasesApi(Service)
                .InAppPurchasesV2InAppPurchaseAvailabilityGetToOneRelatedAsync(iap.Id);

            return response?.Data is not null;
        }
        catch (ApiException ex) when (ex.ErrorCode == 404)
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
        localizeCommand.Initialize(Service, Config, Args);
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
