# Protokite Playtest Sample

A one-script screen showing every call a game can make to the playtest. A playtest needs no code at all: turn it on in
**Protokite > Playtest > Settings** and it records, measures and asks for feedback by itself. These calls are for a game
that wants its own menus or buttons around it.

## Setup

1. Set up the Flock SDK in **Flock > Settings**, with **Game Version** set to your playtest's version name, `pt-<test id>`.
2. Turn on **Playtesting Enabled** in **Protokite > Playtest > Settings**. **Protokite > Playtest > Setup Checks And Test
   Video** says what else a build needs.
3. Leave **Auto-Initialize On Load** on in **Flock > Settings**, or add a **FlockBootstrap** to the scene, so the Flock SDK
   starts when you press Play.
4. Create an empty GameObject, add the **ProtokitePlaytestSample** component, and press **Play**.

## What it shows

| On screen | Call |
|---|---|
| Sign In With This Device | `FlockClient.Instance.Authentication.LoginWithDeviceAsync`, registering a device Flock has never seen (with no name, as Flock keeps names unique). The playtest's session starts after the game's own sign-in. |
| Status, playtest and session | `ProtokitePlaytest.Status`, `Describe(status)`, `Config`, `PlaytestSessionId`, `IsFeatureEnabled` |
| Consent | `PlaytestConsent`, `PlayersConsentAnswer`, `Describe(answer)`, `AskForPlaytestConsent`, `SetPlaytestConsent`, `IsConsentQuestionOpen` |
| Use This Steam Id | `SetSteamId`, before the session starts |
| Record A Playtest Event | `RecordPlaytestEvent`, while the playtest measures play data |
| Stop And Send It, Stop It | `CanSendTheRecording`, `StopRecordingAndSendIt`, `IsRecordingVideo`, `StopVideoRecording` |
| Open It, Send Answers From Code | `FeedbackForm`, `CanOpenFeedbackForm`, `OpenFeedbackForm`, `SendFeedbackForm` with `ProtokitePlaytestFormAnswers` |
| Close The Form From Code | `IsFeedbackFormOpen`, `CloseFeedbackForm`, shown while the form is open |
| Record A Test Video | `RecordTestVideo`, `TestVideoState`, `FinishedTestVideoPath`, `TestVideoProblem`: never uploaded, 64-bit Windows only |

While the playtest's form or consent question is on screen, the sample draws only a small strip, so it never covers them.
Every call is made on the main thread, as the playtest requires.
