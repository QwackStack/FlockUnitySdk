# Error handling

[← Back to README](../README.md)

Every SDK call **throws on failure** — there are no result/error return types. Wrap calls in `try/catch` and branch on the typed error code.

## Exception types

All SDK exceptions derive from `FlockException` (namespace `Flock.Exceptions`).

| Type | Thrown when |
|------|-------------|
| `FlockException` | Base type — catch this to handle any SDK failure. |
| `FlockAuthException` | Authentication failure (HTTP 401, which the SDK first answers with a token refresh) or a permission refusal (HTTP 403, which it never does). |
| `FlockValidationException` | The request was rejected (HTTP 400/422) — bad input, business-rule violation. |
| `FlockNetworkException` | Any other non-2xx (404, 409, 5xx, …) or a transport failure (timeout, no connection). Carries `RetryAfter` on 429/503. |
| `FlockSerializationException` | A 2xx response body couldn't be parsed into the expected type (malformed/empty JSON). |

`FlockException` members:

- **`ErrorCode`** — a `FlockErrorCode` enum, the readable form of the server's machine-readable code. Use this for checks. `FlockErrorCode.Unknown` when there was no code or this SDK version predates it.
- **`Code`** — the raw wire string (e.g. `"shop.insufficient_funds"`). Keep for logging/forward-compat.
- **`StatusCode`** — the HTTP status when the error came from a server response; `null` for transport failures (an ambiguous outcome — the request may or may not have reached the server).
- **`ServerMessage`** — the server's own reason (`"Invalid login credentials"`); `null` when the body carried none.
- **`Hint`** — how to fix it: the advice for the code, or for the kind of failure when the code has none (no answer in time, no connection, an answer that did not come from Flock's game API, a server failure). Never empty.
- **`Operation`** — the SDK call that failed (`"Device login"`), stamped by the provider that issued it.
- **`Message`** — all of the above composed into one line (see below).
- **`Body`** — the raw server response body.

## What the message says

`Message` names the call, the server's reason, the coded identifier, the status — then a `Fix:` line:

```
Device login failed: Invalid login credentials [player.invalid_login_credentials, HTTP 400]
Fix: This device is not registered yet. Call Authentication.RegisterWithDeviceAsync(deviceId) once to create the account, then Authentication.LoginWithDeviceAsync(deviceId) on later launches.
```

The fix is specific to context where one code means different things: the same `player.invalid_login_credentials` on an *email* sign-in says the password was wrong instead.

A failure with no server reason reads as the SDK's plain cause, and still ends with its fix. A failure the SDK finds itself, before sending anything, says what is wrong and what to do about it:

```
Shop Item ID is empty
Fix: Pass a Shop Item ID; this call cannot run without one.
```

Every failure Flock's game API sends carries a code, so an answer without one came from something else — a proxy, a captive portal, another server — and the fix says so. A `request.not_found` code whose reason is a bare `Not Found` means no route answers at that address (any other reason names a record the route did not find): the API URL in Flock > Settings must be the server's address alone, such as `https://api.example.com`, with no `/v1` or other path after it.

`FlockErrorHints.For(errorCode)` returns the same next-step text for any code, if you want it in your own debug UI. **It is written for developers, not players** — it names SDK methods and dashboard steps, so write separate player-facing copy.

> Log `ex.Message` to see the composed line; log the exception itself (`Debug.LogException(ex)`) to also get the type, stack, and raw response body that `ToString()` appends.

## Branching on an error

```csharp
try
{
    await FlockClient.Instance.Shop.PurchaseAsync(shopItemId);
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

For the "this identity already belongs to an account" case (email/device/OAuth), use the grouping helper instead of listing each code:

```csharp
catch (FlockException ex) when (ex.IsAlreadyRegistered())
{
    // Offer sign-in instead of register.
}
```

> **`RegisterWith*` will not reach that catch.** Those methods swallow the already-registered codes and return `null` instead of throwing, so test the return value rather than catching:
>
> ```csharp
> FlockAuthProvider auth = FlockClient.Instance.Authentication;
>
> // null means "this identity already has an account" — created nothing, signed in nobody.
> if (await auth.RegisterWithDeviceAsync(deviceId) == null)
>     await auth.LoginWithDeviceAsync(deviceId);
> ```
>
> A *successful* register does sign the player in, so the login call is only needed on the `null` path. `IsAlreadyRegistered()` remains useful on calls that don't swallow, such as the `Link*Async` methods.

> A taken **display name** is not part of `IsAlreadyRegistered()` — it's a different fix. Catch `FlockErrorCode.PlayerNameAlreadyRegistered` and prompt for another name.

## One code, several causes

A few codes, all from Flock's payment and purchase routes (redeeming a store receipt, opening a web checkout, reading a
purchase, consuming an inventory entry), cover more than one cause. The server's reason (`ServerMessage`) tells them apart, and
the `Hint` covers each, so read the reason before deciding what to tell the player:

| Code | Causes |
|------|--------|
| `PaymentProviderReceiptInvalid` | The store does not know the receipt or refunded it, or the receipt lacks the store's ids; the store's answer could not be read (send the same receipt again later). An older Flock server also sends it for three causes newer ones give codes of their own: the store refused the game's own keys (`Apple returned 401`, `Google rejected the service account`), the store was busy, down or out of reach (a 429 or 5xx; HTTP 502), and a Google payment still pending (`purchase state is 2`); send the same receipt again later for those last two |
| `PaymentProviderReceiptBelongsToAnotherPlayer` | The store purchase was started for another player, or another player already redeemed the receipt (HTTP 403) |
| `PaymentProviderReceiptProductMismatch` | The shop item has no product id for that store, or the receipt is for another product |
| `PaymentProviderProviderNotFound` | The provider was never connected, or it is paused |
| `PaymentProviderCredentialsUnreadable` | The keys stored for the provider cannot be read, or cannot be used (HTTP 503; a setting of the game, so trying again does not help) |
| `ShopRealCurrencySettlementUnavailable` | No web checkout provider is connected and active (HTTP 422), or the Flock server has no address for payment confirmations (HTTP 503) |
| `ShopWebhookProviderUnknown` | The provider named does not open web checkouts, or several are active and none was named |
| `ShopPurchaseNotFound` | No purchase of the signed-in player has that id (another player's reads the same), or purchases were listed by a status the server does not have (HTTP 422) |
| `PlayerInventoryInventoryEntryNotFound` | No entry has that id in this game, or, while a player is signed in, it is another player's entry |

Two payment codes are never final: `PaymentProviderStoreUnavailable` (the store could not be asked) and
`PaymentProviderReceiptPending` (Google is still waiting for the player's payment). Leave the store's purchase unfinished and
send the same receipt again later. `PaymentProviderStoreCredentialsRejected` is the studio's to fix (the store refused the
game's own keys); the receipt goes through once the keys are fixed.

## Money safety

`AddGameFundsAsync` and shop `PurchaseAsync` never auto-retry ambiguous failures (a client timeout may have already committed on the server). They throw, and the caller decides. Only provably-unprocessed failures (HTTP 408/429) are retried automatically. See the [Shop guide](shop.md).

## Coded-error reference

`FlockErrorCode` mirrors the backend's `detail.code` set. The member name is the wire code PascalCased (`shop.insufficient_funds` → `ShopInsufficientFunds`). Unrecognized codes parse to `Unknown` while the raw string stays on `Code`.

| Enum member | Wire code |
|-------------|-----------|
| `AnalyticsCurrencyNotFound` | `analytics.currency_not_found` |
| `AnalyticsInvalidCurrencyId` | `analytics.invalid_currency_id` |
| `AnalyticsPlayerNotFound` | `analytics.player_not_found` |
| `AnalyticsSessionNotFound` | `analytics.session_not_found` |
| `AssetAssetNotFound` | `asset.asset_not_found` |
| `GameGameNotFound` | `game.game_not_found` |
| `GameInvalidApiKey` | `game.invalid_api_key` |
| `GameMissingApiKey` | `game.missing_api_key` |
| `GameMissingStudioId` | `game.missing_studio_id` |
| `GameCommandAchievementNotFound` | `game_command.achievement_not_found` |
| `GameCommandCurrencyNotFound` | `game_command.currency_not_found` |
| `GameCommandInvalidAmount` | `game_command.invalid_amount` |
| `GameCommandNotAWallet` | `game_command.not_a_wallet` |
| `GameCommandNotAnAchievementRecord` | `game_command.not_an_achievement_record` |
| `GameCommandPlayerDataNotFound` | `game_command.player_data_not_found` |
| `GameCommandPlayerDataNotLinkedToTemplate` | `game_command.player_data_not_linked_to_template` |
| `GameCommandPlayerTemplateNotFound` | `game_command.player_template_not_found` |
| `GameCommandRateLimited` | `game_command.rate_limited` |
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
| `LeaderboardNotFound` | `leaderboard.not_found` |
| `LogEventGameNotFound` | `log_event.game_not_found` |
| `MatchmakingAlreadyQueued` | `matchmaking.already_queued` |
| `MatchmakingMatchNotFound` | `matchmaking.match_not_found` |
| `MatchmakingNotPartyLeader` | `matchmaking.not_party_leader` |
| `MatchmakingPartyTooLarge` | `matchmaking.party_too_large` |
| `MatchmakingPlayerNotEligible` | `matchmaking.player_not_eligible` |
| `MatchmakingQueueNotFound` | `matchmaking.queue_not_found` |
| `MatchmakingSearchInputRequired` | `matchmaking.search_input_required` |
| `MatchmakingTicketNotCancelable` | `matchmaking.ticket_not_cancelable` |
| `MatchmakingTicketNotFound` | `matchmaking.ticket_not_found` |
| `MatchmakingVersionMismatch` | `matchmaking.version_mismatch` |
| `MultiplayerAlreadyInSession` | `multiplayer.already_in_session` |
| `MultiplayerInvalidJoinCode` | `multiplayer.invalid_join_code` |
| `MultiplayerInvalidJoinToken` | `multiplayer.invalid_join_token` |
| `MultiplayerJoinCodeUnavailable` | `multiplayer.join_code_unavailable` |
| `MultiplayerMintRateLimited` | `multiplayer.mint_rate_limited` |
| `MultiplayerNotAParticipant` | `multiplayer.not_a_participant` |
| `MultiplayerNotHost` | `multiplayer.not_host` |
| `MultiplayerSeatMintLimit` | `multiplayer.seat_mint_limit` |
| `MultiplayerSessionFull` | `multiplayer.session_full` |
| `MultiplayerSessionNotFound` | `multiplayer.session_not_found` |
| `MultiplayerTargetNotAParticipant` | `multiplayer.target_not_a_participant` |
| `MultiplayerVersionMismatch` | `multiplayer.version_mismatch` |
| `NotificationTemplateNotFound` | `notification_template.not_found` |
| `PartyAlreadyInParty` | `party.already_in_party` |
| `PartyCannotKickLeader` | `party.cannot_kick_leader` |
| `PartyFull` | `party.full` |
| `PartyInvalidInviteCode` | `party.invalid_invite_code` |
| `PartyNotAMember` | `party.not_a_member` |
| `PartyNotFound` | `party.not_found` |
| `PartyNotPartyLeader` | `party.not_party_leader` |
| `PartyTargetNotAMember` | `party.target_not_a_member` |
| `PaymentProviderCheckoutFailed` | `payment_provider.checkout_failed` |
| `PaymentProviderCredentialsUnreadable` | `payment_provider.credentials_unreadable` |
| `PaymentProviderProviderNotFound` | `payment_provider.provider_not_found` |
| `PaymentProviderProviderNotSupportedHere` | `payment_provider.provider_not_supported_here` |
| `PaymentProviderReceiptBelongsToAnotherPlayer` | `payment_provider.receipt_belongs_to_another_player` |
| `PaymentProviderReceiptInvalid` | `payment_provider.receipt_invalid` |
| `PaymentProviderReceiptPending` | `payment_provider.receipt_pending` |
| `PaymentProviderReceiptProductMismatch` | `payment_provider.receipt_product_mismatch` |
| `PaymentProviderStoreCredentialsRejected` | `payment_provider.store_credentials_rejected` |
| `PaymentProviderStoreUnavailable` | `payment_provider.store_unavailable` |
| `PlayerAccountAlreadyLinked` | `player.account_already_linked` |
| `PlayerAccountNotLinked` | `player.account_not_linked` |
| `PlayerAppleAccountAlreadyRegistered` | `player.apple_account_already_registered` |
| `PlayerCannotUnlinkLastCredential` | `player.cannot_unlink_last_credential` |
| `PlayerDeviceAlreadyRegistered` | `player.device_already_registered` |
| `PlayerEmailAlreadyRegistered` | `player.email_already_registered` |
| `PlayerExpiredToken` | `player.expired_token` |
| `PlayerGameJwkNotConfigured` | `player.game_jwk_not_configured` |
| `PlayerGameVersionIdRequired` | `player.game_version_id_required` |
| `PlayerGoogleAccountAlreadyRegistered` | `player.google_account_already_registered` |
| `PlayerInvalidDeviceRegistrationRequest` | `player.invalid_device_registration_request` |
| `PlayerInvalidLinkRequest` | `player.invalid_link_request` |
| `PlayerInvalidLoginCredentials` | `player.invalid_login_credentials` |
| `PlayerInvalidRefreshToken` | `player.invalid_refresh_token` |
| `PlayerInvalidRegistrationRequest` | `player.invalid_registration_request` |
| `PlayerInvalidResetCode` | `player.invalid_reset_code` |
| `PlayerInvalidToken` | `player.invalid_token` |
| `PlayerInvalidVerificationCode` | `player.invalid_verification_code` |
| `PlayerMissingToken` | `player.missing_token` |
| `PlayerNameAlreadyRegistered` | `player.name_already_registered` |
| `PlayerNoEmailAccount` | `player.no_email_account` |
| `PlayerOauthFailed` | `player.oauth_failed` |
| `PlayerPlayerNotFound` | `player.player_not_found` |
| `PlayerSteamAccountAlreadyRegistered` | `player.steam_account_already_registered` |
| `PlayerBanPlayerNotFound` | `player_ban.player_not_found` |
| `PlayerDataNotFound` | `player_data.not_found` |
| `PlayerDataPlayerNotFound` | `player_data.player_not_found` |
| `PlayerInventoryAlreadyUsed` | `player_inventory.already_used` |
| `PlayerInventoryInventoryEntryNotFound` | `player_inventory.inventory_entry_not_found` |
| `PlayerInventoryPlayerNotFound` | `player_inventory.player_not_found` |
| `PlayerTemplateNotFound` | `player_template.not_found` |
| `PlayerTemplateNotFoundByName` | `player_template.not_found_by_name` |
| `RequestConflict` | `request.conflict` |
| `RequestFailed` | `request.failed` |
| `RequestForbidden` | `request.forbidden` |
| `RequestMethodNotAllowed` | `request.method_not_allowed` |
| `RequestNotFound` | `request.not_found` |
| `RequestPayloadTooLarge` | `request.payload_too_large` |
| `RequestRateLimited` | `request.rate_limited` |
| `RequestUnauthorized` | `request.unauthorized` |
| `RequestValidationFailed` | `request.validation_failed` |
| `ShopCurrencyNotHeld` | `shop.currency_not_held` |
| `ShopCurrencyTemplateNotFound` | `shop.currency_template_not_found` |
| `ShopIdempotencyKeyReused` | `shop.idempotency_key_reused` |
| `ShopInsufficientFunds` | `shop.insufficient_funds` |
| `ShopItemNotFound` | `shop.item_not_found` |
| `ShopMalformedReward` | `shop.malformed_reward` |
| `ShopNotARealCurrencyItem` | `shop.not_a_real_currency_item` |
| `ShopPackGrantsNothing` | `shop.pack_grants_nothing` |
| `ShopPlayerMismatch` | `shop.player_mismatch` |
| `ShopPlayerNotFound` | `shop.player_not_found` |
| `ShopPlayerRequired` | `shop.player_required` |
| `ShopPurchaseNotFound` | `shop.purchase_not_found` |
| `ShopRealCurrencyNeedsCheckout` | `shop.real_currency_needs_checkout` |
| `ShopRealCurrencySettlementUnavailable` | `shop.real_currency_settlement_unavailable` |
| `ShopRewardCurrencyNotHeld` | `shop.reward_currency_not_held` |
| `ShopShopNotFound` | `shop.shop_not_found` |
| `ShopWalletNotFound` | `shop.wallet_not_found` |
| `ShopWebhookProviderUnknown` | `shop.webhook_provider_unknown` |
| `ShopItemShopItemNotFound` | `shop_item.shop_item_not_found` |
| `ShopItemShopNotFound` | `shop_item.shop_not_found` |
