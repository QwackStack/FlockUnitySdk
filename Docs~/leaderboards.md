# Leaderboards

[← Back to README](../README.md)

A board is a **projection over a player-data field**, configured in the dashboard. There is no score-submit
call and none is needed: write the field the board reads and the player moves up it.

```csharp
// This is how a score is "submitted" — the board projects over the field.
await FlockClient.Instance.Commands.UpdatePlayerDataFieldAsync("player-data-id", "score", 9999);
```

Boards are addressed by **name** — the name you gave the board in the dashboard. IDs never appear in the
read API.

```csharp
// Ranked page. Open to signed-out players.
Standings top = await FlockClient.Instance.Leaderboard.GetStandingsAsync("weekly_high_scores", page: 1, limit: 20);

foreach (StandingEntry entry in top.Items)
    Debug.Log($"#{entry.Rank} {entry.PlayerName} — {entry.Score}");

// The signed-in player's placement. Rank and Score are null when they have no entry yet.
PlayerRank mine = await FlockClient.Instance.Leaderboard.GetMyRankAsync("weekly_high_scores");

// A "you are here" slice: the 5 entries either side of the player.
Standings around = await FlockClient.Instance.Leaderboard.GetAroundMeAsync("weekly_high_scores", neighbours: 5);
```

`Standings.Total` is the board's full entry count, not the size of `Items`.

## Board configuration

`GetByNameAsync` returns how the board measures and ranks, which is what UI code needs to label and format
itself:

```csharp
Leaderboard board = await FlockClient.Instance.Leaderboard.GetByNameAsync("best_lap");

string heading = board.IsHigherBetter ? "High scores" : "Best times";
string display = board.FormatScore(mine.Score);   // "" when the player is unranked
```

| Member | Values | Meaning |
|---|---|---|
| `ValueType` | `Integer`, `Float`, `DurationSeconds` | What the score measures. **Duration scores are in seconds** — the enum name says so because the wire value (`duration`) doesn't. |
| `Direction` | `Higher`, `Lower` | Which end wins. `IsHigherBetter` is the readable form. |
| `Aggregation` | `Best`, `Latest`, `Sum` | How repeated writes to the source field fold into one score. |
| `WindowType` | `Never`, `Weekly`, `Seasonal` | How the board buckets over time. Board config — not the window you read with. |
| `Scope` | `Global`, `Country` | Whether the board ranks everyone together or per country. |

`FormatScore(double?)` renders a score the way the board measures: `12345` for `Integer`, `90.5` for
`Float`, `1:05.250` for `DurationSeconds` (gaining an `h:` field past an hour), and an empty string for a
null score. It's a convenience, not a requirement — the raw `double` is always there.

`ResolveIdAsync(name)` hands back the board's ID for logging or a deep link. Nothing in the read API takes
one.

## Windows

The `window` parameter is a window **key**, not the board's `WindowType`. `never`/`weekly`/`seasonal`
describe how a board buckets and are never sent.

```csharp
FlockLeaderboardWindow.Current              // default — the live window (all-time / this week / this season)
FlockLeaderboardWindow.Season("season-id")  // one finished season on a seasonal board
FlockLeaderboardWindow.Period("2026-W31")   // a raw period key on a weekly board

Standings lastSeason = await FlockClient.Instance.Leaderboard.GetStandingsAsync(
    "weekly_high_scores", FlockLeaderboardWindow.Season("season-id"));
```

`country` filters a `Country`-scoped board to one country code; omit it for the whole board.

## Sign-in and caching

`GetByNameAsync` and `GetStandingsAsync` work signed out. `GetMyRankAsync` and `GetAroundMeAsync` need a
signed-in player and throw `FlockAuthException` immediately when there isn't one, before any request.

Every read resolves the board name to its ID once per session and reuses it; the first read of a board
costs two calls, later ones cost one. Results are snapshot-cached like other reads — after one online
session a board still reads offline, with per-player scoping so one player's rank is never served to the
next on a shared device. `FlockClient.Instance.Leaderboard.ClearCache()` drops both the name lookups and
the snapshots.

See also: [Player Data & Game Commands](player-data.md) for the writes that move a score, and
[Error handling](errors.md) for the exception types.
