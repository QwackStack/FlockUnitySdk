using System;
using System.Collections.Generic;
using Flock.Exceptions;
using NUnit.Framework;

namespace Flock.Tests
{
    // Locks hint coverage. FlockErrorCode is hand-maintained, and a member added without a hint fails
    // silently: FlockErrorHints.For returns null, the error simply loses its "Fix:" line, and nothing
    // in a normal run points at the omission.
    public class FlockErrorHintCoverageTests
    {
        // Codes allowed to have no hint. Unknown is "no code, or one this SDK version predates" — there is
        // nothing to advise about a failure the SDK cannot identify, so the server's own reason is all there is.
        private static readonly HashSet<FlockErrorCode> NoHintAllowed = new HashSet<FlockErrorCode>
        {
            FlockErrorCode.Unknown,
        };

        [Test]
        public void EveryErrorCode_HasAHintOrIsAllowlisted()
        {
            List<FlockErrorCode> unhinted = new List<FlockErrorCode>();
            foreach (FlockErrorCode code in Enum.GetValues(typeof(FlockErrorCode)))
            {
                if (NoHintAllowed.Contains(code))
                    continue;
                if (string.IsNullOrEmpty(FlockErrorHints.For(code)))
                    unhinted.Add(code);
            }

            Assert.IsEmpty(
                unhinted,
                "FlockErrorCode members with no hint: " + string.Join(", ", unhinted) +
                ". Add one to FlockErrorHints, or add the code to NoHintAllowed with a comment saying why.");
        }

        // Keeps the allowlist from becoming where codes go to be forgotten: one that gained a hint must leave it.
        [Test]
        public void AllowlistedCodes_HaveNoHint()
        {
            foreach (FlockErrorCode code in NoHintAllowed)
            {
                Assert.IsNull(
                    FlockErrorHints.For(code),
                    code + " is on the no-hint allowlist but FlockErrorHints returns one. Remove it from NoHintAllowed.");
            }
        }
    }
}
