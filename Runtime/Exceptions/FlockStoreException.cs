using Flock.Exceptions;

namespace Flock.Providers
{
    /// <summary>Why a store or a web checkout did not take a payment (<see cref="FlockStoreException.Reason"/>). Strings rather than an enum, so a reason added later still reads.</summary>
    public static class FlockStoreFailureReasons
    {
        /// <summary>The player cancelled in the store's dialog.</summary>
        public const string Cancelled = "cancelled";
        /// <summary>The player already owns this one-time product: show it as owned rather than for sale.</summary>
        public const string AlreadyOwned = "already_owned";
        /// <summary>An earlier purchase of this product is still waiting for its payment.</summary>
        public const string AlreadyPending = "already_pending";
        /// <summary>The store or payment gateway declined the payment (the App Store and web checkout; Google Play reports a declined payment as <see cref="StoreUnavailable"/>).</summary>
        public const string PaymentDeclined = "payment_declined";
        /// <summary>The store does not sell this product here, or it is not set up in the store.</summary>
        public const string ProductUnavailable = "product_unavailable";
        /// <summary>The store could not be reached or purchases are switched off on this device; on Google Play, which reports both the same way, also a payment method it could not charge.</summary>
        public const string StoreUnavailable = "store_unavailable";
        /// <summary>Nobody is signed in to the store on this device.</summary>
        public const string NotSignedInToTheStore = "not_signed_in_to_the_store";
        /// <summary>The player did not pay before the web checkout closed, an hour after it opened.</summary>
        public const string Expired = "expired";
        /// <summary>Any other reason; the exception's message has the store's words.</summary>
        public const string Unknown = "unknown";
    }

    /// <summary>The store or the web checkout did not take the payment for a real-money purchase, so nothing was charged. <see cref="Reason"/> says why; the message has the store's own words.</summary>
    public sealed class FlockStoreException : FlockException
    {
        /// <summary>Why the payment was not taken, one of <see cref="FlockStoreFailureReasons"/>.</summary>
        public string Reason { get; }

        internal FlockStoreException(string reason, string message) : base(message)
        {
            Reason = reason;
        }
    }
}
