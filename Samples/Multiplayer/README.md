# Flock Multiplayer Samples

Two one-screen samples of players playing together over Netcode for GameObjects, each one component:

- **FlockPlayWithFriendsSample**: one player taps **Host** and reads out the session's code; a friend types it and taps
  **Join**.
- **FlockQuickMatchSample**: each player taps **Find Match**; the players Flock matches start playing together, the one who
  waited longest hosting.

Once connected, each lists the players, **Wave** sends a message over the netcode that the others see, and **Leave** (or
**End Session** for the host) ends it.

Players on the host's network connect to it directly; everyone else connects through Flock's relay, so nobody forwards a port.
If the relay is switched off for the game in the Flock dashboard, players on other networks reach the host only on a port
forwarded to it.

If the host leaves or its game stops, Flock makes another player the host, and each sample starts its netcode again: the new
host hosts and the others join it. The same happens on a device whose relay stopped working. What the old host held is not
kept.

## Setup

1. **Install Netcode for GameObjects** 1.x or 2.x (Package Manager). These samples, and Flock's netcode support, compile only
   with it installed.
2. **Configure Flock**: open **Flock > Settings** and, on the **Configuration** tab, fill in **Api Url**, **Api Key**,
   **Game Name** and **Game Version**. No bootstrap is needed while **Auto-Initialize On Load** is on (the default, on the
   **Advanced Settings** tab); with it off, click **Add to Scene** on the **Scene bootstrap** row of the **Setup** card.
3. **For Quick Match**, make a matchmaking queue in the Flock dashboard, under Matchmaking, and put its name in the
   component's **Queue Name** (`quick-match` by default).
4. **Add a sample**: create an empty GameObject and add **FlockPlayWithFriendsSample** or **FlockQuickMatchSample**. If the
   scene has no NetworkManager, the sample makes one listening on its **Port** (7777).
5. **Press Play** in two copies of the game (a build and the editor, or two computers), and press **Sign In With This
   Device** in each. The **Device id** field starts empty, which signs in with the device id Flock keeps for this install
   (`Authentication.ThisDeviceId`). Two copies on one computer share that id, and so would be one player: type a different
   id in one of them first (any text, kept the same between runs to come back as the same player). The first sign-in of a
   device id registers it as a new player.

## What they show

- Signing in with a device: `Authentication.LoginWithDeviceAsync`, and `RegisterWithDeviceAsync` the first time, with no id
  (the kept one) or the id typed in the field.
- Hosting and joining by code: `Multiplayer.HostSessionAsync`, `Multiplayer.JoinSessionAsync(code)`, `session.JoinCode`.
- Finding a match: `Multiplayer.FindMatchAsync(queueName)` and its result's `Session`.
- Starting the netcode for a session: `session.StartNetcodeAsync(networkManager)`. The host lets in only players whose Flock
  join token checks out, and `session.PlayerForConnection(clientId)` names the player behind each connection. Its result's
  `ThroughRelay` says whether the connection goes through Flock's relay, and `RelayFailureReason` why the relay could not be used.
- Following the session: on `session.HostChanged` and `session.RelayLost` the netcode is shut down and `StartNetcodeAsync`
  called again; a start that a newer one replaced ends with the outcome `restarted` and is left alone.
- Leaving: `session.LeaveAsync()`, or `session.EndAsync()` for the host, and the session's `Ended` event.

The [Multiplayer guide](../../Docs~/multiplayer.md) explains each of these, with code to copy.
