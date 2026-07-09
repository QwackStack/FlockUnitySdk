# Authentication

[← Back to README](../README.md)

All auth methods live on `FlockClient.Instance.Authentication` and return a `PlayerLoginResponse`
with access and refresh tokens. The SDK stores the tokens internally.

```csharp
// Email
var response = await FlockClient.Instance.Authentication.LoginWithEmailAsync("player@example.com", "password");
var response = await FlockClient.Instance.Authentication.RegisterWithEmailAsync("player@example.com", "password", "PlayerName");

// Device
var response = await FlockClient.Instance.Authentication.LoginWithDeviceAsync("device-uuid");
var response = await FlockClient.Instance.Authentication.RegisterWithDeviceAsync("device-uuid", "PlayerName");

// Google
var response = await FlockClient.Instance.Authentication.LoginWithGoogleAsync(idToken);
var response = await FlockClient.Instance.Authentication.RegisterWithGoogleAsync(idToken, "PlayerName");

// Apple
var response = await FlockClient.Instance.Authentication.LoginWithAppleAsync(identityToken);
var response = await FlockClient.Instance.Authentication.RegisterWithAppleAsync(identityToken, "PlayerName");

// Steam
var response = await FlockClient.Instance.Authentication.LoginWithSteamAsync(sessionTicket);
var response = await FlockClient.Instance.Authentication.RegisterWithSteamAsync(sessionTicket, "PlayerName");

// Facebook (login only — no register route server-side)
var response = await FlockClient.Instance.Authentication.LoginWithFacebookAsync(facebookId);

// Discord (login only — no register route server-side)
var response = await FlockClient.Instance.Authentication.LoginWithDiscordAsync(discordId);

// Logout — clears local token state
FlockClient.Instance.Authentication.Logout();
```

> **Note on `name` during registration.** The backend enforces a **unique** display name across registered players. The `RegisterWith*` methods swallow the coded *already-registered* identity errors (`FlockErrorCode.Player*AlreadyRegistered` — email/device/OAuth) and return `null` instead of throwing; you can test for that group yourself with `ex.IsAlreadyRegistered()`.
>
> A duplicate **name** is different — it is *not* swallowed. The backend rejects it with `FlockErrorCode.PlayerNameAlreadyRegistered` (HTTP 400), surfaced as a `FlockValidationException`. If you pass a `name`, catch it and prompt for another:
>
> ```csharp
> try
> {
>     await FlockClient.Instance.Authentication.RegisterWithEmailAsync(email, password, name);
> }
> catch (FlockValidationException ex) when (ex.ErrorCode == FlockErrorCode.PlayerNameAlreadyRegistered)
> {
>     // Ask the player for a different display name.
> }
> ```
>
> To avoid the round-trip, preflight with `IsNameAvailableAsync` (below) — advisory only, since another player can still take the name between the check and the register call. Alternatively, omit `name` and let the backend assign a default.

## Name Availability

Checks whether a display name is still free before registering. Advisory only — another
player can still take the name between the check and the register call.

```csharp
bool available = await FlockClient.Instance.Authentication.IsNameAvailableAsync("PlayerName");
```

## Password Reset

Two-step email flow for email/password accounts. `ForgotPasswordAsync` returns the
backend's success flag (it never reveals whether an email is registered) and works
logged-out — it's the "I can't log in" entry point. `ResetPasswordAsync` requires the
player to be **signed in with email** (a restored email session counts; a social/device
login throws `FlockAuthException`) and throws `FlockValidationException` on a bad or
expired code.

```csharp
// Step 1 — player enters their email, backend sends a reset code
bool sent = await FlockClient.Instance.Authentication.ForgotPasswordAsync("player@example.com");

// Step 2 — player enters the emailed code plus their new password (requires email sign-in)
await FlockClient.Instance.Authentication.ResetPasswordAsync("player@example.com", code, newPassword);
```

## Email Verification

Code-based verification. Neither call enforces a sign-in client-side (the player's bearer
token is attached automatically when present). Sending is never automatic — trigger it from
your UI when verification matters to your game, and re-call it for expired or lost codes.

```csharp
await FlockClient.Instance.Authentication.SendEmailVerificationAsync();

// Player enters the emailed code
await FlockClient.Instance.Authentication.VerifyEmailAsync(code);
```

> The backend does not yet expose a readable "is verified" flag, so the SDK can't query
> verification status back. Treat verification as a one-way action for now.

## Token Revocation

`Logout()` is local-only by design (it clears token state on the device, matching the
Firebase/PlayFab convention). `RevokeTokenAsync()` goes further to allow from the client: 
it kills the player's refresh token **server-side**, so a stolen
refresh token stops working. Already-issued access tokens live out their short TTL.

```csharp
// Full sign-out: revoke server-side, then clear local state
await FlockClient.Instance.Authentication.RevokeTokenAsync();
FlockClient.Instance.Authentication.Logout();
```

Use plain `Logout()` for routine account switching; add `RevokeTokenAsync()` when the
player signs out on a shared device or you're responding to a compromised session.

## Token Refresh

The SDK silently refreshes the access token on `401` responses. You can also
trigger it manually, and listen for the case where the refresh fails (the
player must re-authenticate).

```csharp
FlockClient.Instance.OnSessionExpired += () => ShowLoginScreen();

bool refreshed = await FlockClient.Instance.RefreshTokenAsync();
```

See also: [SDK Events](events.md) for `OnAuthenticated`, `OnAuthExpired`, `OnLoggedOut`, and `OnSessionRestored`.
