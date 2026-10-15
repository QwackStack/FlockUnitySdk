#if FLOCK_NETCODE_FOR_GAMEOBJECTS && FLOCK_UNITY_TRANSPORT_1
using System;
using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace Flock.Tests.PlayMode
{
    // Unity Transport 1.x and Netcode 1.x log errors of their own when a peer goes away (read in their source): those lines pass, anything else fails the test.
    internal sealed class NetcodeLibraryLogs
    {
        // Lines tests already expect: a player that reached no host, and a host whose port another program holds (on Linux the
        // transport's socket says so first: BaselibNetworkInterface, measured on CI).
        private static readonly string[] ExpectedByTests =
        {
            "Failed to connect to server.",
            "Server failed to bind. This is usually caused by another process being bound to the same port.",
            "Failed to bind the socket because address is already in use. It is likely that another process is already listening on the same port.",
            "[Netcode] Host is shutting down due to network transport start failure of UnityTransport!",
        };
        // The transport's two lines when its socket failed: the error, and the warning that it makes a new socket.
        private static readonly string[] TransportSocketLines =
        {
            "All socket receive requests were marked as failed, likely because socket itself has failed.",
            "Socket error encountered; attempting recovery by creating a new one.",
        };
        private const string LateSynchronizeException = "KeyNotFoundException";
        private const string LateSynchronizeFrame = "NetworkSceneManager.HandleServerSceneEvent";

        private readonly object _gate = new object();
        private readonly List<string> _unexpected = new List<string>();

        internal static NetcodeLibraryLogs Start()
        {
            NetcodeLibraryLogs logs = new NetcodeLibraryLogs();
            Application.logMessageReceivedThreaded += logs.Heard;
            logs.LetThrough();
            return logs;
        }

        // Set-up, the test itself and teardown each judge logs in a scope of their own (the test framework's LogScope), so each turns it on.
        internal void LetThrough() => LogAssert.ignoreFailingMessages = true;

        internal void Finish()
        {
            StopListening();
            lock (_gate)
                Assert.IsEmpty(_unexpected, "Logged besides the libraries' own lines: " + string.Join(" | ", _unexpected));
        }

        // Without judging: for a teardown that failed before it could finish.
        internal void StopListening()
        {
            Application.logMessageReceivedThreaded -= Heard;
            LogAssert.ignoreFailingMessages = false;
        }

        // For a test that counts the warnings Netcode logs: the transport's socket lines are not Netcode's.
        internal static bool IsTransportSocketLine(string message) => Array.IndexOf(TransportSocketLines, message) >= 0;

        internal void HeardForTesting(string message, string stackTrace, LogType type) => Heard(message, stackTrace, type);

        private void Heard(string message, string stackTrace, LogType type)
        {
            if (type == LogType.Log || type == LogType.Warning)
                return;
            if (IsTransportSocketLine(message) || Array.IndexOf(ExpectedByTests, message) >= 0)
                return;
            if (type == LogType.Exception && message.StartsWith(LateSynchronizeException, StringComparison.Ordinal) && stackTrace != null && stackTrace.Contains(LateSynchronizeFrame))
                return;
            lock (_gate)
                _unexpected.Add(type + ": " + message);
        }
    }
}
#endif
