# Flock Multiplayer Samples

Two one-screen samples of players playing together over Netcode for GameObjects, each one component:

- **FlockPlayWithFriendsSample**: one player taps **Host** and reads out the session's code; a friend types it and taps
  **Join**.
- **FlockQuickMatchSample**: each player taps **Find Match**; the players Flock matches start playing together, the one who
  waited longest hosting.

Once connected, each lists the players, **Wave** sends a message over the netcode that the others see, and **Leave** (or
**End Session** for the host) ends it.

They connect players directly: on the same network, or where the host's port is forwarded to it. Playing across the internet
without forwarding needs Flock's relay, which comes in a later version.

## Setup

1. **Install Netcode for GameObjects** 1.x or 2.x (Package Manager). These samples, and Flock's netcode support, compile only
   with it installed.
2. **Configure Flock**: open **Flock > Settings**, fill in your API URL, API Key, Game ID and Game Version, and click **Add
   Flock Bootstrap to Scene**.
3. **For Quick Match**, make a matchmaking queue in the Flock dashboard, under Matchmaking, and put its name in the
   component's **Queue Name** (`quick-match` by default).
4. **Add a sample**: create an empty GameObject and add **FlockPlayWithFriendsSample** or **FlockQuickMatchSample**. If the
   scene has no NetworkManager, the sample makes one listening on its **Port** (7777).
5. **Press Play** in two copies of the game (a build and the editor, or two computers). Two copies on one computer share a
   device id, so change the device id in one of them before **Sign In With This Device**.

## What they show

- Signing in with a device: `Authentication.LoginWithDeviceAsync`, and `RegisterWithDeviceAsync` the first time.
- Hosting and joining by code: `Multiplayer.HostSessionAsync`, `Multiplayer.JoinSessionAsync(code)`, `session.JoinCode`.
- Finding a match: `Multiplayer.FindMatchAsync(queueName)` and its result's `Session`.
- Starting the netcode for a session: `session.StartNetcodeAsync(networkManager)`. The host lets in only players whose Flock
  join token checks out, and `session.PlayerForConnection(clientId)` names the player behind each connection.
- Leaving: `session.LeaveAsync()`, or `session.EndAsync()` for the host, and the session's `Ended` event.
