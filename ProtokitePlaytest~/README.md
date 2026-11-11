# Protokite Playtest

Playtesting for games built with the Flock SDK: each play session, gameplay recording and in-game feedback,
reported to your Protokite playtest.

It asks the player what the playtest may collect, runs one Protokite session per launch, records the game's screen on 64-bit
Windows and Android and uploads it to that session, sends performance and scene events through the Flock SDK, and shows your playtest's
feedback form. In the editor it checks your setup, records test videos and runs a live self-test. A sample puts the calls a
game makes most on one screen.

> **Setting it up for the first time?** The step-by-step guide, with screenshots, is at
> [docs.qwacks.com/protokite/unity-package](https://docs.qwacks.com/protokite/unity-package). This README is the full reference.

## Install

You need the **Flock SDK** in your project first, at the **same version** as this package.

**One click (recommended).** Open **Flock > Settings**, go to the **Playtesting** tab and press **Install Protokite
Playtest**. It downloads the version that matches your Flock SDK and imports it into `Assets/ProtokitePlaytest`. No Git
needed. Once Unity has compiled it, the tab shows the version installed and where, with **Open Playtest Settings**, **Check
Playtest Setup** and **Remove**. Pictures of the tab before and after:
[docs.qwacks.com/protokite/unity-package#step-1-install-the-package](https://docs.qwacks.com/protokite/unity-package#step-1-install-the-package).

**From the release page.** Download `ProtokitePlaytest-<version>.unitypackage` from the same GitHub release as your
Flock SDK and double-click it. It imports into `Assets/ProtokitePlaytest`, as the one-click install does.

**With Package Manager.** **Window > Package Manager > + > Add package from git URL**, then:

```
https://github.com/QwackStack/FlockUnitySDK.git?path=/ProtokitePlaytest~#v<version>
```

Use the same `<version>` as your Flock SDK. This route needs Git installed on your machine. The package then sits in
Package Manager's own folder, `Packages/com.protokite.playtest`, shown in the Project window as **Packages > Protokite
Playtest**, and you change its version in Package Manager.

Whichever way you install it, the folder holds `Runtime/`, `Editor/` and `Samples/PlaytestSample/` (see [The sample](#the-sample)).

**Which version is installed.** Flock's **Playtesting** tab reads `Installed: version …, in …`; a package added through
Package Manager also shows its version in Package Manager.

**A game with assembly definitions of its own.** The package's code is the assembly `Protokite.Playtest`. Scripts outside
any assembly definition see it already; a script inside one of your own assembly definitions needs `Protokite.Playtest` added
to that definition's **Assembly Definition References** (select the `.asmdef`, add it, press **Apply**).

## Switch it on

1. **Protokite > Playtest > Settings** (or **Open Playtest Settings** on Flock's Playtesting tab). The settings are created
   the first time, at `Assets/Resources/ProtokitePlaytestSettings.asset`, with playtesting **off**.
2. Tick **Playtesting Enabled**.
3. Paste the ID from your playtest's page in Protokite into **Playtest ID**: the test's ID, or the Flock version ID the
   page's SDK block shows. The editor finds the playtest's version through Flock and shows it under **Resolved Version ID**.
   **Leave it empty and a build joins your game's newest playtest**, whichever is newest in Protokite when it starts.

A picture of the settings is at
[docs.qwacks.com/protokite/unity-package#step-2-point-the-build-at-the-playtest](https://docs.qwacks.com/protokite/unity-package#step-2-point-the-build-at-the-playtest).

**Protokite Api Url** is filled in for you with Protokite's address, `https://api-protokite.qwacks.com`. Leave it as it is,
unless you run a Protokite server of your own. Your Flock API key is used as it is, and your Flock **Game Version** stays your
game's own: only the playtest's requests to Protokite carry the playtest's version, so everything you set up in Flock under
your Game Version (player templates, configs, the shop) keeps working in a playtest build.

- The Playtest ID is resolved when it, the Flock API URL or the API key changes, while the settings are open, and **Resolve
  Again** asks again. The line under it says what Flock found: the playtest, an ID no playtest of your game has, a release
  version's ID, another game's playtest, or that Flock could not be reached (a build then asks for the version resolved before).
- A Playtest ID that is set but not resolved leaves playtesting off, with a warning, and a build with **Playtesting Enabled**
  on is refused until it is resolved. It never falls back to the Game Version. A build is refused too when the Playtest ID was
  resolved with other Flock settings (another API URL or key, such as a development Flock before a release build): open the
  playtest settings once with the new Flock settings, and it is resolved again.
- **With Playtest ID empty, a build joins the game's newest playtest** in Protokite, whichever is newest when the build starts
  (even one you have closed, which then takes no sessions); a playtest created later is joined from the next launch, never in
  the middle of one. The one exception keeps earlier projects as they were: a Flock **Game Version** that is a playtest's own
  name, `pt-<test id>`, still joins that playtest. Fill in Playtest ID to choose.

**Three ways to choose the playtest; use one.** A filled-in **Playtest ID** joins that playtest: use it for a playtest you
send to players. An empty Playtest ID joins the game's newest playtest: handy while you try things out with one playtest at a
time. An empty Playtest ID with a `pt-<test id>` **Game Version** is the way before 1.68.0, kept so earlier projects still
work; the whole game then runs under the playtest's version, so a new project uses the Playtest ID instead.

## Check your setup

**Protokite > Playtest > Setup Checks And Test Video** (or **Check Playtest Setup** on Flock's Playtesting tab) checks what
a build of this project needs for a playtest, and says what to change for each check that fails, with a button that opens
the place to change it (**Open Playtest Settings**, **Open Flock Settings**, **Open Build Settings**, **Resolve Playtest
ID**, or **Set Game Version To pt-...**). A picture of the window is at
[docs.qwacks.com/protokite/unity-package#step-3-check-your-setup](https://docs.qwacks.com/protokite/unity-package#step-3-check-your-setup).

The window shows four checks: the switch, the URL, which playtest a build joins, and video. The three rows about which playtest
are alternatives, and only the one that matches your settings is shown.

| The window says, when it passes | When |
|---|---|
| Playtesting is turned on | the playtest settings exist and **Playtesting Enabled** is on |
| Protokite API URL is usable | **Protokite Api Url** is an http or https address with a host and no spaces, by the same rule the game uses |
| Playtest ID chooses this build's playtest | **Playtest ID** is resolved to a playtest's version |
| The game's newest playtest | **Playtest ID** is empty, so a build joins the game's newest playtest |
| Game Version is a playtest's version | **Playtest ID** is empty and Flock's **Game Version** is a playtest's name, `pt-<test id>`, resolved to the ID a build sends |
| Players built for this platform record video | the build target is 64-bit Windows (x64), or Android with **Record Video On Android** on |

A check that fails says so in its own words instead, such as "Playtesting is turned off", "Playtest ID is not resolved yet"
or "Players built for WebGL record no video", with what to change under it.

- **Paste an ID into Playtest ID, never into Game Version.** Flock resolves Game Version by name: an ID pasted into Game
  Version resolves to nothing, so a build keeps sending the ID resolved before. With Playtest ID empty and such an ID in Game
  Version, the window asks Flock, with your API key, which version the ID is, and when that version is a playtest's whose
  name resolves back to it in your game, offers **Set Game Version To pt-...**, which sets the name and the ID it resolves to.
  Only a playtest's name is ever offered.
- The Playtest ID, or with it empty the Game Version, is checked with Flock when the window opens and when the settings
  change; **Check Again** asks again, and **Resolve Playtest ID** resolves it. When Flock cannot be asked, the check goes by
  what the settings hold and says so.
- Under **While testing**, **Forget This Machine's Answer** makes the next Play ask the consent question again (and, on a phone,
  the question about upload networks after it), and in Play Mode
  **Open Feedback Form** opens the form the way its key does.
- The Video check reads the active build target: players built for another platform, or for 32-bit or ARM64 Windows, record
  no video, and everything else in the playtest still runs there. An Android build records with the phone's own hardware
  encoder; the check cannot see the phones your players use, so a test video on a phone shows whether it records there.
- Game Version is read from the `FlockConfig` asset in a Resources folder, where the Flock SDK's own start-up reads it.

## Is it running?

Once the Flock SDK is running, the playtest asks Protokite for this build's playtest, using your Flock API key and the
playtest's version (the one **Playtest ID** resolved to; with it empty none, so Protokite answers with the game's newest
playtest, or a `pt-` Game Version's ID). With no Playtest ID, the playtest that answers is kept for the whole launch. `ProtokitePlaytest.Status` says whether the playtest can run, and if not, why. Each status that stops it is
logged once, as a warning that says what to change, and `ProtokitePlaytest.Describe(status)` gives the same words.

| Status | Meaning |
|---|---|
| `TurnedOff` | Playtesting is switched off, or the project has no playtest settings |
| `ProtokiteApiUrlMissing` | **Protokite Api Url** is empty |
| `ProtokiteApiUrlUnusable` | **Protokite Api Url** is not an http or https address, or has a space in it |
| `WaitingForFlock` | Everything is set; the Flock SDK has not started yet |
| `FetchingPlaytestConfig` | Asking Protokite for this build's playtest |
| `PlaytestNotLinked` | Protokite has no playtest for this build: none for the version it asks for, or, with Playtest ID empty, none in the game at all |
| `ProtokiteRefusedApiKey` | Protokite did not accept the Flock API key |
| `PlaytestConfigUnavailable` | Protokite could not be reached. The game carries on, and it is asked again when the next Flock session starts |
| `PlaytestConfigForAnotherVersion` | Protokite answered with another version's playtest (a proxy dropping the version header does this) |
| `Ready` | The playtest's config is loaded |
| `PlaytestNoLongerCollecting` | The playtest has closed and takes no more sessions, so playtesting is off until the game is launched again |
| `WaitingForPlayerConsent` | The playtest is loaded, and nothing is collected until the player answers the consent question |
| `PlayerRefusedPlaytest` | The player asked the playtest to collect nothing, so it behaves as with playtesting off, except that a feedback form they send still goes |
| `PlaytestIdNotResolved` | **Playtest ID** is set but not resolved to a playtest, so no playtest is asked for. Open the settings while Flock can be reached |

A refusal (`PlaytestNotLinked`, `ProtokiteRefusedApiKey`, `PlaytestConfigForAnotherVersion`) is not asked again until the Flock SDK
is started again (or the game relaunched), since the answer would be the same.

To react to the status in your game, for a debug overlay or to hide what only makes sense in a playtest, read it each frame
and act when it changes:

```csharp
using Protokite.Playtest;
using UnityEngine;

public class PlaytestStatusLog : MonoBehaviour
{
    private ProtokitePlaytestStatus? _lastStatus;

    private void Update()
    {
        ProtokitePlaytestStatus status = ProtokitePlaytest.Status;
        if (status == _lastStatus)
            return;
        _lastStatus = status;

        // The same words the playtest logs, for a debug overlay or your own log.
        Debug.Log($"Playtest status: {status}. {ProtokitePlaytest.Describe(status)}");

        switch (status)
        {
            case ProtokitePlaytestStatus.Ready:
                Debug.Log("Playtest " + ProtokitePlaytest.Config.TestId + " is running.");
                break;
            case ProtokitePlaytestStatus.WaitingForPlayerConsent:
                Debug.Log("The consent question is on screen: keep your own menus closed until it is answered.");
                break;
            case ProtokitePlaytestStatus.TurnedOff:
            case ProtokitePlaytestStatus.PlayerRefusedPlaytest:
                Debug.Log("Nothing is collected in this launch.");
                break;
        }
    }
}
```

Add it to any GameObject in your first scene. The Console then shows one line each time the status changes, for example
`Playtest status: WaitingForFlock. Playtesting is set up and waiting for the Flock SDK to initialize.`, then `FetchingPlaytestConfig`,
then `WaitingForPlayerConsent` or `Ready`.

## The player's consent

Before a playtest build collects anything, it asks its player what the playtest may collect: **the screen and play data**,
**the screen only**, **play data only**, or **nothing**. Nothing is recorded, measured or sent, and no Protokite session is
started, until they answer, and **nothing** reads like **Playtesting Enabled** off, except that a feedback form the player
sends themselves still goes: it is their own message, not something the playtest collects. The question says in its own words
that it is the playtest's, separate from any privacy or analytics choice the game asks about: it does not change the Flock
SDK's analytics consent, and exceptions the Flock SDK captures are the Flock SDK's, whatever the answer.

The question is drawn over the game once this build's playtest has loaded, with UI Toolkit built from code: nothing to
import, and no EventSystem of the game's needed; it works with the Input Manager, the Input System or both. It is answered
with the mouse: while it is on screen the cursor is shown and free, and it goes back to how the game had it once the player
answers. No answer is selected when it appears, so the game's own Submit key (Space, by default) presses nothing, and a
press in its first half second is ignored. A game that locks the cursor again every frame puts each click at the screen's
centre, so no click is taken as an answer there and a warning says so: stop locking it while `IsConsentQuestionOpen`.

The answer is kept in `ProtokitePlaytest/playtest_consent.json` under the game's persistent data folder and used by every
later launch. **An answer already given counts even in a build that stops asking.** Each session start carries it in its
debug facts as `playtest_consent` (`video_and_play_data`, `video_only`, `play_data_only`, `nothing` or `not_answered`) and
`playtest_consent_asked` (`true` or `false`), so a session with no recording reads as a player who asked for none.

| Setting | Default | |
|---|---|---|
| Ask The Player For Playtest Consent | on | Put the question, and collect nothing until it is answered; on a phone that records, the question about upload networks follows (below). Turn it off only where players were asked another way, or for a test run with nobody to answer; everything the playtest turns on is then collected, and each session says nobody was asked |

A picture of the question is at
[docs.qwacks.com/protokite/unity-package#step-5-press-play](https://docs.qwacks.com/protokite/unity-package#step-5-press-play).

Your game can show the answer and take the question over from its own menus:

```csharp
using Protokite.Playtest;
using UnityEngine;

public class MyPlaytestPrivacyMenu : MonoBehaviour
{
    // Show what the playtest may collect, in the playtest's own words.
    public void ShowCurrentAnswer()
    {
        ProtokitePlaytestConsentChoice inForce = ProtokitePlaytest.PlaytestConsent;
        Debug.Log("In force: " + inForce + ". " + ProtokitePlaytest.Describe(inForce));
        Debug.Log("What the player answered on this device: " + ProtokitePlaytest.PlayersConsentAnswer);
    }

    // A button in your own menu answers for the player.
    public void AnswerScreenOnly()
    {
        if (!ProtokitePlaytest.SetPlaytestConsent(ProtokitePlaytestConsentChoice.VideoOnly))
            Debug.LogWarning("The answer could not be saved, so nothing is collected; the Console says why.");
    }

    // Forget the answer: the playtest puts its question again.
    public void ForgetTheAnswer()
    {
        ProtokitePlaytest.SetPlaytestConsent(ProtokitePlaytestConsentChoice.NotAnswered);
    }

    // Put the playtest's own question on screen again, so the player can change their answer.
    public void AskAgain()
    {
        if (!ProtokitePlaytest.AskForPlaytestConsent())
            Debug.Log("The question could not be put: " + ProtokitePlaytest.Describe(ProtokitePlaytest.Status));
    }

    private void Update()
    {
        // Leave the screen to the playtest's question while it is open.
        if (ProtokitePlaytest.IsConsentQuestionOpen)
            return;
        // Your menu's own input goes here.
    }
}
```

- `PlaytestConsent` is the answer in force: the player's, or with none `NotAnswered` in a build that asks and
  `VideoAndPlayData` in one that does not. `PlayersConsentAnswer` is only what the player answered on this device.
- The answers are `VideoAndPlayData`, `VideoOnly`, `PlayDataOnly`, `Nothing`, and `NotAnswered`, which forgets the answer so
  the question is put again.
- `SetPlaytestConsent` applies the answer at once and logs `[Protokite Playtest]` with what the answer lets the playtest
  collect and where it is kept. It returns false, and collects nothing, when the answer could not be saved.
- `AskForPlaytestConsent` returns false when no playtest is loaded, as there is nothing to ask about.

- **Taking the screen back deletes this launch's recording**, rather than keeping it for a later launch to send. One already
  uploading goes on.
- **Recordings earlier launches kept wait** while the question is on screen (and while the config that decides whether it is
  put is on its way), and for as long as the answer is **nothing**, even with Playtesting Enabled off. They are kept, and go
  in the same launch the player changes their mind.
- A game that runs in batch mode or without graphics cannot show the question, so it collects nothing and says so once:
  answer with `SetPlaytestConsent`, or turn asking off.
- In the editor, **Forget This Machine's Answer** in **Protokite > Playtest > Setup Checks And Test Video** forgets the answer,
  so the next Play asks again.

### On a phone: which networks recordings upload on

On an Android player that records video (**Record Video On Android** on, and the playtest turns video on), an answer that lets
the screen be recorded is followed, on the same panel, by a second question: upload recordings **on Wi-Fi only**, or **on
Wi-Fi or mobile data**. The second answer says what uploading on mobile data costs at the recording's own bitrate (about 11 MB
for each minute played at the default 1500 kbps). The answer decides only when recordings upload, never whether the screen is
recorded: a Wi-Fi only player's recordings wait on the phone and upload the next time it is on Wi-Fi, in the same launch or a
later one. Unity reports any network that is not the phone's mobile data (Wi-Fi, Ethernet, tethering) as a local one, and that
counts as Wi-Fi. An upload under way stops when the phone leaves Wi-Fi, and its recording is kept and sent again, from the
start, on Wi-Fi.

The answer is kept beside the consent answer, in `ProtokitePlaytest/playtest_upload_network.json`, and like it counts in a build
that stops asking. The Protokite session waits for it, so each session start carries it in its debug facts as
`playtest_upload_network`: `wifi_only`, `wifi_and_mobile_data`, or `not_asked` when no answer is saved (a desktop, a build that
asks nobody, or an answer that records nothing, unless the player answered in an earlier launch). The screen is recorded
meanwhile; a game closed while the question is open has no session for what it recorded, so the next launch deletes it. A phone
that has already said it can record no video is not asked. Recordings earlier launches kept wait while the question may
still be put. Asking the consent question again (`AskForPlaytestConsent`) puts this one again after an answer that lets the
screen be recorded. Nothing is asked on Windows, and nothing waits there.

A picture of the question is at
[docs.qwacks.com/protokite/unity-features#on-a-phone-which-networks-recordings-upload-on](https://docs.qwacks.com/protokite/unity-features#on-a-phone-which-networks-recordings-upload-on).
Your game can show the answer and change it from its own settings screen:

```csharp
using Protokite.Playtest;
using UnityEngine;

public class MyUploadNetworkSetting : MonoBehaviour
{
    // The player's answer, for your own settings screen.
    public string CurrentAnswer()
    {
        switch (ProtokitePlaytest.PlayersUploadNetworkAnswer)
        {
            case ProtokitePlaytestUploadNetworkChoice.WiFiOnly: return "Recordings upload on Wi-Fi only";
            case ProtokitePlaytestUploadNetworkChoice.WiFiAndMobileData: return "Recordings upload on Wi-Fi or mobile data";
            default: return "Not answered: recordings upload on any network";
        }
    }

    // Your settings screen's toggle answers for the player.
    public void SetWiFiOnly(bool wifiOnly)
    {
        ProtokitePlaytestUploadNetworkChoice choice = wifiOnly
            ? ProtokitePlaytestUploadNetworkChoice.WiFiOnly
            : ProtokitePlaytestUploadNetworkChoice.WiFiAndMobileData;
        if (!ProtokitePlaytest.SetPlaytestUploadNetwork(choice))
            Debug.LogWarning("The answer holds for this launch but could not be kept; the next launch asks again.");
    }
}
```

The answer applies at once, and the Console says what it allows. `SetPlaytestUploadNetwork(ProtokitePlaytestUploadNetworkChoice.NotAnswered)`
forgets it, so a phone build that asks puts the question again.

## What the playtest turns on

`ProtokitePlaytest.Config` is the loaded config, and null unless the status is `Ready`: the playtest's `TestId`, its feature
switches (`Features`) and its feedback form (`Form`, null when the playtest has none). The feedback form itself is there
earlier: `ProtokitePlaytest.FeedbackForm` and **F9** already work while the status is `WaitingForPlayerConsent` or
`PlayerRefusedPlaytest`, since the form is the player's own message (see [The feedback form](#the-feedback-form)).

Check a feature with `ProtokitePlaytest.IsFeatureEnabled`, and one of `ProtokitePlaytestFeatures.VideoRecording`,
`HeavyAnalytics` or `ExceptionCapturing`. It is true only when the playtest turns the feature on **and** the player's answer
allows it; a feature the config does not mention is off (and one this build does not know needs the answer that allows
everything):

```csharp
using Protokite.Playtest;
using UnityEngine;

public class RecordingIndicator : MonoBehaviour
{
    [SerializeField] private GameObject _recordingIcon;   // a small "REC" icon in your HUD

    private void Update()
    {
        // True only when the playtest turns video on and the player let their screen be recorded.
        _recordingIcon.SetActive(ProtokitePlaytest.IsFeatureEnabled(ProtokitePlaytestFeatures.VideoRecording));
    }

    public void LogWhatThisPlaytestCollects()
    {
        ProtokitePlaytestConfig config = ProtokitePlaytest.Config;
        if (config == null)
        {
            Debug.Log("No playtest is running: " + ProtokitePlaytest.Describe(ProtokitePlaytest.Status));
            return;
        }
        Debug.Log("Playtest " + config.TestId
            + ": video " + ProtokitePlaytest.IsFeatureEnabled(ProtokitePlaytestFeatures.VideoRecording)
            + ", play data " + ProtokitePlaytest.IsFeatureEnabled(ProtokitePlaytestFeatures.HeavyAnalytics)
            + ", exceptions " + ProtokitePlaytest.IsFeatureEnabled(ProtokitePlaytestFeatures.ExceptionCapturing)
            + ", feedback form " + (config.Form != null));
    }
}
```

Drag your HUD's icon onto **Recording Icon** in the Inspector. The icon shows from the moment the playtest is loaded and the
player's answer lets the screen be recorded; `LogWhatThisPlaytestCollects` logs a line such as
`Playtest 01J…: video True, play data True, exceptions True, feedback form True`.

## The feedback form

When your playtest publishes a feedback form in Protokite, the player opens it over the game with **F9** and fills it in.
It is built from what you published, every question, label, help text and option, so editing the form in Protokite needs no
new build: text, many-line text, a rating from 1 to 5, a choice of options, and a checkbox. A question of a kind this
package does not know yet is a text box, which is how Protokite reads it. It is the player's own message, so it opens and
sends **whatever their consent answer**, "nothing" included; it never opens over the consent question.

- **Send checks the answers the way Protokite does**, and shows every problem against its question before anything is sent:
  a required question left empty, a rating out of range, an option the question does not offer. A required checkbox counts
  when it is left unticked, and an empty optional answer is left out rather than sent empty.
- **A sent form is kept on the device first and sent from there**, so neither a closed game nor a lost network loses it: one
  that could not go now is sent when the network comes back, two minutes later, or by a later launch. One Protokite refuses
  for good is deleted, and the log names the question it refused. It names this launch's Protokite session once one has
  started, and is sent with the Game Version ID it was filled in under.
- A form that names no session, whose send failed after Protokite may already have stored it, is sent again: the studio may
  see that report twice rather than lose it. One naming a session is replaced, never added.
- Closing it without sending keeps what was typed for the rest of the launch. Escape in a text box does nothing (Unity's own
  text box would put back what it held before), and a click puts the caret where it lands rather than selecting everything.
- **Upload your recording**: while this launch records the screen and its session has started, the form offers to stop the
  recording and send it now. Opening the form never stops the recording on its own.
- While the form is open the cursor is shown and free. **The game still reads its own keys**: a game that moves on WASD, or
  opens a menu on Escape, should ignore its input while `ProtokitePlaytest.IsFeedbackFormOpen`, or turn on **Pause The Game
  While The Form Is Open**. A click in the form's first half second is ignored, and so is one while the game locks the cursor
  again every frame (a warning says so).
- With the old Input Manager, Unity's runtime UI misses the odd quick click, or takes it where the cursor has moved to (up to
  10 in 100 measured; the Input System missed none). Nothing wrong is sent: the player sees the click did nothing, and clicks again.

| Setting | Default | |
|---|---|---|
| Feedback Form Key | F9 | Opens the form and closes it again, also while the player types in it. Click **Detect Key** and press the key (Escape or **Cancel** keeps the old one), or choose it from the list. Read when the playtest loads; None leaves opening it to your game |
| Pause The Game While The Form Is Open | off | The time scale is 0 while the form is open, and put back when it closes unless the game set another one meanwhile |
| Feedback Form Logos | empty | Your own images (a studio's, an event's or its sponsors'), drawn left to right in a row above the form's title, each in its own shape. Import each with **Non-Power of 2** set to **None** (otherwise Unity rounds its size to a power of two and the logo is drawn stretched) and **Generate Mip Maps** on (so it stays smooth drawn smaller). Images with transparent backgrounds suit the form's dark panel. Only the images listed go into a build |
| Feedback Form Logo Height | 40 | How tall each logo is drawn, from 16 to 96 points at a 1280x720 screen |
| Feedback Form Footer Logo | empty | Empty, the form shows Powered by Qwacks at its bottom left, on the row of Close and Send. One small image of your own drawn there instead, in its own shape; on a narrow screen it is drawn smaller, never the buttons. Import it like the logos above |
| Feedback Form Footer Logo Height | 22 | How tall the footer logo is drawn, from 12 to 48 points at a 1280x720 screen |

### Open it from your own button

F9 works with no code. To open the form from a button of your own, show the button only while there is a form to open, and
call `OpenFeedbackForm` when it is pressed:

```csharp
using Protokite.Playtest;
using UnityEngine;
using UnityEngine.UI;

public class FeedbackButton : MonoBehaviour
{
    [SerializeField] private Button _feedbackButton;

    private void Update()
    {
        // Show the button only while there is a form to open.
        _feedbackButton.gameObject.SetActive(ProtokitePlaytest.CanOpenFeedbackForm && !ProtokitePlaytest.IsFeedbackFormOpen);
    }

    // Wire this to the button's On Click.
    public void OpenFeedback()
    {
        if (!ProtokitePlaytest.OpenFeedbackForm())
            Debug.Log("The feedback form cannot open now: no form, the consent question is up, or one is already open.");
    }
}
```

- `CanOpenFeedbackForm` is true once a playtest with a published form has loaded, whatever the player's consent answer. It
  is false with no playtest, with a playtest that publishes no form, and before the playtest has loaded.
- `OpenFeedbackForm()` returns false when there is no form, one is already open, the consent question is on screen, or nothing
  can be drawn (batch mode, no graphics). `CloseFeedbackForm()` closes it without sending and keeps what was typed.
- `CanSendTheRecording` and `StopRecordingAndSendIt()` are what the form's recording button does, for a button of your own.

### A form of your own

A game that draws its own form reads the questions from `ProtokitePlaytest.FeedbackForm` and sends the answers through the
same checking and keeping as the built-in form. Each answer is recorded by the question's `Id`, so read the ids from the form
rather than typing them: the studio can change the questions in Protokite without a new build.

```csharp
using System.Collections.Generic;
using Protokite.Playtest;
using UnityEngine;

public class MyFeedbackScreen : MonoBehaviour
{
    private readonly ProtokitePlaytestFormAnswers _answers = new ProtokitePlaytestFormAnswers();

    // 1. Read the questions the studio published.
    public void ShowQuestions()
    {
        ProtokitePlaytestForm form = ProtokitePlaytest.FeedbackForm;
        if (form == null)
        {
            Debug.Log("No form: the playtest has none, or it has not loaded.");
            return;
        }
        Debug.Log(form.Title + ": " + form.Description);
        foreach (ProtokitePlaytestFormField field in form.Fields)
        {
            string required = field.Required ? " (required)" : "";
            Debug.Log($"{field.Id}: {field.Label}{required} [{field.Type}] {field.HelpText}");
            if (field.Type == ProtokitePlaytestFormFieldTypes.Select)
                Debug.Log("  options: " + string.Join(", ", field.Options));
        }
    }

    // 2. Record an answer for each kind of question, by the field's Id.
    public void Answer(ProtokitePlaytestFormField field, string text, int stars, bool ticked, string chosen)
    {
        switch (field.Type)
        {
            case ProtokitePlaytestFormFieldTypes.Rating:
                _answers.SetRating(field.Id, stars);            // 1 to 5; 0 is sent as unanswered (Remove takes it back)
                break;
            case ProtokitePlaytestFormFieldTypes.Checkbox:
                _answers.SetChecked(field.Id, ticked);          // unticked is an answer too
                break;
            case ProtokitePlaytestFormFieldTypes.Select:
                _answers.SetChosenOption(field.Id, chosen);     // one of field.Options, letter for letter
                break;
            default:
                _answers.SetText(field.Id, text);               // text, textarea, and any kind this package does not know
                break;
        }
    }

    // 3. Find problems before sending, then send.
    public bool Send()
    {
        IReadOnlyList<ProtokitePlaytestFormProblem> problems = _answers.FindProblems(ProtokitePlaytest.FeedbackForm);
        foreach (ProtokitePlaytestFormProblem problem in problems)
            Debug.Log(problem.FieldId + ": " + problem.Message);   // show each next to its question
        if (problems.Count > 0)
            return false;

        // True: the answers are kept on this device and sent in the background (the Console says when Protokite took them).
        bool kept = ProtokitePlaytest.SendFeedbackForm(_answers);
        if (kept)
            _answers.Clear();
        return kept;
    }
}
```

| Question kind (`field.Type`) | Constant | Record it with | Read it back with |
|---|---|---|---|
| One line of text | `ProtokitePlaytestFormFieldTypes.Text` | `SetText(id, text)` | `GetText(id)` |
| Many lines of text | `ProtokitePlaytestFormFieldTypes.TextArea` | `SetText(id, text)` | `GetText(id)` |
| A rating from 1 to 5 | `ProtokitePlaytestFormFieldTypes.Rating` | `SetRating(id, stars)`; 0 is sent as unanswered | `GetRating(id)`, 0 when unanswered |
| One of the options | `ProtokitePlaytestFormFieldTypes.Select` | `SetChosenOption(id, option)`, one of `field.Options`; "" is sent as unanswered | `GetText(id)` |
| A checkbox | `ProtokitePlaytestFormFieldTypes.Checkbox` | `SetChecked(id, ticked)` | `IsChecked(id)` |
| A kind this package does not know yet | its own `field.Type` text | `SetText(id, text)` | `GetText(id)` |

`IsAnswered(id)` says whether anything was recorded for a question, a rating of 0 or an empty option included
(they are sent as unanswered, yet `IsAnswered` stays true); `Remove(id)` takes an answer back entirely and `Clear()` empties them all. An answer
under an id the form does not have is left out when sending, not refused, so a typo loses that answer quietly: take ids from
`form.Fields`.

**Finding problems.** `FindProblems(form)` checks the answers the way Protokite does and returns one problem per question
(`FieldId`, and a `Message` to show beside it): "This one is needed." for a required question left empty, "Choose a rating
from 1 to 5." and "Choose one of the options given.". An empty list means Protokite would take the form.

**What sending tells you.** `SendFeedbackForm` returns **true when the answers are kept on this device**, not when Protokite has
them: sending happens in the background, and nothing is lost if the game closes or the network drops meanwhile. It returns
false, with a warning in the Console, when no playtest with a form is loaded, no answers were given, `FindProblems` is not
empty, or the answers could not be kept. What happens next shows only in the Console:

| Console line | What it means | What to do |
|---|---|---|
| `[Protokite Playtest] A feedback form was sent to Protokite (stored as …)` | Protokite has the answers | Nothing |
| `… could not be sent now (HTTP …), so the waiting forms are kept and tried again when the network comes back or in 2 minutes` | No network, or Protokite could not take it now | Nothing: it is sent later, or by a later launch |
| `Protokite refused this build's Flock API key when sending a feedback form (HTTP 401)` | The build's API key is wrong | Check **Api Key** in **Flock > Settings > Configuration**; the forms are sent by a later launch with the right key |
| `A feedback form kept at … was refused by Protokite (HTTP 422 …): … It is deleted.` | Protokite refused that form for good (HTTP 422 or 404), so sending it again would be refused again | Read Protokite's reason in the line, which names the question when Protokite names one, and check the form in Protokite |

A game cannot wait for the answer from Protokite: there is no event or result for it, only these lines.

Forms waiting to be sent are kept in `ProtokitePlaytest/FeedbackForms` under the game's persistent data folder, one file each,
with no API key in them. In the editor, **Open Feedback Form** in **Protokite > Playtest > Setup Checks And Test Video** opens it
in Play Mode.

## Sessions

A Protokite session is one launch of the game, as it shows on the playtest's **Sessions** page in Protokite. Each launch runs
**one**. It starts once the playtest's config is loaded, the player's answer lets it collect something, and a Flock session
(the Flock SDK's own record of a play session) has reached the server. That happens when a player signs in with **Analytics
Enabled** and **Analytics Auto Start Session** on (both in **Flock > Settings > Advanced Settings**, the second under
**Analytics Settings**), or when you call `FlockClient.Instance.Analytics.StartSessionAsync()`, and once consent is given
when **Analytics Require Explicit Consent** is on. Signing out and in, a new Flock session or restarting Flock never start a
second one. The session ends when the game quits; quitting waits up to 3 seconds for Protokite to take the end.
`ProtokitePlaytest.PlaytestSessionId` is its id once it has started, and null before.

**The playtest never signs a player in**, so your game must. A game with no sign-in screen of its own can sign in with this
device, registering it the first time:

```csharp
using System;
using Flock;
using Flock.Exceptions;
using UnityEngine;

public class SignInWithThisDevice : MonoBehaviour
{
    private async void Start()
    {
        // The Flock SDK starts by itself with Auto-Initialize On Load on, or with a FlockBootstrap in the scene.
        if (!FlockClient.IsInitialized || FlockClient.Instance.IsAuthenticated)
            return;
        // With no id, both calls use the device id the Flock SDK keeps for this install.
        try
        {
            try
            {
                await FlockClient.Instance.Authentication.LoginWithDeviceAsync();
            }
            // A device Flock has never seen has no account yet: register it, which also signs it in.
            catch (FlockException ex) when (ex.ErrorCode == FlockErrorCode.PlayerInvalidLoginCredentials)
            {
                await FlockClient.Instance.Authentication.RegisterWithDeviceAsync();
            }
            Debug.Log("Signed in as player " + FlockClient.Instance.CurrentPlayerId + ". The playtest's session starts next.");
        }
        catch (Exception ex)
        {
            Debug.LogWarning("Sign-in failed: " + ex.Message);
        }
    }
}
```

Add it to a GameObject in your first scene. With no id passed, the Flock SDK makes a device id on the first launch and
keeps it, so every launch signs in as the same player; a game that already signs players in with an id of its own passes
that id instead. Once signed in, the Console shows
`[Protokite Playtest] Protokite session … started for this launch`.

**Who is playing.** The package makes a device id of its own once (not the one Flock signs in with) and keeps it in
`ProtokitePlaytest/device_id.txt` under the game's persistent data folder. A game with Steam sends the Steam id instead, before the session starts. This needs the
[Steamworks.NET](https://github.com/rlabrecque/Steamworks.NET) package, with Steam running:

```csharp
using Protokite.Playtest;
using Steamworks;   // the Steamworks.NET package
using UnityEngine;

public class PlaytestSteamName : MonoBehaviour
{
    // Call once Steam is running, before your game signs the player in to Flock.
    public void UseSteamId()
    {
        string steamId = SteamUser.GetSteamID().ToString();
        if (!ProtokitePlaytest.SetSteamId(steamId, SteamFriends.GetPersonaName()))
            Debug.Log("Steam id not used: the session has already started, or the id was refused; the Console says which.");
    }
}
```

An id that is empty, longer than 64 characters or holds whitespace is refused (not trimmed), and the device id is sent. One set
after the session has started changes nothing for this launch.

When a player is signed in to Flock, the session start also carries their sign-in, renewed first if it has run out or has a
minute or less left, so Protokite can link the session to the player's Qwacks Community account. Nothing else is needed from
the game. With no one signed in, or a sign-in that has run out and cannot be renewed, the session starts as before, unlinked;
one that could not be renewed but has not run out yet is still sent, and one Flock refuses to renew signs the player out, so
that session starts unlinked. The start carries only the sign-in its Flock session started under: if the player has signed
out, or another player has signed in, since that Flock session started, it carries none. The sign-in goes with the session
start alone and is never logged.

## Heavy analytics

When the playtest's config turns **heavy_analytics** on, the playtest sends events through the Flock SDK's `TrackEvent`,
filed under the category `playtest` (`ProtokitePlaytestEvents.Category`):

- **`performance_window`**, for every ten seconds of play: `window_seconds`, `frames`, `median_frame_time_ms`,
  `frame_time_95th_percentile_ms`, `frame_time_99th_percentile_ms` (the time 95 or 99 frames in 100 stay within, so the slowest
  frames show), `hitches` (frames slow enough to see as a stutter: those that took the hitch threshold or longer),
  `hitch_threshold_ms`, `memory_used_mb` and `memory_peak_mb` (the process's memory now, and the most it used since measuring
  started; left out where the platform does not report them), and `map` (the active scene, however it became active). A frame
  time is the real time from one frame's Update to the next, so slow motion or a paused game is measured as the frames the
  player saw.
- **`level_loaded`**, for every scene the game loads in place of the one before: `map`, `previous_map` (the scene active before
  it), and `load_seconds`, how long the load held the game up.

A window is ten seconds of play, never of the clock: time in the background and the frame a scene load holds up (a scene added
beside the current one included) are left out. A window a stop cuts short is dropped, not sent. Scenes loaded before the config
turned heavy analytics on are not reported.

**The game's own events** go on the same timeline, with the same category:

```csharp
using System.Collections.Generic;
using Protokite.Playtest;
using UnityEngine;

public class BossFightEvents : MonoBehaviour
{
    // Call when the fight starts: it lands on the session's timeline beside the performance windows.
    public void OnBossFightStarted(string bossName)
    {
        bool queued = ProtokitePlaytest.RecordPlaytestEvent("boss_fight_started",
            new Dictionary<string, object> { { "boss", bossName } });
        if (!queued)
            Debug.Log("Not recorded: no playtest is measuring play data right now.");
    }
}
```

`RecordPlaytestEvent` answers true when the event was queued, can be called from any thread, and sends only while heavy
analytics runs, which starts once the playtest's config has loaded: an event recorded earlier in the launch answers false and
is not kept. The two names the playtest sends itself, `performance_window` and `level_loaded`, are refused. The event shows in
Flock under **Dashboards > Game Metrics**, in the `playtest` category, once the Flock SDK has sent it.

| Setting | Default | |
|---|---|---|
| Hitch Frame Time Ms | 60 | A frame that takes this many milliseconds or longer is counted as a hitch |

Heavy analytics needs the Flock SDK's **Analytics Enabled** (**Flock > Settings > Advanced Settings**): with it off, nothing is
measured and the log says so once. The events are Flock events like any other, so they wait for a signed-in player and follow
the Flock SDK's analytics consent. They also share its offline queue (**Analytics Max Cached Events**, on the same tab under
**Analytics Settings**, 1000 by default, oldest dropped first):
six windows a minute fill it in under three hours offline, so raise it for playtests played offline. The playtest calls the
Flock SDK's analytics, so it cannot be used with a Flock SDK exported without Analytics, and the Playtesting tab will not
install it there.

## Video

When the playtest's config turns **video_recording** on, the game's screen is recorded from the moment the config loads,
before anyone signs in: one recording a launch, encoded as H.264 by the graphics card's (or the phone's) own video encoder and written as it
records as an MP4 file a browser plays with nothing installed. Each frame is written whole as it comes, so a recording cut
off by a crash keeps every frame before the cut. It stops for good at the length or size limit, or when the game quits;
quitting waits for the file within the same 3 seconds as the session end, and a file not finished by then stays as its
`.part` file, which the next launch finishes. Time the game spends in the background is left out, and nothing fills it: the
video goes on from the moment the player left. As the game goes to the background, everything the recording holds (frames on
their way from the graphics card, and those the encoder has not handed back) is written to its file, so a game Android ends
while it is away keeps every frame recorded before. On a phone the encoder is given back to the phone meanwhile, and the first
frame after the return starts a new stream on a keyframe in the same file.

There is nothing to call: it records by itself. `ProtokitePlaytest.IsRecordingVideo` is true while this launch's recording
captures (a test video does not count). A game that ends play on its own schedule, such as a demo's last level, can stop the
recording for good, and it then uploads like any finished recording:

```csharp
using Protokite.Playtest;
using UnityEngine;

public class EndOfDemo : MonoBehaviour
{
    // Call when your demo's last level ends: the recording stops for good and uploads like any finished one.
    public void OnDemoFinished()
    {
        if (ProtokitePlaytest.StopVideoRecording())
            Debug.Log("The playtest's recording stopped; it uploads once its file is finished.");
    }
}
```

`StopVideoRecording` returns false when nothing is recording. The Console then logs what the recording holds and, once it is
uploaded, that it was.

### Test videos

In Play Mode, **Record Test Video** in **Protokite > Playtest > Setup Checks And Test Video** records the Game view for the
number of seconds you set, with the video settings below and no playtest needed: playtesting off, nobody signed in, whatever
the player answered the consent question, since a test video is never sent. It is written to
`ProtokitePlaytest/Recordings/TestVideos/` under the game's persistent data folder, is never uploaded, and is kept until making
room for a later recording deletes it; the window shows where it went.

- It makes room for what its own length records at its bitrate (at least 1 MB), never more, and never grows past that. Room is
  made by deleting older test videos only, never a recording waiting to upload; with too little left, it is not recorded and
  the window says why.
- One at a time, and never beside the playtest's own recording: it is refused while the playtest records or is about to, and
  one recording when the playtest's config turns video on stops there, keeping what it recorded, so the playtest's recording
  starts as it would have.
- It starts at the end of a frame, once the recordings earlier launches left are gone through. Quitting finishes it, as it does
  the playtest's.
- A playtest collecting play data in the same Play session measures the test video's cost with the game's.
- On 64-bit Windows and Android, as video recording is; on a phone, from code (below).

A picture of the window in Play Mode is at
[docs.qwacks.com/protokite/unity-features#trying-it-in-the-editor](https://docs.qwacks.com/protokite/unity-features#trying-it-in-the-editor).

A game can record one itself, a debug menu's "record a clip" button for instance. It records in Play Mode or a player; in the editor
outside Play Mode it is refused, with the reason:

```csharp
using Protokite.Playtest;
using UnityEngine;

public class RecordAClipButton : MonoBehaviour
{
    private ProtokitePlaytestTestVideoState _lastState;

    // Wire this to a debug menu's "Record a clip" button.
    public void RecordAClip()
    {
        if (!ProtokitePlaytest.RecordTestVideo(30.0, out string whyNot))
            Debug.Log("No test video: " + whyNot);
    }

    private void Update()
    {
        // None, WaitingToStart, Recording, Finishing, then Finished or NotRecorded.
        ProtokitePlaytestTestVideoState state = ProtokitePlaytest.TestVideoState;
        if (state == _lastState)
            return;
        _lastState = state;
        if (state == ProtokitePlaytestTestVideoState.Finished)
            Debug.Log("Test video saved to " + ProtokitePlaytest.FinishedTestVideoPath);
        else if (state == ProtokitePlaytestTestVideoState.NotRecorded)
            Debug.Log("Test video not recorded: " + ProtokitePlaytest.TestVideoProblem);
    }
}
```

`TestVideoState` is `None` until a test video is asked for, then `WaitingToStart`, `Recording`, `Finishing`, and last `Finished`
(`FinishedTestVideoPath` is the file) or `NotRecorded` (`TestVideoProblem` says why). Once the 30 seconds are recorded and the
file is finished, the Console shows where it is.

### Uploading

**Uploads are automatic**: there is no call to make, and no event or result a game can read about them. They show in the
Console only, as `[Protokite Playtest]` lines saying when an upload starts, when it is done, and why a recording was kept.

A recording is uploaded to its Protokite session once its file is finished (it reached its length or size limit, or the game
stopped it) and the session has started, whichever comes second. It is sent straight from disk, never whole in memory, and
counts as uploaded only when the storage accepts the file itself: Protokite marks a session as having a recording as soon as
it hands out a link, which is not the same thing. A failed upload is tried once more with a fresh link. An uploaded
recording is deleted from disk; one that was not is kept, with its session, and the log says why.

A player who wants their recording sent now, rather than at its length limit or the next launch, presses **Upload your
recording** on the feedback form. A button of your own does the same. Show it only while `CanSendTheRecording` is true: it
is false when no session has started for the recording, and a press then would stop the recording and send nothing.

```csharp
using Protokite.Playtest;
using UnityEngine;

public class SendMyRecordingButton : MonoBehaviour
{
    private void OnGUI()
    {
        // Shown only while there is a recording with a session to send it to.
        if (ProtokitePlaytest.CanSendTheRecording && GUILayout.Button("Send my recording"))
            ProtokitePlaytest.StopRecordingAndSendIt();
    }
}
```

The Console then shows `[Protokite Playtest] The player asked for their recording to be sent.`, and the upload lines once the
file is finished.

Nothing is uploaded while the game quits: a recording still going then, or an upload the quit interrupts, is kept and sent by
the next launch. When a launch starts, the recordings earlier launches kept go once the folders below are gone through and
the Flock SDK is running, one at a time and the oldest first, with this build's API key and the Game Version ID each
recording's session started with. **This happens with Playtesting Enabled off too**, so a release build of the game never
strands what a playtest build recorded; with it off, nothing else is recorded or sent. They wait while the player's answer is
**nothing**, or while the consent question may still be put (see [The player's consent](#the-players-consent)). On a phone whose
player chose Wi-Fi only, every upload waits for Wi-Fi (see
[On a phone: which networks recordings upload on](#on-a-phone-which-networks-recordings-upload-on)).

Every refusal keeps the recording, including Protokite saying the session belongs to another playtest (403) or no longer
exists (404): a later launch asks again. Such a recording goes only when a launch that records needs its room in the disk
budget, so a build that never records keeps asking for it, and says so in the log each launch.

### Where recordings are kept

Each recording has a folder of its own under the game's persistent data folder,
`ProtokitePlaytest/Recordings/Playtest/<UTC time>-<8 hex digits>/` (a test video's under `TestVideos/`, with no session), holding the video, `session.json` (the Protokite session
it belongs to: the session id, the Protokite API URL and the Game Version ID the session started with, never the API key),
`reserved-bytes.txt` (the most it may take) and `in-use.lock`, which the game keeps open, shared with nobody, until it
closes (in the Editor, until Play Mode ends). The session is saved whenever it starts, before or after the recording, and
quitting saves one it waited for.

When a later launch starts, a thread of its own goes through the folders whose game has closed, however it closed:

- A video cut off by a crash, a power cut or a killed game is finished with every whole frame it holds.
- A recording with a saved session is kept, to be uploaded to that session.
- A recording no Protokite session started for is deleted: there is nothing to upload it to.
- A folder left with no video is deleted.
- A folder whose game still runs is never touched. Anything that cannot be finished or deleted (another program has it
  open) is named in a warning and tried again by the next launch.

This runs in every launch, Playtesting Enabled or not. Files an earlier version left straight in `ProtokitePlaytest/Recordings/`
are left where they are and not counted below.

**Recordings Disk Budget Mb** (on Android, **Android Recordings Disk Budget Mb**) is the most every recording kept on the
machine may take together. Before a recording starts,
recordings whose game has closed are deleted until it fits: test videos first, then recordings waiting to upload, the oldest
first. **While a phone's uploads wait on the player's answer** (Wi-Fi only and the phone off Wi-Fi, or the question still to be
answered), a recording waiting to upload is never deleted for a new one: the new one records into the room left, and with less
than 1 MB that launch records no video and the log says why, not as a warning, since it is the player's choice. At the default
1024 MB, about 94 minutes of play can wait for Wi-Fi in full. A recording starts once the folders above have
been gone through (usually milliseconds, at most 10 seconds), so nothing is deleted for room a cut-off file only seems to
take. A recording makes room only for what its length limit records at its bitrate, with a quarter to spare (about 810 MB
at the defaults), so a short recording never deletes one waiting to be uploaded that it would fit beside; it may then grow
into all the room left, up to Max Recording Size Mb, and the log says so when the budget cuts it shorter. A recording still
being written counts at the most it may grow to and is never deleted, so two copies of the game recording at once never
take each other's room. One that is finished but in use (being uploaded, or its game still running) is never deleted
either, and counts at what it takes. With less than 1 MB left, that launch records no video, and a warning names the
setting to raise.

On a phone, recordings also never take its free space below the line where Android warns that storage is running out (500 MB,
or 5% of the storage when that is less), read when each recording starts: a fuller phone gives a recording less room, the
log says when the phone's free space cut it shorter, and with less than 1 MB the warning says to free some of the phone's
storage. A recording deleted to make room counts as free space again.

The video's size is set when the recording starts. A window resized, or a phone turned between upright and sideways, later
on is recorded at its own shape inside that size, with black bars beside or above it, rather than stretched.

The frame is copied, scaled and converted on the graphics card and read back without waiting for it, then encoded on the
graphics card's (or the phone's) video encoder; handing frames to the encoder and writing the file each run on a thread of their own, and a frame that
cannot keep up is dropped rather than stalling the game. The log says where the recording goes when it starts, and what it
holds, which encoder made it, and how many frames were dropped and why, when it ends.

The settings are in **Protokite > Playtest > Settings**. An Android player reads **Video recording (Android)**; every other
build, the editor included whatever it builds for, reads **Video recording (64-bit Windows)**. A test video in the editor
therefore records with the Windows settings.

**Video recording (64-bit Windows)**

| Setting | Default | |
|---|---|---|
| Video Width, Video Height | 1280, 720 | The largest the video is. The screen's shape is kept, a smaller window is not enlarged, and each side is rounded down to a multiple of 16 |
| Video Frames Per Second | 15 | |
| Video Bitrate Kbps | 1500 | The most the video uses; a still picture uses far less |
| Allow Software Encoder | off | On: a PC whose graphics card has no H.264 encoder records with Windows' own encoder on the processor instead, which costs the game frame rate (measured at the defaults: 11% of a processor core on a desktop, against 3% on the graphics card; about 30% of a core on a slow laptop, where capturing the screen took 2 to 4% and the game kept its 60 frames a second) |

**Video recording (Android)**

| Setting | Default | |
|---|---|---|
| Record Video On Android | on | Off: Android players record no video and never ask the phone for its encoders; everything else in the playtest still runs. Read as the game starts: turned on later in a launch, it takes effect from the next one |
| Android Video Long Side | 1280 | The longer side of the video, whichever way the phone is held: on a 20:9 phone, a game held sideways records at 1280x576 and one held upright at 576x1280. A smaller screen is not enlarged, and each side is rounded down to a multiple of 16 |
| Android Video Frames Per Second | 15 | 30 doubles the phone's encoding work and the frames copied from the screen (measured) |
| Android Video Bitrate Kbps | 1500 | Held as a constant rate where the phone's encoder says it takes one (1,520 kbps measured against 1,500 asked); on an encoder that does not, a busy picture can run over (26% measured in the phone's default mode) |
| Android Allow Software Encoder | off | On: a phone with no hardware H.264 encoder records with Android's software encoder on the processor instead, which costs frame rate and battery (about 6.5 times the hardware encoder's processor time, measured) |
| Slow Down The Recording When The Phone Is Hot | on | When Android says it has started to slow the phone down for heat (thermal status moderate), the recording takes half its frame rate until the phone cools; when Android says it is slowing the phone down enough for the player to notice (severe or above), the recording stops for the launch, and what it holds is kept and uploaded as usual. Phones before Android 10 do not report their heat, and record as set; the log says so once |
| Stop The Recording Below Battery Percent | 15 | The recording stops for the launch, and what it holds is kept and uploaded as usual, when the battery falls below this percentage while the phone is not charging. 0: never |
| Android Recordings Disk Budget Mb | 1024 | The most every recording kept on the phone may take together, in place of Recordings Disk Budget Mb, and never past the phone's own low-storage line (above) |

A phone whose encoder does not take the video's size or rate records at the next size down it does (three quarters, a half,
three eighths of the long side), then at 15 and 10 frames a second, and the log says which.

While an Android player records, the phone is asked about its heat and battery every 5 seconds of play (0.14 ms for the heat and
0.07 ms for the battery on the main thread, measured on a Galaxy S23 Ultra), and at once when a recording starts; only what a setting uses is asked. Each change of rate, and a stop, is
logged once.

**Recordings (every platform)**

| Setting | Default | |
|---|---|---|
| Max Recording Minutes | 60 | |
| Max Recording Size Mb | 1536 | |
| Recordings Disk Budget Mb | 4096 | The most every recording kept on the machine may take together (above); Android players use Android Recordings Disk Budget Mb |

## The sample

The sample is one component, `ProtokitePlaytestSample` (in `Samples/PlaytestSample/ProtokitePlaytestSample.cs`), that draws a
panel of buttons over the Game view in Play Mode. It is installed with the package:

- One-click install or the `.unitypackage`: `Assets/ProtokitePlaytest/Samples/PlaytestSample/`.
- Package Manager: **Packages > Protokite Playtest > Samples > PlaytestSample** in the Project window.

To use it, set the playtest up as above, create an empty GameObject in a scene, press **Add Component** in the Inspector, type
**Protokite Playtest Sample** and pick it, then press **Play**. It signs in with this device, and shows the status, the playtest
and its session, the features on, the consent answer and the question, the upload network answer, a Steam id, a playtest
event, the recording, the feedback form (opened, sent from code and closed from code) and a test video. It does not set the
upload network (`SetPlaytestUploadNetwork`, above) or run the live self-test (below). Its README lists which button makes
which call.

## Live self-test

The self-test checks this build's playtest end to end against the live Protokite. Each check is paired with a request Protokite
must refuse, and a refusal only counts when Protokite gave the status that check expects. It runs in the editor and in
development builds; a release build refuses it. It sends each refused request once, through a client of its own with no
retries, so the refusals it provokes change nothing in the running game.

Run it in Play Mode from **Run Live Self-Test** in **Protokite > Playtest > Setup Checks And Test Video** (a picture is at
[docs.qwacks.com/protokite/unity-features#trying-it-in-the-editor](https://docs.qwacks.com/protokite/unity-features#trying-it-in-the-editor)),
or from code, from a development build's debug menu for instance:

```csharp
using Protokite.Playtest;
using UnityEngine;

public class RunPlaytestSelfTest : MonoBehaviour
{
    // Wire this to a development build's debug menu, once a player is signed in.
    public async void RunIt()
    {
        if (ProtokitePlaytestSelfTest.IsRunning)
            return;
        ProtokitePlaytestSelfTestReport report = await ProtokitePlaytestSelfTest.RunAsync();
        if (report.NotRunBecause != null)
        {
            Debug.LogWarning("The self-test did not run: " + report.NotRunBecause);
            return;
        }
        foreach (ProtokitePlaytestSelfTestStep step in report.Steps)
        {
            if (step.Outcome != ProtokitePlaytestSelfTestOutcome.Passed)
                Debug.Log($"{step.Outcome}: {step.Name} - {step.Detail}");
        }
        Debug.Log($"Self-test {report.RunId}: {report.Passed} passed, {report.Failed} failed, {report.Skipped} skipped. All passed: {report.AllRunStepsPassed}");
    }
}
```

`NotRunBecause` says why nothing ran (a release build, another self-test under way, or the Flock SDK not running). Each step
has a `Name`, an `Outcome` (`Passed`, `Failed` or `Skipped`) and a `Detail`; `AllRunStepsPassed` is true when it ran, nothing
failed and at least one step passed.

- **Sign a player in first.** It signs nobody in, and the playtest's session starts only once the game's own sign-in has.
- **Run it in a Play or launch of its own.** It stops this launch's recording to upload it. It sends a filled-in form for this
  launch's session, which replaces a form the player sent this launch, as Protokite keeps one per session. And it raises two
  exceptions on purpose to check that exceptions reach the Flock SDK, so with **Error Pause** on in the Console, Play pauses there
  until you resume it.
- Every step is logged as `[Protokite Playtest] Self-test: PASS|FAIL|SKIPPED <what it checks> - <what it found>`, a failure as a
  warning, and the run ends with its counts. The last line is a warning when anything failed or nothing passed.
- A step is **skipped** when this build or playtest leaves nothing for it to check, and it says why: no feedback form, no video on
  this platform, heavy analytics off, or no answer yet to the consent question.
- Pass a closed playtest's Game Version ID (`RunAsync("<id>")`, or the window's field) to also check that a closed playtest takes
  no session.

It checks, in order:
- The playtest config loads.
- A wrong API key, a missing one and a version no playtest is linked to are refused.
- This launch's session starts.
- A session start naming no player, and one for a closed playtest, are refused.
- An exception reaches the Flock SDK once, with its repeat counted.
- A playtest event is recorded, and the playtest's own event name is refused.
- A form missing a needed answer, choosing an option not on the list, or naming a session that does not exist is refused, and a
  filled-in form is taken.
- An upload link and an end for a session that does not exist are refused.
- This launch's recording is uploaded, and is no longer kept on disk.

The game itself cannot see whether the exception and the event arrived. The run's id (`report.RunId`) is in the exception's
message, in the event's properties, in the text answers of the form it sends, and in the extra data of every session start it
sends, so you can find them in Flock and Protokite. The session's end is sent when the game quits.

## Platforms

Everything above runs wherever the Flock SDK runs. Measured in 64-bit Windows players built with Unity 2021.3 and 6000.3, Mono (Minimal
stripping) and IL2CPP (High stripping): each records, uploads and ends its session. **Video is recorded on 64-bit Windows** (the Editor and players,
Mono and IL2CPP), through Windows' own H.264 encoder on the graphics card (Media Foundation), **and on Android** (below). The package ships no native
file, so there is nothing of its own for Windows 11's Smart App Control, which refuses unsigned native files it does not
recognise, to refuse. A PC whose graphics card has no H.264 encoder records no video and says why once in the log, unless
**Allow Software Encoder** is on; so does a Windows N edition without its Media Feature Pack, which has no Media Foundation.
Any other build records no video and says so once in the log, including 32-bit and ARM64 Windows builds. On 64-bit Windows,
recording runs on Direct3D 11 and 12, Vulkan and OpenGL, in the Built-in Render Pipeline and URP, in Linear and Gamma
colour. It needs a graphics card that runs compute shaders; a game that draws nothing (a server build, or one started with
`-nographics` or `-batchmode`) records nothing. The setup window names the encoder Windows offers first on the PC you work
on, and a test video shows whether it records.

**On Android** the screen is recorded with the phone's own hardware H.264 encoder, called through Android's media library from
C#, so the package ships no native file there either. Measured on a Galaxy S23 Ultra (Android 16) in Unity 2021.3 and 6000.3 players, IL2CPP
64-bit and Mono 32-bit, both built with High managed stripping, on Vulkan and OpenGL ES, held sideways and upright: each recorded 20 seconds at 15
frames a second that the phone's own decoder read back frame for frame and Chrome plays, at 1,511 to 1,517 kbps against the 1,500
asked, while the game kept its 60 frames a second. That is a flagship phone, so treat its costs as a best case. Measured against the
same game built without the package (400 moving lit cubes at the phone's full 3088x1440, 60 frames a second, three rounds each,
IL2CPP and Mono, High stripping): recording at the defaults cost no frame rate (60.01 frames a second against 60.01 in IL2CPP,
59.95 against 60.01 in Mono), and collecting play data alone nothing measurable. On the one frame in four that is captured,
recording took about 1 to 3.5 ms more of the graphics card's time (95th and 99th percentile) and 1.3 to 1.8 ms more of the render
thread, and the phone's media service took about 6% of a processor core; a Mono build's main thread spent about 3 ms more on those
frames. The package adds 7.0 MB to an IL2CPP APK and 1.4 MB to a Mono one. Unity builds Mono for 32-bit ARM only. Installed from its
git URL into a new Unity 6000.3 project with that project's own defaults (IL2CPP, Minimal stripping), a live playtest answered by
taps on the phone recorded, uploaded its recording and played it back. A phone whose
graphics cannot run compute shaders (OpenGL ES 3.0, measured by forcing it) or read the screen back without waiting records no
video and says why once. A phone with no hardware H.264 encoder records no video unless **Android Allow Software Encoder** is on.
A live playtest on that phone signed in, recorded, uploaded its recording (stored as MP4 that Chrome plays) and ended its session
when the game quit. Sent to the home screen for five minutes and brought back, a recording on that phone went on in the same file
from a keyframe, with the time away left out, and a game Android ended while it was away kept every frame recorded before it left.
Heat and a low battery were measured through the package's stand-ins, since ten minutes of encoding raised no heat on that phone.
Not yet measured: other phones, and x86 Android devices (some Chromebooks and emulators).

Recordings an earlier version of the package kept as WebM files are still finished after a crash and uploaded as they are.

**On WebGL** the playtest's files (the consent answer, the device id and feedback forms waiting to be sent) are copied to the
browser's storage after every change, so they are there on the player's next visit wherever the browser keeps the site's data
(a private window forgets it when closed, and a browser where the player blocked site data keeps none). A change made in the
moment before the tab closes may not finish copying, and two tabs of one game share one storage, where the last to copy wins.

## Remove it

Flock's **Playtesting** tab has a **Remove** button, or remove it as you installed it (delete
`Assets/ProtokitePlaytest`, or remove it in Package Manager). Your settings asset stays until you delete it.
