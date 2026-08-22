using AppStoreConnect.Net.Api;
using AppStoreConnect.Net.Model;

/// <summary>
/// Shared plumbing for the subcommands that touch In-App Purchase texts. Both of them need the
/// whole catalog with every language loaded, and the api hands those out one product at a time.
/// </summary>
public abstract class IapLocalesCommandBase : LocalesCommandBase
{
    /// <summary>
    /// key suffixes. A product id may legally contain dots, so the suffix is split off at the LAST
    /// one on import
    /// </summary>
    public const string NameField = "name";
    public const string DescriptionField = "description";

    /// <summary>
    /// the csv rows of one product, with the limits App Store Connect enforces.
    /// The numbers are the ones its own "Add App Store Localization" dialog counts down from
    /// </summary>
    public static readonly TextField[] IapFields =
    {
        new(NameField, "Display Name", 35),
        new(DescriptionField, "Description", 55),
    };

    protected override TextField[] Fields => IapFields;

    public static string? ValueOf(InAppPurchaseLocalization? localization, string field)
        => field switch
        {
            NameField => localization?.Attributes?.Name,
            DescriptionField => localization?.Attributes?.Description,
            _ => null,
        };

    /// <summary>a product and every language it has, in one object</summary>
    public class IapTexts
    {
        public InAppPurchaseV2 Product { get; set; } = null!;
        public List<InAppPurchaseLocalization> Localizations { get; set; } = new();

        public string ProductId => Product.Attributes?.ProductId ?? "";
        public string ReferenceName => Product.Attributes?.Name ?? "";

        public List<string> Locales => Localizations
            .Select(l => l.Attributes?.Locale ?? "")
            .Where(l => !string.IsNullOrWhiteSpace(l))
            .ToList();

        public InAppPurchaseLocalization? Find(string locale)
            => Localizations.FirstOrDefault(l => string.Equals(l.Attributes?.Locale, locale, StringComparison.OrdinalIgnoreCase));
    }

    protected async Task<List<IapTexts>> GetProductsAsync(bool verbose)
    {
        Console.WriteLine("   -> Receiving IAP list...");

        var appsApi = new AppsApi(Service);

        var products = await FetchAllPagesAsync<InAppPurchasesV2Response, InAppPurchaseV2>(
            appsApi.AsynchronousClient,
            appsApi.Configuration,
            () => appsApi.AppsInAppPurchasesV2GetToManyRelatedAsync(Config.AppId, limit: 200),
            r => r.Data,
            r => r.Links?.Next,
            verbose
        );

        var result = products
            .Where(p => !string.IsNullOrWhiteSpace(p.Attributes?.ProductId))
            .OrderBy(p => p.Attributes?.ProductId, StringComparer.Ordinal)
            .Select(p => new IapTexts { Product = p })
            .ToList();

        Console.WriteLine($"   -> {result.Count} product(s), receiving their languages...");

        foreach (var product in result)
            await LoadLocalizationsAsync(product, verbose);

        return result;
    }

    protected async Task LoadLocalizationsAsync(IapTexts product, bool verbose)
    {
        var api = new InAppPurchasesApi(Service);

        try
        {
            product.Localizations = await FetchAllPagesAsync<InAppPurchaseLocalizationsResponse, InAppPurchaseLocalization>(
                api.AsynchronousClient,
                api.Configuration,
                () => api.InAppPurchasesV2InAppPurchaseLocalizationsGetToManyRelatedAsync(product.Product.Id, limit: 200),
                r => r.Data,
                r => r.Links?.Next,
                verbose
            );

            if (verbose)
                Console.WriteLine($"      {product.ProductId,-40} {product.Localizations.Count} language(s)");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Warning: could not read the languages of {product.ProductId}: {ex.Message}");
            product.Localizations = new();
        }
    }
}
