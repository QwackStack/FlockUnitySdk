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

> **Note on `name` during registration.** The backend enforces a **unique** display name across registered players. `IsAlreadyRegisteredError` swallows the backend's coded already-registered errors (`FlockErrorCode.Player*AlreadyRegistered` — email/device/OAuth) and returns `null` from `RegisterWith*` instead of throwing. A duplicate **name**, however, isn't coded yet — the backend currently surfaces it as an unhandled `500`, so it is *not* swallowed (see **Backend backlog / known constraints** in [ARCHITECTURE.md](../ARCHITECTURE.md)).
>
> Until the backend ships a structured "name taken" error code (or a name-availability check), the recommended path is to **pass `null` (or omit `name`)** and let the backend assign a default. If you need a display name, collect it on a separate post-registration screen where retrying on collision is natural UX.

## Token Refresh

The SDK silently refreshes the access token on `401` responses. You can also
trigger it manually, and listen for the case where the refresh fails (the
player must re-authenticate).

```csharp
FlockClient.Instance.OnSessionExpired += () => ShowLoginScreen();

bool refreshed = await FlockClient.Instance.RefreshTokenAsync();
```

See also: [SDK Events](events.md) for `OnAuthenticated`, `OnAuthExpired`, `OnLoggedOut`, and `OnSessionRestored`.
