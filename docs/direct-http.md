# Talking to App Store Connect over direct HTTP

The generated `AppStoreConnect.Net` client is being removed. Everything goes through
`AscHttp` (src/AscHttp.cs) instead: endpoints named by path, `JsonNode` in both directions.
`Command_LocalesList` (src/commands/Command_LocalesList.cs) is the converted exemplar - when in
doubt, do what it does.

## The client

Every command already has one. `CommandBase.Initialize` builds a `protected AscHttp Http` from the
same token the library uses, so inside a command you just call it:

```csharp
var response = await Http.GetAsync($"/v1/apps/{Config.AppId}/gameCenterDetail");
var created  = await Http.PostAsync("/v1/reviewSubmissions", body);
var updated  = await Http.PatchAsync($"/v1/appInfoLocalizations/{id}", body);
await Http.DeleteAsync($"/v1/inAppPurchaseLocalizations/{id}");
```

Paths are relative to `https://api.appstoreconnect.apple.com` and carry their query string inline:
`$"/v2/inAppPurchases/{id}/inAppPurchaseLocalizations?limit=200"`. Use the exact path the library
called (they are literal strings in the generated `Api/*.cs` files) so behavior does not drift.

Outside a command, construct one directly: `new AscHttp(new AscAuth(keyId, issuerId, p8Content))`,
or `new AscHttp(configuration)` to share the token with code still on the library.

## Paging

`GetPagedAsync` follows `links.next` until there is none and hands back everything at once:

```csharp
var page = await Http.GetPagedAsync($"/v1/apps/{Config.AppId}/inAppPurchasesV2?limit=200");
// page.Data     - JsonArray, the "data" elements of every page
// page.Included - JsonArray, the "included" elements of every page
```

Ask for the biggest page (`limit=200` for most collections, `limit=50` for app store versions) like
the old code did. This replaces every `FetchAllPagesAsync` call. Note one difference to keep in
mind: the old helper swallowed an error on a later page and returned what it had; `GetPagedAsync`
throws, and the command's own try/catch reports it.

## Reading responses

Indexers all the way down, cast at the leaf, null-conditional at every hop:

```csharp
var productId = (string?)product?["attributes"]?["productId"];
var state     = (string?)product?["attributes"]?["state"];          // "MISSING_METADATA"
var imageId   = (string?)loc?["relationships"]?["gameCenterAchievementImage"]?["data"]?["id"];
var items     = response["data"] as JsonArray ?? new JsonArray();
var next      = (string?)response["links"]?["next"];
var count     = (int?)op["length"] ?? 0;
```

Two things the generated client hid:

- **Enum names.** The api returns `MISSING_METADATA`; the library's enums printed
  `MISSINGMETADATA`. Anywhere a state was printed, strip the underscores (see `StateName` in
  Command_LocalesList) so the output text stays identical.
- **Types are just strings.** A resource's `"type"` is the camelCase plural from the api docs:
  `"apps"`, `"inAppPurchases"`, `"gameCenterAchievementImages"`.

## Writing requests

The body helpers on `AscHttp` cover the three shapes the api has:

```csharp
AscHttp.Body("reviewSubmissionItems", new JsonObject          // create: type + relationships (+ attributes)
{
    ["reviewSubmission"] = AscHttp.Link("reviewSubmissions", submissionId),
});

AscHttp.BodyWithAttributes("inAppPurchaseLocalizations", id,  // update: type + id + attributes
    new JsonObject { ["name"] = name, ["description"] = description });

AscHttp.Link("apps", Config.AppId)                            // a to-one relationship
AscHttp.LinkMany("territories", ids)                          // a to-many relationship
```

## Errors

A non-2xx response throws `AscApiException` with `StatusCode`, the raw `ResponseBody`, and
`FirstErrorDetail` (`errors[0].detail`, the sentence meant for a human). The old patterns map over:

```csharp
catch (AscApiException ex) when (ex.StatusCode == 404)   // was: ApiException.ErrorCode == 404
```

`PrintApiError` in AppMetadataCommandBase already knows the type, so an outer
`catch (Exception ex)` that funnels into it needs no change.

## Uploads

`AscUpload` (src/AscUpload.cs) is the `JsonNode` port of MediaUpload, same three steps:

```csharp
var ops = created["data"]?["attributes"]?["uploadOperations"];
var chunks = await AscUpload.SendAllChunksAsync(ops, bytes);
// then PATCH the asset with uploaded=true (and AscUpload.Checksum(bytes) where the endpoint wants one)
```

`AscUpload.DownloadUrl(assetNode)` fills the `{w}`/`{h}`/`{f}` placeholders of an `imageAsset` to
get already-uploaded bytes back out.

## The rule

**Output text must not change.** Every `Console.WriteLine` - wording, spacing, alignment widths,
the order lines appear in - stays byte-for-byte as it was. The conversion swaps the transport, not
the behavior: same endpoints, same page sizes, same filters, same error wording. When the library
did something odd (the underscore-stripped enums), reproduce the oddity.
