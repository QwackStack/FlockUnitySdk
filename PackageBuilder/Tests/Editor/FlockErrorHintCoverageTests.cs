using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
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

        // A hint that names a call the SDK does not have sends a developer looking for it.
        [Test]
        public void EveryCallAHintNames_IsAPublicMethodOfTheSdk()
        {
            HashSet<string> publicMethods = new HashSet<string>(
                typeof(FlockClient).Assembly.GetExportedTypes()
                    .SelectMany(type => type.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static))
                    .Select(method => method.Name),
                StringComparer.Ordinal);
            Assert.IsTrue(publicMethods.Contains("GetMyPartyAsync"), "The scan reads the SDK's public methods");
            Assert.IsFalse(publicMethods.Contains("CancelOldSearchAsync"), "A name the SDK does not have is not found");

            List<string> missing = new List<string>();
            foreach (FlockErrorCode code in Enum.GetValues(typeof(FlockErrorCode)))
            {
                string hint = FlockErrorHints.For(code);
                if (string.IsNullOrEmpty(hint))
                    continue;
                foreach (Match call in Regex.Matches(hint, @"\b[A-Z][A-Za-z]*Async\b"))
                {
                    if (!publicMethods.Contains(call.Value))
                        missing.Add(code + ": " + call.Value);
                }
            }

            Assert.IsEmpty(missing, "Hints name calls the SDK does not have: " + string.Join(", ", missing));
        }
    }
}
