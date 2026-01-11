public class Config
{
    public string KeyId { get; set; } = "";
    public string IssuerId { get; set; } = "";
    public string PrivateKeyFilePath { get; set; } = "";

    public string AppId { get; set; } = "";
    public string DefaultPricesFilePath { get; set; } = "";
    public string LocalizedPricesTemplateFilePath { get; set; } = "";
    
    public string DefaultRegion { get; set; } = "USA";
    public string Iap { get; set; } = "";
}

public class ProductConfigs : Dictionary<string, decimal> { }
public class LocalizedPricesPercentagesConfigs : Dictionary<string, decimal> { }
