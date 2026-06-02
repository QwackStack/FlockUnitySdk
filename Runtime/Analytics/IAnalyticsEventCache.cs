using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Flock.Analytics
{
    // Generic write-ahead cache for any payload type — analytics events, log events, etc.
    // Each consumer constructs its own instance with a unique subfolder.
    internal interface IEventCache<T> where T : class
    {
        int PendingCount { get; }

        // Returns handle the caller can pass to Remove on successful send,
        // or null if the cache is unavailable / the write failed.
        string Enqueue(T evt);

        void Remove(string handle);

        // Walks every cached entry; for each one where `shouldRewrite` returns true,
        // applies `mutate` and atomically rewrites the file.
        // Used for retag-after-auth flows where user ID was defaulted due to auth failure.
        void Rewrite(Func<T, bool> shouldRewrite, Action<T> setAuthID);

        // Drains the cache one batch at a time via the supplied sender.
        Task FlushAsync(
            Func<IReadOnlyList<T>, CancellationToken, Task> sender,
            CancellationToken cancellationToken);

        void Clear();
    }
}
