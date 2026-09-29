using System;
using System.IO;

namespace Protokite.Playtest.Tests
{
    /// <summary>
    /// For a Play Mode test on the project's own settings: the player's consent answer is kept in a file of the test's own, never
    /// the project's, and asking is off unless the test turns it on. Everything is put back when disposed.
    /// </summary>
    internal sealed class ProtokitePlaytestConsentForPlayModeTests : IDisposable
    {
        private readonly ProtokitePlaytestSettings _settings;
        private readonly bool _askedBefore;
        private readonly string _folder;

        public ProtokitePlaytestConsentForPlayModeTests(ProtokitePlaytestSettings settings, bool askThePlayer = false)
        {
            _settings = settings;
            _askedBefore = settings.AskThePlayerForPlaytestConsent;
            settings.AskThePlayerForPlaytestConsent = askThePlayer;
            _folder = Path.Combine(Path.GetTempPath(), "protokite_consent_" + Guid.NewGuid().ToString("N"));
            ConsentFilePath = Path.Combine(_folder, "playtest_consent.json");
            ProtokitePlaytest.ConsentFilePathForTesting = ConsentFilePath;
        }

        public string ConsentFilePath { get; }

        public void Dispose()
        {
            ProtokitePlaytest.ConsentFilePathForTesting = null;
            _settings.AskThePlayerForPlaytestConsent = _askedBefore;
            try
            {
                if (Directory.Exists(_folder))
                    Directory.Delete(_folder, true);
            }
            catch (IOException)
            {
            }
        }
    }
}
