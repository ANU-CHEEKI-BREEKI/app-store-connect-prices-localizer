using System.Text.Json.Nodes;

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

    public static string? ValueOf(JsonNode? localization, string field)
        => field switch
        {
            NameField => (string?)localization?["attributes"]?["name"],
            DescriptionField => (string?)localization?["attributes"]?["description"],
            _ => null,
        };
}
