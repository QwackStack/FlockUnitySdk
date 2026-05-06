#if UNITY_WEBGL
namespace Flock.Auth
{
    /// <summary>
    /// WebGL token store. The browser sandbox has no real secure storage —
    /// anything reachable by JS in the page is also reachable via XSS or
    /// devtools, and the build itself is decompilable. Persistence is still
    /// supported via <see cref="OtherTokenStore"/>'s PlayerPrefs path
    /// (IndexedDB/localStorage) so session restore works across reloads, but
    /// treat tokens here as "recoverable", not "secret". For higher security
    /// on browser builds, move to server-managed sessions (HttpOnly cookies)
    /// — that's a backend choice, not something the client SDK can solve.
    /// </summary>
    public class WebGlTokenStore : OtherTokenStore { }
}
#endif
