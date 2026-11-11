# Protokite Playtest Sample

A one-script screen showing the calls a game makes most to the playtest. A playtest needs no code at all: turn it on in
**Protokite > Playtest > Settings** and it records, measures and asks for feedback by itself. These calls are for a game
that wants its own menus or buttons around it. Two calls are not on it, and are shown in the package's README instead:
`SetPlaytestUploadNetwork` (which networks a phone's recordings upload on) and the live self-test
(`ProtokitePlaytestSelfTest.RunAsync`, also the **Run Live Self-Test** button in **Protokite > Playtest > Setup Checks And
Test Video**).

## Setup

1. Set up the Flock SDK in **Flock > Settings**, with your game's own **Game Version**.
2. Turn on **Playtesting Enabled** in **Protokite > Playtest > Settings**, and paste the ID from your playtest's page in Protokite
   into **Playtest ID**. **Protokite > Playtest > Setup Checks And Test Video** says what else a build needs.
3. In **Flock > Settings > Advanced Settings**, leave **Auto-Initialize On Load** on (in the **Tools** card), or add a
   **FlockBootstrap** to the scene, so the Flock SDK starts when you press Play. Leave **Analytics Enabled** on, and **Analytics
   Auto Start Session** on under **Analytics Settings**: the playtest's session starts once a Flock session has.
4. Find the sample. Installed from Flock's **Playtesting** tab or the `.unitypackage`, it is in
   `Assets/ProtokitePlaytest/Samples/PlaytestSample/`; installed with Package Manager, in **Packages > Protokite Playtest >
   Samples > PlaytestSample** in the Project window. Nothing needs copying: the component is already compiled.
5. Create an empty GameObject in a scene (**GameObject > Create Empty**), press **Add Component** in the Inspector, type
   **Protokite Playtest Sample** and pick it, then press **Play**. A panel appears at the top left of the Game view.
6. Press **Sign In With This Device**. The Console shows the playtest's lines, each starting `[Protokite Playtest]`, and the
   panel's Session line shows the session's id once it has started.

## What it shows

| On screen | Call |
|---|---|
| Sign In With This Device | `FlockClient.Instance.Authentication.LoginWithDeviceAsync()`, registering a device Flock has never seen (with no name, as Flock keeps names unique). With no id passed, both use the device id Flock keeps for this install. The playtest's session starts after the game's own sign-in. |
| Status, playtest and session | `ProtokitePlaytest.Status`, `Describe(status)`, `Config`, `PlaytestSessionId`, `IsFeatureEnabled` |
| Consent | `PlaytestConsent`, `PlayersConsentAnswer`, `Describe(answer)`, `AskForPlaytestConsent`, `SetPlaytestConsent`, `IsConsentQuestionOpen` |
| Use This Steam Id | `SetSteamId`, before the session starts |
| Record A Playtest Event | `RecordPlaytestEvent`, while the playtest measures play data |
| Stop And Send It, Stop It | `CanSendTheRecording`, `StopRecordingAndSendIt`, `IsRecordingVideo`, `StopVideoRecording` |
| Open It, Send Answers From Code | `FeedbackForm`, `CanOpenFeedbackForm`, `OpenFeedbackForm`, `SendFeedbackForm` with `ProtokitePlaytestFormAnswers` |
| Close The Form From Code | `IsFeedbackFormOpen`, `CloseFeedbackForm`, shown while the form is open |
| Record A Test Video | `RecordTestVideo`, `TestVideoState`, `FinishedTestVideoPath`, `TestVideoProblem`: never uploaded, 64-bit Windows and Android |

While the playtest's form or consent question is on screen, the sample draws only a small strip, so it never covers them.
Every call is made on the main thread, as the playtest requires.
