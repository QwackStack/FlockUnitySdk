using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Flock.Http;
using Flock.Models;
using UnityEngine;

namespace Flock.Providers
{
    /// <summary>The entry point for multiplayer.</summary>
    public class FlockMultiplayerProvider : FlockProviderBase
    {
        private readonly FlockRepeatingCalls _repeatingCalls;
        private readonly FlockMatchmakingQueueNames _queueNames;
        private FlockBehaviour _frames;

        public FlockMultiplayerProvider(FlockClient client) : base(client)
        {
            _repeatingCalls = new FlockRepeatingCalls(client);
            _queueNames = new FlockMatchmakingQueueNames(ReadQueuesAsync, client.Logger);
        }

        internal FlockRepeatingCalls RepeatingCalls => _repeatingCalls;

        /// <summary>The id of the matchmaking queue named exactly <paramref name="queueName"/>, found without starting a search.</summary>
        internal Task<string> FindQueueIdAsync(string queueName, CancellationToken cancellationToken)
        {
            RequireNotEmpty(queueName, "Queue Name");
            return _queueNames.FindIdAsync(queueName, cancellationToken);
        }

        // Repeating calls run on the game's frames, so a web player needs no timers. Called once from FlockClient init.
        internal void StartFrameUpdates()
        {
            if (_frames != null || !Application.isPlaying)
                return;
            FlockBehaviour behaviour = FlockBehaviour.Instance;
            if (behaviour == null)
                return;

            _frames = behaviour;
            _frames.OnTick += HandleFrame;
            _frames.OnQuit += HandleQuit;
        }

        /// <summary>Stops every repeating call and lets go of the game's frames; runs when Flock shuts down.</summary>
        internal void StopForShutdown()
        {
            if (_frames != null)
            {
                _frames.OnTick -= HandleFrame;
                _frames.OnQuit -= HandleQuit;
                _frames = null;
            }
            _repeatingCalls.StopAll();
        }

        private void HandleFrame() => _repeatingCalls.SendThoseDue();

        private void HandleQuit() => _repeatingCalls.StopAll();

        private Task<List<MatchmakingQueue>> ReadQueuesAsync(CancellationToken cancellationToken)
        {
            return ExecuteAsync(async () =>
            {
                GenericResponse<List<MatchmakingQueue>> response = await FlockHttpClient.GetAsync<GenericResponse<List<MatchmakingQueue>>>(
                    $"{Client.GetVersionedApiUrl()}/{FlockEndpoints.MatchmakingQueues}?limit={FlockMatchmakingQueueNames.MostQueuesListed}",
                    Client.GetBaseHeaders(), cancellationToken);
                ValidateResponse(response);
                return response.Result;
            }, "Read matchmaking queues", cancellationToken);
        }
    }
}
