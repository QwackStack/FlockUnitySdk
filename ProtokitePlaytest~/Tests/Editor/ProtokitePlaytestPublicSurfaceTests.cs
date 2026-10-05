using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using Flock;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace Protokite.Playtest.Tests
{
    /// <summary>Every public member of <see cref="ProtokitePlaytest"/> is called here, in a launch where playtesting is on and Flock has not started; a member added without a call fails.</summary>
    public class ProtokitePlaytestPublicSurfaceTests
    {
        private ProtokitePlaytestSettingsForTests _settings;

        // What each member answers before Flock starts, in a build that does not ask the player: the answer a game reads most often.
        private static readonly Dictionary<string, Action> Calls = new Dictionary<string, Action>
        {
            ["Status"] = () => Assert.AreEqual(ProtokitePlaytestStatus.WaitingForFlock, ProtokitePlaytest.Status),
            ["Config"] = () => Assert.IsNull(ProtokitePlaytest.Config),
            ["IsFeatureEnabled(String)"] = () => Assert.IsFalse(ProtokitePlaytest.IsFeatureEnabled(ProtokitePlaytestFeatures.VideoRecording)),
            ["Describe(ProtokitePlaytestStatus)"] = () => StringAssert.Contains("waiting for the Flock SDK", ProtokitePlaytest.Describe(ProtokitePlaytestStatus.WaitingForFlock)),
            ["Describe(ProtokitePlaytestConsentChoice)"] = () => StringAssert.Contains("collect nothing", ProtokitePlaytest.Describe(ProtokitePlaytestConsentChoice.Nothing)),
            ["PlaytestConsent"] = () => Assert.AreEqual(ProtokitePlaytestConsentChoice.VideoAndPlayData, ProtokitePlaytest.PlaytestConsent, "What a build that does not ask assumes"),
            ["PlayersConsentAnswer"] = () => Assert.AreEqual(ProtokitePlaytestConsentChoice.NotAnswered, ProtokitePlaytest.PlayersConsentAnswer, "Never what a build that does not ask assumes"),
            ["IsConsentQuestionOpen"] = () => Assert.IsFalse(ProtokitePlaytest.IsConsentQuestionOpen),
            ["PlayersUploadNetworkAnswer"] = () => Assert.AreEqual(ProtokitePlaytestUploadNetworkChoice.NotAnswered, ProtokitePlaytest.PlayersUploadNetworkAnswer),
            ["SetPlaytestUploadNetwork(ProtokitePlaytestUploadNetworkChoice)"] = () =>
            {
                Assert.IsTrue(ProtokitePlaytest.SetPlaytestUploadNetwork(ProtokitePlaytestUploadNetworkChoice.WiFiOnly));
                Assert.AreEqual(ProtokitePlaytestUploadNetworkChoice.WiFiOnly, ProtokitePlaytest.PlayersUploadNetworkAnswer);
            },
            ["SetPlaytestConsent(ProtokitePlaytestConsentChoice)"] = () =>
            {
                Assert.IsTrue(ProtokitePlaytest.SetPlaytestConsent(ProtokitePlaytestConsentChoice.PlayDataOnly));
                Assert.AreEqual(ProtokitePlaytestConsentChoice.PlayDataOnly, ProtokitePlaytest.PlayersConsentAnswer);
                Assert.AreEqual(ProtokitePlaytestConsentChoice.PlayDataOnly, ProtokitePlaytest.PlaytestConsent);
            },
            ["AskForPlaytestConsent()"] = () => Assert.IsFalse(ProtokitePlaytest.AskForPlaytestConsent(), "No playtest is loaded to ask about"),
            ["FeedbackForm"] = () => Assert.IsNull(ProtokitePlaytest.FeedbackForm),
            ["CanOpenFeedbackForm"] = () => Assert.IsFalse(ProtokitePlaytest.CanOpenFeedbackForm),
            ["IsFeedbackFormOpen"] = () => Assert.IsFalse(ProtokitePlaytest.IsFeedbackFormOpen),
            ["OpenFeedbackForm()"] = () => Assert.IsFalse(ProtokitePlaytest.OpenFeedbackForm()),
            ["CloseFeedbackForm()"] = () => Assert.IsFalse(ProtokitePlaytest.CloseFeedbackForm()),
            ["SendFeedbackForm(ProtokitePlaytestFormAnswers)"] = () =>
            {
                LogAssert.Expect(LogType.Warning, new Regex("The feedback form was not sent: this build's playtest is not loaded"));
                Assert.IsFalse(ProtokitePlaytest.SendFeedbackForm(new ProtokitePlaytestFormAnswers()));
            },
            ["CanSendTheRecording"] = () => Assert.IsFalse(ProtokitePlaytest.CanSendTheRecording),
            ["StopRecordingAndSendIt()"] = () => Assert.IsFalse(ProtokitePlaytest.StopRecordingAndSendIt()),
            ["IsRecordingVideo"] = () => Assert.IsFalse(ProtokitePlaytest.IsRecordingVideo),
            ["StopVideoRecording()"] = () => Assert.IsFalse(ProtokitePlaytest.StopVideoRecording()),
            ["RecordPlaytestEvent(String,Dictionary`2)"] = () => Assert.IsFalse(ProtokitePlaytest.RecordPlaytestEvent("boss_defeated", new Dictionary<string, object> { ["boss"] = "dragon" })),
            ["PlaytestSessionId"] = () => Assert.IsNull(ProtokitePlaytest.PlaytestSessionId),
            ["SetSteamId(String,String)"] = () =>
            {
                Assert.IsTrue(ProtokitePlaytest.SetSteamId("76561198000000001", "A Player"), "Taken before the session starts");
                Assert.IsFalse(ProtokitePlaytest.SetSteamId("7656 1198", "A Player"), "An id holding whitespace is refused, not trimmed");
            },
            ["RecordTestVideo(Double,String&)"] = () =>
            {
                Assert.IsFalse(ProtokitePlaytest.RecordTestVideo(0.0, out string whyNot));
                StringAssert.Contains("above 0 seconds", whyNot);
            },
            ["TestVideoState"] = () => Assert.AreEqual(ProtokitePlaytestTestVideoState.None, ProtokitePlaytest.TestVideoState),
            ["TestVideoProblem"] = () => Assert.IsNull(ProtokitePlaytest.TestVideoProblem),
            ["FinishedTestVideoPath"] = () => Assert.IsNull(ProtokitePlaytest.FinishedTestVideoPath),
        };

        private static IEnumerable<string> MemberNames() => Calls.Keys;

        [SetUp]
        public void SetUp()
        {
            ProtokitePlaytest.ResetForNewLaunch();
            Assert.IsFalse(FlockClient.IsInitialized, "Precondition: no Flock client left running by another test");
            _settings = new ProtokitePlaytestSettingsForTests();
        }

        [TearDown]
        public void TearDown()
        {
            ProtokitePlaytest.ResetForNewLaunch();
            _settings?.Dispose();
            _settings = null;
        }

        [Test]
        public void EveryPublicMemberHasACallHere()
        {
            List<string> members = PublicMembers().ToList();
            List<string> uncalled = members.Except(Calls.Keys).ToList();
            List<string> gone = Calls.Keys.Except(members).ToList();

            Assert.IsEmpty(uncalled, "Public members with no call in this test: " + string.Join(", ", uncalled));
            Assert.IsEmpty(gone, "Calls for members that are no longer public: " + string.Join(", ", gone));
        }

        [TestCaseSource(nameof(MemberNames))]
        public void EachPublicMemberAnswersBeforeFlockStarts(string member)
        {
            Calls[member]();
        }

        [Test]
        public void TheSurfaceIsReadFromTheFacadeItself()
        {
            // Control: the list the first test compares against is not empty, and holds members both kinds of reading find.
            List<string> members = PublicMembers().ToList();
            CollectionAssert.Contains(members, "Status");
            CollectionAssert.Contains(members, "OpenFeedbackForm()");
        }

        // Properties by name, methods by name and parameter types, so each overload needs its own call.
        private static IEnumerable<string> PublicMembers()
        {
            foreach (MemberInfo member in typeof(ProtokitePlaytest).GetMembers(BindingFlags.Public | BindingFlags.Static | BindingFlags.DeclaredOnly))
            {
                if (member is MethodInfo method)
                {
                    if (!method.IsSpecialName)
                        yield return method.Name + "(" + string.Join(",", method.GetParameters().Select(parameter => parameter.ParameterType.Name)) + ")";
                }
                else if (member is PropertyInfo || member is FieldInfo || member is EventInfo)
                {
                    yield return member.Name;
                }
            }
        }
    }
}
