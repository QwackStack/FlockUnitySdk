using System;
using System.Text;

namespace Flock.Exceptions
{
    // Mirrors the backend OpenAPI detail.code set; keep in sync when the spec adds codes.
    /// <summary>Typed view of the backend's coded-error contract (the `detail.code` string). Member name = wire code PascalCased, e.g. "player.email_already_registered" -> PlayerEmailAlreadyRegistered. Unknown = no code, or one this SDK version predates (read FlockException.Code for the raw string).</summary>
    public enum FlockErrorCode
    {
        Unknown = 0,
        //* is just the wildcard
        // analytics.*
        AnalyticsCurrencyNotFound,                  // analytics.currency_not_found
        AnalyticsInvalidCurrencyId,                 // analytics.invalid_currency_id
        AnalyticsPlayerNotFound,                    // analytics.player_not_found
        AnalyticsSessionNotFound,                   // analytics.session_not_found

        // asset.*
        AssetAssetNotFound,                         // asset.asset_not_found

        // game.*
        GameGameNotFound,                           // game.game_not_found
        GameMissingStudioId,                        // game.missing_studio_id

        // game_command.*
        GameCommandAchievementNotFound,             // game_command.achievement_not_found
        GameCommandCurrencyNotFound,                // game_command.currency_not_found
        GameCommandInvalidAmount,                   // game_command.invalid_amount
        GameCommandNotAWallet,                      // game_command.not_a_wallet
        GameCommandNotAnAchievementRecord,          // game_command.not_an_achievement_record
        GameCommandPlayerDataNotFound,              // game_command.player_data_not_found
        GameCommandPlayerDataNotLinkedToTemplate,   // game_command.player_data_not_linked_to_template
        GameCommandPlayerTemplateNotFound,          // game_command.player_template_not_found
        GameCommandRateLimited,                     // game_command.rate_limited
        GameCommandTemplateValidationFailed,        // game_command.template_validation_failed

        // game_config.*
        GameConfigConfigNotFound,                   // game_config.config_not_found
        GameConfigFeatureConfigNotFound,            // game_config.feature_config_not_found
        GameConfigInvalidTag,                       // game_config.invalid_tag
        GameConfigPlayerNoGameVersion,              // game_config.player_no_game_version
        GameConfigPlayerNotFound,                   // game_config.player_not_found

        // game_patch.*
        GamePatchGameConfigNotFound,                // game_patch.game_config_not_found
        GamePatchPatchNotFound,                     // game_patch.patch_not_found

        // game_version.*
        GameVersionGameVersionByNameNotFound,       // game_version.game_version_by_name_not_found
        GameVersionGameVersionNotFound,             // game_version.game_version_not_found

        // leaderboard.*
        // Only the by-name lookup answers with a code; the spec under-declares it, so don't drop it.
        LeaderboardNotFound,                        // leaderboard.not_found

        // log_event.*
        LogEventGameNotFound,                       // log_event.game_not_found

        // notification_template.*
        NotificationTemplateNotFound,               // notification_template.not_found

        // player.*
        PlayerAccountAlreadyLinked,                 // player.account_already_linked
        PlayerAccountNotLinked,                     // player.account_not_linked
        PlayerAppleAccountAlreadyRegistered,        // player.apple_account_already_registered
        PlayerCannotUnlinkLastCredential,           // player.cannot_unlink_last_credential
        PlayerDeviceAlreadyRegistered,              // player.device_already_registered
        PlayerEmailAlreadyRegistered,               // player.email_already_registered
        PlayerGameJwkNotConfigured,                 // player.game_jwk_not_configured
        PlayerGameVersionIdRequired,                // player.game_version_id_required
        PlayerGoogleAccountAlreadyRegistered,       // player.google_account_already_registered
        PlayerInvalidDeviceRegistrationRequest,     // player.invalid_device_registration_request
        PlayerInvalidLinkRequest,                   // player.invalid_link_request
        PlayerInvalidLoginCredentials,              // player.invalid_login_credentials
        PlayerInvalidRefreshToken,                  // player.invalid_refresh_token
        PlayerInvalidRegistrationRequest,           // player.invalid_registration_request
        PlayerInvalidResetCode,                     // player.invalid_reset_code
        PlayerInvalidVerificationCode,              // player.invalid_verification_code
        PlayerNameAlreadyRegistered,                // player.name_already_registered
        PlayerNoEmailAccount,                       // player.no_email_account
        PlayerOauthFailed,                          // player.oauth_failed
        PlayerPlayerNotFound,                       // player.player_not_found
        PlayerSteamAccountAlreadyRegistered,        // player.steam_account_already_registered

        // player_ban.*
        PlayerBanPlayerNotFound,                    // player_ban.player_not_found

        // player_data.*
        PlayerDataNotFound,                         // player_data.not_found
        PlayerDataPlayerNotFound,                   // player_data.player_not_found

        // player_inventory.*
        PlayerInventoryAlreadyUsed,                 // player_inventory.already_used
        PlayerInventoryInventoryEntryNotFound,      // player_inventory.inventory_entry_not_found
        PlayerInventoryPlayerNotFound,              // player_inventory.player_not_found

        // player_template.*
        PlayerTemplateNotFound,                     // player_template.not_found
        PlayerTemplateNotFoundByName,               // player_template.not_found_by_name

        // shop.*
        ShopCurrencyNotHeld,                        // shop.currency_not_held
        ShopCurrencyTemplateNotFound,               // shop.currency_template_not_found
        ShopInsufficientFunds,                      // shop.insufficient_funds
        ShopItemNotFound,                           // shop.item_not_found
        ShopMalformedReward,                        // shop.malformed_reward
        ShopPackGrantsNothing,                      // shop.pack_grants_nothing
        ShopPlayerNotFound,                         // shop.player_not_found
        ShopRewardCurrencyNotHeld,                  // shop.reward_currency_not_held
        ShopShopNotFound,                           // shop.shop_not_found
        ShopWalletNotFound,                         // shop.wallet_not_found

        // shop_item.*
        ShopItemShopItemNotFound,                   // shop_item.shop_item_not_found
        ShopItemShopNotFound,                       // shop_item.shop_not_found
    }

    public static class FlockErrorCodes
    {
        /// <summary>Maps a wire code ("namespace.reason_words") to <see cref="FlockErrorCode"/> by PascalCasing it; returns Unknown for null/empty or any code not in the enum.</summary>
        public static FlockErrorCode Parse(string code)
        {
            if (string.IsNullOrEmpty(code))
                return FlockErrorCode.Unknown;

            string[] parts = code.Replace('.', '_').Split(new[] { '_' }, StringSplitOptions.RemoveEmptyEntries);
            StringBuilder name = new StringBuilder(code.Length);
            foreach (string part in parts)
            {
                name.Append(char.ToUpperInvariant(part[0]));
                if (part.Length > 1)
                    name.Append(part.Substring(1));
            }

            bool tryParse = Enum.TryParse<FlockErrorCode>(name.ToString(), false, out FlockErrorCode parsed);
            bool defined = Enum.IsDefined(typeof(FlockErrorCode), parsed);
            bool isDefined = tryParse && defined;
            return isDefined ? parsed : FlockErrorCode.Unknown;
        }
    }
}
