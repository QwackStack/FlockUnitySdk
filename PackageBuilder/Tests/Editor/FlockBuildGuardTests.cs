using Flock.Editor;
using NUnit.Framework;

namespace Flock.Tests
{
    // Covers FlockBuildGuard.GetBuildBlockReason — the pure decision behind failing a player build
    // on an unresolved or drifted Game Version ID.
    public class FlockBuildGuardTests
    {
        [Test]
        public void GuardDisabled_AllowsBuild()
            => Assert.IsNull(FlockBuildGuard.GetBuildBlockReason("", null, guardEnabled: false));

        [Test]
        public void UnresolvedVersion_BlocksBuild()
        {
            string reason = FlockBuildGuard.GetBuildBlockReason("", null, guardEnabled: true);
            Assert.IsNotNull(reason);
            StringAssert.Contains("not resolved", reason);
        }

        [Test]
        public void MatchingVersions_AllowBuild()
            => Assert.IsNull(FlockBuildGuard.GetBuildBlockReason("v1", "v1", guardEnabled: true));

        [Test]
        public void DriftedVersions_BlockBuild()
        {
            string reason = FlockBuildGuard.GetBuildBlockReason("v1", "v2", guardEnabled: true);
            Assert.IsNotNull(reason);
            StringAssert.Contains("drift", reason);
        }

        [Test]
        public void NoGeneratedManifest_AllowsBuild()
        {
            // Codegen hasn't run yet (generated id null) — not drift, so don't block the build.
            Assert.IsNull(FlockBuildGuard.GetBuildBlockReason("v1", null, guardEnabled: true));
        }
    }
}
