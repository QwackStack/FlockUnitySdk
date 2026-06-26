using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;
using Flock.Models;
using Flock.Http;
using Flock.Exceptions;
using Flock.Models.CustomModels;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Flock.Providers
{
    public class FlockCommandProvider : FlockProviderBase
    {
        private const string SnapshotCategory = "command";
        private const string PendingWritesKey = "pending_writes";

        // shared by the live call and the offline replay so the two cant drift.
        private const string PathUpdatePlayerData = "game_command/update_player_data";
        private const string PathUpdatePlayerDataKey = "game_command/update_player_data_key";
        private const string PathUnlockAchievement = "game_command/unlock_achievement";

        public FlockCommandProvider(FlockClient client) : base(client) { }
        private Queue<PendingDataWrite> _pendingWrites = new Queue<PendingDataWrite>();
        private bool _queueLoaded;

        private bool _flushTriggersHooked;
        private bool _wasReachable = true;
        private bool _flushInFlight;

        // flush the queue of pending writes to the server, if any.
        public async Task FlushPendingWritesAsync(CancellationToken cancellationToken = default)
        {
            EnsureQueueLoaded();
            if (_pendingWrites.Count == 0 || !IsServerReachable())
                return;

            while (_pendingWrites.Count > 0)
            {
                PendingDataWrite write = _pendingWrites.Peek();
                try
                {
                    PlayerData result = await ExecuteAsync(
                        () => FlockHttpClient.PostAsync<PlayerData>(
                            $"{Client.GetVersionedApiUrl()}/{write.Path}",
                            JObject.Parse(write.PayloadJson), Client.GetBaseHeaders(), cancellationToken),
                        write.Context, cancellationToken);

                    _pendingWrites.Dequeue();
                    PersistQueue();
                    ApplyToPlayerCache(result);
                }
                catch (FlockException ex)
                {
                    // transient/auth -> keep queued, retry next flush; permanent 4xx will never succeed -> drop so it cant block the queue.
                    if (!IsPermanentFailure(ex))
                    {
                        Client.Logger.LogWarning($"Pending-write flush halted at '{write.Context}', will retry next flush: {ex.Message}");
                        break;
                    }
                    Client.Logger.LogError($"Dropping rejected queued write '{write.Context}' (HTTP {ex.StatusCode}): {ex.Message}");
                    _pendingWrites.Dequeue();
                    PersistQueue();
                    // the optimistic value we cached for it was never accepted -> evict so the next read refetches authoritative state.
                    EvictOptimisticRow(write.PayloadJson);
                }
            }
        }

        // Auto-flush wiring: replays the queue when the app regains focus, when connectivity returns mid-session, and right after login. Called once from FlockClient init.
        internal void SubscribeFlushTriggers()
        {
            if (_flushTriggersHooked || !Application.isPlaying)
                return;
            FlockBehaviour behaviour = FlockBehaviour.Instance;
            if (behaviour == null)
                return;

            _wasReachable = IsServerReachable();
            behaviour.OnTick += HandleTick;
            behaviour.OnFocus += HandleFocus;
            FlockEvents.OnAuthenticated += HandleAuthenticated;
            _flushTriggersHooked = true;
        }

        // Mirror of SubscribeFlushTriggers; runs on FlockClient.Shutdown so a re-init doesnt leave a stale handler on the DontDestroyOnLoad FlockBehaviour.
        internal void UnsubscribeFlushTriggers()
        {
            if (!_flushTriggersHooked)
                return;
            if (FlockBehaviour.IsAvailable)
            {
                FlockBehaviour.Instance.OnTick -= HandleTick;
                FlockBehaviour.Instance.OnFocus -= HandleFocus;
            }
            FlockEvents.OnAuthenticated -= HandleAuthenticated;
            _flushTriggersHooked = false;
        }

        // Cheap per-frame check: flush the instant connectivity returns (offline -> online) so queued writes dont wait for a focus change. No-ops while the queue is empty.
        private void HandleTick()
        {
            if (_pendingWrites.Count == 0)
                return;
            bool reachable = IsServerReachable();
            if (reachable && !_wasReachable)
                TriggerFlush();
            _wasReachable = reachable;
        }

        private void HandleFocus(bool hasFocus)
        {
            if (hasFocus)
                TriggerFlush();
        }

        private void HandleAuthenticated(FlockAuthInfo info)
        {
            TriggerFlush();
        }

        // Fire-and-forget flush with a single-flight guard; only runs while authenticated since replays are player-scoped.
        private async void TriggerFlush()
        {
            if (_flushInFlight || !Client.IsAuthenticated)
                return;
            _flushInFlight = true;
            try
            {
                await FlushPendingWritesAsync();
            }
            catch (Exception ex)
            {
                Client.Logger.LogWarning($"Auto-flush of pending writes failed: {ex.Message}");
            }
            finally
            {
                _flushInFlight = false;
            }
        }

        public async Task<PlayerData> UpdatePlayerDataAsync(
            string playerDataId, List<DataField> data,
            CancellationToken cancellationToken = default)
        {
            RequireNotEmpty(playerDataId, "Player Data ID");
            UpdatePlayerDataInput request = new UpdatePlayerDataInput
            {
                PlayerDataId = playerDataId,
                Data = data
            };

            if (!IsServerReachable())
            {
                PlayerData playerData = ApplyOffline(playerDataId, data);
                return EnqueueOffline(PathUpdatePlayerData, request, "Update player data", playerData);
            }

            PlayerData result = await ExecuteAsync(
                () => FlockHttpClient.PostAsync<PlayerData>(
                    $"{Client.GetVersionedApiUrl()}/{PathUpdatePlayerData}",
                    request, Client.GetBaseHeaders(), cancellationToken),
                "Update player data", cancellationToken);

            return ApplyToPlayerCache(result);
        }

        public async Task<PlayerData> UpdatePlayerDataFieldAsync(
            string playerDataId, string key, object value,
            CancellationToken cancellationToken = default)
        {
            RequireNotEmpty(playerDataId, "Player Data ID");
            RequireNotEmpty(key, "Key");
            UpdatePlayerDataKeyInput request = new UpdatePlayerDataKeyInput
            {
                PlayerDataId = playerDataId,
                Key = key,
                Value = value
            };

            if (!IsServerReachable())
                return EnqueueOffline(PathUpdatePlayerDataKey, request, "Update player data field", ApplyOffline(playerDataId, new List<DataField> { new DataField { FieldName = key, Value = value } }));

            PlayerData result = await ExecuteAsync(
                () => FlockHttpClient.PostAsync<PlayerData>(
                    $"{Client.GetVersionedApiUrl()}/{PathUpdatePlayerDataKey}",
                    request, Client.GetBaseHeaders(), cancellationToken),
                "Update player data field", cancellationToken);

            return ApplyToPlayerCache(result);
        }

#if !FLOCK_NO_PLAYER
        /// <summary>Adds funds to the current player's currency wallet, resolving the "currency"-tagged player template at runtime. Use the id overload (or the generated FlockFundId method) to pass a known currency template id and skip the lookup.</summary>
        public async Task<PlayerData> AddGameFundsAsync(
            string currency, int amount,
            CancellationToken cancellationToken = default)
        {
            RequireNotEmpty(currency, "Currency");
            PlayerTemplateSchema currencyTemplate = await Client.Player.GetTemplateByTagAsync("currency", cancellationToken);
            return await AddGameFundsAsync(currency, amount, currencyTemplate.Id, cancellationToken);
        }

        /// <summary>Adds funds to the currency wallet for <paramref name="currencyTemplateId"/> (the "currency"-tagged player template); resolves the player's wallet row by that template, so no player-data id. Codegen passes the baked id here.</summary>
        public async Task<PlayerData> AddGameFundsAsync(
            string currency, int amount, string currencyTemplateId,
            CancellationToken cancellationToken = default)
        {
            RequireNotEmpty(currency, "Currency");
            RequireNotEmpty(currencyTemplateId, "Currency Template ID");
            PlayerData wallet = await Client.Player.GetMyDataByTemplateAsync(currencyTemplateId, cancellationToken);
            if (wallet == null || string.IsNullOrEmpty(wallet.Id))
                throw new FlockValidationException("No currency wallet found for the current player.");
            
            if (!IsServerReachable())
                throw new FlockNetworkException("Add game funds requires a network connection; money grants are not queued offline.");

            // Money grant is non-idempotent: an ambiguous failure may mean it already committed, so surface it rather than re-send (double-credit).
            PlayerData result = await ExecuteAsync(async () =>
            {
                AddGameFundsInput request = new AddGameFundsInput
                {
                    PlayerDataId = wallet.Id,
                    Currency = currency,
                    Amount = amount
                };

                return await FlockHttpClient.PostAsync<PlayerData>(
                    $"{Client.GetVersionedApiUrl()}/game_command/add_game_funds",
                    request, Client.GetBaseHeaders(), cancellationToken);
            }, "Add game funds", cancellationToken, idempotent: false);

            return ApplyToPlayerCache(result);
        }

        /// <summary>Unlocks an achievement on the current player's achievements row (the player template tagged "achievement"); the row is resolved for you, so no player-data id.</summary>
        public async Task<PlayerData> UnlockAchievementAsync(
            string achievementName,
            CancellationToken cancellationToken = default)
        {
            RequireNotEmpty(achievementName, "Achievement Name");
            PlayerData row = await Client.Player.GetMyDataByTagAsync("achievement", cancellationToken);
            if (row == null || string.IsNullOrEmpty(row.Id))
                throw new FlockValidationException("No achievements data found for the current player.");

            UnlockAchievementInput request = new UnlockAchievementInput
            {
                PlayerDataId = row.Id,
                AchievementName = achievementName
            };

            if (!IsServerReachable())
                return EnqueueOffline(PathUnlockAchievement, request, "Unlock achievement", row);

            PlayerData result = await ExecuteAsync(
                () => FlockHttpClient.PostAsync<PlayerData>(
                    $"{Client.GetVersionedApiUrl()}/{PathUnlockAchievement}",
                    request, Client.GetBaseHeaders(), cancellationToken),
                "Unlock achievement", cancellationToken);

            return ApplyToPlayerCache(result);
        }
#endif

        // offline path for every queueable write: persist, enqueue for replay, return the (optimistically updated) cached row.
        private PlayerData EnqueueOffline(string path, object payload, string context, PlayerData data)
        {
            EnsureQueueLoaded();
            _pendingWrites.Enqueue(new PendingDataWrite
            {
                Path = path,
                PayloadJson = JsonConvert.SerializeObject(payload),
                Context = context
            });
            PersistQueue();
            Client.Logger.LogWarning($"{context}: offline — queued for sync on reconnect");
            return data;
        }

        // overlays the queued change onto the cached row in place so reads-after-write see it; null if not cached, reconciled on flush.
        private PlayerData ApplyOffline(string playerDataId, List<DataField> changes)
        {
#if !FLOCK_NO_PLAYER
            PlayerData cached = Client.Player?.TryGetCachedRow(playerDataId);
            if (cached == null)
                return null;
            cached.Data = cached.Data ?? new List<DataField>();
            OverlayFields(cached.Data, changes);
            return cached;
#else
            return null;
#endif
        }

        // replace fields by name (change wins, keeps existing type when the change omits it), append new ones.
        private static void OverlayFields(List<DataField> target, List<DataField> changes)
        {
            if (changes == null)
                return;
            foreach (DataField change in changes)
            {
                if (change == null || string.IsNullOrEmpty(change.FieldName))
                    continue;
                int idx = target.FindIndex(f => f != null && f.FieldName == change.FieldName);
                if (idx < 0)
                {
                    target.Add(change);
                    continue;
                }
                if (string.IsNullOrEmpty(change.Type)) 
                    change.Type = target[idx].Type;
                if (string.IsNullOrEmpty(change.TypeName)) 
                    change.TypeName = target[idx].TypeName;
                target[idx] = change;
            }
        }

        // 4xx except 408/429 is an authoritative failure that wont succeed on retry -> drop; auth is recoverable via re-login -> keep queued.
        private static bool IsPermanentFailure(FlockException ex)
        {
            if (ex is FlockAuthException)
                return false;
            return FlockNetworkException.IsPermanentStatus(ex.StatusCode);
        }

        // Evicts the optimistic cache row a rejected write had written, so the next read pulls authoritative state. player_data_id is read from the queued payload.
        private void EvictOptimisticRow(string payloadJson)
        {
#if !FLOCK_NO_PLAYER
            string playerDataId = null;
            try { playerDataId = JObject.Parse(payloadJson).Value<string>("player_data_id"); }
            catch (JsonException) { }
            if (!string.IsNullOrEmpty(playerDataId))
                Client.Player?.EvictPlayerCacheByRow(playerDataId);
#endif
        }

        //queue that  lives in memory only.
        private void EnsureQueueLoaded()
        {
            if (_queueLoaded)
                return;
            _queueLoaded = true;

            FlockSnapshotStore store = Client.SnapshotStore;
            if (store != null && store.TryRead(GetSnapshotScope(SnapshotCategory), PendingWritesKey, out List<PendingDataWrite> saved) && saved != null)
                _pendingWrites = new Queue<PendingDataWrite>(saved);
        }

        private void PersistQueue()
        {
            Client.SnapshotStore?.Write(GetSnapshotScope(SnapshotCategory), PendingWritesKey, new List<PendingDataWrite>(_pendingWrites));
        }

        private PlayerData ApplyToPlayerCache(PlayerData data)
        {
#if !FLOCK_NO_PLAYER
            Client.Player?.ApplyServerPlayerData(data);
#endif
            return data;
        }
    }
}
