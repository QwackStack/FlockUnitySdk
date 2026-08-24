using System.Collections.Generic;

namespace Flock.Exceptions
{
    /// <summary>Turns a coded server error into the next step the developer should take. Keyed on <see cref="FlockErrorCode"/> only — never on message text.</summary>
    public static class FlockErrorHints
    {
        // Dashboard-authored content only reaches the game after a codegen sync, which is the step newcomers miss.
        private const string AuthorAndSync = "Author it in the Flock dashboard, then run Flock > Settings > Codegen > Sync so the generated code under Assets/Flock/Generated matches.";

        private const string AuthorInDashboard = "Author it in the Flock dashboard and publish it to this game version.";

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
            { FlockErrorCode.PlayerInvalidVerificationCode, "The email verification code is wrong or expired. Call Authentication.SendEmailVerificationAsync() to issue a new one." },
            { FlockErrorCode.PlayerInvalidResetCode, "The password reset code is wrong or expired. Call Authentication.ForgotPasswordAsync(email) to issue a new one." },
            { FlockErrorCode.PlayerNoEmailAccount, "This player has no email credential. Link one with Authentication.LinkEmailAsync(email, password) before using email-only flows." },
            { FlockErrorCode.PlayerAccountAlreadyLinked, "That credential already belongs to an account. Call Authentication.GetLinkedAccountsAsync() to see what this player is already linked to." },
            { FlockErrorCode.PlayerAccountNotLinked, "That credential is not linked to this player. Link it with the matching Authentication.Link*Async call first." },
            { FlockErrorCode.PlayerCannotUnlinkLastCredential, "A player must keep at least one way to sign in. Link another credential before unlinking this one." },
            { FlockErrorCode.PlayerGameJwkNotConfigured, "The game has no signing key configured. Set it up in the Flock dashboard before players can authenticate." },
            { FlockErrorCode.PlayerGameVersionIdRequired, "This route needs a game version. Pick one in Flock > Settings so it is baked into the build." },
            { FlockErrorCode.PlayerPlayerNotFound, "No player matches that id. Sign in first — most calls act on FlockClient.Instance.CurrentPlayerId." },

            // Dashboard-authored content the consumer has not created or not synced yet.
            { FlockErrorCode.GameCommandPlayerTemplateNotFound, "No player-data template by that name. " + AuthorAndSync },
            { FlockErrorCode.PlayerTemplateNotFound, "No player-data template by that id. " + AuthorAndSync },
            { FlockErrorCode.PlayerTemplateNotFoundByName, "No player-data template by that name. " + AuthorAndSync },
            { FlockErrorCode.GameCommandAchievementNotFound, "No achievement by that id. " + AuthorAndSync },
            { FlockErrorCode.GameCommandCurrencyNotFound, "No currency by that name. " + AuthorAndSync },
            { FlockErrorCode.ShopCurrencyTemplateNotFound, "No currency template by that name. " + AuthorAndSync },
            { FlockErrorCode.GameCommandTemplateValidationFailed, "The value does not match the template's schema. Compare the field names and types in the Flock dashboard, then re-run Flock > Settings > Codegen > Sync so the generated types match." },
            { FlockErrorCode.GameCommandPlayerDataNotLinkedToTemplate, "That record is not linked to a template, so template-aware commands cannot act on it. Re-create it from the template in the Flock dashboard." },
            { FlockErrorCode.GameCommandNotAWallet, "That record is not a wallet. Currency commands only work on wallet-typed player data." },
            { FlockErrorCode.GameCommandNotAnAchievementRecord, "That record is not an achievement. Achievement commands only work on achievement-typed player data." },
            { FlockErrorCode.GameCommandInvalidAmount, "The amount must be greater than zero." },
            { FlockErrorCode.GameConfigConfigNotFound, "No game config by that name. " + AuthorInDashboard },
            { FlockErrorCode.GameConfigFeatureConfigNotFound, "No feature config by that name. " + AuthorInDashboard },
            { FlockErrorCode.NotificationTemplateNotFound, "No notification template by that name. " + AuthorInDashboard },
            { FlockErrorCode.AssetAssetNotFound, "No asset by that id. Upload it in the Flock dashboard and publish it to this game version." },
            { FlockErrorCode.ShopShopNotFound, "No shop by that name. " + AuthorInDashboard },
            { FlockErrorCode.ShopItemNotFound, "No shop item by that id. " + AuthorInDashboard },
            { FlockErrorCode.ShopItemShopNotFound, "No shop by that id. " + AuthorInDashboard },
            { FlockErrorCode.ShopItemShopItemNotFound, "No shop item by that id. " + AuthorInDashboard },
            { FlockErrorCode.GameGameNotFound, "The API key does not resolve to a game. Check the API Key in Flock > Settings against the dashboard." },
            { FlockErrorCode.GameVersionGameVersionNotFound, "That game version does not exist. Pick an existing one in Flock > Settings — the id is baked at build time." },
            { FlockErrorCode.GameVersionGameVersionByNameNotFound, "That game version name does not exist. Pick an existing one in Flock > Settings — the id is baked at build time." },
            { FlockErrorCode.GameConfigPlayerNoGameVersion, "The player has no game version attached. Pick one in Flock > Settings so it is sent on every request." },

            // Runtime state the caller can act on rather than misconfiguration.
            { FlockErrorCode.ShopInsufficientFunds, "The player cannot afford this item. Read the wallet balance before offering the purchase." },
            { FlockErrorCode.ShopCurrencyNotHeld, "The player holds no wallet for that currency yet. Grant funds once with Commands.AddGameFundsAsync to create it." },
            { FlockErrorCode.ShopWalletNotFound, "The player has no wallet for that currency yet. Grant funds once with Commands.AddGameFundsAsync to create it." },
            { FlockErrorCode.GameCommandPlayerDataNotFound, "The player has no record for that template yet. Reads do not create it — write it first with Commands.UpdatePlayerDataAsync." },
            { FlockErrorCode.PlayerDataNotFound, "The player has no record for that template yet. Reads do not create it — write it first with Commands.UpdatePlayerDataAsync." },
            { FlockErrorCode.AnalyticsCurrencyNotFound, "Transaction analytics need a currency entity for this game. Create one in the Flock dashboard." },
        };

        /// <summary>Next step for a coded error, or null when the SDK has nothing to add beyond the server's own reason.</summary>
        public static string For(FlockErrorCode code)
        {
            if (code == FlockErrorCode.Unknown)
                return null;
            return Hints.TryGetValue(code, out string hint) ? hint : null;
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
