# HTTP and your own providers

[← Back to README](../README.md)

Most games never need this page: every feature calls the server for you. The SDK's HTTP layer is public for two jobs:
calling another Qwacks service as your game, and writing a provider of your own on the same retry and error rules as the
SDK's.

## Your game's identity

```csharp
FlockClient flock = FlockClient.Instance;
Dictionary<string, string> headers = flock.GetGameHeaders();   // your API key and Game Version ID
string versionName = flock.GameVersion;                        // "1.0.0", as set in Flock > Settings
string versionId = flock.GameVersionId;                        // the ID that name was resolved to
```

- `GetGameHeaders()` returns a new copy on every call: the headers that identify your game to a Qwacks service, its API
  key and Game Version ID. It never carries the player's sign-in, and changing the copy changes nothing in the client.
- **Send them only to Qwacks services.** They carry your API key, so never to your own servers or to anyone else's.
- `GameVersion` and `GameVersionId` are what the client was initialized with, so reading them makes no call; see
  [Game & Config](game-config.md).

## Requests

```csharp
// A route that answers with JSON: read it into your type.
ServiceStatus status = await FlockHttpClient.GetAsync<ServiceStatus>($"{serviceUrl}/status", headers);

// A route with nothing to read in its answer: no type argument.
await FlockHttpClient.PostAsync($"{serviceUrl}/reports", new { reason = "stuck" }, headers);
```

- The body you pass is written as JSON. Each call is sent once: nothing is retried (see below).
- **Without a type argument** (`PostAsync`, `PutAsync`, `PatchAsync`, `DeleteAsync`), any 2xx is a success, whether its
  body is empty (a `204` included) or JSON. A body that is not JSON, such as a captive portal's sign-in page, raises
  `FlockSerializationException`, since the server never answered.
- **With a type argument**, an empty body raises `FlockSerializationException`: the call exists for its answer.
- A refusal raises the same exceptions either way (401 and 403 as `FlockAuthException`); see
  [Error handling](errors.md). A cancelled token throws `OperationCanceledException`.

## Retrying the way the SDK does

```csharp
RetryHandler retries = new RetryHandler(FlockClient.Instance.RetryPolicy, new UnityFlockLogger());
await retries.ExecuteAsync(async () =>
{
    await FlockHttpClient.PostAsync($"{serviceUrl}/reports", new { reason = "stuck" }, headers);
    return true;
});
```

- `FlockClient.Instance.RetryPolicy` is a copy of the retry settings the client was initialized with (`MaxRetries`,
  `InitialDelay`, `MaxDelay`, `BackoffMultiplier`, `UseJitter`). Changing the copy changes nothing in the client.
- `RetryHandler` retries a timeout, a lost connection, a 5xx, a 408 and a 429, and waits as long as the server asks when
  it says (up to `MaxDelay`). It never retries a refused sign-in, a validation error, an answer it could not read or any
  other 4xx.
- **A call that must not happen twice** (it moves money, say) passes `retryAmbiguousFailures: false`. Then only a 408 or a
  429 is retried, since the server refused those before doing anything; a timeout or a 5xx may already have been applied,
  so it is raised rather than sent again.
- The logger prints each retry to the console; pass `null` for none.

## Uploading a file

```csharp
FlockFileUploadOutcome outcome = await FlockHttpClient.UploadFileAsync(uploadUrl, filePath, "video/mp4");
if (!outcome.IsUploaded)
    Debug.LogWarning($"Upload failed ({outcome.Result}, status {outcome.StatusCode}): {outcome.Body}");
```

- Sends the file with a `PUT`, the way a presigned storage link wants it. The file is read from disk as it is sent and is
  never whole in memory (measured with 1.5 GiB on Mono and IL2CPP), and the request carries the content type you give and
  no other header of the SDK's, so the link's signature still matches.
- Call it on the main thread. A whole upload has no time limit; one that sends no byte for 60 seconds while it waits to
  finish is given up, with `Result` set to `Timeout`.
- **Only `IsUploaded` proves the file arrived**: it is true for a 2xx from the storage and nothing else. `Result` is
  `Success` whenever an answer came back, whatever its status; `StatusCode` is that answer's status, `Body` the start of its
  text (512 characters at most), and `BytesSent` how much of the file went.
- Cancelling throws `OperationCanceledException`. Every other failure comes back in the outcome, with the reason in `Body`
  and `Result` set to `Timeout` or `ConnectionError`: no answer at all, a missing file, an address Unity cannot parse, or a
  file another program holds open.
- Nothing is retried. A presigned link expires within minutes, so ask for a fresh one before trying again.
- **WebGL cannot stream a file**, so there nothing is sent and the outcome says why, rather than the file being read into
  memory.

### Another uploader

```csharp
FlockHttpClient.UseFileUploader(new UnityWebRequestFileUploader(TimeSpan.FromMinutes(5)));   // waits longer on a stall
FlockHttpClient.UseFileUploader(new MyTestUploader());                                      // implements IFlockFileUploader
FlockHttpClient.UseFileUploader(null);                                                      // back to the platform's
```

`IFlockFileUploader` is the seam behind `UploadFileAsync`, kept apart from the request transport, so a transport of your
own needs no change for uploads. Implement it for tests or to upload your own way: it throws only when cancelled and
reports every other failure in the outcome. The platform's own is `UnityWebRequestFileUploader`; given a stall time of zero
or less, it never gives up.

## A provider of your own

```csharp
public sealed class ReportProvider : FlockProviderBase
{
    private readonly string _serviceUrl;

    public ReportProvider(string serviceUrl) : base(FlockClient.Instance)
    {
        _serviceUrl = serviceUrl;
    }

    public Task SendReportAsync(string reason, CancellationToken cancellationToken = default)
        => ExecuteWithoutResultAsync(
            () => FlockHttpClient.PostAsync($"{_serviceUrl}/reports", new { reason }, Client.GetGameHeaders(), cancellationToken),
            "Send report", cancellationToken);

    public Task<ServiceStatus> GetStatusAsync(CancellationToken cancellationToken = default)
        => ExecuteAsync(
            () => FlockHttpClient.GetAsync<ServiceStatus>($"{_serviceUrl}/status", Client.GetGameHeaders(), cancellationToken),
            "Get status", cancellationToken);
}
```

- `ExecuteAsync<T>` runs a call that returns an answer, and `ExecuteWithoutResultAsync` one that returns nothing. Both
  follow the SDK's own rules: the call is retried by the client's `RetryPolicy`, a failure that is not a `FlockException`
  is raised as a `FlockNetworkException`, and the exception's `Operation` is the name you pass (`"Send report"`), so its
  message says which call failed.
- Pass `idempotent: false` for a call that must not happen twice: only a 408 or a 429 is then retried.
- While a player is signed in, a call refused as unauthorized refreshes the player's sign-in and is tried once more. A
  refresh answered after that player signed out changes nothing, and the call is not sent again as whoever signed in next.
- A call that acts as the signed-in player through their sign-in (your service reads the player from the token) passes
  `actsForSignIn: SignInToActFor`. Then every try, retries included, is cancelled once that player signs out or another
  signs in: it throws `OperationCanceledException` rather than going out with the next player's sign-in. A call that needs
  only the game's key (as above, through `GetGameHeaders()`) leaves it out and keeps retrying.
