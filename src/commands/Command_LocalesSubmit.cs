using System.Text.Json.Nodes;

/// <summary>
/// Sends everything that is waiting to App Store Connect review.
///
/// Three different things, three different mechanisms, and none of them is the button next to the
/// other two in the console:
/// - an In-App Purchase gets its own submission, independent of any app version
/// - a Game Center achievement goes into the open review submission as an item, and that submission
///   is left for the console to send, so the list can be looked at first
/// - the app store version goes into a review submission, as an item of it, and is submitted
///
/// Kept out of the imports on purpose: a translation is normally imported a few times before it is
/// right, and review is the one step in this tool that is not free to redo.
/// </summary>
public class Command_LocalesSubmit : GameCenterCommandBase
{
    protected override TextField[] Fields => Array.Empty<TextField>();

    public override string Name => "locales submit";

    public override string Description
        => "Sends everything that is waiting to App Store Connect review: the In-App Purchases, the Game Center achievements and the app store version.";

    public override void PrintHelp()
    {
        Console.WriteLine("locales submit [--iaps] [--texts] [--achievements] [--achievement <id[,id...]>] [--app] [--iap <id[,id...]>] [-n] [-v]");
        Console.WriteLine();
        Console.WriteLine();

        Console.WriteLine("description:");
        CommandLinesUtils.PrintDescription(Description);
        CommandLinesUtils.PrintDescription("Without any of the three flags all three are done. With one or more, only those.");
        CommandLinesUtils.PrintDescription("In-App Purchases: every product sitting in READY_TO_SUBMIT gets its own submission, and so does an approved product with a language still in 'Prepare for Submission'. A product already waiting for review or in review is left alone, so running this twice is safe.");
        CommandLinesUtils.PrintDescription("Achievements: every achievement with a version in 'Prepare for Submission' (a new one, or one given a new version) is added to the open review submission, or to a new one. That submission is NOT submitted - press Submit in App Review in the console once the list looks right. A language added to a live achievement does not need any of this: it is live the moment it is imported.");
        CommandLinesUtils.PrintDescription("App store version: the editable version is added to the open review submission, or to a new one, and that submission is then submitted.");
        CommandLinesUtils.PrintDescription("Run with -n first. This is the one command here that can not be undone by running it again.");

        Console.WriteLine();
        Console.WriteLine("options:");

        CommandLinesUtils.PrintOption("--iaps", "Submit the In-App Purchases only.");
        CommandLinesUtils.PrintOption("--texts", "Also submit approved products that have a language still in 'Prepare for Submission'. The api does not report those languages as submitted afterwards, so run this once.");
        CommandLinesUtils.PrintOption("--achievements", "Add the Game Center achievements to the review submission only.");
        CommandLinesUtils.PrintOption("--achievement <id[,id...]>", "Live achievements to make a new version of, by vendor identifier, so that version goes into the submission. Rarely needed: a language added to a live achievement is live at once, without review.");
        CommandLinesUtils.PrintOption("--app", "Submit the app store version only.");
        CommandLinesUtils.PrintOption(CommandLinesUtils.IapOptionName, CommandLinesUtils.IapOptionDescription);
        CommandLinesUtils.PrintOption("-n|--dry-run", "Print everything that would be submitted, without sending a single write request.");
        CommandLinesUtils.PrintOption("-v", "Include additional verbose output");
    }

    protected override async Task InternalExecuteAsync()
    {
        var wantIaps = Args.HasFlag("--iaps");
        var wantAchievements = Args.HasFlag("--achievements");
        var wantApp = Args.HasFlag("--app");

        // no flag means all three, which is what "submit everything" reads like
        if (!wantIaps && !wantAchievements && !wantApp)
            wantIaps = wantAchievements = wantApp = true;

        try
        {
            if (string.IsNullOrWhiteSpace(Config.AppId))
            {
                Console.WriteLine("[ERROR] no app id. specify it in config.json or with --app-id");
                return;
            }

            if (DryRun)
                Console.WriteLine("   -> DRY RUN, nothing will be submitted.");

            if (wantIaps)
                await SubmitIapsAsync();

            if (wantAchievements)
                await AddAchievementsToSubmissionAsync();

            if (wantApp)
                await SubmitVersionAsync();
        }
        catch (Exception ex)
        {
            PrintApiError("failed to submit for review", ex);
        }
    }

    /// <summary>states in which a product is already on its way, so submitting it again is noise</summary>
    private static readonly string[] AlreadyOnItsWay =
    {
        "WAITING_FOR_REVIEW",
        "IN_REVIEW",
        "PENDING_BINARY_APPROVAL",
    };

    /// <summary>the state the way the generated client printed it: the enum names had no underscores</summary>
    private static string? StateName(string? state)
        => state?.Replace("_", "");

    /// <summary>
    /// An approved product stays APPROVED when a language is added to it: the new text carries its
    /// own state instead, and the product is worth a submission exactly when some of it is still
    /// 'Prepare for Submission'.
    ///
    /// That state is not to be trusted after a submission though: the api keeps answering
    /// PREPARE_FOR_SUBMISSION while the console already says 'Waiting for Review'. Which is why
    /// this path is behind --texts, and running it twice is not safe.
    /// </summary>
    private async Task<int> PendingLocalizationsAsync(JsonNode? product)
    {
        var response = await Http.GetAsync($"/v2/inAppPurchases/{(string?)product?["id"]}/inAppPurchaseLocalizations?limit=200");

        return (response["data"] as JsonArray ?? new JsonArray())
            .Count(l => (string?)l?["attributes"]?["state"] == "PREPARE_FOR_SUBMISSION");
    }

    private async Task SubmitIapsAsync()
    {
        var texts = Args.HasFlag("--texts");

        Console.WriteLine();
        Console.WriteLine("   -> In-App Purchases...");

        var page = await Http.GetPagedAsync($"/v1/apps/{Config.AppId}/inAppPurchasesV2?limit=200");

        var products = FilterByIap(page.Data, p => (string?)p?["attributes"]?["productId"]);

        var ready = new List<JsonNode?>();

        foreach (var product in products)
        {
            var productId = (string?)product?["attributes"]?["productId"];
            var state = (string?)product?["attributes"]?["state"];

            if (state is not null && AlreadyOnItsWay.Contains(state))
            {
                if (Verbose)
                    Console.WriteLine($"      [SAME] {productId} is already {StateName(state)}.");
                continue;
            }

            if (state == "READY_TO_SUBMIT")
            {
                ready.Add(product);
                continue;
            }

            if (state == "APPROVED" && texts)
            {
                var pending = await PendingLocalizationsAsync(product);

                if (pending > 0)
                {
                    Console.WriteLine($"      [TEXT] {productId} is approved, {pending} language(s) still to be submitted.");
                    ready.Add(product);
                }
                else if (Verbose)
                    Console.WriteLine($"      [SAME] {productId} is already {StateName(state)}.");

                continue;
            }

            if (Verbose)
                Console.WriteLine($"      [SKIP] {productId} is {StateName(state)}, not ready to submit.");
        }

        if (ready.Count == 0)
        {
            Console.WriteLine("      nothing to submit, no product is ready.");
            return;
        }

        await SubmitProductsAsync(Http, ready, DryRun, Verbose);
    }

    /// <summary>
    /// Submits products for review, one submission each. Shared with 'locales import iaps --submit',
    /// which passes exactly the products it just changed
    /// </summary>
    public static async Task SubmitProductsAsync(AscHttp http, IEnumerable<JsonNode?> products, bool dryRun, bool verbose)
    {
        var submitted = 0;
        var failed = 0;

        foreach (var product in products)
        {
            var id = (string?)product?["id"] ?? "";
            var productId = (string?)product?["attributes"]?["productId"] ?? id;

            Console.WriteLine($"      [SEND] {productId}");

            if (dryRun)
            {
                submitted++;
                continue;
            }

            try
            {
                await http.PostAsync("/v1/inAppPurchaseSubmissions", AscHttp.Body(
                    "inAppPurchaseSubmissions",
                    new JsonObject { ["inAppPurchaseV2"] = AscHttp.Link("inAppPurchases", id) }
                ));

                submitted++;
            }
            catch (Exception ex)
            {
                PrintApiError($"failed to submit {productId}", ex);
                failed++;
            }
        }

        Console.WriteLine($"      {submitted} product(s) submitted, {failed} failed.");
    }

    /// <summary>
    /// Same product list, but taken straight from what an import changed rather than from the api.
    /// The import holds them as IapTexts, which is where the real product sits
    /// </summary>
    public static Task SubmitProductsAsync(AscHttp http, IEnumerable<IapLocalesCommandBase.IapTexts> products, bool dryRun, bool verbose)
        => SubmitProductsAsync(http, products.Select(p => (JsonNode?)p.Product), dryRun, verbose);

    /// <summary>
    /// Puts every achievement that has something new into the open review submission, without
    /// submitting it: that button stays in the console, where the whole list can be looked at first.
    /// Nothing is released any more - a reviewed achievement goes live on its own.
    /// </summary>
    private async Task AddAchievementsToSubmissionAsync()
    {
        Console.WriteLine();
        Console.WriteLine("   -> Game Center achievements...");

        var achievements = await GetAchievementsAsync(Verbose);
        if (achievements is null)
            return;

        if (achievements.Count == 0)
        {
            Console.WriteLine("      this app has no achievements.");
            return;
        }

        var only = new HashSet<string>(ParseList("--achievement"), StringComparer.Ordinal);

        var toAdd = new List<(Achievement Achievement, string VersionId)>();
        var inDraft = 0;
        var skipped = 0;

        foreach (var achievement in achievements)
        {
            var versions = await Http.GetAsync($"/v2/gameCenterAchievements/{achievement.Id}/versions?fields[gameCenterAchievementVersions]=version,state&limit=50");
            var latest = (versions["data"] as JsonArray)?
                .OrderByDescending(v => (int?)v?["attributes"]?["version"] ?? 0)
                .FirstOrDefault();

            var state = (string?)latest?["attributes"]?["state"] ?? "";
            var versionId = (string?)latest?["id"] ?? "";

            switch (state)
            {
                case "PREPARE_FOR_SUBMISSION":
                    toAdd.Add((achievement, versionId));
                    Console.WriteLine($"      [ADD]  {achievement.VendorIdentifier}");
                    break;

                case "READY_FOR_REVIEW":
                    inDraft++;
                    if (Verbose)
                        Console.WriteLine($"      [SAME] {achievement.VendorIdentifier} is already in the review submission.");
                    break;

                // a language added to a live achievement lands inside the live version and is live at
                // once, no review. A new version is only for a real change, and it is asked for by
                // name because the api can not tell; once made it can not be deleted, only reviewed
                case "LIVE" when only.Contains(achievement.VendorIdentifier):
                    toAdd.Add((achievement, ""));
                    Console.WriteLine($"      [ADD]  {achievement.VendorIdentifier} (new version)");
                    break;

                case "LIVE":
                    skipped++;
                    if (Verbose)
                        Console.WriteLine($"      [SKIP] {achievement.VendorIdentifier} is live.");
                    break;

                default:
                    skipped++;
                    if (Verbose)
                        Console.WriteLine($"      [SKIP] {achievement.VendorIdentifier} is {state}, nothing new.");
                    break;
            }
        }

        Console.WriteLine($"      {toAdd.Count} to add, {inDraft} already in the submission, {skipped} with nothing new.");

        if (toAdd.Count == 0)
            return;

        var platform = Args.TryGetOption("--platform", "IOS").ToUpperInvariant();

        // a submission that was never submitted has no submittedDate, and the generated client
        // choked on that, so the draft is looked up and made by hand
        var submissions = await Http.GetAsync($"/v1/reviewSubmissions?filter[app]={Config.AppId}&filter[platform]={platform}&filter[state]=READY_FOR_REVIEW&limit=50");
        var openId = (string?)(submissions["data"] as JsonArray)?.FirstOrDefault()?["id"];

        Console.WriteLine(openId is null
            ? "      [NEW]  review submission"
            : $"      [ADD]  to the open review submission {openId}");

        if (DryRun)
            return;

        var submissionId = openId;

        if (submissionId is null)
        {
            var created = await Http.PostAsync("/v1/reviewSubmissions", AscHttp.Body(
                "reviewSubmissions",
                new JsonObject { ["app"] = AscHttp.Link("apps", Config.AppId) },
                new JsonObject { ["platform"] = platform }
            ));
            submissionId = (string?)created["data"]?["id"];
        }

        if (string.IsNullOrWhiteSpace(submissionId))
            return;

        var added = 0;
        var failed = 0;

        foreach (var (achievement, knownVersionId) in toAdd)
        {
            try
            {
                var versionId = knownVersionId;

                if (string.IsNullOrEmpty(versionId))
                {
                    var created = await Http.PostAsync("/v2/gameCenterAchievementVersions", AscHttp.Body(
                        "gameCenterAchievementVersions",
                        new JsonObject { ["achievement"] = AscHttp.Link("gameCenterAchievements", achievement.Id) }
                    ));
                    versionId = (string?)created["data"]?["id"] ?? throw new Exception("no version id in the response");
                }

                await Http.PostAsync("/v1/reviewSubmissionItems", AscHttp.Body(
                    "reviewSubmissionItems",
                    new JsonObject
                    {
                        ["reviewSubmission"] = AscHttp.Link("reviewSubmissions", submissionId),
                        ["gameCenterAchievementVersion"] = AscHttp.Link("gameCenterAchievementVersions", versionId),
                    }
                ));
                added++;
            }
            catch (Exception ex)
            {
                PrintApiError($"failed to add {achievement.VendorIdentifier}", ex);
                failed++;
            }
        }

        Console.WriteLine($"      {added} achievement(s) added to review submission {submissionId}, {failed} failed.");
        Console.WriteLine("      not submitted: open App Review in App Store Connect, check the list and press Submit.");
    }

    /// <summary>a review submission that can still take items</summary>
    private static readonly string[] OpenStates =
    {
        "READY_FOR_REVIEW",
        "UNRESOLVED_ISSUES",
    };

    private async Task SubmitVersionAsync()
    {
        Console.WriteLine();
        Console.WriteLine("   -> App store version...");

        var platform = Args.TryGetOption("--platform", "IOS").ToUpperInvariant();

        var versionsResponse = await Http.GetAsync(
            $"/v1/apps/{Config.AppId}/appStoreVersions?filter[platform]={platform}&fields[appStoreVersions]=versionString,appVersionState,appStoreState,platform,createdDate&limit=50"
        );

        var version = (versionsResponse["data"] as JsonArray ?? new JsonArray())
            .OrderByDescending(CreatedDate)
            .FirstOrDefault(v => (string?)v?["attributes"]?["appVersionState"] == "PREPARE_FOR_SUBMISSION");

        if (version is null)
        {
            Console.WriteLine("      no version is waiting for submission.");
            return;
        }

        Console.WriteLine($"      version {(string?)version["attributes"]?["versionString"]}");

        var versionId = (string?)version["id"] ?? "";

        var submissions = await Http.GetAsync(
            $"/v1/reviewSubmissions?filter[app]={Config.AppId}&filter[platform]={platform}&limit=50&include=items"
        );

        var open = (submissions["data"] as JsonArray ?? new JsonArray())
            .FirstOrDefault(s => (string?)s?["attributes"]?["state"] is { } state && OpenStates.Contains(state));

        if (open is not null && HasVersion(open, versionId))
        {
            Console.WriteLine("      already in the open review submission, nothing to add.");
            return;
        }

        Console.WriteLine(open is null
            ? "      [NEW]  review submission"
            : $"      [ADD]  to the open review submission {(string?)open["id"]}");

        if (DryRun)
            return;

        try
        {
            var submissionId = (string?)open?["id"] ?? await CreateSubmissionAsync(platform);
            if (string.IsNullOrWhiteSpace(submissionId))
                return;

            await Http.PostAsync("/v1/reviewSubmissionItems", AscHttp.Body(
                "reviewSubmissionItems",
                new JsonObject
                {
                    ["reviewSubmission"] = AscHttp.Link("reviewSubmissions", submissionId),
                    ["appStoreVersion"] = AscHttp.Link("appStoreVersions", versionId),
                }
            ));

            await Http.PatchAsync($"/v1/reviewSubmissions/{submissionId}", AscHttp.BodyWithAttributes(
                "reviewSubmissions",
                submissionId,
                new JsonObject { ["submitted"] = true }
            ));

            Console.WriteLine($"      submitted, review submission {submissionId}.");
        }
        catch (Exception ex)
        {
            PrintApiError("failed to submit the app store version", ex);
        }
    }

    private async Task<string?> CreateSubmissionAsync(string platform)
    {
        var response = await Http.PostAsync("/v1/reviewSubmissions", AscHttp.Body(
            "reviewSubmissions",
            new JsonObject { ["app"] = AscHttp.Link("apps", Config.AppId) },
            new JsonObject { ["platform"] = ParsePlatform(platform) }
        ));

        return (string?)response["data"]?["id"];
    }

    private static string ParsePlatform(string platform) => platform switch
    {
        "MAC_OS" or "MACOS" => "MAC_OS",
        "TV_OS" or "TVOS" => "TV_OS",
        "VISION_OS" or "VISIONOS" => "VISION_OS",
        _ => "IOS",
    };

    private static DateTimeOffset CreatedDate(JsonNode? version)
        => DateTimeOffset.TryParse((string?)version?["attributes"]?["createdDate"], out var date) ? date : DateTimeOffset.MinValue;

    private static bool HasVersion(JsonNode submission, string versionId)
        => (submission["relationships"]?["items"]?["data"] as JsonArray)?
            .Any(i => string.Equals((string?)i?["id"], versionId, StringComparison.Ordinal)) == true;
}
