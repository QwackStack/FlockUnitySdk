// Needs Player, Commands and Leaderboard: a package exported without one of them leaves this sample out.
#if !FLOCK_NO_PLAYER && !FLOCK_NO_COMMANDS && !FLOCK_NO_LEADERBOARD
using System;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;
using Flock.Exceptions;
using Flock.Models;

[assembly: InternalsVisibleTo("Flock.Tests.Editor")]

namespace Flock.Samples
{
    /// A leaderboard where every time is its own entry, under the player's typed name and a code the game makes up, like "Ahmed#0042".
    /// Built for a booth: after a match each player types a name and gets a time. Every recorded time is a new entry, so a second
    /// "Ahmed", or the same Ahmed playing again, never changes an earlier one; show the player their entry name for a prize claim.
    /// Several booths can share one board: each entry is a new player, and the server refuses an entry name already taken, so a
    /// clash picks a new code. Set up the template and board in the dashboard first (README).
    public class FlockBoothLeaderboard : MonoBehaviour
    {
        [Tooltip("The board the times land on, as the dashboard names it.")]
        [SerializeField] private string boardName = "FastestMatchTime";

        [Tooltip("The id of the player template holding the time: MatchTimeTemplate.SourceId after a codegen sync, or the id the dashboard shows.")]
        [SerializeField] private string scoreTemplateId = "";

        [Tooltip("The field inside that template the board reads.")]
        [SerializeField] private string scoreField = "ElapsedSeconds";

        // Starts every device id this sample makes, so an entry can never match a real device's id.
        private const string EntryDevicePrefix = "board-entry:";
        // How many codes to try when the entry name is already taken by another entry.
        private const int CodeAttempts = 5;

        /// <summary>The board the times land on.</summary>
        public string BoardName { get => boardName; set => boardName = value; }

        /// <summary>The id of the player template holding the time.</summary>
        public string ScoreTemplateId { get => scoreTemplateId; set => scoreTemplateId = value; }

        /// <summary>The field inside that template the board reads.</summary>
        public string ScoreField { get => scoreField; set => scoreField = value; }

        // Makes up the code shown after the name, 0 to 9999; replaced by tests.
        internal Func<int> PickCode = () => UnityEngine.Random.Range(0, 10000);

        /// <summary>Records <paramref name="elapsedSeconds"/> as a new entry for the player who typed <paramref name="typedName"/>, and
        /// returns the entry's name on the board, like "Ahmed#0042". Safe to call for both players at once, and from several machines.
        /// Throws a <see cref="FlockException"/> with a fix when it cannot.</summary>
        public async Task<string> RecordTimeAsync(string typedName, double elapsedSeconds, CancellationToken cancellationToken = default)
        {
            string name = typedName?.Trim();
            if (string.IsNullOrEmpty(name))
                throw new FlockValidationException("A name is required to record a time") { Hint = "Ask the player to type a name first." };
            if (string.IsNullOrEmpty(scoreTemplateId))
                throw new FlockValidationException("The booth leaderboard has no score template id")
                {
                    Hint = "Set Score Template Id to the time template's id: MatchTimeTemplate.SourceId after a codegen sync "
                        + "(Flock > Settings > Codegen), or the id the dashboard shows."
                };
            // A write made offline is queued for the entry's player, who never signs in again, so it would never be sent.
            if (Application.internetReachability == NetworkReachability.NotReachable)
                throw new FlockNetworkException("Recording a time needs a network connection") { Hint = "Record it again once the device is online." };

            FlockClient client = FlockClient.Instance;
            FlockLocalPlayer player = client.AddLocalPlayer();
            try
            {
                // A new player for every entry, under a device id nobody else has: "board-entry:" + 32 random hex characters.
                byte[] random = new byte[16];
                using (RandomNumberGenerator generator = RandomNumberGenerator.Create())
                    generator.GetBytes(random);
                string deviceId = EntryDevicePrefix + BitConverter.ToString(random).Replace("-", "").ToLowerInvariant();

                // Named "Ahmed#0042"; when another entry already has that name, try a new code.
                string entryName = null;
                for (int attempt = 1; entryName == null; attempt++)
                {
                    string candidate = $"{name}#{PickCode():D4}";
                    try
                    {
                        await player.Authentication.RegisterWithDeviceAsync(deviceId, candidate, cancellationToken);
                        entryName = candidate;
                    }
                    catch (FlockException taken) when (taken.ErrorCode == FlockErrorCode.PlayerNameAlreadyRegistered && attempt < CodeAttempts)
                    {
                    }
                }

                PlayerData row = await player.Player.GetMyDataByTemplateAsync(scoreTemplateId, cancellationToken);
                if (row == null)
                    throw new FlockValidationException($"The new entry has no row of the template {scoreTemplateId} to hold the time")
                    {
                        Hint = "Check Score Template Id is the time template's id, and tag that template 'gameplay' in the dashboard: "
                            + "registering creates a row for every gameplay template."
                    };
                await player.Commands.UpdatePlayerDataFieldAsync(row.Id, scoreField, elapsedSeconds, cancellationToken);
                return entryName;
            }
            finally
            {
                try
                {
                    client.RemoveLocalPlayer(player);
                }
                catch (FlockValidationException)
                {
                    // A shutdown meanwhile has ended the player already.
                }
            }
        }

        /// <summary>The fastest <paramref name="count"/> entries on the board; each entry's PlayerName is its entry name.</summary>
        public Task<Standings> FastestTimesAsync(int count = 10, CancellationToken cancellationToken = default)
            => FlockClient.Instance.Leaderboard.GetStandingsAsync(boardName, limit: count, cancellationToken: cancellationToken);
    }
}
#endif
