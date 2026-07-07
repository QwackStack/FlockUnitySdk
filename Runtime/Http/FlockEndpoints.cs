using System;

namespace Flock.Http
{
    /// <summary>Every relative API path the SDK calls, rooted at <see cref="FlockClient.GetVersionedApiUrl"/> — one place to view/diff the wire surface. Query strings stay at call sites unless the path builder owns escaping.</summary>
    internal static class FlockEndpoints
    {
        // Auth — login/register
        public const string PlayerLogin = "player/login";
        public const string PlayerLoginDevice = "player/login/device";
        public const string PlayerLoginGoogle = "player/login/google";
        public const string PlayerLoginApple = "player/login/apple";
        public const string PlayerLoginSteam = "player/login/steam";
        public const string PlayerRegister = "player/register";
        public const string PlayerRegisterDevice = "player/register/device";
        public const string PlayerRegisterGoogle = "player/register/google";
        public const string PlayerRegisterApple = "player/register/apple";
        public const string PlayerRegisterSteam = "player/register/steam";

        // Auth — session & account
        public const string PlayerTokenRefresh = "player/token/refresh";
        public const string PlayerTokenRevoke = "player/token/revoke";
        public const string PlayerPasswordForgot = "player/password/forgot";
        public const string PlayerPasswordReset = "player/password/reset";
        public const string PlayerEmailSendVerification = "player/email/send-verification";
        public const string PlayerEmailVerify = "player/email/verify";
        public static string PlayerNameAvailable(string name) => $"player/name-available?name={Uri.EscapeDataString(name)}";

        // Player data / templates / bans
        public const string PlayerData = "player_data";
        public static string PlayerDataById(string playerDataId) => $"player_data/{playerDataId}";
        public const string PlayerTemplate = "player_template";
        public static string PlayerTemplateById(string playerTemplateId) => $"player_template/{playerTemplateId}";
        public static string PlayerTemplateByName(string name) => $"player_template/by-name/{Uri.EscapeDataString(name)}";
        public static string PlayerTemplateData(string playerTemplateId) => $"player_template/{playerTemplateId}/player-data";
        public const string PlayerBan = "player-ban";

        // Game / versions
        public const string Game = "game";
        public const string GameVersion = "game_version";
        public static string GameVersionByName(string name) => $"game_version/by-name/{Uri.EscapeDataString(name)}";

        // Config / patches
        public const string GameConfig = "game_config";
        public const string GameConfigVersion = "game_config/version";
        public static string GameConfigById(string configId) => $"game_config/{configId}";
        public static string GameConfigByName(string name) => $"game_config/by-name/{Uri.EscapeDataString(name)}";
        public static string GameConfigPlayerFeatures(string playerId) => $"game_config/player/{playerId}/features";
        public const string GamePatch = "game_patch";
        public static string GamePatchById(string configId) => $"game_patch/{configId}";
        public static string GamePatchByConfig(string schemaId) => $"game_patch/config/{schemaId}";

        // Shop / inventory
        public const string Shop = "shop";
        public static string ShopById(string shopId) => $"shop/{shopId}";
        public static string ShopByName(string name) => $"shop/by-name/{Uri.EscapeDataString(name)}";
        public const string ShopTransaction = "shop/transaction";
        public static string ShopItemById(string shopItemId) => $"shop_item/{shopItemId}";
        public static string ShopItemsByShop(string shopId) => $"shop_item/shop/{shopId}";
        public static string PlayerInventoryByPlayer(string playerId) => $"player_inventory/player/{playerId}";

        // Assets
        public const string Asset = "asset";
        public static string AssetById(string assetId) => $"asset/{assetId}";

        // Analytics
        public const string AnalyticsSessions = "analytics/sessions";
        public static string AnalyticsSessionById(string sessionId) => $"analytics/sessions/{sessionId}";
        public const string AnalyticsEvents = "analytics/events";
        public const string AnalyticsEventsSingle = "analytics/events/single";
        public const string AnalyticsTransactions = "analytics/transactions";
        public const string LogEvent = "log_event";
        public const string LogEventSingle = "log_event/single";

        // Commands — shared by the live call and the offline replay so the two can't drift; codegen-generated command paths stay dynamic.
        public const string CommandUpdatePlayerData = "game_command/update_player_data";
        public const string CommandUpdatePlayerDataKey = "game_command/update_player_data_key";
        public const string CommandUnlockAchievement = "game_command/unlock_achievement";
        public const string CommandAddGameFunds = "game_command/add_game_funds";
    }
}
