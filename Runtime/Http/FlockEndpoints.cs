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

        // Account linking — `provider` is a closed LoginType set (FlockCredentialProviders.ToWire), so no escaping needed.
        public const string PlayerAccounts = "player/accounts";
        public const string PlayerLinkEmail = "player/link/email";
        public const string PlayerLinkDevice = "player/link/device";
        public static string PlayerLinkOAuth(string provider) => $"player/link/oauth/{provider}";
        public static string PlayerUnlink(string provider) => $"player/unlink/{provider}";

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
        public static string PlayerInventoryConsume(string inventoryId) => $"player_inventory/{inventoryId}/consume";

        // Leaderboards — read-only. There is no submit path by design: a board projects over a player-data field, so scores move by writing that field.
        // Every read is addressed by name — the /v1 surface has no by-id read routes.
        public static string LeaderboardByName(string name) => $"leaderboard/by-name/{Uri.EscapeDataString(name)}";
        public static string LeaderboardStandings(string name) => $"{LeaderboardByName(name)}/standings";
        public static string LeaderboardMe(string name) => $"{LeaderboardByName(name)}/me";
        public static string LeaderboardAroundMe(string name) => $"{LeaderboardByName(name)}/around-me";
        // Notifications — the player inbox plus game-scheduled reminders.
        public const string Notification = "notification";
        public const string NotificationUnreadCount = "notification/unread_count";
        public const string NotificationSummary = "notification/summary";
        public const string NotificationReadAll = "notification/read_all";
        public static string NotificationReadById(string notificationId) => $"notification/{notificationId}/read";
        public const string DeviceTokenRegister = "device_token/register";
        public const string DeviceTokenUnregister = "device_token/unregister";
        public const string NotificationSchedule = "notification/schedule";
        public static string NotificationScheduleById(string scheduledId) => $"notification/schedule/{scheduledId}";
        public const string NotificationTemplate = "notification_template";

        // Name rides in the query, not the path: template names carry spaces, colons and slashes, none of which survive a single path segment.
        public static string NotificationTemplateByName(string name, string locale)
        {
            string query = $"?name={Uri.EscapeDataString(name)}";
            if (!string.IsNullOrEmpty(locale))
                query += $"&locale={Uri.EscapeDataString(locale)}";
            return $"notification_template/by-name{query}";
        }

        // Matchmaking — the queue list needs only the API key; queues are found by name from it, since no by-name route exists.
        public const string MatchmakingQueues = "matchmaking/queues";

        // Parties — every route acts as the signed-in player; only the two reads carry the members.
        public const string Party = "party";
        public const string PartyJoin = "party/join";
        public const string PartyMine = "party/me";
        public static string PartyById(string partyId) => $"party/{Uri.EscapeDataString(partyId)}";
        public static string PartyLeave(string partyId) => $"{PartyById(partyId)}/leave";
        public static string PartyKick(string partyId) => $"{PartyById(partyId)}/kick";
        public static string PartyTransfer(string partyId) => $"{PartyById(partyId)}/transfer";

        // Multiplayer sessions — every route acts as the signed-in player and answers with the whole session.
        public const string Sessions = "multiplayer/sessions";
        public const string SessionJoin = "multiplayer/sessions/join";
        public static string SessionById(string sessionId) => $"multiplayer/sessions/{Uri.EscapeDataString(sessionId)}";
        public static string SessionLeave(string sessionId) => $"{SessionById(sessionId)}/leave";
        public static string SessionEnd(string sessionId) => $"{SessionById(sessionId)}/end";
        public static string SessionHost(string sessionId) => $"{SessionById(sessionId)}/host";
        public static string SessionConnection(string sessionId) => $"{SessionById(sessionId)}/connection-info";
        public static string SessionJoinToken(string sessionId) => $"{SessionById(sessionId)}/join-token";
        public static string SessionVerifyJoinToken(string sessionId) => $"{SessionById(sessionId)}/verify-join-token";

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
