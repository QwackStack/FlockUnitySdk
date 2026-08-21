using System;
using System.Reflection;
using Flock;
using Flock.Models;
using NUnit.Framework;

namespace Flock.Tests.Editor
{
    // FlockEvents is a static hub, so a subscription that survives ClearAll() outlives the object that made it.
    // With domain reload disabled (the default fast enter-play-mode), Play -> Stop -> Play then leaves a delegate
    // holding a destroyed MonoBehaviour, and the next invoke throws MissingReferenceException from inside the SDK.
    // OnNotificationReceived was missed by ClearAll for exactly this reason.
    public class FlockEventsTests
    {
        // 17 events, of which two (OnInitialized / OnInitializationFailed) have hand-written accessors over
        // _onInitialized / _onInitializationFailed. The rest are field-like, so the compiler names the backing
        // field after the event.
        private const int ExpectedEventCount = 17;

        [TearDown]
        public void TearDown()
        {
            FlockEvents.ClearAll();
        }

        private static FieldInfo[] DelegateFields()
        {
            FieldInfo[] all = typeof(FlockEvents).GetFields(BindingFlags.NonPublic | BindingFlags.Static);
            return Array.FindAll(all, f => typeof(Delegate).IsAssignableFrom(f.FieldType));
        }

        // ---- EVT-01: every event is dropped by ClearAll, not all-but-one ----
        [Test]
        public void ClearAll_DropsEverySubscription()
        {
            SubscribeToEverything();

            foreach (FieldInfo field in DelegateFields())
                Assert.IsNotNull(field.GetValue(null), $"Test setup missed '{field.Name}' — it must be subscribed for this to prove anything.");

            FlockEvents.ClearAll();

            foreach (FieldInfo field in DelegateFields())
                Assert.IsNull(field.GetValue(null), $"'{field.Name}' still has subscribers after ClearAll() — it will leak across a domain reload.");
        }

        // ---- EVT-02: a new event forces this file to be updated rather than silently going unguarded ----
        [Test]
        public void EventCount_IsUnchanged_OtherwiseUpdateClearAllAndThisFile()
        {
            Assert.AreEqual(ExpectedEventCount, DelegateFields().Length,
                "FlockEvents gained or lost an event. Add it to ClearAll(), subscribe to it in SubscribeToEverything(), " +
                "and update ExpectedEventCount — EVT-01 only proves what it subscribes to.");
        }

        private static void SubscribeToEverything()
        {
            FlockEvents.OnInitialized += Noop;
            FlockEvents.OnInitializationFailed += NoopException;
            FlockEvents.OnShutdown += Noop;
            FlockEvents.OnAuthenticated += NoopAuthInfo;
            FlockEvents.OnTokenRefreshed += Noop;
            FlockEvents.OnAuthExpired += Noop;
            FlockEvents.OnLoggedOut += Noop;
            FlockEvents.OnSessionRestored += NoopBool;
            FlockEvents.OnAccountLinked += NoopProvider;
            FlockEvents.OnAccountUnlinked += NoopProvider;
            FlockEvents.OnSessionStarted += NoopString;
            FlockEvents.OnSessionEnded += NoopSessionEnded;
            FlockEvents.OnSessionPaused += Noop;
            FlockEvents.OnSessionResumed += Noop;
            FlockEvents.OnUnreadCountChanged += NoopInt;
            FlockEvents.OnNotificationReceived += NoopNotification;
            FlockEvents.OnConsentChanged += NoopBool;
        }

        private static void Noop() { }
        private static void NoopBool(bool value) { }
        private static void NoopInt(int value) { }
        private static void NoopString(string value) { }
        private static void NoopException(Exception value) { }
        private static void NoopAuthInfo(FlockAuthInfo value) { }
        private static void NoopProvider(FlockCredentialProvider value) { }
        private static void NoopSessionEnded(FlockSessionEndedArgs value) { }
        private static void NoopNotification(Notification value) { }
    }
}
