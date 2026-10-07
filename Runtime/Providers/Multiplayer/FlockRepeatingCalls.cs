using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Flock.Exceptions;

namespace Flock.Providers
{
    /// <summary>Every multiplayer call the SDK repeats (search checks, heartbeats, party refreshes): one request at a time each, at a jittered interval, for the sign-in it started under.</summary>
    internal sealed class FlockRepeatingCalls
    {
        private static readonly Random SharedRandom = new Random();
        private static readonly object SharedRandomLock = new object();

        private readonly FlockClient _client;
        private readonly Func<double> _nextRoll;
        private readonly Stopwatch _clock = Stopwatch.StartNew();
        private readonly List<FlockRepeatingCall> _calls = new List<FlockRepeatingCall>();
        // Reused every frame, so sending what is due allocates nothing while nothing is.
        private readonly List<FlockRepeatingCall> _due = new List<FlockRepeatingCall>();

        /// <param name="nextRoll">A number from 0 to 1 for each wait's jitter; null uses a shared random source.</param>
        internal FlockRepeatingCalls(FlockClient client, Func<double> nextRoll = null)
        {
            _client = client ?? throw new ArgumentNullException(nameof(client));
            _nextRoll = nextRoll ?? NextSharedRoll;
        }

        internal int CurrentSignInNumber => _client.SignInNumber;

        /// <summary>Starts repeating <paramref name="send"/> one jittered interval from now, under the current sign-in; refused while a call of that name runs.</summary>
        /// <param name="useAnswer">Gets each answer, never one that arrives after the call stopped or its sign-in ended.</param>
        /// <param name="stoppedByFailure">Gets the failure that ended the call: anything but no answer, a 5xx, 408, 429 or an unreadable answer.</param>
        internal FlockRepeatingCall Start<T>(
            string name,
            TimeSpan interval,
            Func<CancellationToken, Task<T>> send,
            Action<T> useAnswer,
            Action<Exception> stoppedByFailure)
        {
            if (string.IsNullOrEmpty(name))
                throw new ArgumentException("A repeating call needs a name", nameof(name));
            if (interval <= TimeSpan.Zero)
                throw new ArgumentOutOfRangeException(nameof(interval), "A repeating call needs an interval above zero");
            if (send == null)
                throw new ArgumentNullException(nameof(send));

            foreach (FlockRepeatingCall running in _calls)
            {
                if (running.Name == name && running.IsRunning)
                    throw new InvalidOperationException($"The repeating call '{name}' is already running");
            }

            FlockRepeatingCall call = new FlockRepeatingCall(this, name, CurrentSignInNumber, interval);
            call.SendOnce = () => SendOnceAsync(call, send, useAnswer, stoppedByFailure);
            call.DueAt = _clock.Elapsed + Jittered(interval);
            _calls.Add(call);
            return call;
        }

        /// <summary>Sends every call that is due and not waiting for an answer; run once a frame.</summary>
        internal void SendThoseDue()
        {
            if (_calls.Count == 0)
                return;

            TimeSpan now = _clock.Elapsed;
            foreach (FlockRepeatingCall call in _calls)
            {
                if (!call.IsWaitingForAnswer && now >= call.DueAt)
                    _due.Add(call);
            }

            // Decided as each one goes, so a call stopped (or a sign-out) by a handler of one sent just before it sends nothing.
            foreach (FlockRepeatingCall call in _due)
            {
                // Stopped, or its sign-in ended: it sends nothing more.
                if (!call.IsRunning)
                {
                    _calls.Remove(call);
                    continue;
                }
                _ = call.SendOnce();
            }
            _due.Clear();
        }

        /// <summary>Stops every call: Flock shutting down, or the game quitting.</summary>
        internal void StopAll()
        {
            foreach (FlockRepeatingCall call in _calls)
                call.Stop();
            _calls.Clear();
        }

        /// <summary>The wait before a call's next request: its interval, from three quarters to one and a quarter of it.</summary>
        internal static TimeSpan JitteredWait(TimeSpan interval, double roll)
        {
            double fraction = 0.75 + (roll * 0.5);
            return TimeSpan.FromTicks((long)(interval.Ticks * fraction));
        }

        private TimeSpan Jittered(TimeSpan interval) => JitteredWait(interval, _nextRoll());

        private static double NextSharedRoll()
        {
            lock (SharedRandomLock)
                return SharedRandom.NextDouble();
        }

        // Never faults: everything it can throw is caught, so nothing reaches the unobserved-task handler.
        private async Task SendOnceAsync<T>(
            FlockRepeatingCall call,
            Func<CancellationToken, Task<T>> send,
            Action<T> useAnswer,
            Action<Exception> stoppedByFailure)
        {
            call.IsWaitingForAnswer = true;
            TimeSpan wait = Jittered(call.Interval);
            try
            {
                T answer = await send(call.Cancellation);
                // Stopped, or its sign-in ended, while the answer was on its way.
                if (!call.IsRunning)
                    return;
                HandOver(call, useAnswer, answer);
            }
            catch (Exception failure)
            {
                // A failure that lands after the call ended says nothing to anyone.
                if (!call.IsRunning)
                    return;
                if (IsWorthAnotherTry(failure))
                {
                    TimeSpan? retryAfter = (failure as FlockNetworkException)?.RetryAfter;
                    if (retryAfter.HasValue && retryAfter.Value > wait)
                        wait = retryAfter.Value;
                    _client.Logger.LogDebug($"{call.Name} failed, trying again in {wait.TotalSeconds:0.#} s: {failure.Message}");
                    return;
                }
                call.Stop();
                HandOver(call, stoppedByFailure, failure);
            }
            finally
            {
                call.IsWaitingForAnswer = false;
                call.DueAt = _clock.Elapsed + wait;
            }
        }

        // No answer, a server error, a timeout, a limit or a page that is not the server's can pass; anything else is the server's word on this call.
        private static bool IsWorthAnotherTry(Exception failure)
        {
            if (failure is FlockSerializationException)
                return true;
            if (failure is FlockNetworkException network)
                return !FlockNetworkException.IsPermanentStatus(network.StatusCode);
            return false;
        }

        // A handler the game hangs off an answer must not end the call or reach the task scheduler.
        private void HandOver<T>(FlockRepeatingCall call, Action<T> handler, T value)
        {
            if (handler == null)
                return;
            try
            {
                handler(value);
            }
            catch (Exception ex)
            {
                _client.Logger.LogError($"{call.Name}: a handler of its answer threw", ex);
            }
        }
    }

    /// <summary>One call the SDK repeats. <see cref="Stop"/> ends it, and nothing it was waiting for is used afterwards.</summary>
    internal sealed class FlockRepeatingCall
    {
        private readonly FlockRepeatingCalls _owner;
        private readonly CancellationTokenSource _cancellation = new CancellationTokenSource();
        private bool _stopped;

        internal FlockRepeatingCall(FlockRepeatingCalls owner, string name, int signInNumber, TimeSpan interval)
        {
            _owner = owner;
            Name = name;
            SignInNumber = signInNumber;
            Interval = interval;
        }

        internal string Name { get; }
        internal int SignInNumber { get; }
        internal TimeSpan Interval { get; }
        internal TimeSpan DueAt { get; set; }
        internal bool IsWaitingForAnswer { get; set; }
        internal Func<Task> SendOnce { get; set; }
        internal CancellationToken Cancellation => _cancellation.Token;

        /// <summary>False once stopped, or once the sign-in it started under has ended.</summary>
        internal bool IsRunning => !_stopped && SignInNumber == _owner.CurrentSignInNumber;

        internal void Stop()
        {
            if (_stopped)
                return;
            _stopped = true;
            _cancellation.Cancel();
        }
    }
}
