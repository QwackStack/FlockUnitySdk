using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;
using Flock.Auth;
using Flock.Exceptions;
using Flock.Http;
using Flock.Tests.Support;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityEngine;

namespace Flock.Tests.Editor
{
    /// <summary>ThisDeviceId: made once, kept in the SDK's saved files across launches, and sent by the device calls when the game passes no id.</summary>
    // Every test keeps the file in a temporary folder of its own; none touches the game's saved files.
    public class FlockThisDeviceIdTests
    {
        private string _folder;
        private string _file;
        private readonly List<(string ChangedPath, string FileHeld)> _copies = new List<(string, string)>();

        [SetUp]
        public void SetUp()
        {
            if (FlockClient.IsInitialized)
                FlockClient.Shutdown();
            _folder = Path.Combine(Path.GetTempPath(), "flock_device_id_" + Guid.NewGuid().ToString("N"));
            _file = Path.Combine(_folder, FlockDeviceIdFile.FileName);
            lock (_copies)
                _copies.Clear();
            FlockDeviceIdFile.UseFolderForTesting(_folder);
            // Only this test's own changes: another test's work still finishing on a background thread asks for copies too.
            FlockSavedFiles.CopyToBrowserStorageForTesting = changedPath =>
            {
                if (!changedPath.StartsWith(_folder, StringComparison.Ordinal))
                    return;
                lock (_copies)
                    _copies.Add((changedPath, ReadOrNull(_file)));
            };
        }

        [TearDown]
        public void TearDown()
        {
            FlockSavedFiles.CopyToBrowserStorageForTesting = null;
            FlockDeviceIdFile.BeforeNextSaveForTesting = null;
            FlockDeviceIdFile.UseFolderForTesting(null);
            if (FlockClient.IsInitialized)
                FlockClient.Shutdown();
            try
            {
                if (Directory.Exists(_folder))
                    Directory.Delete(_folder, true);
            }
            catch (Exception)
            {
                // A temporary folder is cleaned up by the system in time.
            }
        }

        private static string ReadOrNull(string path)
        {
            try
            {
                return File.Exists(path) ? File.ReadAllText(path) : null;
            }
            catch (Exception)
            {
                return null;
            }
        }

        // An id the SDK makes: a lower-case GUID with hyphens.
        private static bool IsNewId(string text)
            => Guid.TryParseExact(text, "D", out Guid guid) && guid != Guid.Empty && guid.ToString("D") == text;

        // Written long enough ago that no launch can still be writing it.
        private void WriteLeftOver(string contents)
        {
            Directory.CreateDirectory(_folder);
            File.WriteAllText(_file, contents);
            File.SetLastWriteTimeUtc(_file, DateTime.UtcNow.AddMinutes(-10));
        }

        private static FlockTestClient Launch() => FlockTestClient.Create(new FlockFakeTransport()
            .On(FlockEndpoints.PlayerLoginDevice, FlockFakeTransport.Ok(LoginBody()))
            .On(FlockEndpoints.PlayerRegisterDevice, FlockFakeTransport.Ok(LoginBody()))
            .On(FlockEndpoints.PlayerLinkDevice, FlockFakeTransport.Ok("{\"accounts\":[{\"provider\":\"device_id\",\"provider_user_id\":\"linked\"}]}")));

        private static string LoginBody()
            => $"{{\"player_id\":\"player-a\",\"access_token\":\"{FlockTestClient.MakeJwt("player-a", 3600)}\",\"refresh_token\":\"refresh-a\"}}";

        private static JObject Body(FlockTestClient launch, string route)
        {
            FlockHttpRequest request = launch.Transport.LastTo(route);
            Assert.IsNotNull(request, "Sent to " + route);
            return JObject.Parse(request.JsonBody);
        }

        // The device calls send exactly these fields: the device type, the id, and the name for a registration.
        private static void AssertDeviceBody(JObject body, string expectedId, params string[] otherFields)
        {
            List<string> expected = new List<string> { "device_type", "device_id" };
            expected.AddRange(otherFields);
            CollectionAssert.AreEquivalent(expected, body.Properties().Select(field => field.Name).ToList(), "The body's fields: " + body);
            Assert.AreEqual(JTokenType.String, body["device_id"].Type, "The id is a string: " + body);
            Assert.AreEqual(expectedId, (string)body["device_id"]);
            Assert.AreEqual(SystemInfo.deviceType.ToString(), (string)body["device_type"]);
        }

        private static List<string> WarningsAbout(FlockTestClient launch, string fragment)
            => launch.Logger.Warnings.FindAll(warning => warning.Contains(fragment));

        [Test]
        public void TheIdIsMadeOnceAndTheNextLaunchReadsItBack()
        {
            string first;
            using (FlockTestClient launch = Launch())
            {
                first = launch.Client.Authentication.ThisDeviceId;
                Assert.IsTrue(IsNewId(first), "A lower-case GUID: " + first);
                Assert.AreEqual(first, File.ReadAllText(_file), "Kept in the SDK's saved files");

                // Read once a launch: a file changed under a running game does not change its id.
                File.WriteAllText(_file, Guid.NewGuid().ToString("D"));
                Assert.AreEqual(first, launch.Client.Authentication.ThisDeviceId);
                File.WriteAllText(_file, first);
            }

            // The next launch remembers nothing and reads the file.
            FlockDeviceIdFile.UseFolderForTesting(_folder);
            using (FlockTestClient next = Launch())
            {
                Assert.AreEqual(first, next.Client.Authentication.ThisDeviceId);
                next.Run(() => next.Client.Authentication.LoginWithDeviceAsync());
                AssertDeviceBody(Body(next, FlockEndpoints.PlayerLoginDevice), first);
                Assert.IsEmpty(WarningsAbout(next, "device id"), "Nothing to say: " + string.Join(" | ", next.Logger.Warnings));
            }
            CollectionAssert.AreEqual(new[] { _file }, Directory.GetFiles(_folder), "One file, and no temporary file left");
        }

        [Test]
        public void ANewPlaySessionReadsTheFileAgain()
        {
            // With domain reload off, statics outlive a play session; the reset runs as each one starts.
            MethodInfo reset = typeof(FlockDeviceIdFile).GetMethod("ResetStaticState", BindingFlags.Static | BindingFlags.NonPublic);
            Assert.IsNotNull(reset, "The reset is where the test expects it");
            RuntimeInitializeOnLoadMethodAttribute runsAt = reset.GetCustomAttribute<RuntimeInitializeOnLoadMethodAttribute>();
            Assert.IsNotNull(runsAt, "Runs as play starts");
            Assert.AreEqual(RuntimeInitializeLoadType.SubsystemRegistration, runsAt.loadType);

            using (FlockTestClient launch = Launch())
            {
                string first = launch.Client.Authentication.ThisDeviceId;
                File.Delete(_file);
                Assert.AreEqual(first, launch.Client.Authentication.ThisDeviceId, "Remembered within a play session");

                reset.Invoke(null, null);
                string next = launch.Client.Authentication.ThisDeviceId;
                Assert.IsTrue(IsNewId(next), next);
                Assert.AreNotEqual(first, next, "The deleted file gave a new id");
                Assert.AreEqual(next, File.ReadAllText(_file));
            }
        }

        [Test]
        public void TheDeviceCallsSendTheKeptIdWhenTheGamePassesNone()
        {
            using (FlockTestClient launch = Launch())
            {
                launch.Run(() => launch.Client.Authentication.RegisterWithDeviceAsync());
                launch.Run(() => launch.Client.Authentication.LoginWithDeviceAsync());
                launch.Run(() => launch.Client.Authentication.LinkDeviceAsync());

                string kept = File.ReadAllText(_file);
                Assert.IsTrue(IsNewId(kept), kept);
                Assert.AreEqual(kept, launch.Client.Authentication.ThisDeviceId);
                JObject registration = Body(launch, FlockEndpoints.PlayerRegisterDevice);
                AssertDeviceBody(registration, kept, "name");
                Assert.AreEqual(JTokenType.Null, registration["name"].Type, "No name passed");
                AssertDeviceBody(Body(launch, FlockEndpoints.PlayerLoginDevice), kept);
                AssertDeviceBody(Body(launch, FlockEndpoints.PlayerLinkDevice), kept);
            }
        }

        [Test]
        public void AnIdTheGamePassesIsSentAsItIsAndNoIdIsKept()
        {
            const string ownId = " the game's own id ";
            using (FlockTestClient launch = Launch())
            {
                launch.Run(() => launch.Client.Authentication.RegisterWithDeviceAsync(ownId, "Sam"));
                launch.Run(() => launch.Client.Authentication.LoginWithDeviceAsync(ownId));
                launch.Run(() => launch.Client.Authentication.LinkDeviceAsync(ownId));

                JObject registration = Body(launch, FlockEndpoints.PlayerRegisterDevice);
                AssertDeviceBody(registration, ownId, "name");
                Assert.AreEqual("Sam", (string)registration["name"]);
                AssertDeviceBody(Body(launch, FlockEndpoints.PlayerLoginDevice), ownId);
                AssertDeviceBody(Body(launch, FlockEndpoints.PlayerLinkDevice), ownId);
            }
            Assert.IsFalse(Directory.Exists(_folder), "A game passing its own id never makes the kept one");
        }

        [Test]
        public void AnEmptyIdIsNotLeavingItOut()
        {
            using (FlockTestClient launch = Launch())
            {
                // Sent as it is, never swapped for the kept id; Flock's server is what refuses it (this fake answers anything).
                launch.Run(() => launch.Client.Authentication.LoginWithDeviceAsync(""));
                AssertDeviceBody(Body(launch, FlockEndpoints.PlayerLoginDevice), "");

                // Refused before it is sent.
                Assert.Throws<FlockValidationException>(() => launch.Run(() => launch.Client.Authentication.LinkDeviceAsync("")));
                Assert.IsFalse(launch.Transport.Sent(FlockEndpoints.PlayerLinkDevice));
            }
            Assert.IsFalse(Directory.Exists(_folder), "An empty id never makes the kept one");
        }

        [TestCase("")]
        [TestCase("not a device id")]
        [TestCase("6f9619ff-8b86-d011")]
        [TestCase("{6f9619ff-8b86-d011-b42d-00c04fc964ff")]
        public void AFileHoldingNoGuidGetsANewId(string contents)
        {
            WriteLeftOver(contents);
            using (FlockTestClient launch = Launch())
            {
                string id = launch.Client.Authentication.ThisDeviceId;
                Assert.IsTrue(IsNewId(id), id);
                Assert.AreEqual(id, File.ReadAllText(_file), "Made in place of what was there");
                (string changedPath, string fileHeld) last = _copies.Last();
                Assert.AreEqual(_file, last.changedPath, "The new file asked a browser to keep it");
                Assert.AreEqual(id, last.fileHeld);
            }
            CollectionAssert.AreEqual(new[] { _file }, Directory.GetFiles(_folder), "What was moved aside is gone");
        }

        [TestCase("6F9619FF-8B86-D011-B42D-00C04FC964FF", false, "6F9619FF-8B86-D011-B42D-00C04FC964FF")]
        [TestCase("6f9619ff-8b86-d011-b42d-00c04fc964ff\n", false, "6f9619ff-8b86-d011-b42d-00c04fc964ff")]
        [TestCase("  6f9619ff-8b86-d011-b42d-00c04fc964ff\r\n", false, "6f9619ff-8b86-d011-b42d-00c04fc964ff")]
        [TestCase("6f9619ff-8b86-d011-b42d-00c04fc964ff", true, "6f9619ff-8b86-d011-b42d-00c04fc964ff")]
        [TestCase("00000000-0000-0000-0000-000000000000", false, "00000000-0000-0000-0000-000000000000")]
        public void AGuidWrittenAnotherWayIsUsedAsWrittenAndTheFileLeftAlone(string contents, bool byteOrderMark, string expected)
        {
            // The server may already know the id this way, so it is never rewritten.
            Directory.CreateDirectory(_folder);
            byte[] bytes = Encoding.UTF8.GetBytes(contents);
            if (byteOrderMark)
                bytes = new byte[] { 0xEF, 0xBB, 0xBF }.Concat(bytes).ToArray();
            File.WriteAllBytes(_file, bytes);
            using (FlockTestClient launch = Launch())
            {
                Assert.AreEqual(expected, launch.Client.Authentication.ThisDeviceId);
                launch.Run(() => launch.Client.Authentication.LoginWithDeviceAsync());
                AssertDeviceBody(Body(launch, FlockEndpoints.PlayerLoginDevice), expected);
            }
            CollectionAssert.AreEqual(bytes, File.ReadAllBytes(_file), "The file is as it was");
            Assert.IsEmpty(_copies, "Nothing saved");
        }

        [Test]
        public void AnEmptyFileAnotherLaunchIsStillWritingIsReadOnceItHoldsTheId()
        {
            string otherLaunch = Guid.NewGuid().ToString("D");
            using (FlockTestClient launch = Launch())
            {
                // Another launch has made the file and not yet written its id.
                Directory.CreateDirectory(_folder);
                File.WriteAllText(_file, "");
                Thread writer = new Thread(() =>
                {
                    Thread.Sleep(200);
                    DateTime giveUpAt = DateTime.UtcNow.AddSeconds(3);
                    while (true)
                    {
                        try
                        {
                            File.WriteAllText(_file, otherLaunch);
                            return;
                        }
                        catch (IOException) when (DateTime.UtcNow < giveUpAt)
                        {
                            // The launch under test is reading it this moment.
                            Thread.Sleep(5);
                        }
                    }
                });
                writer.Start();
                try
                {
                    Assert.AreEqual(otherLaunch, launch.Client.Authentication.ThisDeviceId);
                }
                finally
                {
                    writer.Join();
                }
            }
            Assert.AreEqual(otherLaunch, File.ReadAllText(_file));
        }

        [Test]
        public void AFileThatCannotBeReadIsLeftAloneAndTheLaunchStillSignsIn()
        {
            string saved = Guid.NewGuid().ToString("D");
            WriteLeftOver(saved);
            using (new FileStream(_file, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            using (FlockTestClient launch = Launch())
            {
                launch.Run(() => launch.Client.Authentication.LoginWithDeviceAsync());

                Assert.IsTrue(launch.Client.IsAuthenticated);
                string used = (string)Body(launch, FlockEndpoints.PlayerLoginDevice)["device_id"];
                Assert.IsTrue(IsNewId(used), used);
                Assert.AreNotEqual(saved, used);
                Assert.AreEqual(used, launch.Client.Authentication.ThisDeviceId, "One id for the whole launch");
                List<string> said = WarningsAbout(launch, _file);
                Assert.AreEqual(1, said.Count, "Said once: " + string.Join(" | ", launch.Logger.Warnings));
                StringAssert.Contains("could not be read", said[0]);
                StringAssert.Contains("start the game again", said[0]);
            }
            Assert.AreEqual(saved, File.ReadAllText(_file), "Left alone");
        }

        [Test]
        public void ANewIdThatCannotBeSavedIsUsedForTheLaunchAndSaidOutLoud()
        {
            // A file where the folder should be, so nothing can be saved in it.
            Directory.CreateDirectory(_folder);
            string notAFolder = Path.Combine(_folder, "not-a-folder");
            File.WriteAllText(notAFolder, "");
            FlockDeviceIdFile.UseFolderForTesting(notAFolder);
            using (FlockTestClient launch = Launch())
            {
                launch.Run(() => launch.Client.Authentication.LoginWithDeviceAsync());

                string used = (string)Body(launch, FlockEndpoints.PlayerLoginDevice)["device_id"];
                Assert.IsTrue(IsNewId(used), used);
                Assert.AreEqual(used, launch.Client.Authentication.ThisDeviceId, "One id for the whole launch");
                List<string> said = WarningsAbout(launch, "could not be saved");
                Assert.AreEqual(1, said.Count, "Said once: " + string.Join(" | ", launch.Logger.Warnings));
                StringAssert.Contains("pass a device id of your own", said[0]);
            }
        }

        [Test]
        public void AnotherLaunchMakingTheFileFirstWins()
        {
            string otherLaunch = Guid.NewGuid().ToString("D");
            // Between this launch finding no file and making it, another launch makes it.
            FlockDeviceIdFile.BeforeNextSaveForTesting = () => File.WriteAllText(_file, otherLaunch);
            using (FlockTestClient launch = Launch())
                Assert.AreEqual(otherLaunch, launch.Client.Authentication.ThisDeviceId);
            Assert.AreEqual(otherLaunch, File.ReadAllText(_file));
            CollectionAssert.AreEqual(new[] { _file }, Directory.GetFiles(_folder), "Nothing else left");
        }

        [Test]
        public void AnotherLaunchReplacingABadFileFirstWins()
        {
            string otherLaunch = Guid.NewGuid().ToString("D");
            WriteLeftOver("not a device id");
            // Both launches found the bad file; the other one put its new id in first.
            FlockDeviceIdFile.BeforeNextSaveForTesting = () => File.WriteAllText(_file, otherLaunch);
            using (FlockTestClient launch = Launch())
                Assert.AreEqual(otherLaunch, launch.Client.Authentication.ThisDeviceId);
            Assert.AreEqual(otherLaunch, File.ReadAllText(_file));
            CollectionAssert.AreEqual(new[] { _file }, Directory.GetFiles(_folder), "What was moved aside is gone");
        }

        [Test]
        public void TheSaveAsksABrowserToKeepTheFileOnceTheIdIsInIt()
        {
            using (FlockTestClient launch = Launch())
            {
                string id = launch.Client.Authentication.ThisDeviceId;
                (string changedPath, string fileHeld) last = _copies.Last();
                Assert.AreEqual(_file, last.changedPath);
                Assert.AreEqual(id, last.fileHeld);
            }
        }

        [Test]
        public void ATemporaryFileALaunchLeftIsDeletedAndAFreshOneKept()
        {
            Directory.CreateDirectory(_folder);
            string old = _file + "." + Guid.NewGuid().ToString("N") + ".tmp";
            string fresh = _file + "." + Guid.NewGuid().ToString("N") + ".tmp";
            File.WriteAllText(old, "left over");
            File.SetLastWriteTimeUtc(old, DateTime.UtcNow.AddMinutes(-5));
            File.WriteAllText(fresh, "another launch's file under way");
            using (FlockTestClient launch = Launch())
                Assert.IsTrue(IsNewId(launch.Client.Authentication.ThisDeviceId));
            Assert.IsFalse(File.Exists(old));
            Assert.IsTrue(File.Exists(fresh));
        }
    }
}
