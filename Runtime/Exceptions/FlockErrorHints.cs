using System.Collections.Generic;

namespace Flock.Exceptions
{
    /// <summary>
    /// Turns a failure into the next step the developer should take. The one owner of every fix the SDK hands out:
    /// <see cref="FlockException.Hint"/> falls back to <see cref="ForFailure"/>, so no failure leaves the SDK without one. Keyed on
    /// <see cref="FlockErrorCode"/> first — never on message text — and on the kind of failure when the code has no advice of its own.
    /// </summary>
    public static class FlockErrorHints
    {
        // Dashboard-authored content only reaches the game after a codegen sync, which is the step newcomers miss.
        private const string AuthorAndSync = "Author it in the Flock dashboard, then run Flock > Settings > Code Generation > Sync Schemas so the generated code under Assets/Flock/Generated matches.";

        private const string AuthorInDashboard = "Author it in the Flock dashboard and publish it to this game version.";

        // Every request is the API URL plus /v1/<route>, so a path in the setting doubles or misses part of every route.
        private const string ApiUrlAlone = "The API URL in Flock > Settings must be the Flock server's address alone, such as https://api.example.com, with no /v1 or other path after it.";

        // A purchase and the shop reads take the shop item's own id, which a newcomer cannot tell from the item's name.
        private const string ShopItemIdFromShop = "Shop.PurchaseAsync, Shop.RedeemStoreReceiptAsync and Shop.GetItemAsync take the shop item's Id (ShopItem.Id, read from its shop with Shop.GetAllAsync, Shop.GetByNameAsync or Shop.GetItemsByShopAsync), not the item's name or the shop's id. If the id is right, check the API Key in Flock > Settings is the key of the game that owns the shop.";

        // A wallet is built once, at registration; neither a purchase nor Commands.AddGameFundsAsync adds a currency to it.
        private const string WalletHoldsTemplateCurrencies = "A wallet holds the currencies of its Game Version's currency template, each starting at zero when the player registers.";

        // A game's payment providers (stores and web checkout) and their keys are set up on one dashboard page.
        private const string PaymentsPage = "Monetization > Payments in the Flock dashboard";

        // A receipt the store has not settled yet is never finished at the store, so it comes back to be sent again.
        private const string SendTheReceiptAgainLater = "leave the store's purchase unfinished and send the same receipt again later with Shop.RedeemStoreReceiptAsync.";

        // A setup fault met while redeeming a store purchase: the player has paid the store, and nothing was kept.
        private const string PaidInTheStoreAlready = " If this came from Shop.RedeemStoreReceiptAsync, the player has already paid the store: leave the store's purchase unfinished, and the same receipt redeems once this is fixed.";

        // A store refusing the game's own keys is fixed by the studio, never by the player.
        private const string StoreKeysFix = "check the App Store or Google Play keys under " + PaymentsPage + ", and that the Google service account has access to the app in the Google Play Console.";

        // The fixes several failures the SDK finds itself share, kept here so each has one wording.

        /// <summary>A call that acts as the player, made with nobody signed in.</summary>
        public const string SignInFirst = "Sign the player in first, and make this call once the sign-in has succeeded.";

        /// <summary>A call made before the Flock client exists.</summary>
        public const string InitializeFlockFirst = "Create the client before this call: turn on Auto Initialize On Load in Flock > Settings, or call FlockClient.Create(config) once at startup. The Console says what stopped an automatic start (an unresolved Game Version, an empty API URL).";

        /// <summary>A 2xx answer that lacks what only Flock's game API would send.</summary>
        public const string NotFlocksAnswer = "The answer did not come from Flock's game API: a captive portal (a Wi-Fi sign-in page), a proxy, or an API URL that points at another server answers like this. Sign in to the network if it asks, and check the address. " + ApiUrlAlone;

        /// <summary>No answer came within the timeout.</summary>
        public const string NoAnswerInTime = "Check the device's network connection, then try again; the SDK retries a read by itself. A purchase or other change that timed out may still have been made, so read the result before sending it again.";

        /// <summary>The request never reached the server.</summary>
        public const string CouldNotReachTheServer = "Check the device is online and can reach the Flock server: a wrong address, a firewall or a VPN stops every call. " + ApiUrlAlone;

        /// <summary>Something the SDK did not expect went wrong inside it.</summary>
        public const string ReportSdkFault = "This is a fault in the Flock SDK, not in the call; the inner exception says where. Report it with this log line.";

        /// <summary>A session call given a session that has ended.</summary>
        public const string FindAnotherSession = "Host or join another session (HostSessionAsync, JoinSessionAsync), or get the player's current one with GetMySessionAsync.";

        /// <summary>A party call given a party that has ended.</summary>
        public const string FindAnotherParty = "Create or join another party (CreatePartyAsync, JoinPartyAsync), or get the player's current one with GetMyPartyAsync.";

        private static readonly Dictionary<FlockErrorCode, string> Hints = new Dictionary<FlockErrorCode, string>
        {
            // Auth — login never creates an account, which is the single most common first-run mistake.
            { FlockErrorCode.PlayerInvalidLoginCredentials, "No account matches these credentials. Logging in never creates an account — call the matching Authentication.RegisterWith*Async method once first." },
            { FlockErrorCode.PlayerDeviceAlreadyRegistered, "This device already has an account. Call Authentication.LoginWithDeviceAsync(deviceId) instead of registering it again." },
            { FlockErrorCode.PlayerEmailAlreadyRegistered, "That email already has an account. Call Authentication.LoginWithEmailAsync(email, password), or Authentication.ForgotPasswordAsync(email) if the password is lost." },
            { FlockErrorCode.PlayerGoogleAccountAlreadyRegistered, "That Google account is already registered. Call Authentication.LoginWithGoogleAsync(idToken) instead." },
            { FlockErrorCode.PlayerAppleAccountAlreadyRegistered, "That Apple account is already registered. Call Authentication.LoginWithAppleAsync(identityToken) instead." },
            { FlockErrorCode.PlayerSteamAccountAlreadyRegistered, "That Steam account is already registered. Call Authentication.LoginWithSteamAsync(...) instead." },
            { FlockErrorCode.PlayerNameAlreadyRegistered, "Display names are unique per game. Check Authentication.IsNameAvailableAsync(name) first, or register with name: null and set it later." },
            { FlockErrorCode.PlayerInvalidRefreshToken, "The saved session is no longer valid. Call Authentication.Logout() and sign in again." },
            { FlockErrorCode.PlayerExpiredToken, "The player's sign-in expired and could not be renewed. Sign the player in again; the SDK renews a sign-in by itself while its saved session is still valid." },
            { FlockErrorCode.PlayerInvalidToken, "The server does not accept the player's sign-in: it was made by another game or another Flock server, or the game's signing key changed since. Call Authentication.Logout() and sign the player in again." },
            { FlockErrorCode.PlayerMissingToken, "This call acts as the signed-in player and went out with no sign-in. Sign the player in first, and make the call once the sign-in has succeeded." },
            { FlockErrorCode.PlayerInvalidVerificationCode, "The email verification code is wrong or expired. Call Authentication.SendEmailVerificationAsync() to issue a new one." },
            { FlockErrorCode.PlayerInvalidResetCode, "The password reset code is wrong or expired. Call Authentication.ForgotPasswordAsync(email) to issue a new one." },
            { FlockErrorCode.PlayerNoEmailAccount, "This player has no email credential. Link one with Authentication.LinkEmailAsync(email, password) before using email-only flows." },
            { FlockErrorCode.PlayerAccountAlreadyLinked, "That credential already belongs to an account. Call Authentication.GetLinkedAccountsAsync() to see what this player is already linked to." },
            { FlockErrorCode.PlayerAccountNotLinked, "That credential is not linked to this player. Link it with the matching Authentication.Link*Async call first." },
            { FlockErrorCode.PlayerCannotUnlinkLastCredential, "A player must keep at least one way to sign in. Link another credential before unlinking this one." },
            { FlockErrorCode.PlayerGameJwkNotConfigured, "The game has no signing key configured. Set it up in the Flock dashboard before players can authenticate." },
            { FlockErrorCode.PlayerGameVersionIdRequired, "This route needs a game version. Pick one in Flock > Settings so it is baked into the build." },
            { FlockErrorCode.PlayerInvalidRegistrationRequest, "The registration was rejected. Check the fields this provider requires — Authentication.RegisterWithEmailAsync(email, password) needs both, and a blank one never reaches the server as null." },
            { FlockErrorCode.PlayerInvalidDeviceRegistrationRequest, "The device registration was rejected. Pass a stable, non-empty device id to Authentication.RegisterWithDeviceAsync(deviceId) — the same string on every launch, or the account cannot be found again." },
            { FlockErrorCode.PlayerInvalidLinkRequest, "The link request was rejected. Every Authentication.Link*Async call takes the raw token the platform handed you, not a wrapped payload of your own." },
            { FlockErrorCode.PlayerOauthFailed, "The identity provider rejected the token. It has usually expired — fetch a fresh one from the platform immediately before calling Authentication.LoginWith*Async." },
            { FlockErrorCode.PlayerPlayerNotFound, "No player matches that id. Sign in first — most calls act on FlockClient.Instance.CurrentPlayerId." },

            // Dashboard-authored content the consumer has not created or not synced yet.
            { FlockErrorCode.GameCommandPlayerTemplateNotFound, "No player-data template by that name. " + AuthorAndSync },
            { FlockErrorCode.PlayerTemplateNotFound, "No player-data template by that id. " + AuthorAndSync },
            { FlockErrorCode.PlayerTemplateNotFoundByName, "No player-data template by that name. " + AuthorAndSync },
            { FlockErrorCode.GameCommandAchievementNotFound, "No achievement by that id. " + AuthorAndSync },
            { FlockErrorCode.GameCommandCurrencyNotFound, "The player's wallet has no currency by that code. " + WalletHoldsTemplateCurrencies + " Use one of those codes, letter case included, or add the currency to the currency template in the Flock dashboard." },
            { FlockErrorCode.ShopCurrencyTemplateNotFound, "This Game Version has no currency template (a player-data template tagged currency), so no player has a wallet to pay from. Create one in the Flock dashboard under the version this build runs (the Game Version in Flock > Settings)." + PaidInTheStoreAlready },
            { FlockErrorCode.GameCommandTemplateValidationFailed, "The value does not match the template's schema. Compare the field names and types in the Flock dashboard, then re-run Flock > Settings > Code Generation > Sync Schemas so the generated types match." },
            { FlockErrorCode.GameCommandPlayerDataNotLinkedToTemplate, "That record is not linked to a template, so template-aware commands cannot act on it. Re-create it from the template in the Flock dashboard." },
            { FlockErrorCode.GameCommandNotAWallet, "That record is not a wallet. Currency commands only work on wallet-typed player data." },
            { FlockErrorCode.GameCommandNotAnAchievementRecord, "That record is not an achievement. Achievement commands only work on achievement-typed player data." },
            { FlockErrorCode.GameCommandInvalidAmount, "The amount must be greater than zero." },
            { FlockErrorCode.GameConfigConfigNotFound, "No game config by that name. " + AuthorInDashboard },
            { FlockErrorCode.GameConfigFeatureConfigNotFound, "No feature config by that name. " + AuthorInDashboard },
            { FlockErrorCode.NotificationTemplateNotFound, "No notification template by that name. " + AuthorInDashboard },
            { FlockErrorCode.LeaderboardNotFound, "No leaderboard by that name — boards are addressed by name, never by id. " + AuthorInDashboard },
            { FlockErrorCode.GamePatchPatchNotFound, "No patch by that id. " + AuthorInDashboard + " Call Config.ClearCache() if it was re-published while the game was running." },
            { FlockErrorCode.GamePatchGameConfigNotFound, "The patch points at a config that no longer exists. Re-check the config it patches in the Flock dashboard." },
            { FlockErrorCode.GameConfigInvalidTag, "The backend does not accept that config tag. Valid tags are the SchemaTag values — if generated code is sending something else, re-run Flock > Settings > Code Generation > Sync Schemas." },
            { FlockErrorCode.ShopMalformedReward, "The item's stored reward cannot be granted, so the purchase was refused rather than half-applied. Fix that item's rewards in the Flock dashboard — no change to the call will work around it." + PaidInTheStoreAlready },
            { FlockErrorCode.ShopPackGrantsNothing, "This currency pack has no rewards configured, so buying it would grant nothing. Add its rewards in the Flock dashboard, or take it off sale." + PaidInTheStoreAlready },
            { FlockErrorCode.ShopRealCurrencyNeedsCheckout, "This item is priced in a currency the game's currency config lists as real money, and Shop.PurchaseAsync only spends game currencies from the player's wallet. Price the item in a game currency in the Flock dashboard, or sell it through the App Store or Google Play and redeem the store's purchase with Shop.RedeemStoreReceiptAsync (ShopItem.IsRealMoney says which items these are)." },
            { FlockErrorCode.GameMissingStudioId, "The game record has no studio attached. Fix it in the Flock dashboard — nothing in the Unity project can supply it." },
            { FlockErrorCode.LogEventGameNotFound, "The API key does not resolve to a game, so log events have nowhere to land. Check the API Key in Flock > Settings against the dashboard." },
            { FlockErrorCode.AssetAssetNotFound, "No asset by that id. Upload it in the Flock dashboard and publish it to this game version." },
            { FlockErrorCode.ShopShopNotFound, "No shop by that name in this build's Game Version, or by that id in this game. A shop belongs to one Game Version: create it in the Flock dashboard under the version this build runs (the Game Version in Flock > Settings), and give its name exactly as the dashboard shows it, letter case included." },
            { FlockErrorCode.ShopItemNotFound, "No shop item has that id in this game. " + ShopItemIdFromShop },
            { FlockErrorCode.ShopItemShopNotFound, "No shop has that id in this game. Shop.GetItemsByShopAsync takes the shop's Id (Shop.Id, read with Shop.GetAllAsync or Shop.GetByNameAsync), not its name." },
            { FlockErrorCode.ShopItemShopItemNotFound, "No shop item has that id in this game. " + ShopItemIdFromShop },
            { FlockErrorCode.GameGameNotFound, "The API key does not resolve to a game. Check the API Key in Flock > Settings against the dashboard." },
            { FlockErrorCode.GameInvalidApiKey, "The server does not know this API key. Copy the game's key from the Flock dashboard into the API Key in Flock > Settings; a key from another Flock server (local, staging or live) is refused too, so check the API URL points at the server the key came from." },
            { FlockErrorCode.GameMissingApiKey, "The request reached the server with a blank API key. Set the API Key in Flock > Settings (or on the FlockInitConfig given to FlockClient.Create) to the game's key from the Flock dashboard." },

            // Request — the server's own codes for a failure no route raised itself. A wrong API URL lands here.
            { FlockErrorCode.RequestConflict, "The change clashed with another made at the same moment, or with what the record holds now. Read it again, then make the change again if it still applies." },
            { FlockErrorCode.RequestFailed, "The server could not complete the request; the reason above is its own. Try again later, and check the server is running if it lasts." },
            { FlockErrorCode.RequestForbidden, "The server does not allow this request; the reason above is its own. Check the call is one a game may make with its API key, for the player signed in now." },
            { FlockErrorCode.RequestMethodNotAllowed, "The server has a route at this address, but not for this kind of request, so the address is not the one this SDK expects. " + ApiUrlAlone + " If it is, the server runs an older Flock than this SDK." },
            { FlockErrorCode.RequestNotFound, "The server found nothing for this request; the reason above says what. A bare \"Not Found\" means no route answers at this address: " + ApiUrlAlone + " If it is, the server runs an older Flock that does not have this feature yet. Any other reason names a record this game or player does not have: check the id passed to the call." },
            { FlockErrorCode.RequestPayloadTooLarge, "The request is larger than the server accepts. Send less in one call: split a large write into several." },
            { FlockErrorCode.RequestRateLimited, "The server turned the request away for volume; the reason above says which kind. Too many calls in a short time: the SDK retries reads after the wait the server asks for, so send writes less often, several values together in one call. A limit on how many of a thing a player may hold, such as pending scheduled notifications: cancel some, as waiting will not help." },
            { FlockErrorCode.RequestUnauthorized, "The server refused the request's credentials. Check the API Key in Flock > Settings against the Flock dashboard, and sign the player in again." },
            { FlockErrorCode.RequestValidationFailed, "The server refused a value in the request; the reason above names it (body.<name> is a member of what the SDK sent, header.<name> a header). It is usually a value passed to this call: check it against the call's documentation. header.X-Flock-API-Key means the API Key in Flock > Settings is empty. A member you never pass means this SDK and the server are on different Flock versions." },
            { FlockErrorCode.GameVersionGameVersionNotFound, "That game version does not exist. Pick an existing one in Flock > Settings — the id is baked at build time." },
            { FlockErrorCode.GameVersionGameVersionByNameNotFound, "That game version name does not exist. Pick an existing one in Flock > Settings — the id is baked at build time." },
            { FlockErrorCode.GameConfigPlayerNoGameVersion, "The player has no game version attached. Pick one in Flock > Settings so it is sent on every request." },

            // Runtime state the caller can act on rather than misconfiguration.
            { FlockErrorCode.ShopInsufficientFunds, "The player cannot afford this item. Read the wallet balance before offering the purchase." },
            { FlockErrorCode.ShopCurrencyNotHeld, "The player's wallet has no balance in the item's currency. " + WalletHoldsTemplateCurrencies + " Price the item in one of those, or add the currency to the currency template in the Flock dashboard." },
            { FlockErrorCode.ShopWalletNotFound, "This player has no wallet in this Game Version. The server makes a player's wallet when the player registers, from the version's currency template, so a player who registered before that template existed, or under another Game Version, has none. Create the currency template first, then register the player under this build's Game Version." + PaidInTheStoreAlready },
            { FlockErrorCode.GameCommandPlayerDataNotFound, "The player has no record for that template yet. Reads do not create it — write it first with Commands.UpdatePlayerDataAsync." },
            { FlockErrorCode.PlayerDataNotFound, "The player has no record for that template yet. Reads do not create it — write it first with Commands.UpdatePlayerDataAsync." },
            { FlockErrorCode.AnalyticsCurrencyNotFound, "Transaction analytics need a currency entity for this game. Create one in the Flock dashboard." },
            { FlockErrorCode.AnalyticsInvalidCurrencyId, "AnalyticsTransactionRequest.CurrencyId is not a 26-character ULID. Leave it null and set CurrencyCode instead — the backend resolves the code to an id." },
            { FlockErrorCode.AnalyticsSessionNotFound, "That analytics session is no longer open on the server. The SDK opens one on sign-in and closes it on logout; a new one starts on the next sign-in." },
            { FlockErrorCode.GameCommandRateLimited, "Too many writes to that player-data row. Send the fields together in one Commands.UpdatePlayerDataAsync call instead of one Commands.UpdatePlayerDataFieldAsync per field." },
            { FlockErrorCode.ShopRewardCurrencyNotHeld, "The reward pays out a currency the player's wallet does not have. " + WalletHoldsTemplateCurrencies + " Reward one of those, or add the currency to the currency template in the Flock dashboard." + PaidInTheStoreAlready },
            { FlockErrorCode.PlayerInventoryAlreadyUsed, "That inventory entry has already been consumed — each one is spendable once. Re-read Shop.GetPlayerInventoryAsync before offering it again." },
            { FlockErrorCode.PlayerInventoryInventoryEntryNotFound, "No inventory entry by that id in this game, or, while a player is signed in, the entry is another player's, which the server answers the same way. Shop.ConsumeAsync takes the signed-in player's own entry id — a PurchaseResult.Inventory.Id — not the shop item id." },

            // Player-scoped calls that name an id. Each provider differs on how the signed-in player is addressed.
            { FlockErrorCode.AnalyticsPlayerNotFound, "The analytics call named a player the backend does not have. Sessions follow sign-in — start one only after an Authentication.LoginWith*Async call has returned." },
            { FlockErrorCode.GameConfigPlayerNotFound, "No player matches that id. Player-scoped config reads act on the signed-in player — sign in first, then check FlockClient.Instance.CurrentPlayerId." },
            { FlockErrorCode.PlayerBanPlayerNotFound, "No player matches that id. Player.GetBanAsync requires a player id — pass FlockClient.Instance.CurrentPlayerId for the signed-in player." },
            { FlockErrorCode.PlayerDataPlayerNotFound, "No player matches that id. Omit the playerId argument on Player.GetAllDataAsync to read the signed-in player's records." },
            { FlockErrorCode.PlayerInventoryPlayerNotFound, "No player matches that id. Omit the playerId argument on Shop.GetPlayerInventoryAsync to read the signed-in player's inventory." },
            { FlockErrorCode.ShopPlayerNotFound, "No player has that id in this game. Leave playerId out so Shop.PurchaseAsync acts on the signed-in player, and sign in before offering a purchase; a player id from another game or another Flock server is not found." },
            { FlockErrorCode.ShopPlayerMismatch, "The call names a different player than the one signed in, and the server buys only for the signed-in player, so nothing was bought or charged. Leave playerId out of Shop.PurchaseAsync so it buys for the signed-in player, or sign that other player in first." },
            { FlockErrorCode.ShopPlayerRequired, "The call named no player: it went out with no sign-in and no player id. " + SignInFirst },

            // Payments: a store receipt redeemed, a web checkout opened; a code's causes differ only in the server's reason, which the hints quote.
            { FlockErrorCode.PaymentProviderReceiptInvalid, "The store did not confirm this purchase, so nothing was granted; the reason above tells its causes apart. An \"unreadable\" or \"unexpected\" answer, or one Flock \"could not read\": the store had a bad moment, so " + SendTheReceiptAgainLater + " Anything else is the receipt itself: the store does not know it or refunded it, or it lacked the store's ids (the App Store's transaction id; Google Play's product id and purchase token). Send the receipt exactly as the store handed it over. An older Flock server sends this code for three causes that newer ones give codes of their own. \"Apple returned 401\", \"Google rejected the service account\", or Google returning 401 or 403: the store refused this game's own keys, not the receipt; " + StoreKeysFix + " The store returning 429 or a 5xx, or \"Could not reach\" (HTTP 502): the store was busy or down. \"purchase state is 2\": Google is still waiting for the player's payment. In those last two, " + SendTheReceiptAgainLater },
            { FlockErrorCode.PaymentProviderStoreUnavailable, "Flock could not ask the store about this receipt: the store was busy, down or out of reach. Nothing is known about the purchase yet and nothing was granted, and it is neither a fake receipt nor a setting, so " + SendTheReceiptAgainLater },
            { FlockErrorCode.PaymentProviderStoreCredentialsRejected, "The store refused this game's own keys, so the receipt could not be checked; it may well be genuine, and nothing was granted, so " + StoreKeysFix + " Leave the store's purchase unfinished: once the keys are fixed, the same receipt goes through." },
            { FlockErrorCode.PaymentProviderReceiptPending, "Google is still waiting for the player's payment (a slow way to pay, such as cash at a shop), so nothing was paid or granted yet: " + SendTheReceiptAgainLater },
            { FlockErrorCode.PaymentProviderReceiptBelongsToAnotherPlayer, "This store purchase is another player's, not the signed-in player's, so nothing was granted; the reason above says how. \"belongs to a different player\": the purchase was started for another Flock player, whose account token the store keeps with it. \"redeemed by another player\": another player already redeemed this receipt. Redeem a receipt while the player who bought it is signed in; leave a purchase another account made on this device unfinished until that player signs in." },
            { FlockErrorCode.PaymentProviderReceiptProductMismatch, "The receipt is not for this shop item; the reason above says which way. \"This item has no ... product id configured\": enter the item's product id for that store under Store product IDs on the shop item in the Flock dashboard, exactly as App Store Connect or the Google Play Console shows it. \"Receipt is for 'X', but this item sells 'Y'\": the player bought the store product X, so redeem the receipt for the shop item whose store product id is X, or correct this item's id if it was entered wrong." },
            { FlockErrorCode.PaymentProviderProviderNotSupportedHere, "That payment provider cannot be used for this. A store receipt names its store as apple_app_store or google_play, in lower case exactly; tap, moyasar and stripe take payments through web checkout, which has no receipt to redeem, and any other name is not a payment provider Flock knows." },
            { FlockErrorCode.PaymentProviderProviderNotFound, "This game has no active payment provider by that name: it was never connected, or it is paused (the reason reads the same for both). Connect it with the game's own keys under " + PaymentsPage + ", or turn Active back on for a paused one. If the dashboard shows it connected and active, check the API Key in Flock > Settings is the key of that game." },
            { FlockErrorCode.PaymentProviderCredentialsUnreadable, "The server cannot use the keys stored for this payment provider, so nothing was checked or granted, and trying again will not help: it is a setting of the game, not of the player. Enter the provider's keys again under " + PaymentsPage + ". The App Store's private key is the whole text of the .p8 file Apple gave you, its BEGIN and END lines included; Google Play's service account JSON is the whole key file Google gave you." },
            { FlockErrorCode.PaymentProviderCheckoutFailed, "The payment provider would not open a checkout, so nothing was charged; the reason above carries the provider's own answer. Keys the provider refuses are fixed under " + PaymentsPage + "; a price in a currency the provider does not take is fixed in the Real Currencies of the currency config, whose code must be one the provider accepts, such as USD. A provider that was down or timed out recovers: try again later." },
            { FlockErrorCode.ShopNotARealCurrencyItem, "This item is priced in a game currency, so it is bought from the player's wallet with Shop.PurchaseAsync, never with real money. Sell it through Shop.PurchaseAsync, or, if it should cost real money, price it in one of the Real Currencies of the currency config of this build's Game Version in the Flock dashboard." },
            { FlockErrorCode.ShopRealCurrencySettlementUnavailable, "No web checkout can be opened for this game, so nothing was charged; the reason above says why. \"no web payment provider configured\" (HTTP 422): connect Tap Payments, Moyasar or Stripe under " + PaymentsPage + ", or turn Active back on for a paused one. \"PAYMENT_WEBHOOK_BASE_URL is not configured\" (HTTP 503): the Flock server itself is not set up to confirm payments, which nothing in the game can change; whoever runs that server sets it." },
            { FlockErrorCode.ShopWebhookProviderUnknown, "The server could not tell which web checkout provider to use; the reason above says why. \"does not open web checkouts\": the provider named is a store (apple_app_store, google_play) or a name Flock does not know; name tap, moyasar or stripe. \"Several payment providers are active\": name one of those the reason lists, or pause the others under " + PaymentsPage + "." },
            { FlockErrorCode.ShopPurchaseNotFound, "The signed-in player has no purchase with that id in this game; another player's purchase is not found either. A purchase's id is the PurchaseResult.PurchaseId its purchase returned; an id from another game, or from another Flock server, is not found. \"Unknown purchase status\" (HTTP 422): purchases were listed by a status the server does not have; ask for pending, paid, failed, expired or refunded." },

            // Matchmaking, parties and sessions. A 404 here usually means "not yours, or no longer live", not "never existed".
            { FlockErrorCode.MatchmakingQueueNotFound, "No matchmaking queue by that id in this game. Create the queue in the Flock dashboard first." },
            { FlockErrorCode.MatchmakingNotPartyLeader, "Only the party leader can put the party in a queue. Start the search from the leader's game." },
            { FlockErrorCode.MatchmakingPartyTooLarge, "The party has more players than the queue allows, or than one of the queue's teams holds. Raise the queue's maximum players, or its team size, in the Flock dashboard, or search with a smaller party." },
            { FlockErrorCode.MatchmakingSearchInputRequired, "This queue matches players by region or skill, and this version of the SDK sends neither. Remove the queue's region and skill rules in the Flock dashboard, or search in a queue without them." },
            { FlockErrorCode.MatchmakingPlayerNotEligible, "A player in this search is outside the player segments the queue allows. Check the queue's allowed segments in the Flock dashboard." },
            { FlockErrorCode.MatchmakingAlreadyQueued, "A player in this search is already searching, and a player searches once at a time per game. FindMatchAsync replaces the player's own earlier search itself, so this is the party's search (wait for it with the party's FindMatchAsync), or a party member searching alone, whose game cancels that search first." },
            { FlockErrorCode.MatchmakingTicketNotFound, "No search by that id for the signed-in player. A player can only read or cancel their own search." },
            { FlockErrorCode.MatchmakingTicketNotCancelable, "That search has already ended: it was matched, cancelled or ran out of time. Read it again to see which; a matched one has a game waiting." },
            { FlockErrorCode.MatchmakingVersionMismatch, "The party's members are on different Game Versions, and this queue matches one build at a time. A member's game records its version when it reads the party (GetMyPartyAsync), so have every member on the same build read it, then search again; or turn off \"Same game version only\" on the queue in the Flock dashboard." },
            { FlockErrorCode.MatchmakingMatchNotFound, "No match by that id that the signed-in player is in. Only the players in a match can read it." },
            { FlockErrorCode.MultiplayerSessionNotFound, "The session does not exist, has ended, or the signed-in player no longer holds a seat in it. The SDK keeps the seat with heartbeats while the game runs, so a game paused past the game's timeouts (in the background) loses it; join again with the session's join code through JoinSessionAsync." },
            { FlockErrorCode.MultiplayerNotAParticipant, "The signed-in player holds no seat in this session, so there is nothing to leave." },
            { FlockErrorCode.MultiplayerNotHost, "Only the session's current host can do this. Hosting may have moved to another player; GetSessionAsync reads the session again to show who hosts now." },
            { FlockErrorCode.MultiplayerSessionFull, "The session has no free seats, or a session created for a party has fewer seats than the party has players. A session's seat count is set when it is created; a matched session takes it from the game's multiplayer settings in the Flock dashboard." },
            { FlockErrorCode.MultiplayerInvalidJoinCode, "No open session has that join code. Codes are 6 characters, are not case sensitive, and stop working once the session ends." },
            { FlockErrorCode.MultiplayerInvalidJoinToken, "The join token is wrong, expired (they last 60 seconds), meant for another session, or its player no longer holds a seat. The joining player asks for a new token with RequestJoinTokenAsync and connects again; one that lost its seat joins again with JoinSessionAsync first." },
            { FlockErrorCode.MultiplayerVersionMismatch, "The session was opened on another Game Version, and its host keeps it to that build. Players join sessions of their own build; a host lets any build join by hosting with sameVersionOnly: false." },
            { FlockErrorCode.MultiplayerTargetNotAParticipant, "That player holds no seat in this session, so hosting cannot be handed to them." },
            { FlockErrorCode.MultiplayerJoinCodeUnavailable, "The server could not find a free join code for a new session. It is temporary; host the session again with HostSessionAsync." },
            { FlockErrorCode.MultiplayerAlreadyInSession, "Two requests tried to seat this player at the same moment. A player holds one seat per game; try again once the first has finished." },
            { FlockErrorCode.MultiplayerMintRateLimited, "Relay credentials were requested too often (at most 12 a minute per player). Request them once per session and reuse them until they expire." },
            { FlockErrorCode.MultiplayerSeatMintLimit, "This player's seat has drawn the most relay credentials one seat may (10), usually from starting the netcode again and again in one session. Nothing more is given for this seat, even after leaving and joining the same session again; another session gives a seat of its own." },
            { FlockErrorCode.PartyNotFound, "No party by that id that the signed-in player belongs to. It may have been disbanded, or the player removed from it." },
            { FlockErrorCode.PartyInvalidInviteCode, "No open party has that invite code. Ask the party leader for the current code." },
            { FlockErrorCode.PartyAlreadyInParty, "The signed-in player is already in a party in this game, and a player can be in one at a time. GetMyPartyAsync returns it; leave it with LeaveAsync before creating or joining another." },
            { FlockErrorCode.PartyNotAMember, "The signed-in player is not in that party, so there is nothing to leave." },
            { FlockErrorCode.PartyNotPartyLeader, "Only the party leader can do this. Leadership may have moved to another member; GetMyPartyAsync reads the party again to show who leads now." },
            { FlockErrorCode.PartyFull, "The party has no free places, or its size was set below the number of players already in it." },
            { FlockErrorCode.PartyCannotKickLeader, "The leader cannot remove themselves. Hand leadership to another member with MakeLeaderAsync and then leave, or disband the party with DisbandAsync." },
            { FlockErrorCode.PartyTargetNotAMember, "That player is not in the party, so they cannot be removed or made leader." },
        };

        /// <summary>Next step for a coded error, or null when the SDK has nothing to add beyond the server's own reason.</summary>
        public static string For(FlockErrorCode code)
        {
            if (code == FlockErrorCode.Unknown)
                return null;
            return Hints.TryGetValue(code, out string hint) ? hint : null;
        }

        /// <summary>Next step for a failed download of an asset's file, which comes from storage rather than the Flock server; <paramref name="statusCode"/> is null when storage was never reached.</summary>
        public static string ForStorageDownload(int? statusCode)
        {
            if (!statusCode.HasValue)
                return "Check the device is online and that a firewall or VPN lets it reach the asset's download address; asset files come from storage, not from the Flock server.";
            if (statusCode.Value == 401 || statusCode.Value == 403)
                return "The download link expired or storage refused it. Read the asset again (Assets.GetByIdAsync or GetAllAsync) for a fresh link, then download it again.";
            if (statusCode.Value == 404)
                return "The asset's file is missing from storage. Upload it again in the Flock dashboard and publish it to this game version.";
            if (statusCode.Value >= 500)
                return "Storage failed while serving the file; the call did not cause it. Try again later.";
            return "Storage refused the download. Read the asset again for a fresh link, then download it again.";
        }

        /// <summary>
        /// Next step for any failure: its code's advice when it has one, otherwise the advice for its kind — an answer that
        /// did not come from Flock's game API (no code), a code this SDK version does not know, a server failure, a failure
        /// the SDK found itself. Never empty. Reads the failure's code, status and type only, never its <see cref="FlockException.Hint"/>.
        /// </summary>
        public static string ForFailure(FlockException failure)
        {
            string forCode = For(failure.ErrorCode);
            if (!string.IsNullOrEmpty(forCode))
                return forCode;

            int? status = failure.StatusCode;
            // The HTTP client gives an unreadable answer its own fix, so one without came from the SDK's own work.
            if (failure is FlockSerializationException)
                return ReportSdkFault;

            // Made by the SDK itself, which states its own fix wherever it has one; these are what is left.
            if (!status.HasValue)
            {
                if (failure is FlockAuthException)
                    return SignInFirst;
                if (failure is FlockValidationException)
                    return "Change what the reason above names, then make the call again.";
                return "Make the call again. If it keeps failing, check the device's network connection.";
            }

            if (status.Value >= 500)
                return "The server, or a proxy in front of it, failed while handling the call; the call did not cause it. Try again later (the SDK retries a read by itself), and check the server is running if it lasts.";
            // Flock answered with a code newer than this SDK: its reason is all there is.
            if (!string.IsNullOrEmpty(failure.Code))
                return $"The reason above is the server's own. This version of the Flock SDK has no advice for the code {failure.Code}; a newer version may.";
            if (status.Value == 408 || status.Value == 429)
                return "Something between the game and Flock is asking it to slow down. Wait a moment and try again; the SDK retries a read by itself.";
            // Every failure Flock's game API sends carries a code, so an answer without one came from something else.
            return "The answer carries no Flock error code, so it did not come from Flock's game API: a proxy, a firewall or a captive portal answered, or the request went to another server. " + ApiUrlAlone;
        }

        /// <summary>Next step for an auth failure. The credential disambiguates codes that mean different things per method — notably invalid_login_credentials, which means "register this device first" but "wrong password" for email.</summary>
        public static string ForAuth(FlockErrorCode code, FlockAuthMethod method)
        {
            if (code != FlockErrorCode.PlayerInvalidLoginCredentials)
                return For(code);

            switch (method)
            {
                case FlockAuthMethod.Device:
                    return "This device is not registered yet. Call Authentication.RegisterWithDeviceAsync(deviceId) once to create the account, then Authentication.LoginWithDeviceAsync(deviceId) on later launches.";
                case FlockAuthMethod.Email:
                    return "Wrong email or password. If the account does not exist yet, call Authentication.RegisterWithEmailAsync(email, password) first — logging in never creates one.";
                case FlockAuthMethod.Google:
                case FlockAuthMethod.Apple:
                case FlockAuthMethod.Steam:
                    return $"No Flock account is linked to that {method} identity yet. Call Authentication.RegisterWith{method}Async(...) once, or link it to the signed-in player with Authentication.Link{method}Async(...).";
                // No register route exists for these two — linking to an existing player is the only way in.
                case FlockAuthMethod.Facebook:
                case FlockAuthMethod.Discord:
                    return $"No Flock account is linked to that {method} identity yet. Sign in another way, then call Authentication.Link{method}Async(...) — there is no {method} registration route.";
                default:
                    return For(code);
            }
        }
    }
}
