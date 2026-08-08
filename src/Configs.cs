using AppStoreConnect.Net.Model;
using Newtonsoft.Json;

public class Config
{
    public string KeyId { get; set; } = "";
    public string IssuerId { get; set; } = "";
    public string PrivateKeyFilePath { get; set; } = "";

    public string AppId { get; set; } = "";
    public string DefaultPricesFilePath { get; set; } = "";
    public string LocalizedPricesTemplateFilePath { get; set; } = "";
    public string ProductDefinitionsFilePath { get; set; } = "";
    public string AppMetadataFilePath { get; set; } = "";

    /// <summary>
    /// locale codes of the source/original languages used for translation (e.g. ["en-US", "uk"]).
    /// export-metadata places these columns first in the csv so translators see the originals on the left
    /// </summary>
    public List<string> SourceLocales { get; set; } = new();

    /// <summary>where config.json was found, the fallback location for the app metadata csv</summary>
    [JsonIgnore]
    public string ConfigDirectory { get; set; } = "";

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
