using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Flock.Logging;

namespace Flock.Providers
{
    /// <summary>Opens a relay address through the first way in that works, for a network that blocks some of them (UDP to the relay, as some phone hotspots and offices do): UDP over IPv4, then over IPv6, then TCP and TLS over IPv4, then over IPv6, each starting once the one before it failed or has had its time to open: <see cref="FlockRelayConnection.Timing.NextPortAfter"/> before another port of the same transport and family, <see cref="FlockRelayConnection.Timing.NextWayAfter"/> otherwise. The first to open is kept; every other is closed, giving back any address it reserved.</summary>
    internal static class FlockRelayWays
    {
        /// <summary>Run once a way has opened and before it is handed out, so a test can end the session then.</summary>
        internal static Action AfterAWayOpenedForTesting;

        /// <summary>The address on the first of <paramref name="logins"/> (in Flock's order) to open; fails with a <see cref="FlockRelayException"/> when none does, or OperationCanceledException once <paramref name="cancellationToken"/> is cancelled.</summary>
        internal static async Task<FlockRelayConnection> OpenFirstAsync(IReadOnlyList<FlockRelayLogin> logins, FlockRelayConnection.Timing timing, IFlockLogger logger, CancellationToken cancellationToken)
        {
            List<FlockRelayLogin> ways = FlockRelayLogin.InTheOrderTried(logins);
            List<Task<FlockRelayConnection>> started = new List<Task<FlockRelayConnection>>(ways.Count);
            // Ends every way still opening once one is kept; disposed only once each has ended, since one still opening registers on it.
            CancellationTokenSource raceOver = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            FlockRelayConnection kept = null;
            try
            {
                Task nextWayDue = null;
                while (true)
                {
                    // Checked before anything else starts: a way that fails because the session ended would otherwise start the next.
                    cancellationToken.ThrowIfCancellationRequested();
                    kept = FirstOpened(started);
                    if (kept != null)
                    {
                        AfterAWayOpenedForTesting?.Invoke();
                        break;
                    }
                    bool anyOpening = started.Exists(opening => !opening.IsCompleted);
                    // The next way starts once the last one started has failed, even while an earlier one still waits, or once it has had its time.
                    bool lastStartedFailed = started.Count > 0 && started[started.Count - 1].IsCompleted;
                    if (started.Count < ways.Count && (!anyOpening || lastStartedFailed || (nextWayDue != null && nextWayDue.IsCompleted)))
                    {
                        Task<FlockRelayConnection> opening = FlockRelayConnection.OpenAsync(ways[started.Count], timing, logger, raceOver.Token);
                        FlockMultiplayerSessions.LetRun(opening);
                        started.Add(opening);
                        nextWayDue = started.Count < ways.Count ? FlockWaiting.DelayAsync(TimeBefore(ways[started.Count - 1], ways[started.Count], timing), raceOver.Token) : null;
                        continue;
                    }
                    if (!anyOpening)
                        break;
                    List<Task> waitingFor = new List<Task>();
                    foreach (Task<FlockRelayConnection> opening in started)
                    {
                        if (!opening.IsCompleted)
                            waitingFor.Add(opening);
                    }
                    if (nextWayDue != null && started.Count < ways.Count)
                        waitingFor.Add(nextWayDue);
                    await Task.WhenAny(waitingFor);
                }
            }
            finally
            {
                raceOver.Cancel();
                foreach (Task<FlockRelayConnection> opening in started)
                    CloseUnlessKept(opening, kept);
                FlockMultiplayerSessions.LetRun(Task.WhenAll(started).ContinueWith(_ => raceOver.Dispose(), TaskContinuationOptions.ExecuteSynchronously));
            }
            if (cancellationToken.IsCancellationRequested)
            {
                // The session ended as the way opened: nobody will hold this one, so it is given back too.
                kept?.Close();
                throw new OperationCanceledException(cancellationToken);
            }
            if (kept == null)
                throw NoWayOpened(logins, ways, started);
            if (kept.Transport != FlockRelayTransport.Udp || kept.OverIpv6)
            {
                string why = kept.OverIpv6
                    ? "no way in over IPv4 opened first, as on a network with IPv6 alone"
                    : ways.Exists(way => way.Transport == FlockRelayTransport.Udp)
                        ? "no UDP way in opened first, as on a network that blocks UDP to the relay"
                        : "Flock lists no UDP way in to it";
                string stutter = kept.Transport == FlockRelayTransport.Udp
                    ? ""
                    : " A packet lost on the way holds back the ones after it until it is sent again, so play may stutter on a poor connection.";
                logger.LogInfo($"Flock's relay is reached at {kept.Way}: {why}.{stutter}");
            }
            return kept;
        }

        // Another port of the way just started (the same transport and family) comes soon after it; the next kind of way waits longer.
        private static TimeSpan TimeBefore(FlockRelayLogin started, FlockRelayLogin next, FlockRelayConnection.Timing timing)
            => next.Transport == started.Transport && next.OverIpv6 == started.OverIpv6 ? timing.NextPortAfter : timing.NextWayAfter;

        // The first way, in the order tried, that opened: of two that opened together, the better one.
        private static FlockRelayConnection FirstOpened(List<Task<FlockRelayConnection>> started)
        {
            foreach (Task<FlockRelayConnection> opening in started)
            {
                if (opening.Status == TaskStatus.RanToCompletion)
                    return opening.Result;
            }
            return null;
        }

        // A way that opened anyway, beside the one kept or after the race ended, is closed, so its address is given back.
        private static void CloseUnlessKept(Task<FlockRelayConnection> opening, FlockRelayConnection kept)
        {
            opening.ContinueWith(done =>
            {
                if (done.Status == TaskStatus.RanToCompletion && done.Result != kept)
                    done.Result.Close();
            }, TaskContinuationOptions.ExecuteSynchronously);
        }

        // Told with the reason of Flock's main server (the first it lists), from a way that reached it when one did (a refused login says more than
        // a blocked port), and every way's own message.
        private static FlockRelayException NoWayOpened(IReadOnlyList<FlockRelayLogin> logins, List<FlockRelayLogin> ways, List<Task<FlockRelayConnection>> started)
        {
            List<FlockRelayException> failures = new List<FlockRelayException>(started.Count);
            for (int i = 0; i < started.Count; i++)
                failures.Add(FailureOf(ways[i], started[i]));
            if (failures.Count == 1)
                return failures[0];
            FlockRelayException told = null;
            for (int i = 0; i < failures.Count; i++)
            {
                if (!ways[i].IsOn(logins[0].ToString()))
                    continue;
                if (told == null || (told.Reason == FlockRelayFailure.Unreachable && failures[i].Reason != FlockRelayFailure.Unreachable))
                    told = failures[i];
            }
            told = told ?? failures[0];
            StringBuilder message = new StringBuilder("No way in to Flock's relay opened.");
            foreach (FlockRelayException failure in failures)
                message.Append(' ').Append(failure.Cause.TrimEnd('.')).Append('.');
            return new FlockRelayException(told.Reason, message.ToString(), told);
        }

        private static FlockRelayException FailureOf(FlockRelayLogin way, Task<FlockRelayConnection> opening)
        {
            Exception failure = opening.Exception?.InnerException;
            if (failure is FlockRelayException relayFailure)
                return relayFailure;
            string why = failure == null ? "it was stopped" : $"{failure.GetType().Name}: {FlockExceptionText.OneLineMessageOf(failure)}";
            return new FlockRelayException(FlockRelayFailure.Unreachable, $"The relay at {way.Way} could not be opened ({why}).", failure);
        }
    }
}
