# Authentication

[← Back to README](../README.md)

All auth methods live on `FlockClient.Instance.Authentication`. A login or register returns a `PlayerLoginResponse` (the
player's id and tokens), and the SDK keeps the tokens itself, so you rarely need to read them. Auth methods throw on
failure; they never return an error value.

## Signing a player in

A login never creates an account. On a device (or with an email) Flock has never seen, `LoginWithDeviceAsync` fails with
`FlockErrorCode.PlayerInvalidLoginCredentials`; register then. A successful register signs the player in too.

```csharp
using System.Threading.Tasks;
using Flock;
using Flock.Exceptions;
using Flock.Models;
using Flock.Providers;

public static class SignIn
{
    // Signs in with this device, registering it the first time. With no id, both calls use Authentication.ThisDeviceId.
    public static async Task WithThisDeviceAsync()
    {
        FlockAuthProvider auth = FlockClient.Instance.Authentication;
        try
        {
            await auth.LoginWithDeviceAsync();
        }
        catch (FlockException ex) when (ex.ErrorCode == FlockErrorCode.PlayerInvalidLoginCredentials)
        {
            // First launch on this device: make the account (the name is optional).
            await auth.RegisterWithDeviceAsync();
        }
    }

    // Signs in with an email, making the account when none has that email. False means the password was wrong.
    public static async Task<bool> WithEmailAsync(string email, string password, string name)
    {
        FlockAuthProvider auth = FlockClient.Instance.Authentication;
        try
        {
            await auth.LoginWithEmailAsync(email, password);
            return true;
        }
        catch (FlockException ex) when (ex.ErrorCode == FlockErrorCode.PlayerInvalidLoginCredentials)
        {
            // No account with this email, or a wrong password: try to make the account.
            PlayerLoginResponse created = await auth.RegisterWithEmailAsync(email, password, name);
            // null: the email already has an account, so the password was wrong.
            return created != null;
        }
    }
}
```

**Which device id.** With no id, `LoginWithDeviceAsync`, `RegisterWithDeviceAsync` and `LinkDeviceAsync` use
`Authentication.ThisDeviceId`: an id the SDK makes the first time it is needed and keeps in the game's saved files
(`Flock/device_id.txt` under `Application.persistentDataPath`; the browser's storage on WebGL), so the same player comes
back on every launch. `Logout()` keeps it. A new one is made after a reinstall, after the app's data is cleared or the file
is deleted, and on WebGL after the site's data is cleared, in a private window, or when the game is served from another
page address; restoring a phone from a backup can bring the old one back. With a new id the player signs in as somebody
new, with none of their progress; to keep the player across a reinstall or a new device, [link](#account-linking) an
email or a platform account to them.

Pass an id of your own only in two cases. An id you pass is sent as it is. An empty string is not the same as leaving the
id out: the sign-in and registration calls send it and the server refuses it, and `LinkDeviceAsync` refuses it before
sending.

- **Your game already signs players in with an id of its own** (`SystemInfo.deviceUniqueIdentifier`, or an id kept in
  PlayerPrefs): keep passing that same id. Leaving it out would sign every existing player in to a new, empty account.
- **Two copies of the game on one computer** (the Editor and a build, say) share the kept id, so they are one player.
  Pass another id in one of them, for example one you made with `Guid.NewGuid().ToString()` and kept.

**After the first launch** the SDK brings the player back by itself: with **Auto-Initialize On Load** on (the default) it
restores the saved sign-in at startup, so check `FlockClient.Instance.IsAuthenticated` (or handle
`FlockEvents.OnSessionRestored`) before showing a login screen. See [Where the sign-in is kept](#where-the-sign-in-is-kept).

## Other sign-in providers

Google, Apple and Steam sign-ins take the token that platform's own SDK gives your game (a Google ID token, Sign in with
Apple's identity token, a Steam session ticket); Flock checks it with the platform. Facebook and Discord have a login only,
no register.

```csharp
using System.Threading.Tasks;
using Flock;
using Flock.Models;
using Flock.Providers;

public static class PlatformSignIn
{
    public static async Task<PlayerLoginResponse> GoogleAsync(string idToken, string playerName)
    {
        FlockAuthProvider auth = FlockClient.Instance.Authentication;
        // null when this Google account already has a player: log in instead.
        PlayerLoginResponse response = await auth.RegisterWithGoogleAsync(idToken, playerName);
        return response ?? await auth.LoginWithGoogleAsync(idToken);
    }

    public static async Task<PlayerLoginResponse> AppleAsync(string identityToken, string playerName)
    {
        FlockAuthProvider auth = FlockClient.Instance.Authentication;
        PlayerLoginResponse response = await auth.RegisterWithAppleAsync(identityToken, playerName);
        return response ?? await auth.LoginWithAppleAsync(identityToken);
    }

    public static async Task<PlayerLoginResponse> SteamAsync(string sessionTicket, string playerName)
    {
        FlockAuthProvider auth = FlockClient.Instance.Authentication;
        PlayerLoginResponse response = await auth.RegisterWithSteamAsync(sessionTicket, playerName);
        return response ?? await auth.LoginWithSteamAsync(sessionTicket);
    }

    // Facebook and Discord: login only.
    public static Task<PlayerLoginResponse> FacebookAsync(string facebookId)
    {
        return FlockClient.Instance.Authentication.LoginWithFacebookAsync(facebookId);
    }

    public static Task<PlayerLoginResponse> DiscordAsync(string discordId)
    {
        return FlockClient.Instance.Authentication.LoginWithDiscordAsync(discordId);
    }

    // Signs the player out on this device (no network call).
    public static void SignOut()
    {
        FlockClient.Instance.Authentication.Logout();
    }
}
```

> **Note on `name` during registration.** The backend enforces a **unique** display name across registered players. The `RegisterWith*` methods swallow the coded *already-registered* identity errors (`FlockErrorCode.Player*AlreadyRegistered` — email/device/OAuth) and return `null` instead of throwing; you can test for that group yourself with `ex.IsAlreadyRegistered()`.
>
> A duplicate **name** is different — it is *not* swallowed. The backend rejects it with `FlockErrorCode.PlayerNameAlreadyRegistered` (HTTP 400), surfaced as a `FlockValidationException`. If you pass a `name`, catch it and prompt for another:
>
> ```csharp
> using System.Threading.Tasks;
> using Flock;
> using Flock.Exceptions;
>
> public static class Registration
> {
>     // False means the name is taken: ask the player for another one.
>     public static async Task<bool> RegisterAsync(string email, string password, string name)
>     {
>         try
>         {
>             await FlockClient.Instance.Authentication.RegisterWithEmailAsync(email, password, name);
>             return true;
>         }
>         catch (FlockValidationException ex) when (ex.ErrorCode == FlockErrorCode.PlayerNameAlreadyRegistered)
>         {
>             return false;
>         }
>     }
> }
> ```
>
> To avoid the round-trip, preflight with `IsNameAvailableAsync` (below) — advisory only, since another player can still take the name between the check and the register call. Alternatively, omit `name` and let the backend assign a default.

## Name Availability

Checks whether a display name is still free before registering. Advisory only — another
player can still take the name between the check and the register call.

```csharp
using System.Threading.Tasks;
using Flock;

public static class NameCheck
{
    public static Task<bool> IsFreeAsync(string playerName)
    {
        return FlockClient.Instance.Authentication.IsNameAvailableAsync(playerName);
    }
}
```

## Account Linking

Attaches extra credentials to the **signed-in** player, so one account can be reached
several ways — the "I played as a guest, now let me keep my progress" flow. Every call
here requires a signed-in player and throws `FlockAuthException` if there isn't one.

Each method returns the player's **full updated credential list**, so a link or unlink
doubles as a refresh — you rarely need `GetLinkedAccountsAsync` right after one.

```csharp
using System.Collections.Generic;
using System.Threading.Tasks;
using Flock;
using Flock.Models;
using Flock.Providers;
using UnityEngine;

public static class AccountSettings
{
    // What is attached right now.
    public static async Task ShowLinkedAccountsAsync()
    {
        List<PlayerLinkedAccount> accounts = await FlockClient.Instance.Authentication.GetLinkedAccountsAsync();
        foreach (PlayerLinkedAccount account in accounts)
            Debug.Log($"{account.ProviderType}: {account.Email} (verified: {account.EmailVerified})");
    }

    // Attach an email and password to a guest (device) account.
    public static Task<List<PlayerLinkedAccount>> AddEmailAsync(string email, string password)
    {
        return FlockClient.Instance.Authentication.LinkEmailAsync(email, password);
    }

    // Attach this device (its kept id, Authentication.ThisDeviceId) and platform accounts: each platform takes the token
    // you would pass to its Login call.
    public static async Task AddPlatformAccountsAsync(string googleIdToken, string appleIdentityToken,
        string steamSessionTicket, string facebookToken, string discordToken)
    {
        FlockAuthProvider auth = FlockClient.Instance.Authentication;
        await auth.LinkDeviceAsync();
        await auth.LinkGoogleAsync(googleIdToken);
        await auth.LinkAppleAsync(appleIdentityToken);
        await auth.LinkSteamAsync(steamSessionTicket);
        await auth.LinkFacebookAsync(facebookToken);
        await auth.LinkDiscordAsync(discordToken);
    }

    // Detach one: pass account.ProviderType straight through.
    public static Task<List<PlayerLinkedAccount>> RemoveAsync(FlockCredentialProvider provider)
    {
        return FlockClient.Instance.Authentication.UnlinkAsync(provider);
    }
}
```

Two failures are worth handling explicitly:

```csharp
using System.Threading.Tasks;
using Flock;
using Flock.Exceptions;

public static class LinkFailures
{
    public static async Task LinkGoogleAsync(string idToken)
    {
        try
        {
            await FlockClient.Instance.Authentication.LinkGoogleAsync(idToken);
        }
        catch (FlockException ex) when (ex.ErrorCode == FlockErrorCode.PlayerAccountAlreadyLinked)
        {
            // That Google account belongs to a different player. The SDK has no merge flow:
            // offer to sign in as that player instead (and lose this account's progress), or cancel.
        }
    }

    public static async Task UnlinkEmailAsync()
    {
        try
        {
            await FlockClient.Instance.Authentication.UnlinkAsync(FlockCredentialProvider.Email);
        }
        catch (FlockException ex) when (ex.ErrorCode == FlockErrorCode.PlayerCannotUnlinkLastCredential)
        {
            // The server never lets a player remove their only way back in. Grey out the last row.
        }
    }
}
```

`FlockEvents.OnAccountLinked` / `OnAccountUnlinked` fire on success with the provider that
changed — useful for refreshing an account-settings screen from one place.

> Credential state is **never cached**: every call goes to the server, the same rule the SDK
> applies to bans and inventory. Linking is also never queued offline — a link that may or may
> not have landed is never re-sent, because the retry would come back as `account_already_linked`.

## Password Reset

Two-step email flow for email/password accounts. `ForgotPasswordAsync` returns the
backend's success flag (it never reveals whether an email is registered) and works
signed out — it's the "I can't log in" entry point. `ResetPasswordAsync` works signed out
too: the email and the emailed code name the account. A bad or expired code throws
`FlockValidationException`.

```csharp
using System.Threading.Tasks;
using Flock;

public static class PasswordReset
{
    // Step 1: the player enters their email, and Flock emails them a reset code.
    public static Task<bool> SendCodeAsync(string email)
    {
        return FlockClient.Instance.Authentication.ForgotPasswordAsync(email);
    }

    // Step 2: the player enters the emailed code and their new password.
    public static Task ResetAsync(string email, string code, string newPassword)
    {
        return FlockClient.Instance.Authentication.ResetPasswordAsync(email, code, newPassword);
    }
}
```

## Email Verification

Code-based verification. Neither call needs a sign-in (the player's sign-in is sent
automatically when there is one). Sending is never automatic — trigger it from
your UI when verification matters to your game, and call it again for expired or lost codes.

```csharp
using System.Threading.Tasks;
using Flock;

public static class EmailVerification
{
    public static Task SendCodeAsync()
    {
        return FlockClient.Instance.Authentication.SendEmailVerificationAsync();
    }

    // The player enters the emailed code.
    public static Task VerifyAsync(string code)
    {
        return FlockClient.Instance.Authentication.VerifyEmailAsync(code);
    }
}
```

> There is no dedicated "is verified" call, but `GetLinkedAccountsAsync()` reports
> `EmailVerified` per credential — read the email entry from that list if you need the flag.

## Where the sign-in is kept

The SDK saves the sign-in through the platform's own store (the Android Keystore, the iOS and macOS Keychain, Windows'
data protection, the browser's storage on WebGL, PlayerPrefs in the Editor and elsewhere), so `TryRestoreSessionAsync`
brings the same player back on the next launch. The automatic start and `FlockBootstrap` call it for you. `Logout()`
deletes the saved sign-in. `FlockClient.Shutdown()` only lets go of this client's tokens and keeps the saved sign-in, so the
next `FlockClient.Create` restores the same player.

Each game keeps its own saved sign-in, named by its backend and API key, so a project that switches to another game (or
another backend) never restores a player of the old one. The sign-in an earlier version kept for every game is taken over by
the first game that reads one, so updating signs nobody out; on macOS it is left where it was (the login Keychain is shared by
every app of the user), and the player signs in once more.

To keep tokens somewhere else, start the SDK yourself ([Code-Based Configuration](../README.md#code-based-configuration))
and set `FlockInitConfig.TokenStore` to a class of your own deriving from `ITokenStore` before `FlockClient.Create`:

```csharp
using Flock;
using Flock.Auth;
using Flock.Config;
using UnityEngine;

public class MyTokenStore : ITokenStore
{
    public override void Save(string accessToken, string refreshToken) { /* write both somewhere safe */ }
    public override StoredTokens Load() { /* read them back; null when none is saved */ return null; }
    public override void Clear() { /* delete them */ }
}

public class FlockStartupWithMyTokenStore : MonoBehaviour
{
    private void Awake()
    {
        FlockInitConfig config = Resources.Load<FlockConfigAsset>("FlockConfig").ToInitConfig();
        config.TokenStore = new MyTokenStore();
        FlockClient.Create(config);
    }
}
```

## Token Revocation

`Logout()` is local-only by design: it clears the sign-in on this device and tells the server nothing.
`RevokeTokenAsync()` goes further: it kills the player's refresh token **on the server**, so a stolen
refresh token stops working. Access tokens already issued keep working until they expire, a short while later.

```csharp
using System.Threading.Tasks;
using Flock;

public static class FullSignOut
{
    // Revoke on the server, then clear the sign-in on this device.
    public static async Task SignOutEverywhereAsync()
    {
        await FlockClient.Instance.Authentication.RevokeTokenAsync();
        FlockClient.Instance.Authentication.Logout();
    }
}
```

Use plain `Logout()` for routine account switching; add `RevokeTokenAsync()` when the
player signs out on a shared device or you're responding to a compromised session.

## Token Refresh

The SDK silently refreshes the access token when the server answers `401`. A `403` is never answered with a refresh: it means the
player may not do what was asked (for example, a player who is not the host), not that the sign-in lapsed. You can also
refresh yourself, and listen for the case where the refresh fails (the
player must sign in again). A refresh that answers after the player signed out,
or after another player signed in, is ignored, so it never signs the earlier player
back in. The server takes only the newest refresh token of each player, so a refresh that answers after
`FlockClient.Shutdown()` still saves its new tokens for the next `Create`, while the saved sign-in is that player's. A
refresh refused because another run of the game sharing the saved sign-in renewed it first carries on with that saved
sign-in, when it is the same player's, instead of signing the player out. When Flock is started again while a refresh is
waiting for its answer, the next client's refresh with the same refresh token waits for that answer and takes the sign-in
it saved, rather than sending the token a second time.

A client that has shut down keeps no sign-in and announces nothing: a login answered after `FlockClient.Shutdown()` fails
with a `FlockException` saying Flock was shut down before the server answered (sign the player in again once Flock is
initialized), and a restore, link or unlink that finishes after it raises no event for the next client's listeners.

```csharp
using System.Threading.Tasks;
using Flock;
using Flock.Exceptions;
using UnityEngine;

public class SignInWatcher : MonoBehaviour
{
    private void OnEnable()
    {
        FlockEvents.OnAuthExpired += HandleAuthExpired;
    }

    private void OnDisable()
    {
        FlockEvents.OnAuthExpired -= HandleAuthExpired;
    }

    // The refresh failed: the player must sign in again.
    private void HandleAuthExpired()
    {
        Debug.Log("Show the login screen");
    }

    // Refreshes now; false when the server refused or could not be reached.
    public async Task<bool> RefreshNowAsync()
    {
        try
        {
            return await FlockClient.Instance.RefreshTokenAsync();
        }
        catch (FlockAuthException)
        {
            return false;   // No saved sign-in to renew: nobody is signed in, so show the login screen.
        }
    }
}
```

`RefreshTokenAsync` returns false when the refresh fails, and throws `FlockAuthException` when there is no saved sign-in
to renew (nobody signed in, or the player signed out).

`FlockEvents.OnAuthExpired` is raised at the same moment as `FlockClient.Instance.OnSessionExpired`; the `FlockEvents`
one can be subscribed to before the SDK starts.

See also: [SDK Events](events.md) for `OnAuthenticated`, `OnAuthExpired`, `OnLoggedOut`, and `OnSessionRestored`.
