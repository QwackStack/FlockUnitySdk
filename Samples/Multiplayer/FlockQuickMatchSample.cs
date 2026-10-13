using System;
using System.Threading;
using System.Threading.Tasks;
using Flock.Exceptions;
using Flock.Providers;
using UnityEngine;

namespace Flock.Samples
{
    /// Quick match over Netcode for GameObjects: Find Match searches the queue named below, and the players matched start the
    /// netcode together, the longest waiting hosting. Drop it on an empty GameObject with a FlockBootstrap in the scene and press
    /// Play; it makes a NetworkManager if the scene has none. The queue is made in the Flock dashboard, under Matchmaking.
    public class FlockQuickMatchSample : FlockMultiplayerSample
    {
        [Tooltip("The matchmaking queue's name, exactly as it is in the Flock dashboard.")]
        [SerializeField] private string _queueName = "quick-match";

        private CancellationTokenSource _searching;

        /// <summary>The matchmaking queue searched, by its name in the Flock dashboard.</summary>
        public string QueueName
        {
            get => _queueName;
            set => _queueName = value;
        }

        /// <summary>True while a search is under way.</summary>
        public bool Searching => _searching != null;

        protected override string Title => "Quick Match";

        protected override void DrawFindingPlayers()
        {
            GUILayout.Label("Queue: " + _queueName);
            if (_searching == null)
            {
                if (GUILayout.Button("Find Match"))
                    _ = FindMatchAsync();
                return;
            }
            GUI.enabled = true;
            if (GUILayout.Button("Cancel"))
                CancelSearch();
        }

        /// <summary>Searches the queue for other players and, once matched, starts the netcode for the match's session.</summary>
        public async Task FindMatchAsync()
        {
            Busy = true;
            Status = "Searching in " + _queueName + "...";
            _searching = new CancellationTokenSource();
            try
            {
                FlockMatchmakingResult found = await FlockClient.Instance.Multiplayer.FindMatchAsync(_queueName, null, _searching.Token);
                if (found.Session == null)
                {
                    Status = "No match: " + found.Outcome + ".";
                    return;
                }
                await PlayAsync(found.Session);
            }
            catch (OperationCanceledException)
            {
                Status = "Search cancelled.";
            }
            catch (FlockException ex) when (ex.ErrorCode == FlockErrorCode.MatchmakingQueueNotFound)
            {
                Status = $"No queue is named \"{_queueName}\". Make it in the Flock dashboard, under Matchmaking, or set Queue Name on this component.";
            }
            catch (Exception ex)
            {
                Status = "Search failed: " + ex.Message;
            }
            finally
            {
                _searching.Dispose();
                _searching = null;
                Busy = false;
            }
        }

        /// <summary>Stops a search under way.</summary>
        public void CancelSearch() => _searching?.Cancel();
    }
}
