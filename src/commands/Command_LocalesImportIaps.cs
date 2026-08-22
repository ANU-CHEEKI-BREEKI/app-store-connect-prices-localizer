using AppStoreConnect.Net.Api;
using AppStoreConnect.Net.Model;

/// <summary>
/// Writes a translated products csv back into the In-App Purchase localizations.
///
/// Texts only. Prices, availability and the review screenshot are never part of the request, so
/// 'localize' keeps owning them.
/// </summary>
public class Command_LocalesImportIaps : IapLocalesCommandBase
{
    /// <summary>"product|locale|field" of every value validation rejected, so the send pass leaves them out</summary>
    private readonly HashSet<string> _invalid = new(StringComparer.OrdinalIgnoreCase);

    public override string Name => "locales import iaps";

    public override string Description
        => "Writes a translated products csv back into the In-App Purchase localizations. Prices are never part of the request.";

    public override void PrintHelp()
    {
        Console.WriteLine("locales import iaps [--csv <path>] [--iap <id[,id...]>] [--force] [--no-create] [--submit] [-n] [-v]");
        Console.WriteLine();
        Console.WriteLine();

        Console.WriteLine("description:");
        CommandLinesUtils.PrintDescription(Description);
        CommandLinesUtils.PrintDescription($"Reads the csv 'locales export iaps' writes: rows matched by the '{KeyColumn}' column, every key a '<product_id>.{NameField}' or '<product_id>.{DescriptionField}', languages read from the locale code in the trailing parentheses of the column header.");
        CommandLinesUtils.PrintDescription("An empty cell means 'not translated yet' and is left alone - it never wipes a text that is already in App Store Connect. A value identical to what is already there is not sent, so re-running an unchanged csv writes nothing.");
        CommandLinesUtils.PrintDescription("The whole table is validated against the App Store Connect limits first, and nothing at all is sent when something would be rejected. Pass --force to send everything that is valid and skip only the offending values.");
        CommandLinesUtils.PrintDescription("A localization needs both a display name and a description, so a new language that would end up with only one of them is skipped with a warning rather than sent and rejected.");
        CommandLinesUtils.PrintDescription("Nothing goes to review by default: you normally import a few times before the text is right. Pass --submit to send every product this run changed, or run 'locales submit' later.");

        Console.WriteLine();
        Console.WriteLine("options:");

        CommandLinesUtils.PrintOption(
            "--csv <path>",
            $"The table with translations. If not specified, the path from global config json ('IapTranslationsFilePath') is used. A directory is also accepted, then '{Command_LocalesExportIaps.DefaultFileName}' is read from it."
        );
        CommandLinesUtils.PrintOption(
            CommandLinesUtils.IapOptionName,
            CommandLinesUtils.IapOptionDescription
        );
        CommandLinesUtils.PrintOption(
            "--force",
            "Run even though validation found problems: every value that would be rejected is skipped, everything else is sent."
        );
        CommandLinesUtils.PrintOption(
            "--no-create",
            "Do not create localizations for locales a product does not have yet, skip them instead."
        );
        CommandLinesUtils.PrintOption(
            "--submit",
            "Send every product this run changed to App Store Connect review afterwards."
        );
        CommandLinesUtils.PrintOption(
            "-n|--dry-run",
            "Print everything that would be changed, without sending a single write request."
        );
        CommandLinesUtils.PrintOption(
            "-v",
            "Include additional verbose output"
        );
    }

    protected override async Task InternalExecuteAsync()
    {
        var canCreate = !Args.HasFlag("--no-create");
        var force = Args.HasFlag("--force");
        var submit = Args.HasFlag("--submit");

        _invalid.Clear();

        try
        {
            if (string.IsNullOrWhiteSpace(Config.AppId))
            {
                Console.WriteLine("[ERROR] no app id. specify it in config.json or with --app-id");
                return;
            }

            Console.WriteLine("   -> Importing In-App Purchase texts...");

            if (DryRun)
                Console.WriteLine("   -> DRY RUN, nothing will be written.");

            var path = ResolveCsvPath(Config.IapTranslationsFilePath, Command_LocalesExportIaps.DefaultFileName);

            if (!File.Exists(path))
            {
                Console.WriteLine($"[ERROR] translations csv not found: '{Path.GetFullPath(path)}'");
                Console.WriteLine("        run 'locales export iaps' first, or pass --csv <path>");
                return;
            }

            var csv = await Translations.LoadAsync(path, Verbose);

            if (csv.Rows.Count == 0)
            {
                Console.WriteLine($"[ERROR] '{path}' has no data rows.");
                return;
            }

            Console.WriteLine($"   -> read {csv.Rows.Count} key(s) in {csv.Locales.Count} language(s) from {Path.GetFullPath(path)}");

            var products = await GetProductsAsync(Verbose);
            products = FilterByIap(products, p => p.ProductId);

            var byId = products.ToDictionary(p => p.ProductId, StringComparer.Ordinal);

            var updated = new List<string>();
            var created = new List<string>();
            var skipped = new List<string>();
            var failed = new List<string>();

            var groups = ResolveGroups(csv, byId, skipped);

            // everything is checked up front: a run that dies halfway leaves the catalog half
            // translated, which is worse than not having started it
            if (!Validate(groups, force, skipped))
                return;

            var changedProducts = new List<IapTexts>();

            foreach (var group in groups)
            {
                var changedHere = await ImportProductAsync(group, canCreate, updated, created, skipped, failed);
                if (changedHere)
                    changedProducts.Add(group.Product);
            }

            PrintSummary(updated, created, skipped, failed);

            if (submit && changedProducts.Count > 0)
            {
                Console.WriteLine();
                await Command_LocalesSubmit.SubmitProductsAsync(Service, changedProducts, DryRun, Verbose);
            }
            else if (changedProducts.Count > 0)
            {
                Console.WriteLine();
                Console.WriteLine($"{changedProducts.Count} product(s) changed and are waiting for review. Run 'locales submit --iaps' when the text is final.");
            }
        }
        catch (Exception ex)
        {
            PrintApiError("failed to import In-App Purchase texts", ex);
        }
    }

    /// <summary>the csv values of one product, keyed by locale then field</summary>
    private class ProductValues
    {
        public IapTexts Product = null!;

        /// <summary>locale -> (field -> value), only cells that had something in them</summary>
        public Dictionary<string, Dictionary<string, string>> ByLocale = new(StringComparer.OrdinalIgnoreCase);

        public string ProductId => Product.ProductId;
    }

    /// <summary>
    /// Matches the csv rows onto the products, and maps the column locale codes onto the ones
    /// App Store Connect accepts, before a single request goes out.
    /// </summary>
    private List<ProductValues> ResolveGroups(TranslationsCsv csv, Dictionary<string, IapTexts> byId, List<string> skipped)
    {
        var result = new List<ProductValues>();
        var unknown = new List<string>();

        foreach (var group in csv.ById)
        {
            if (!byId.TryGetValue(group.Key, out var product))
            {
                unknown.Add(group.Key);
                continue;
            }

            var values = new ProductValues { Product = product };

            foreach (var row in group)
            {
                if (Fields.All(f => !string.Equals(f.Key, row.Field, StringComparison.OrdinalIgnoreCase)))
                {
                    Console.WriteLine($"Warning: '{row.Key}' is neither a {NameField} nor a {DescriptionField}, skipped.");
                    continue;
                }

                foreach (var (sourceLocale, value) in row.Values)
                {
                    if (!AppStoreLocales.TryResolve(sourceLocale, product.Locales, out var locale, out var note))
                    {
                        var reason = $"{sourceLocale} (language the App Store does not support)";
                        if (!skipped.Contains(reason))
                            skipped.Add(reason);
                        continue;
                    }

                    if (!values.ByLocale.TryGetValue(locale, out var fields))
                        values.ByLocale[locale] = fields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

                    fields[row.Field] = value;
                }
            }

            if (values.ByLocale.Count > 0)
                result.Add(values);
        }

        foreach (var id in unknown)
            Console.WriteLine($"Warning: no In-App Purchase '{id}' in this app, skipped.");

        return result;
    }

    /// <summary>
    /// Checks every value against what App Store Connect accepts, before the first write goes out.
    /// Returns false when the run must not start.
    /// </summary>
    private bool Validate(List<ProductValues> groups, bool force, List<string> skipped)
    {
        var errors = new List<string>();

        foreach (var group in groups)
        {
            foreach (var (locale, fields) in group.ByLocale)
            {
                var existing = group.Product.Find(locale);

                foreach (var (key, value) in fields)
                {
                    var field = Fields.First(f => string.Equals(f.Key, key, StringComparison.OrdinalIgnoreCase));

                    if (value.Length <= field.MaxLength)
                        continue;

                    // App Store Connect accepts more than the console says, and an export of a real
                    // catalog comes back with values already over the documented limit. Refusing to
                    // re-import what it handed us would make a round trip impossible, so only text
                    // that is actually new is held to the limit
                    if (!IsChanged(value, ValueOf(existing, key)))
                        continue;

                    errors.Add($"   {key,-14} {group.ProductId} [{locale}] is {value.Length} characters, the limit is {field.MaxLength}");
                    _invalid.Add($"{group.ProductId}|{locale}|{key}");
                    skipped.Add($"{group.ProductId} [{locale}] {key} (too long)");
                }
            }
        }

        if (errors.Count == 0)
        {
            Console.WriteLine("   -> validation passed.");
            return true;
        }

        Console.WriteLine();
        Console.WriteLine($"[VALIDATION] {errors.Count} values would be rejected by App Store Connect:");
        Console.WriteLine();

        foreach (var error in errors)
            Console.WriteLine(error);

        Console.WriteLine();

        if (force)
        {
            Console.WriteLine("   -> --force: those values are skipped, everything else is sent.");
            Console.WriteLine();
            return true;
        }

        if (DryRun)
        {
            Console.WriteLine("   -> dry run, continuing anyway to show the rest. A real run would stop here.");
            Console.WriteLine();
            return true;
        }

        Console.WriteLine("nothing was sent. Fix the table and run again,");
        Console.WriteLine("or pass --force to send everything that is valid and skip the values listed above.");
        Console.WriteLine();

        return false;
    }

    /// <summary>writes one product's languages. Answers whether anything actually changed</summary>
    private async Task<bool> ImportProductAsync(
        ProductValues group, bool canCreate,
        List<string> updated, List<string> created, List<string> skipped, List<string> failed)
    {
        var changed = false;

        foreach (var (locale, fields) in group.ByLocale.OrderBy(v => v.Key, StringComparer.Ordinal))
        {
            var name = Take(group, locale, NameField, fields);
            var description = Take(group, locale, DescriptionField, fields);

            if (name is null && description is null)
                continue;

            var existing = group.Product.Find(locale);

            if (existing is null)
            {
                if (!canCreate)
                {
                    Console.WriteLine($"      [SKIP] {group.ProductId} [{locale}] does not exist, and --no-create is set.");
                    skipped.Add($"{group.ProductId} [{locale}]");
                    continue;
                }

                // App Store Connect refuses a localization that is missing either half
                if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(description))
                {
                    Console.WriteLine($"[WARN] {group.ProductId} [{locale}] is new and needs both a {NameField} and a {DescriptionField}, skipped.");
                    skipped.Add($"{group.ProductId} [{locale}] (half a localization)");
                    continue;
                }

                Console.WriteLine($"      [NEW]  {group.ProductId} [{locale}] {Preview(name)}");

                if (DryRun)
                {
                    created.Add($"{group.ProductId} [{locale}]");
                    changed = true;
                    continue;
                }

                if (await CreateAsync(group, locale, name, description, created, failed))
                    changed = true;

                continue;
            }

            var fieldsChanged = new List<string>();

            if (IsChanged(name, existing.Attributes?.Name)) fieldsChanged.Add(NameField);
            else name = null;

            if (IsChanged(description, existing.Attributes?.Description)) fieldsChanged.Add(DescriptionField);
            else description = null;

            if (fieldsChanged.Count == 0)
            {
                if (Verbose)
                    Console.WriteLine($"      [SAME] {group.ProductId} [{locale}] already up to date.");
                continue;
            }

            Console.WriteLine($"      [SET]  {group.ProductId} [{locale}] {string.Join(", ", fieldsChanged)}");

            if (DryRun)
            {
                updated.Add($"{group.ProductId} [{locale}] {string.Join("/", fieldsChanged)}");
                changed = true;
                continue;
            }

            if (await UpdateAsync(group, locale, existing, name, description, fieldsChanged, updated, failed))
                changed = true;
        }

        return changed;
    }

    private async Task<bool> CreateAsync(
        ProductValues group, string locale, string name, string description,
        List<string> created, List<string> failed)
    {
        try
        {
            var request = new InAppPurchaseLocalizationCreateRequest(
                new InAppPurchaseLocalizationCreateRequestData(
                    InAppPurchaseLocalizationCreateRequestData.TypeEnum.InAppPurchaseLocalizations,
                    new InAppPurchaseLocalizationCreateRequestDataAttributes(name, locale, description),
                    new InAppPurchaseAppStoreReviewScreenshotCreateRequestDataRelationships(
                        inAppPurchaseV2: new InAppPurchaseAppStoreReviewScreenshotCreateRequestDataRelationshipsInAppPurchaseV2(
                            new AppRelationshipsInAppPurchasesDataInner(
                                AppRelationshipsInAppPurchasesDataInner.TypeEnum.InAppPurchases,
                                group.Product.Product.Id
                            )
                        )
                    )
                )
            );

            var response = await new InAppPurchaseLocalizationsApi(Service).InAppPurchaseLocalizationsCreateInstanceAsync(request);

            if (response?.Data is not null)
                group.Product.Localizations.Add(response.Data);

            created.Add($"{group.ProductId} [{locale}]");
            return true;
        }
        catch (Exception ex)
        {
            PrintApiError($"failed to create the {locale} localization of {group.ProductId}", ex);
            failed.Add($"{group.ProductId} [{locale}]");
            return false;
        }
    }

    private async Task<bool> UpdateAsync(
        ProductValues group, string locale, InAppPurchaseLocalization existing,
        string? name, string? description, List<string> fieldsChanged,
        List<string> updated, List<string> failed)
    {
        try
        {
            // the generated client serializes every attribute, including the ones left null, and
            // App Store Connect reads an explicit null as "clear this field". So the half that is
            // not changing has to be resent as it is
            var request = new InAppPurchaseLocalizationUpdateRequest(
                new InAppPurchaseLocalizationUpdateRequestData(
                    InAppPurchaseLocalizationUpdateRequestData.TypeEnum.InAppPurchaseLocalizations,
                    existing.Id,
                    new GameCenterActivityLocalizationUpdateRequestDataAttributes(
                        name: name ?? existing.Attributes?.Name,
                        description: description ?? existing.Attributes?.Description
                    )
                )
            );

            await new InAppPurchaseLocalizationsApi(Service).InAppPurchaseLocalizationsUpdateInstanceAsync(existing.Id, request);

            updated.Add($"{group.ProductId} [{locale}] {string.Join("/", fieldsChanged)}");
            return true;
        }
        catch (Exception ex)
        {
            PrintApiError($"failed to update the {locale} localization of {group.ProductId}", ex);
            failed.Add($"{group.ProductId} [{locale}]");
            return false;
        }
    }

    /// <summary>
    /// reads a field, leaving out what validation already rejected. A single bad value would fail
    /// the whole request and take the other half of that language with it
    /// </summary>
    private string? Take(ProductValues group, string locale, string field, Dictionary<string, string> fields)
    {
        if (!fields.TryGetValue(field, out var value))
            return null;

        // already reported by Validate, this run only reaches here with --force
        if (_invalid.Contains($"{group.ProductId}|{locale}|{field}"))
            return null;

        return value;
    }
}
