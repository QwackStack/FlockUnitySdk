using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Flock.Http;

namespace Flock.Tests.Support
{
    // Offline-aware fake transport. Records every request, routes by URL fragment, and can be
    // flipped to a transport-level failure with GoOffline(). Replaces the per-file ScriptedAdapter.
    // Route rule: the last On/OnSequence for a given fragment wins, so a test can swap an endpoint's
    // behaviour mid-run (e.g. 401 then OK) just by calling On again.
    // GateNext(fragment)/ReleaseGate() hold a matching request open so tests can interleave state
    // changes (player switch, concurrent refresh) while a call is in flight. Request recording and
    // route resolution are locked so concurrent (post-gate) continuations are race-free.
    public sealed class FlockFakeTransport : IFlockHttpAdapter
    {
        public readonly List<FlockHttpRequest> Requests = new List<FlockHttpRequest>();
        private readonly object _lock = new object();

        private sealed class Route
        {
            public string Fragment;
            public Queue<FlockHttpResponse> Sequence;   // one-shot responses, in order
            public FlockHttpResponse Sticky;            // reused once the sequence drains (or if never sequenced)
        }

        private readonly List<Route> _routes = new List<Route>();
        private Func<FlockHttpRequest, FlockHttpResponse> _default = request => Ok("{}");
        private bool _offline;

        private TaskCompletionSource<bool> _gate;
        private string _gateFragment;

        // Route a URL fragment to a single reused response (replaces any prior route for the same fragment).
        public FlockFakeTransport On(string urlFragment, FlockHttpResponse response)
        {
            lock (_lock)
            {
                _routes.RemoveAll(r => r.Fragment == urlFragment);
                _routes.Add(new Route { Fragment = urlFragment, Sticky = response });
            }
            return this;
        }

        // Route a URL fragment to a sequence (e.g. fail-then-succeed); last entry sticks after the queue drains.
        public FlockFakeTransport OnSequence(string urlFragment, params FlockHttpResponse[] responses)
        {
            lock (_lock)
            {
                _routes.RemoveAll(r => r.Fragment == urlFragment);
                _routes.Add(new Route
                {
                    Fragment = urlFragment,
                    Sequence = new Queue<FlockHttpResponse>(responses),
                    Sticky = responses.Length > 0 ? responses[responses.Length - 1] : Ok("{}")
                });
            }
            return this;
        }

        // Fallback for any unrouted URL.
        public FlockFakeTransport Default(Func<FlockHttpRequest, FlockHttpResponse> responder)
        {
            _default = responder;
            return this;
        }

        public void GoOffline() => _offline = true;
        public void GoOnline() => _offline = false;

        // Hold the next request whose URL contains `urlFragment` open until ReleaseGate() is called.
        // One-shot: once released, later matching requests resolve normally.
        public void GateNext(string urlFragment)
        {
            _gateFragment = urlFragment;
            _gate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        public void ReleaseGate() => _gate?.TrySetResult(true);

        public Task<FlockHttpResponse> SendAsync(FlockHttpRequest request, CancellationToken cancellationToken)
        {
            lock (_lock)
                Requests.Add(request);

            if (_offline)
                return Task.FromResult(Offline());

            TaskCompletionSource<bool> gate = _gate;
            if (gate != null && _gateFragment != null && !gate.Task.IsCompleted && request.Url.Contains(_gateFragment))
                return AwaitGateThenResolve(gate, request);

            return Task.FromResult(Resolve(request));
        }

        private async Task<FlockHttpResponse> AwaitGateThenResolve(TaskCompletionSource<bool> gate, FlockHttpRequest request)
        {
            await gate.Task;
            return Resolve(request);
        }

        private FlockHttpResponse Resolve(FlockHttpRequest request)
        {
            lock (_lock)
            {
                foreach (Route route in _routes)
                {
                    if (!request.Url.Contains(route.Fragment))
                        continue;
                    if (route.Sequence != null && route.Sequence.Count > 0)
                        return route.Sequence.Dequeue();
                    return route.Sticky;
                }
            }
            return _default(request);
        }

        // ---- request-log query helpers (locked for post-gate concurrency) ----
        public FlockHttpRequest LastTo(string urlFragment) { lock (_lock) { return Requests.FindLast(r => r.Url.Contains(urlFragment)); } }
        public int CountTo(string urlFragment) { lock (_lock) { return Requests.FindAll(r => r.Url.Contains(urlFragment)).Count; } }
        public bool Sent(string urlFragment) { lock (_lock) { return Requests.Exists(r => r.Url.Contains(urlFragment)); } }
        public List<FlockHttpRequest> AllTo(string urlFragment) { lock (_lock) { return Requests.FindAll(r => r.Url.Contains(urlFragment)); } }

        // ---- response builders ----
        public static FlockHttpResponse Ok(string body)
            => new FlockHttpResponse { Result = FlockHttpResult.Success, StatusCode = 200, Body = body };

        public static FlockHttpResponse Status(int statusCode, string body)
            => new FlockHttpResponse { Result = FlockHttpResult.Success, StatusCode = statusCode, Body = body };

        public static FlockHttpResponse Coded(int statusCode, string code)
            => new FlockHttpResponse
            {
                Result = FlockHttpResult.Success,
                StatusCode = statusCode,
                Body = "{\"detail\":{\"code\":\"" + code + "\",\"message\":\"test\"}}"
            };

        public static FlockHttpResponse Offline()
            => new FlockHttpResponse { Result = FlockHttpResult.ConnectionError, StatusCode = 0, Body = null };

        public static FlockHttpResponse Timeout()
            => new FlockHttpResponse { Result = FlockHttpResult.Timeout, StatusCode = 0, Body = null };
    }
}
