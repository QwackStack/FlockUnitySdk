using UnityEngine;

namespace Flock.Docs
{
    /// <summary>
    /// In-Editor "Getting Started" panel for the Flock Unity SDK. Opened from the
    /// 'Getting Started' link in the Qwacks/Flock editor window and rendered by
    /// FlockSdkGuideEditor. Intentionally thin — plain-language onboarding and
    /// setup only, aimed at everyone on the team (including non-programmers). The
    /// full developer reference lives in the README / online docs (see DocsUrl);
    /// do not mirror it here. Content lives in the constants below, so editing this
    /// .cs file updates the panel without re-creating the asset.
    /// </summary>
    [CreateAssetMenu(fileName = "FlockSdkGuide", menuName = "Flock/SDK Guide", order = 100)]
    public class FlockSdkGuide : ScriptableObject
    {
        // ─────────────────────────────────────────────────────────────────────
        //  LINKS TO FILL IN  — these are the only placeholders in this file.
        //  Each is surfaced as a button in this panel and in the editor window;
        //  any left unset shows as a disabled "(link not set)" button.
        // ─────────────────────────────────────────────────────────────────────

        /// Full developer documentation (docs site / hosted README).
        public const string DocsUrl = "https://docs.qwacks.com/introduction";

        /// Support / contact page. Pre-filled — confirm this is the canonical URL.
        public const string SupportUrl = "https://www.qwacks.com/flock";

        // ─────────────────────────────────────────────────────────────────────
        //  Onboarding content
        // ─────────────────────────────────────────────────────────────────────

        public const string Overview =
@"Flock is a game backend. This SDK connects your Unity game to it for:

  • Player accounts & login (email, device, Google, Apple, Steam, Facebook, Discord)
  • Saved player data and player templates
  • A shop and player inventory
  • Remote game configuration you can change without rebuilding the game
  • Downloadable assets, and analytics

You set it up once in this window. It's then called from your game's code, while most
day-to-day content (game config, shop, templates) is managed on the Flock
dashboard rather than inside Unity.";

        public const string Configuration =
@"Fill these in the Configuration section of this window. All four come from your
game's page on the Flock dashboard:

  API URL       The Flock server address. Leave the default unless told otherwise
                (default: https://api-flock.qwacks.com).
  API Key       Identifies your game. Treat it like a password.
  Game ID       Your game's unique ID.
  Game Version  Your game version name (e.g. 'v1.0.0'). Must match a version that
                exists on the dashboard.

These are saved into Assets/Resources/FlockConfig.asset.";

        public const string Setup =
@"1. Fill in Configuration above.
2. Click 'Add Flock Bootstrap to Scene'. This drops in a GameObject that starts
   the SDK automatically when the game runs — put it in your first/boot scene.
3. Re-sync from the Codegen tab in Qwacks > Flock whenever you change player templates or game config
   on the dashboard, or change Game Version, to regenerate the typed C# accessors.
4. Done — the SDK is ready to use (see Quick Start below, and the full
   documentation for everything else).";

        public const string Quickstart =
@"The shortest path once Configuration is filled in:

  // Easiest: click 'Add Flock Bootstrap to Scene' above — it initializes the SDK for you.
  // Or initialize once at startup in code (synchronous, no network):
  FlockClient.Create(flockConfig.ToInitConfig());

  // Log a player in — auth methods throw on failure.
  await FlockClient.Instance.Authentication.LoginWithDeviceAsync(""device-uuid"");

  // Read anything via FlockClient.Instance.
  var game = await FlockClient.Instance.Game.GetGameAsync();

Everything else — shop, player data, codegen, assets, offline cache, events — is
in the full documentation.";
    }
}
