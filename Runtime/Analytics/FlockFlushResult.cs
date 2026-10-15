namespace Flock.Analytics
{
    /// <summary>
    /// What a flush delivered and what it lost. <see cref="Sent"/> counts the queued entries (session ends, gameplay events,
    /// diagnostic entries) the server took; <see cref="Refused"/> the entries dropped because the server refused them for good
    /// (a 4xx about the entry itself: a player it does not know, a body it cannot store). Entries a transient failure left
    /// queued for a later flush are in neither count. <see cref="NothingLost"/> is the one answer most callers want; when it is
    /// false, <see cref="FirstRefusal"/> says what went.
    /// </summary>
    public sealed class FlockFlushResult
    {
        /// <summary>Nothing was sent and nothing was lost: nothing was queued, analytics is off, or consent is withheld.</summary>
        public static readonly FlockFlushResult Nothing = new FlockFlushResult(0, 0, null);

        public FlockFlushResult(int sent, int refused, string firstRefusal)
        {
            Sent = sent;
            Refused = refused;
            FirstRefusal = refused > 0 ? firstRefusal : null;
        }

        /// <summary>Queued entries the server took.</summary>
        public int Sent { get; }

        /// <summary>Queued entries dropped because the server refused them for good.</summary>
        public int Refused { get; }

        /// <summary>The first refusal's message, or null when nothing was refused.</summary>
        public string FirstRefusal { get; }

        /// <summary>True when no entry was refused. It does not mean the queue is empty: a transient failure keeps entries for a later flush.</summary>
        public bool NothingLost => Refused == 0;

        internal FlockFlushResult Plus(FlockFlushResult other)
        {
            if (other == null)
                return this;
            return new FlockFlushResult(Sent + other.Sent, Refused + other.Refused, FirstRefusal ?? other.FirstRefusal);
        }

        public override string ToString() => $"sent {Sent}, refused {Refused}" + (FirstRefusal == null ? "" : $" (first: {FirstRefusal})");
    }
}
