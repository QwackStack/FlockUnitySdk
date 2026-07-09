namespace Flock.Exceptions
{
    /// <summary>Readable checks for coded-error GROUPS callers branch on. For a single code, compare <see cref="FlockException.ErrorCode"/> directly instead.</summary>
    public static class FlockErrorCodeExtensions
    {
        /// <summary>True when a register/login route reports this identity (email/device/OAuth) already belongs to an account. Excludes a taken display name — that's a different fix.</summary>
        public static bool IsAlreadyRegistered(this FlockException ex)
        {
            switch (ex?.ErrorCode)
            {
                case FlockErrorCode.PlayerEmailAlreadyRegistered:
                case FlockErrorCode.PlayerDeviceAlreadyRegistered:
                case FlockErrorCode.PlayerGoogleAccountAlreadyRegistered:
                case FlockErrorCode.PlayerAppleAccountAlreadyRegistered:
                case FlockErrorCode.PlayerSteamAccountAlreadyRegistered:
                    return true;
                default:
                    return false;
            }
        }
    }
}
