public record Credentials(string KeyId, string IssuerId, string PrivateKeyFilePath);
public record GlobalConfig(string appId, string baseTerritory, string iapBasePricesConfigPath, string localPricesPercentagesConfigPath);
public class IapBasePrices : Dictionary<string, double> { }
public class IapLocalizedPercentages : Dictionary<string, double> { }