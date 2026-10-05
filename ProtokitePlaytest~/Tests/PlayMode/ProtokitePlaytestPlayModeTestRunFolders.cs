using System;
using System.IO;
using Flock.Tests.Support;
using NUnit.Framework;
using Protokite.Playtest;

/// <summary>
/// Outside any namespace, so it runs once around every test in this assembly: each test SDK keeps its analytics files in a folder of
/// the run's own, and so do the feedback forms waiting to be sent, which any test starting a Flock client would otherwise send, and
/// delete, from the project's own folder.
/// </summary>
[SetUpFixture]
public class ProtokitePlaytestPlayModeTestRunFolders
{
    private string _feedbackForms;

    [OneTimeSetUp]
    public void UseTestFolders()
    {
        FlockTestSavedFiles.Use();
        _feedbackForms = Path.Combine(Path.GetTempPath(), "protokite_run_forms_" + Guid.NewGuid().ToString("N"));
        ProtokitePlaytest.FeedbackFormsFolderForTesting = _feedbackForms;
    }

    [OneTimeTearDown]
    public void ReleaseTestFolders()
    {
        ProtokitePlaytest.FeedbackFormsFolderForTesting = null;
        FlockTestSavedFiles.Release();
        try
        {
            if (Directory.Exists(_feedbackForms))
                Directory.Delete(_feedbackForms, true);
        }
        catch (IOException)
        {
        }
    }
}
