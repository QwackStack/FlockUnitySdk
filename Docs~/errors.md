# Error handling

[← Back to README](../README.md)

Every SDK call **throws on failure** — there are no result/error return types. Wrap calls in `try/catch` and branch on the typed error code.

## Exception types

All SDK exceptions derive from `FlockException` (namespace `Flock.Exceptions`).

| Type | Thrown when |
|------|-------------|
| `FlockException` | Base type — catch this to handle any SDK failure. |
| `FlockAuthException` | Authentication/authorization failure (HTTP 401/403). |
| `FlockValidationException` | The request was rejected (HTTP 400/422) — bad input, business-rule violation. |
| `FlockNetworkException` | Any other non-2xx (404, 409, 5xx, …) or a transport failure (timeout, no connection). Carries `RetryAfter` on 429/503. |
| `FlockSerializationException` | A 2xx response body couldn't be parsed into the expected type (malformed/empty JSON). |

`FlockException` members:

- **`ErrorCode`** — a `FlockErrorCode` enum, the readable form of the server's machine-readable code. Use this for checks. `FlockErrorCode.Unknown` when there was no code or this SDK version predates it.
- **`Code`** — the raw wire string (e.g. `"shop.insufficient_funds"`). Keep for logging/forward-compat.
- **`StatusCode`** — the HTTP status when the error came from a server response; `null` for transport failures (an ambiguous outcome — the request may or may not have reached the server).
- **`Body`** — the raw server response body.

## Branching on an error

```csharp
try
{
    await FlockClient.Instance.Shop.PurchaseAsync(shopId, itemId);
}
catch (FlockException ex) when (ex.ErrorCode == FlockErrorCode.ShopInsufficientFunds)
{
    // The server declined — not enough funds.
}
catch (FlockException ex)
{
    Debug.LogError($"Purchase failed: {ex.ErrorCode} ({ex.Code}) — {ex.Message}");
}
```

For the register/login "this identity already belongs to an account" case (email/device/OAuth), use the grouping helper instead of listing each code:

```csharp
try
{
    await FlockClient.Instance.Authentication.RegisterWithEmailAsync(email, password);
}
catch (FlockException ex) when (ex.IsAlreadyRegistered())
{
    // Offer sign-in instead of register.
}
```

> A taken **display name** is not part of `IsAlreadyRegistered()` — it's a different fix. Catch `FlockErrorCode.PlayerNameAlreadyRegistered` and prompt for another name.

## Money safety

`AddGameFundsAsync` and shop `PurchaseAsync` never auto-retry ambiguous failures (a client timeout may have already committed on the server). They throw, and the caller decides. Only provably-unprocessed failures (HTTP 408/429) are retried automatically. See the [Shop guide](shop.md).

## Coded-error reference

`FlockErrorCode` mirrors the backend's `detail.code` set. The member name is the wire code PascalCased (`shop.insufficient_funds` → `ShopInsufficientFunds`). Unrecognized codes parse to `Unknown` while the raw string stays on `Code`.

| Enum member | Wire code |
|-------------|-----------|
| `AnalyticsCurrencyNotFound` | `analytics.currency_not_found` |
| `AnalyticsPlayerNotFound` | `analytics.player_not_found` |
| `AnalyticsSessionNotFound` | `analytics.session_not_found` |
| `AssetAssetNotFound` | `asset.asset_not_found` |
| `GameGameNotFound` | `game.game_not_found` |
| `GameMissingStudioId` | `game.missing_studio_id` |
| `GameCommandAchievementNotFound` | `game_command.achievement_not_found` |
| `GameCommandCurrencyNotFound` | `game_command.currency_not_found` |
| `GameCommandInvalidAmount` | `game_command.invalid_amount` |
| `GameCommandNotAWallet` | `game_command.not_a_wallet` |
| `GameCommandNotAnAchievementRecord` | `game_command.not_an_achievement_record` |
| `GameCommandPlayerDataNotFound` | `game_command.player_data_not_found` |
| `GameCommandPlayerDataNotLinkedToTemplate` | `game_command.player_data_not_linked_to_template` |
| `GameCommandPlayerTemplateNotFound` | `game_command.player_template_not_found` |
| `GameCommandTemplateValidationFailed` | `game_command.template_validation_failed` |
| `GameConfigConfigNotFound` | `game_config.config_not_found` |
| `GameConfigFeatureConfigNotFound` | `game_config.feature_config_not_found` |
| `GameConfigInvalidTag` | `game_config.invalid_tag` |
| `GameConfigPlayerNoGameVersion` | `game_config.player_no_game_version` |
| `GameConfigPlayerNotFound` | `game_config.player_not_found` |
| `GamePatchGameConfigNotFound` | `game_patch.game_config_not_found` |
| `GamePatchPatchNotFound` | `game_patch.patch_not_found` |
| `GameVersionGameVersionByNameNotFound` | `game_version.game_version_by_name_not_found` |
| `GameVersionGameVersionNotFound` | `game_version.game_version_not_found` |
| `LogEventGameNotFound` | `log_event.game_not_found` |
| `PlayerAppleAccountAlreadyRegistered` | `player.apple_account_already_registered` |
| `PlayerDeviceAlreadyRegistered` | `player.device_already_registered` |
| `PlayerEmailAlreadyRegistered` | `player.email_already_registered` |
| `PlayerGameJwkNotConfigured` | `player.game_jwk_not_configured` |
| `PlayerGameVersionIdRequired` | `player.game_version_id_required` |
| `PlayerGoogleAccountAlreadyRegistered` | `player.google_account_already_registered` |
| `PlayerInvalidDeviceRegistrationRequest` | `player.invalid_device_registration_request` |
| `PlayerInvalidLoginCredentials` | `player.invalid_login_credentials` |
| `PlayerInvalidRefreshToken` | `player.invalid_refresh_token` |
| `PlayerInvalidRegistrationRequest` | `player.invalid_registration_request` |
| `PlayerInvalidResetCode` | `player.invalid_reset_code` |
| `PlayerInvalidVerificationCode` | `player.invalid_verification_code` |
| `PlayerNameAlreadyRegistered` | `player.name_already_registered` |
| `PlayerNoEmailAccount` | `player.no_email_account` |
| `PlayerOauthFailed` | `player.oauth_failed` |
| `PlayerPlayerNotFound` | `player.player_not_found` |
| `PlayerSteamAccountAlreadyRegistered` | `player.steam_account_already_registered` |
| `PlayerBanPlayerNotFound` | `player_ban.player_not_found` |
| `PlayerDataNotFound` | `player_data.not_found` |
| `PlayerDataPlayerNotFound` | `player_data.player_not_found` |
| `PlayerInventoryPlayerNotFound` | `player_inventory.player_not_found` |
| `PlayerTemplateNotFound` | `player_template.not_found` |
| `PlayerTemplateNotFoundByName` | `player_template.not_found_by_name` |
| `ShopCurrencyNotHeld` | `shop.currency_not_held` |
| `ShopCurrencyTemplateNotFound` | `shop.currency_template_not_found` |
| `ShopInsufficientFunds` | `shop.insufficient_funds` |
| `ShopItemNotFound` | `shop.item_not_found` |
| `ShopPlayerNotFound` | `shop.player_not_found` |
| `ShopShopNotFound` | `shop.shop_not_found` |
| `ShopWalletNotFound` | `shop.wallet_not_found` |
| `ShopItemShopItemNotFound` | `shop_item.shop_item_not_found` |
| `ShopItemShopNotFound` | `shop_item.shop_not_found` |
