using System;
using System.Text;

namespace Flock.Exceptions
{
    //TODO hook up the remaining errors and return them appropriately in the FlockException.Code string
    /// <summary>Typed view of the backend's coded-error contract (the `detail.code` string). Member name = wire code PascalCased, e.g. "player.email_already_registered" -> PlayerEmailAlreadyRegistered. Unknown = no code, or one this SDK version predates (read FlockException.Code for the raw string).</summary>
    public enum FlockErrorCode
    {
        Unknown = 0,
        //* is just the wildcard
        // analytics.*
        AnalyticsCurrencyNotFound,                  // analytics.currency_not_found
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

        // log_event.*
        LogEventGameNotFound,                       // log_event.game_not_found

        // player.*
        PlayerAppleAccountAlreadyRegistered,        // player.apple_account_already_registered
        PlayerDeviceAlreadyRegistered,              // player.device_already_registered
        PlayerEmailAlreadyRegistered,               // player.email_already_registered
        PlayerGameJwkNotConfigured,                 // player.game_jwk_not_configured
        PlayerGameVersionIdRequired,                // player.game_version_id_required
        PlayerGoogleAccountAlreadyRegistered,       // player.google_account_already_registered
        PlayerInvalidDeviceRegistrationRequest,     // player.invalid_device_registration_request
        PlayerInvalidLoginCredentials,              // player.invalid_login_credentials
        PlayerInvalidRefreshToken,                  // player.invalid_refresh_token
        PlayerInvalidRegistrationRequest,           // player.invalid_registration_request
        PlayerNameAlreadyTaken,                     // player.name_already_taken (PROVISIONAL — backend doesn't emit this yet; today it 500s on the name unique-constraint. Confirm the exact wire string when it ships.)
        PlayerOauthFailed,                          // player.oauth_failed
        PlayerSteamAccountAlreadyRegistered,        // player.steam_account_already_registered

        // player_ban.*
        PlayerBanPlayerNotFound,                    // player_ban.player_not_found

        // player_data.*
        PlayerDataNotFound,                         // player_data.not_found
        PlayerDataPlayerNotFound,                   // player_data.player_not_found

        // player_inventory.*
        PlayerInventoryPlayerNotFound,              // player_inventory.player_not_found

        // player_template.*
        PlayerTemplateNotFound,                     // player_template.not_found
        PlayerTemplateNotFoundByName,               // player_template.not_found_by_name

        // shop.*
        ShopCurrencyNotHeld,                        // shop.currency_not_held
        ShopCurrencyTemplateNotFound,               // shop.currency_template_not_found
        ShopInsufficientFunds,                      // shop.insufficient_funds
        ShopItemNotFound,                           // shop.item_not_found
        ShopPlayerNotFound,                         // shop.player_not_found
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
