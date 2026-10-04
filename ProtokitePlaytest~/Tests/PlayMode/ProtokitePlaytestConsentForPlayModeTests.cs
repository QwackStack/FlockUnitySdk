using System;
using System.IO;

namespace Protokite.Playtest.Tests
{
    /// <summary>
    /// For a Play Mode test on the project's own settings: the player's answers are kept in files of the test's own, never
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
            UploadNetworkFilePath = Path.Combine(_folder, "playtest_upload_network.json");
            ProtokitePlaytest.UploadNetworkFilePathForTesting = UploadNetworkFilePath;
        }

        public string ConsentFilePath { get; }

        public string UploadNetworkFilePath { get; }

        public void Dispose()
        {
            ProtokitePlaytest.ConsentFilePathForTesting = null;
            ProtokitePlaytest.UploadNetworkFilePathForTesting = null;
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
