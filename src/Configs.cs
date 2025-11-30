public record Credentials(string KeyId, string IssuerId, string PrivateKeyFilePath);

public record GlobalConfig(string appId, string baseTerritory, string iapBasePricesConfigPath);

public class IapBasePrices : Dictionary<string, double> { }