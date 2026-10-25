# Multiplayer

[← Back to README](../README.md)

Friends form a **party**, the party or a lone player **finds a match** (or joins a friend's game by code), and every game
is a **session**: one player hosts, the others hold seats. The SDK then **connects the players** over Netcode for
GameObjects. Players on the host's network connect to it directly; everyone else goes through **Flock's relay**, so
nobody forwards a port, and moves to a direct path to the host when the two can reach each other. When the host leaves,
another player takes over.

Every call is on `FlockClient.Instance.Multiplayer`, or on an object it returns. The step-by-step guides with dashboard
screenshots are on [docs.qwacks.com](https://docs.qwacks.com/guides/multiplayer).

## Setting up

In the dashboard, open **Multiplayer → Sessions → Multiplayer settings**. The defaults suit most games:

| Setting | Default | What it does |
| --- | --- | --- |
| Max players per session | 8 | Seats in a session a player hosts or a match makes, from 2 to 16. |
| Participant timeout (seconds) | 90 | A player silent for longer than this loses their seat. |
| Host timeout (seconds) | 60 | A host silent for longer than this is replaced by the player seated longest. |
| Session max age (seconds) | 14400 | Every session ends after this long (4 hours), however busy it is. |
| Create a session when a match forms | On | Matched players get a session to play in. |
| Hand out relay credentials | On | Players on different networks connect through Flock's relay. When off, only players on the host's network, or on a port forwarded to it, can connect. |

Then make a queue for each mode under **Multiplayer → Matchmaking** (see [Matchmaking](#matchmaking)).

In Unity:

1. Install **Netcode for GameObjects** 1.x or 2.x. Flock's netcode support is its own assembly and compiles only when one
   of them is installed. Every player of a game must use the same major version. Verified on Unity 2021.3 with Netcode
   1.14 and on Unity 6000.3 with Netcode 2.13.
2. In **Flock > Settings**, set **Party Refresh Seconds**: how often the SDK reads the player's party in the background
   (10 by default; 0 turns it off). In code it is `FlockInitConfig.PartyRefreshInterval`.
3. Use one `NetworkManager` with Unity Transport, left at its default message size.
4. Sign the player in before any multiplayer call, and make every call from the main thread. Every multiplayer event is
   raised on the main thread.
5. Scripts that use multiplayer start with `using Flock;` and `using Flock.Providers;` (add `using Flock.Exceptions;` for
   `FlockErrorCode` and `using Unity.Netcode;` for `NetworkManager`). A script in an assembly definition of its own
   references `Flock.Runtime`, and `Flock.Multiplayer.Netcode` for `StartNetcodeAsync`.

The package's `Samples/Multiplayer` folder has two one-screen samples: `FlockPlayWithFriendsSample` (host, read out the
code, a friend joins it) and `FlockQuickMatchSample` (players find each other in a queue). Add either to an empty
GameObject and press Play in two copies of the game.

## Which setup fits your game

| Your game | Players meet by | Relay (`FlockNetcodeOptions.Relay`) | Sample |
| --- | --- | --- | --- |
| Local co-op: devices on one network (a LAN party, one Wi-Fi) | A session's join code | `WhenNeeded` (connects directly on the network), or `Never` for no relay at all | `FlockPlayWithFriendsSample` |
| Online co-op with friends on other networks | A session's join code | `WhenNeeded` | `FlockPlayWithFriendsSample` |
| Online matchmaking with strangers | A queue | `WhenNeeded` | `FlockQuickMatchSample` |
| Friends matched together against or with others | A party, then a queue | `WhenNeeded` | |
| Streamers and competitive play, where nobody may see another's IP address | Any of the above | `Always`, on every game | |
| Steam, Epic Online Services or your own networking | Any of the above | Not used | |

Several players on **one** device (split screen, shared controllers) need none of this: that is one game with one Flock
player, and Flock's multiplayer connects devices.

### Local co-op: devices on one network

Dashboard: nothing beyond the multiplayer settings. One player hosts and reads out the code; the others join with it.
Players who share the host's network connect to it directly, so no packet leaves the network.

```csharp
// The host's game.
FlockMultiplayerSession session = await FlockClient.Instance.Multiplayer.HostSessionAsync(4);
joinCodeLabel.text = session.JoinCode;
await session.StartNetcodeAsync(NetworkManager.Singleton);

// Each other player's game, with the code the host read out.
FlockMultiplayerSession joined = await FlockClient.Instance.Multiplayer.JoinSessionAsync(typedCode);
FlockNetcodeStartResult started = await joined.StartNetcodeAsync(NetworkManager.Singleton);
// started.ThroughRelay is false for a player on the host's network.
```

To keep every game off the relay (a LAN-only game, or a network that must not reach the internet), pass
`new FlockNetcodeOptions { Relay = FlockRelayUse.Never }` on every game; a player elsewhere then cannot connect
(`CouldNotConnect`) unless a port is forwarded to the host.

### Online co-op with friends on other networks

Dashboard: leave **Hand out relay credentials** on. The code is the same as local co-op: the host hosts and shares the
join code however your game lets friends talk (a lobby screen, your own friends list, a chat message), and the friends
join with it. With the default `WhenNeeded`, a friend on another network comes through Flock's relay, then moves to a
direct path to the host when the two can reach each other. Phones and PCs play together the same way, as long as every
game uses the same major version of Netcode for GameObjects.

For friends who play match after match together, make them a [party](#parties) first and search as the party (below).

### Online matchmaking with strangers

Dashboard: one queue per mode under **Multiplayer → Matchmaking**, with its smallest and largest match.

```csharp
FlockMatchmakingResult found = await FlockClient.Instance.Multiplayer.FindMatchAsync("casual-1v1");
if (found.IsMatched && found.Session != null)
    await found.Session.StartNetcodeAsync(NetworkManager.Singleton);   // the player who waited longest hosts
else
    ShowMessage("No match: " + found.Outcome);
```

### Friends matched together

Dashboard: a queue whose largest match holds the whole party. The friends form a party with its invite code, and the
leader searches for all of them; every member's game hears the result.

```csharp
party.SearchEnded += async result =>
{
    if (result.IsMatched && result.Session != null)
        await result.Session.StartNetcodeAsync(NetworkManager.Singleton);
};

if (party.IsLeader)
    await party.FindMatchAsync("squads");
```

Have every member's game call `GetMyPartyAsync` once it is running, so a "Same game version only" queue knows its build.

### Streamers and competitive play

Start the netcode with `Always` on every game, the host's included: every packet then goes through Flock's relay, and no
game learns another's IP address. Each packet takes the relay's extra hop.

```csharp
await session.StartNetcodeAsync(NetworkManager.Singleton, new FlockNetcodeOptions { Relay = FlockRelayUse.Always });
```

### Steam, Epic Online Services or your own networking

Use Flock's parties, matchmaking and sessions to bring players together, then publish and read the connection yourself and
check each player with a join token; see [Other networking](#other-networking).

## Parties

A party is a group of friends who play together: one leader and up to 63 others, joined with the party's invite code. It
lasts until its players leave it, across games and launches. A player is in one party at a time.

```csharp
FlockMultiplayerProvider multiplayer = FlockClient.Instance.Multiplayer;

// The party from last time, or a new one: 2 to 64 players, 4 when not given.
FlockParty party = await multiplayer.GetMyPartyAsync()
    ?? await multiplayer.CreatePartyAsync(4, new Dictionary<string, object> { ["mode"] = "coop" });
inviteCodeLabel.text = party.InviteCode;

party.MembersChanged += () => ShowMembers(party.Members);   // in the order they joined
party.LeaderChanged += () => leaderControls.SetActive(party.IsLeader);
party.Ended += reason => ShowMainMenu(reason);

// A friend's game joins with the code; letter case does not matter.
FlockParty joined = await multiplayer.JoinPartyAsync(typedCode);
if (joined.TryGetSetting("mode", out string mode))
    ShowMode(mode);

// The leader manages the party; anyone can leave it.
await party.UpdateAsync(settings: new Dictionary<string, object> { ["mode"] = "hard" });   // replaces all settings
await party.KickAsync(playerId);
await party.MakeLeaderAsync(playerId);
await party.DisbandAsync();
await party.LeaveAsync();   // when the leader leaves, the first of the others leads
```

The SDK reads the party every Party Refresh Seconds and after each change your game makes, and raises `MembersChanged`
and `LeaderChanged` when something moved. `Ended` is raised once, with a `FlockPartyEndReason`:

| Reason | When |
| --- | --- |
| `Left` | The player left with `LeaveAsync`. |
| `Disbanded` | The player disbanded it. |
| `Removed` | The server no longer lists the player: removed by the leader, the party disbanded by someone else, or the player left from another device. |
| `SignedOut` | The player signed out, or another player signed in. The player is still in the party on the server; call `LeaveAsync` first when the player means to leave. |

Errors: `PartyInvalidInviteCode` (no party has that code), `PartyFull`, `PartyAlreadyInParty` (leave the other party
first), `PartyNotPartyLeader` (a call only the leader can make), `PartyCannotKickLeader`.

## Sessions

A session is one game: one player hosts it and the others hold seats, up to 16 in all. A player holds one seat at a time;
hosting or joining another session gives up the one held. When the host leaves, the player seated longest becomes the
host.

```csharp
// 2 to 16 seats (the game's setting when not given), and the game's own data, set once.
FlockMultiplayerSession session = await multiplayer.HostSessionAsync(4, new Dictionary<string, object> { ["map"] = "crypt" });
joinCodeLabel.text = session.JoinCode;

// A friend's game joins with the code; letter case does not matter.
FlockMultiplayerSession joined = await multiplayer.JoinSessionAsync(typedCode);
joined.TryGetData("map", out string map);

session.PlayersChanged += () => ShowPlayers(session.Players);   // seated longest first
session.HostChanged += OnHostChanged;   // start the netcode again: see "When the host changes"
session.Ended += reason => BackToLobby(reason);

await session.LeaveAsync();                // anyone
await session.EndAsync();                  // the host only: ends it for everyone
await session.MakeHostAsync(otherPlayer);  // the host only: hands hosting over

// After the game restarts, find the seat the player still holds (null when seated nowhere).
FlockMultiplayerSession held = await multiplayer.GetMySessionAsync();
```

A session takes only players on the host's Game Version unless the host passes `sameVersionOnly: false`; a player on
another build is refused with `MultiplayerVersionMismatch`.

The SDK sends the session's heartbeat every 20 seconds while the game runs, so a playing player never times out:

| What happened | What Flock does | What players see |
| --- | --- | --- |
| A player quits the game | Gives the seat up at once | `PlayersChanged` |
| A player's game crashes or loses its network | Gives the seat up after the Participant timeout (90 s) | `PlayersChanged` |
| A phone goes to the home screen | The game stops, so its heartbeats stop; after the Participant timeout the seat is given up | Back in the game, the session ends as `dropped` |
| The host leaves | Makes the player seated longest the host at once | `HostChanged` |
| The host's game crashes | Replaces the host after the Host timeout (60 s) | `HostChanged`, about a minute later |
| The game restarts within the timeout | Keeps the seat | `GetMySessionAsync` finds it |

`Ended` is raised once with one of `FlockMultiplayerSessionEndReason`'s reasons (`EndedByHost`, `HostLeft`, `Empty`,
`Expired`, `Left`, `Dropped`, `MovedToAnotherSession`, `SignedOut`). The server can add reasons, so keep a default.
`Ended` is not raised when the game quits.

Errors: `MultiplayerInvalidJoinCode`, `MultiplayerSessionFull`, `MultiplayerSessionNotFound`,
`MultiplayerVersionMismatch`, `MultiplayerNotHost` (a call only the host can make).

## Matchmaking

Each mode of your game is a **queue** with a smallest and largest number of players, made in the dashboard. A player, or
a whole party, searches in a queue by its name (letter for letter, case included); once the queue holds enough players,
Flock makes a match and seats the players in a session. A party is matched as one group and never split. Players are
matched in the order they search. A search that ends without a match is an outcome, not an exception.

```csharp
CancellationTokenSource searching = new CancellationTokenSource();
cancelButton.onClick.AddListener(searching.Cancel);
try
{
    FlockMatchmakingResult found = await multiplayer.FindMatchAsync("coop-4", null, searching.Token);
    if (found.IsMatched && found.Session != null)
        await PlayAsync(found.Session);
    else if (found.Outcome == FlockMatchmakingOutcome.Expired)
        ShowMessage("Nobody found in 5 minutes. Search again?");
    else
        ShowMessage("The search stopped.");
}
catch (OperationCanceledException)
{
    ShowMessage("Search cancelled.");   // the search is cancelled on the server too
}
catch (FlockException ex) when (ex.ErrorCode == FlockErrorCode.MatchmakingQueueNotFound)
{
    ShowMessage("No queue is named coop-4.");
}
```

Search as a party: the leader's game starts the search, and every member's game hears it through the party, so handle
the result in one place. `SearchEnded` is raised on every member's game, the leader's included.

```csharp
party.SearchStarted += () => ShowSearching();
party.SearchEnded += result =>
{
    if (result.IsMatched && result.Session != null)
        _ = PlayAsync(result.Session);
    else
        ShowLobby();
};

if (party.IsLeader)
    await party.FindMatchAsync("coop-4");
```

The SDK checks a search every 3 seconds. `FlockMatchmakingOptions.Attributes` passes your own data with a search;
matching does not use it yet.

| `FlockMatchmakingOutcome` | When |
| --- | --- |
| `Matched` | `Session` is where the players play; `PlayerIds` lists them, the longest waiting first. If `Session` is null, `GetMySessionAsync` finds it; with "Create a session when a match forms" off, there is none and your game makes one. |
| `Expired` | Nobody was found within 5 minutes. |
| `PartyChanged` | A player joined or left the party during the search; the leader searches again. |
| `Cancelled` | Another game stopped it: a party member's, or the player's own on another device. |

A search your game cancels through its token ends the call with an `OperationCanceledException` instead. The server can
add outcomes, so treat any other as ended without a match. On a "Same game version only" queue, each member's version is
taken from their game's last read of the party: have every member's game call `GetMyPartyAsync` once it is running.

Errors: `MatchmakingQueueNotFound`, `MatchmakingPlayerNotEligible` (not in the queue's allowed segments),
`MatchmakingPartyTooLarge`, `MatchmakingNotPartyLeader`, `MatchmakingVersionMismatch`.

## Connecting players

One call does the whole hand-shake, for the host and for each player. On the host it starts Netcode for GameObjects as
host and says where to reach it. On a player it waits for that (up to 60 seconds, `FlockNetcodeOptions.WaitForHost`),
connects, and proves the player's seat.

```csharp
FlockNetcodeStartResult started = await session.StartNetcodeAsync(NetworkManager.Singleton);
switch (started.Outcome)
{
    case FlockNetcodeStartOutcome.Restarted:
        return;   // a newer start on this NetworkManager took over
    case FlockNetcodeStartOutcome.StartedAsHost:
        ShowMessage("Hosting. Code: " + session.JoinCode);
        break;
    case FlockNetcodeStartOutcome.Connected:
        ShowMessage(started.ThroughRelay ? "Connected through Flock's relay." : "Connected.");
        break;
    case FlockNetcodeStartOutcome.Refused:
        ShowMessage("The host refused you: " + started.RefusedReason);
        break;
    case FlockNetcodeStartOutcome.RelayFailed:
        ShowMessage("The relay could not be used: " + started.RelayFailureReason);
        break;
    default:
        ShowMessage("Could not connect: " + started.Outcome);
        break;
}
```

| `FlockNetcodeStartOutcome` | Meaning |
| --- | --- |
| `StartedAsHost` | This device hosts, and players can reach it. |
| `Connected` | This device joined the host. `ThroughRelay` says whether through the relay. |
| `Refused` | The host refused this player; `RefusedReason` says why. |
| `RelayFailed` | The relay was needed and could not be used; `RelayFailureReason` says why. |
| `HostNeverPublished` | The host did not say where to reach it within `WaitForHost`. |
| `CouldNotConnect` | The host did not answer. |
| `CouldNotStart` | Netcode for GameObjects refused to start, such as when the port is in use. |
| `SessionEnded` | The session ended while waiting. |
| `NotDirect` | The host published a connection of its own kind; see [Other networking](#other-networking). |
| `Restarted` | The game started the netcode again before this start ended, and the newer start took over. |

A start that neither hosts nor connects keeps the player's seat; call `session.LeaveAsync()` if the player cannot play.

### Through the relay, or directly

Flock's relay runs on Cloudflare's network and is on for every game until it is switched off under **Multiplayer
settings**. With it switched off, or while a studio's relay is paused until its bill is paid, players connect on the
host's network or through a port forwarded to the host, and a player on another network gets `RelayFailed` with
`relay_not_offered` or `relay_paused`.

`FlockNetcodeOptions.Relay` chooses who goes through Flock's relay:

| `FlockRelayUse` | Who goes through the relay |
| --- | --- |
| `WhenNeeded` (default) | Players on the host's network try its local address for a few seconds, then the relay if it does not answer. Everyone else goes through the relay, then moves to a direct path when they and the host can reach each other. |
| `Always` | Every player, and they stay on the relay. Players never learn the host's IP address, nor the host theirs. |
| `Never` | Nobody. Players reach the host on its network, or on a port forwarded to it. |

```csharp
await session.StartNetcodeAsync(NetworkManager.Singleton, new FlockNetcodeOptions { Relay = FlockRelayUse.Always });
```

With `WhenNeeded`, once the host has let a relayed player in, the two learn where each other can be reached (from the
relay, or a STUN server Flock lists) and try a direct path. When it works both ways, the game's packets go directly; a
direct path that goes quiet for 4 seconds hands them back to the relay. The game does nothing in either case. The
trade-off is that the host and a relayed player learn each other's IP address; use `Always` to keep them hidden.

The relay is reached over UDP. On a network that blocks UDP to it (some phone hotspots, offices, schools and hotels), the
SDK reaches the same relay over UDP on IPv6, then TCP, then TLS, each way Flock lists: each starts once the one before it has failed or
had 2 seconds, so a blocked way costs the player about 2 seconds. The host and a player need not use the same way. Over
TCP or TLS a lost packet holds back the ones after it until it is sent again, so play may stutter on a poor connection;
with verbose logging on, the SDK says which way it uses when it is not UDP over IPv4. UDP is tried over IPv6 as well before any
TCP or TLS (a phone hotspot that dropped UDP over IPv4 carried it over IPv6), and every way is tried over IPv6 for a network with
IPv6 alone; the relay still gives an IPv4 relay address, so such a player plays with everyone else.

When the relay cannot be used, `RelayFailureReason` is one of `FlockRelayFailure`'s reasons. The server can add reasons,
so keep a default:

| Reason | When |
| --- | --- |
| `relay_not_offered` | Hand out relay credentials is off for the game, or Flock lists no relay. |
| `relay_not_offered_by_host` | The host takes nobody through the relay: its `Relay` is `Never`, or its relay could not be opened. |
| `relay_server_not_listed` | Flock does not list, for this player, the relay server the host uses. |
| `relay_paused` | The studio's relay is paused until its bill is paid. |
| `relay_too_many_logins` | More than 12 relay logins in a minute, or 10 for one seat in a session: do not start the netcode in a loop. |
| `relay_not_in_session` | Flock says the player has no seat in the session. |
| `flock_unreachable` | Flock did not answer the request for relay logins. |
| `relay_logins_refused` | Flock refused the request for relay logins for another reason. |
| `relay_unreachable` | No way in to the relay worked: UDP, TCP and TLS, each way Flock lists, over IPv4 and IPv6, and the reason names every way tried and why it failed. A network that blocks all of them, or inspects encrypted traffic, leaves none. |
| `relay_wrong_login` | The relay refused the login Flock minted. |
| `relay_full` | The relay has no room for another address. |
| `relay_open_refused` | The relay refused to let packets in from the host's relay address, or from a player's. |
| `relay_refused` | The relay refused a request for another reason. |
| `relay_lost` | The relay let the address go, or has answered nothing for 45 s (this device's packets no longer reach it). |
| `relay_closed` | The relay connection was closed by the session's end before the call. |
| `relay_not_on_this_platform` | A web player: a browser cannot open the UDP or TCP connections the relay needs. |

### Who is joining

The host started by `StartNetcodeAsync` lets in only players whose seat Flock confirms, one connection each. Name players
by their connection, never by what a player's game sends:

```csharp
string playerId = session.PlayerForConnection(clientId);   // on the host
```

Add your own check after Flock's, such as a ban list or a team limit:

```csharp
FlockNetcodeOptions options = new FlockNetcodeOptions
{
    ApproveJoiningPlayer = (player, response) =>
    {
        if (bannedPlayers.Contains(player.PlayerId))
        {
            response.Approved = false;
            response.Reason = "banned";
        }
    },
};
```

A refused player's `RefusedReason` is one of `FlockNetcodeRefusedReason`'s: `no_join_token`, `join_token_invalid`,
`already_connected`, `flock_unreachable`, `session_ended`, `not_host`, `refused_by_the_game` or `game_check_failed`.
Both ends of a game must run Flock SDK 1.77.0 or later, and the same major version of Netcode for GameObjects: a host
refuses players on an older Flock SDK, or on another major version of Netcode.

### When the host changes

When the host leaves or its game stops, Flock makes the player seated longest the host and raises `HostChanged` on every
game. Netcode for GameObjects cannot move a running game to a new host, so each game shuts its netcode down and starts it
again: the new host hosts, and the others join it. Do the same on `RelayLost`, raised once when this device's relay stops
working.

```csharp
session.HostChanged += StartAgain;
session.RelayLost += reason => StartAgain();

async void StartAgain()
{
    if (session.HasEnded)
        return;
    NetworkManager.Singleton.Shutdown();
    await session.StartNetcodeAsync(NetworkManager.Singleton);   // waits for the shutdown to finish
}
```

Players are back about 20 seconds after a host leaves, and about a minute after a host's game crashes (the Host timeout).
Everything the old host held is gone: its spawned objects, network variables and scene state. Keep what a new host needs
on every player, such as a snapshot of the game the host sends every few seconds, and rebuild from it.

### Other networking

Games on Steam, Epic Online Services or their own networking still use Flock's parties, matchmaking, sessions and join
check, and publish and read the connection themselves:

```csharp
// The host says how to reach it: a kind of its own and the values it needs, under 4 KB.
await session.PublishConnectionAsync("steam", new Dictionary<string, object> { ["lobby"] = lobbyId.ToString() });

// A player waits for it; null when the time passes or the session ends.
FlockMultiplayerSessionConnection connection = await session.WaitForConnectionAsync(TimeSpan.FromSeconds(60));

// The player proves its seat with a token, good for 60 seconds, sent over your own networking.
FlockMultiplayerSessionJoinToken token = await session.RequestJoinTokenAsync();

// The host checks it and learns who the player is; a bad token throws MultiplayerInvalidJoinToken.
string joiningPlayerId = await session.VerifyJoinTokenAsync(tokenFromPlayer);
```

`PublishDirectConnectionAsync(port)` publishes this device's own address, and a player's
`connection.FindDirectAddressAsync()` picks the one to use: the host's local address on a shared network, its public
address otherwise. Outside a local network, players reach that address only through a port forwarded to the host.

## What counts as an API call

The background reads (the party, a search, the session, waiting for the host to say where it is) and heartbeats are not
billed. They share a limit of 120 a minute per player; the SDK's defaults use under 60. Actions are billed as usual:
making or joining a party or session, starting a search, leaving, the host's check of each joining player, relay logins,
and a relayed player telling the host its relay address (once for each address).

## Limits today

- **No dedicated servers.** A player hosts every session.
- **No skill or region matching yet.** Flock stores a search's attributes but matches in the order players search. Use a
  queue per mode and region (`coop-4-eu`, `coop-4-na`).
- **A host cannot remove a player, and a session's data is set once** when it is created.
- **Netcode for GameObjects 1.x and 2.x only.** Other networking uses the session's published connection and join token
  directly; see [Other networking](#other-networking).
- **Web players** can use parties, matchmaking and sessions, but not the relay: a browser cannot open the UDP or TCP connections it needs.
