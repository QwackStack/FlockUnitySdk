using Flock.Tests.Support;
using NUnit.Framework;

/// <summary>Outside any namespace, so it runs once around every test in this assembly: each test SDK keeps its analytics files in a folder of the run's own.</summary>
[SetUpFixture]
public class ProtokitePlaytestTestRunFolders
{
    [OneTimeSetUp]
    public void UseTestFolders() => FlockTestSavedFiles.Use();

    [OneTimeTearDown]
    public void ReleaseTestFolders() => FlockTestSavedFiles.Release();
}
