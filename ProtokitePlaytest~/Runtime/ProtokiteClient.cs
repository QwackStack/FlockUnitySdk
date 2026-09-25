using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Flock.Exceptions;
using Flock.Http;
using Flock.Logging;
using Newtonsoft.Json.Linq;

namespace Protokite.Playtest
{
    /// <summary>Calls the Protokite API over the Flock SDK's transport and retry rules, as the game (key and version), never as a player.</summary>
    internal sealed class ProtokiteClient
    {
        private readonly RetryHandler _retryHandler;

        // The retry handler's own lines are left out: the playtest reports each outcome once, at the level its meaning deserves.
        internal ProtokiteClient(RetryPolicy retryPolicy)
        {
            _retryHandler = new RetryHandler(retryPolicy, new NullFlockLogger());
        }

        /// <summary>The playtest-config address for a Protokite API URL, with or without trailing slashes.</summary>
        internal static string PlaytestConfigUrl(string protokiteApiUrl) => JoinUrl(protokiteApiUrl, "/game/sdk/playtest-config");

        /// <summary>Fetches this build's playtest; retried only when Protokite or the network fails, and an unreadable answer throws.</summary>
        internal Task<ProtokitePlaytestConfig> FetchPlaytestConfigAsync(string protokiteApiUrl, Dictionary<string, string> headers,
            CancellationToken cancellationToken)
        {
            string url = PlaytestConfigUrl(protokiteApiUrl);
            return _retryHandler.ExecuteAsync(async () =>
            {
                // Enveloped: the route answers GenericResponse[SdkPlaytestConfig], so the config is under result.
                JObject envelope = await FlockHttpClient.GetAsync<JObject>(url, headers, cancellationToken);
                ProtokitePlaytestConfig config = envelope?["result"] is JObject result ? ProtokitePlaytestConfig.FromJson(result) : null;
                if (config == null)
                    throw new FlockSerializationException("The playtest config answer has no result with a test_id") { Body = envelope?.ToString() };
                return config;
            }, cancellationToken);
        }

        /// <summary>The session-start address for a Protokite API URL.</summary>
        internal static string PlaytestSessionStartUrl(string protokiteApiUrl) => JoinUrl(protokiteApiUrl, "/game/sdk/playtest-session");

        /// <summary>The address that ends one session; the session id is escaped.</summary>
        internal static string PlaytestSessionEndUrl(string protokiteApiUrl, string playtestSessionId)
            => JoinUrl(protokiteApiUrl, "/game/sdk/playtest-session/" + System.Uri.EscapeDataString(playtestSessionId) + "/end");

        /// <summary>Starts a playtest session and answers its id. Never retried: every start creates a session, so a retry could leave two.</summary>
        internal Task<string> StartPlaytestSessionAsync(string protokiteApiUrl, Dictionary<string, string> headers, JObject body,
            CancellationToken cancellationToken)
        {
            string url = PlaytestSessionStartUrl(protokiteApiUrl);
            return _retryHandler.ExecuteAsync(async () =>
            {
                // Enveloped: the route answers GenericResponse[PlaytestSessionResponse].
                JObject envelope = await FlockHttpClient.PostAsync<JObject>(url, body, headers, cancellationToken);
                string sessionId = (envelope?["result"] as JObject)?["session_id"]?.Type == JTokenType.String
                    ? (string)envelope["result"]["session_id"]
                    : null;
                if (!ProtokitePlaytestIds.IsUsable(sessionId, int.MaxValue))
                    throw new FlockSerializationException("The playtest session start answer names no usable session_id") { Body = envelope?.ToString() };
                return sessionId;
            }, cancellationToken, retryAmbiguousFailures: false, maxRetriesOverride: 0);
        }

        /// <summary>Ends a playtest session. Protokite ignores a second end, so this is retried like a read; its 204 is a success.</summary>
        internal Task EndPlaytestSessionAsync(string protokiteApiUrl, Dictionary<string, string> headers, string playtestSessionId,
            CancellationToken cancellationToken)
        {
            string url = PlaytestSessionEndUrl(protokiteApiUrl, playtestSessionId);
            // The route takes no body; an empty object keeps the POST well formed.
            return _retryHandler.ExecuteAsync(async () =>
            {
                await FlockHttpClient.PostAsync(url, new JObject(), headers, cancellationToken);
                return true;
            }, cancellationToken);
        }

        // Only trailing slashes are removed: a URL with spaces inside is refused before it gets here.
        private static string JoinUrl(string protokiteApiUrl, string route) => protokiteApiUrl.Trim().TrimEnd('/') + route;
    }
}
