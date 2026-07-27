using AppStoreConnect.Net.Model;

public class Config
{
    public string KeyId { get; set; } = "";
    public string IssuerId { get; set; } = "";
    public string PrivateKeyFilePath { get; set; } = "";

    public string AppId { get; set; } = "";
    public string DefaultPricesFilePath { get; set; } = "";
    public string LocalizedPricesTemplateFilePath { get; set; } = "";
    public string ProductDefinitionsFilePath { get; set; } = "";

    public string DefaultRegion { get; set; } = "USA";
    public string DefaultLocale { get; set; } = "en-US";
    public string Iap { get; set; } = "";
}

public class ProductConfigs : Dictionary<string, decimal> { }
public class LocalizedPricesPercentagesConfigs : Dictionary<string, decimal> { }

/// <summary>
/// single row of the product-definitions csv
/// </summary>
public class ProductDefinition
{
    public string ProductId = "";
    public string ReferenceName = "";
    public InAppPurchaseType Type;
    public decimal DefaultPrice;
    public string LocalizedTitle = "";
    public string LocalizedDescription = "";
}
