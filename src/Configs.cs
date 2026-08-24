using Newtonsoft.Json;

public class Config
{
    public string KeyId { get; set; } = "";
    public string IssuerId { get; set; } = "";
    public string PrivateKeyFilePath { get; set; } = "";

    public string AppId { get; set; } = "";
    public string DefaultPricesFilePath { get; set; } = "";
    public string LocalizedPricesTemplateFilePath { get; set; } = "";

    /// <summary>
    /// csv with the product definitions the 'export-iaps' and 'create-iaps' commands read and write.
    /// the default must not be empty: Program.cs combines it with the config directory, and
    /// Path.Combine(directory, "") gives back the directory itself, which is not a file to write to
    /// </summary>
    public string ProductDefinitionsFilePath { get; set; } = "./product-definitions.csv";
    public string AppMetadataFilePath { get; set; } = "";

    /// <summary>
    /// csv with the Game Center achievement texts the 'locales export/import achievements'
    /// subcommands read and write. Like ProductDefinitionsFilePath, the default must not be empty
    /// </summary>
    public string AchievementTranslationsFilePath { get; set; } = "./achievement-translations.csv";

    /// <summary>csv with the In-App Purchase texts, one row per key and one column per language</summary>
    public string IapTranslationsFilePath { get; set; } = "./iap-translations.csv";

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
    public IapType Type;
    public decimal DefaultPrice;
    public string LocalizedTitle = "";
    public string LocalizedDescription = "";
}

/// <summary>
/// The three purchase kinds this tool creates. Named exactly like the old client's enum members so
/// a '{definition.Type}' print stays byte-for-byte what it always was.
/// </summary>
public enum IapType
{
    CONSUMABLE,
    NONCONSUMABLE,
    NONRENEWINGSUBSCRIPTION,
}
