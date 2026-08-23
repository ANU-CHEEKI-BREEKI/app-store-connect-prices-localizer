using AppStoreConnect.Net.Api;
using AppStoreConnect.Net.Client;
using System.Text.Json.Nodes;
using AppStoreConnect.Net.Model;

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
    private static readonly InAppPurchaseState[] AlreadyOnItsWay =
    {
        InAppPurchaseState.WAITINGFORREVIEW,
        InAppPurchaseState.INREVIEW,
        InAppPurchaseState.PENDINGBINARYAPPROVAL,
    };

    /// <summary>
    /// An approved product stays APPROVED when a language is added to it: the new text carries its
    /// own state instead, and the product is worth a submission exactly when some of it is still
    /// 'Prepare for Submission'.
    ///
    /// That state is not to be trusted after a submission though: the api keeps answering
    /// PREPARE_FOR_SUBMISSION while the console already says 'Waiting for Review'. Which is why
    /// this path is behind --texts, and running it twice is not safe.
    /// </summary>
    private async Task<int> PendingLocalizationsAsync(InAppPurchaseV2 product)
    {
        var api = new InAppPurchasesApi(Service);
        var response = await api.InAppPurchasesV2InAppPurchaseLocalizationsGetToManyRelatedAsync(product.Id, limit: 200);

        return (response?.Data ?? new())
            .Count(l => l.Attributes?.State == InAppPurchaseLocalizationAttributes.StateEnum.PREPAREFORSUBMISSION);
    }

    private async Task SubmitIapsAsync()
    {
        var texts = Args.HasFlag("--texts");

        Console.WriteLine();
        Console.WriteLine("   -> In-App Purchases...");

        var appsApi = new AppsApi(Service);

        var products = await FetchAllPagesAsync<InAppPurchasesV2Response, InAppPurchaseV2>(
            appsApi.AsynchronousClient,
            appsApi.Configuration,
            () => appsApi.AppsInAppPurchasesV2GetToManyRelatedAsync(Config.AppId, limit: 200),
            r => r.Data,
            r => r.Links?.Next,
            Verbose
        );

        products = FilterByIap(products, p => p.Attributes?.ProductId);

        var ready = new List<InAppPurchaseV2>();

        foreach (var product in products)
        {
            var state = product.Attributes?.State;

            if (state is not null && AlreadyOnItsWay.Contains(state.Value))
            {
                if (Verbose)
                    Console.WriteLine($"      [SAME] {product.Attributes?.ProductId} is already {state}.");
                continue;
            }

            if (state == InAppPurchaseState.READYTOSUBMIT)
            {
                ready.Add(product);
                continue;
            }

            if (state == InAppPurchaseState.APPROVED && texts)
            {
                var pending = await PendingLocalizationsAsync(product);

                if (pending > 0)
                {
                    Console.WriteLine($"      [TEXT] {product.Attributes?.ProductId} is approved, {pending} language(s) still to be submitted.");
                    ready.Add(product);
                }
                else if (Verbose)
                    Console.WriteLine($"      [SAME] {product.Attributes?.ProductId} is already {state}.");

                continue;
            }

            if (Verbose)
                Console.WriteLine($"      [SKIP] {product.Attributes?.ProductId} is {state}, not ready to submit.");
        }

        if (ready.Count == 0)
        {
            Console.WriteLine("      nothing to submit, no product is ready.");
            return;
        }

        await SubmitProductsAsync(Service, ready, DryRun, Verbose);
    }

    /// <summary>
    /// Submits products for review, one submission each. Shared with 'locales import iaps --submit',
    /// which passes exactly the products it just changed
    /// </summary>
    public static async Task SubmitProductsAsync(AppStoreConnectConfiguration service, IEnumerable<InAppPurchaseV2> products, bool dryRun, bool verbose)
    {
        var api = new InAppPurchaseSubmissionsApi(service);

        var submitted = 0;
        var failed = 0;

        foreach (var product in products)
        {
            var productId = product.Attributes?.ProductId ?? product.Id;

            Console.WriteLine($"      [SEND] {productId}");

            if (dryRun)
            {
                submitted++;
                continue;
            }

            try
            {
                var request = new InAppPurchaseSubmissionCreateRequest(
                    new InAppPurchaseSubmissionCreateRequestData(
                        InAppPurchaseSubmissionCreateRequestData.TypeEnum.InAppPurchaseSubmissions,
                        new InAppPurchaseAppStoreReviewScreenshotCreateRequestDataRelationships(
                            inAppPurchaseV2: new InAppPurchaseAppStoreReviewScreenshotCreateRequestDataRelationshipsInAppPurchaseV2(
                                new AppRelationshipsInAppPurchasesDataInner(
                                    AppRelationshipsInAppPurchasesDataInner.TypeEnum.InAppPurchases,
                                    product.Id
                                )
                            )
                        )
                    )
                );

                await api.InAppPurchaseSubmissionsCreateInstanceAsync(request);
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
    public static Task SubmitProductsAsync(AppStoreConnectConfiguration service, IEnumerable<IapLocalesCommandBase.IapTexts> products, bool dryRun, bool verbose)
        => SubmitProductsAsync(service, products.Select(p => p.Product), dryRun, verbose);

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

        var http = new AscHttp(Service);
        var toAdd = new List<(Achievement Achievement, string VersionId)>();
        var inDraft = 0;
        var skipped = 0;

        foreach (var achievement in achievements)
        {
            var versions = await http.GetAsync($"/v2/gameCenterAchievements/{achievement.Data.Id}/versions?fields[gameCenterAchievementVersions]=version,state&limit=50");
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

        // the generated client chokes on a submission that was never submitted (no submittedDate),
        // so the draft is looked up and made by hand
        var submissions = await http.GetAsync($"/v1/reviewSubmissions?filter[app]={Config.AppId}&filter[platform]={platform}&filter[state]=READY_FOR_REVIEW&limit=50");
        var openId = (string?)(submissions["data"] as JsonArray)?.FirstOrDefault()?["id"];

        Console.WriteLine(openId is null
            ? "      [NEW]  review submission"
            : $"      [ADD]  to the open review submission {openId}");

        if (DryRun)
            return;

        var submissionId = openId;

        if (submissionId is null)
        {
            var created = await http.PostAsync("/v1/reviewSubmissions", AscHttp.Body(
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
                    var created = await http.PostAsync("/v2/gameCenterAchievementVersions", AscHttp.Body(
                        "gameCenterAchievementVersions",
                        new JsonObject { ["achievement"] = AscHttp.Link("gameCenterAchievements", achievement.Data.Id) }
                    ));
                    versionId = (string?)created["data"]?["id"] ?? throw new Exception("no version id in the response");
                }

                await http.PostAsync("/v1/reviewSubmissionItems", AscHttp.Body(
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
    private static readonly ReviewSubmissionAttributes.StateEnum[] OpenStates =
    {
        ReviewSubmissionAttributes.StateEnum.READYFORREVIEW,
        ReviewSubmissionAttributes.StateEnum.UNRESOLVEDISSUES,
    };

    private async Task SubmitVersionAsync()
    {
        Console.WriteLine();
        Console.WriteLine("   -> App store version...");

        var platform = Args.TryGetOption("--platform", "IOS").ToUpperInvariant();

        var versionsResponse = await new AppsApi(Service).AppsAppStoreVersionsGetToManyRelatedAsync(
            Config.AppId,
            filterPlatform: new List<string> { platform },
            fieldsAppStoreVersions: new List<string> { "versionString", "appVersionState", "appStoreState", "platform", "createdDate" },
            limit: 50
        );

        var version = (versionsResponse.Data ?? new())
            .OrderByDescending(v => v.Attributes?.CreatedDate ?? DateTimeOffset.MinValue)
            .FirstOrDefault(v => v.Attributes?.AppVersionState == AppVersionState.PREPAREFORSUBMISSION);

        if (version is null)
        {
            Console.WriteLine("      no version is waiting for submission.");
            return;
        }

        Console.WriteLine($"      version {version.Attributes?.VersionString}");

        var submissionsApi = new ReviewSubmissionsApi(Service);

        var open = (await submissionsApi.ReviewSubmissionsGetCollectionAsync(
                filterApp: new List<string> { Config.AppId },
                filterPlatform: new List<string> { platform },
                limit: 50,
                include: new List<string> { "items" }
            ))?.Data
            ?.FirstOrDefault(s => s.Attributes?.State is { } state && OpenStates.Contains(state));

        if (open is not null && HasVersion(open, version.Id))
        {
            Console.WriteLine("      already in the open review submission, nothing to add.");
            return;
        }

        Console.WriteLine(open is null
            ? "      [NEW]  review submission"
            : $"      [ADD]  to the open review submission {open.Id}");

        if (DryRun)
            return;

        try
        {
            var submissionId = open?.Id ?? await CreateSubmissionAsync(submissionsApi, platform);
            if (string.IsNullOrWhiteSpace(submissionId))
                return;

            await new ReviewSubmissionItemsApi(Service).ReviewSubmissionItemsCreateInstanceAsync(
                new ReviewSubmissionItemCreateRequest(
                    new ReviewSubmissionItemCreateRequestData(
                        ReviewSubmissionItemCreateRequestData.TypeEnum.ReviewSubmissionItems,
                        new ReviewSubmissionItemCreateRequestDataRelationships(
                            reviewSubmission: new ReviewSubmissionItemCreateRequestDataRelationshipsReviewSubmission(
                                new AppRelationshipsReviewSubmissionsDataInner(
                                    AppRelationshipsReviewSubmissionsDataInner.TypeEnum.ReviewSubmissions,
                                    submissionId
                                )
                            ),
                            appStoreVersion: new AppClipDefaultExperienceCreateRequestDataRelationshipsReleaseWithAppStoreVersion(
                                new AlternativeDistributionPackageCreateRequestDataRelationshipsAppStoreVersionData(
                                    AlternativeDistributionPackageCreateRequestDataRelationshipsAppStoreVersionData.TypeEnum.AppStoreVersions,
                                    version.Id
                                )
                            )
                        )
                    )
                )
            );

            await submissionsApi.ReviewSubmissionsUpdateInstanceAsync(
                submissionId,
                new ReviewSubmissionUpdateRequest(
                    new ReviewSubmissionUpdateRequestData(
                        ReviewSubmissionUpdateRequestData.TypeEnum.ReviewSubmissions,
                        submissionId,
                        new ReviewSubmissionUpdateRequestDataAttributes(submitted: true)
                    )
                )
            );

            Console.WriteLine($"      submitted, review submission {submissionId}.");
        }
        catch (Exception ex)
        {
            PrintApiError("failed to submit the app store version", ex);
        }
    }

    private async Task<string?> CreateSubmissionAsync(ReviewSubmissionsApi api, string platform)
    {
        var response = await api.ReviewSubmissionsCreateInstanceAsync(
            new ReviewSubmissionCreateRequest(
                new ReviewSubmissionCreateRequestData(
                    ReviewSubmissionCreateRequestData.TypeEnum.ReviewSubmissions,
                    new ReviewSubmissionCreateRequestDataAttributes(ParsePlatform(platform)),
                    new AccessibilityDeclarationCreateRequestDataRelationships(
                        new AccessibilityDeclarationCreateRequestDataRelationshipsApp(
                            new AccessibilityDeclarationCreateRequestDataRelationshipsAppData(
                                AccessibilityDeclarationCreateRequestDataRelationshipsAppData.TypeEnum.Apps,
                                Config.AppId
                            )
                        )
                    )
                )
            )
        );

        return response?.Data?.Id;
    }

    private static Platform ParsePlatform(string platform) => platform switch
    {
        "MAC_OS" or "MACOS" => Platform.MACOS,
        "TV_OS" or "TVOS" => Platform.TVOS,
        "VISION_OS" or "VISIONOS" => Platform.VISIONOS,
        _ => Platform.IOS,
    };

    private static bool HasVersion(ReviewSubmission submission, string versionId)
        => submission.Relationships?.Items?.Data?.Any(i => string.Equals(i.Id, versionId, StringComparison.Ordinal)) == true;
}
