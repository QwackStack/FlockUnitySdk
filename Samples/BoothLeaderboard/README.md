# Flock Booth Leaderboard Sample

One component (**FlockBoothLeaderboard**) for a booth or a couch game: after a match each player types a name, and their time
goes on a board of fastest times as an entry of its own, named after them and a code, like **Ahmed#0042**.

- **Every time is a new entry.** A second "Ahmed", or the same Ahmed playing again, never changes an earlier entry. Show the
  player their entry name after the match: the code is what tells two Ahmeds apart, for a prize claim.
- **Several booths share one board.** Each entry is a new player with a device id nobody else has, and the server refuses an
  entry name already taken, so two booths never make the same entry: the second picks a new code (up to five tries).
- **Both players at once.** Each recorded time signs a local player in, writes the time and removes the player, so two times
  recorded together never act for each other.

## Setup

1. **Configure Flock** (Flock > Settings), as for any Flock game.
2. **In the Flock dashboard**, for the Game Version the build uses:
   - a player template named `MatchTime`, tagged `gameplay`, with a field `ElapsedSeconds` of type float, default `0`
     (registering a player creates their row from it);
   - a leaderboard named `FastestMatchTime` reading template `MatchTime`, field `ElapsedSeconds`: value type duration,
     direction lower, aggregation best.
3. **Sync codegen** (Flock > Settings > Codegen) so the template's id is in `MatchTimeTemplate.SourceId`.
4. **Add the component** to a GameObject and set **Score Template Id** to that id (or the one the dashboard shows).

## Use

```csharp
using System.Threading.Tasks;
using Flock.Exceptions;
using Flock.Models;
using Flock.Samples;
using UnityEngine;

public class BoothResults : MonoBehaviour
{
    [SerializeField] private FlockBoothLeaderboard board;

    // After a match: the player typed a name and finished in elapsedSeconds.
    public async Task ShowAsync(string typedName, double elapsedSeconds)
    {
        try
        {
            string entryName = await board.RecordTimeAsync(typedName, elapsedSeconds);
            Debug.Log($"You are on the board as {entryName}");
        }
        catch (FlockException failed)
        {
            Debug.Log(failed.Message);
        }

        Standings fastest = await board.FastestTimesAsync(10);
        foreach (StandingEntry entry in fastest.Items)
            Debug.Log($"{entry.Rank}. {entry.PlayerName}  {entry.Score:0.00} s");
    }
}
```

`RecordTimeAsync` throws a `FlockException` with a fix when it cannot record: an empty name, no Score Template Id, no network
connection (an entry's write is never kept offline, since its player never signs in again), a template id with no row, or every
code tried already taken (`FlockErrorCode.PlayerNameAlreadyRegistered`). `FastestTimesAsync` reads the board through
`FlockClient.Instance.Leaderboard`.
