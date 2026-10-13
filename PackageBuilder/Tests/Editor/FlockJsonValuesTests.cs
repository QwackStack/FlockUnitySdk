using Flock.Models;
using Newtonsoft.Json.Linq;
using NUnit.Framework;

namespace Flock.Tests.Editor
{
    // Reading a JSON value as a type: anything that cannot become the type reads as false, never as an exception.
    public class FlockJsonValuesTests
    {
        [Test]
        public void ANumberTooLargeForTheType_ReadsAsCannotConvert_NotAThrow()
        {
            Assert.IsFalse(FlockJsonValues.TryConvert(99999999999L, out int _), "Past int's range");
            Assert.IsTrue(FlockJsonValues.TryConvert(99999999999L, out long wide), "Within long's range");
            Assert.AreEqual(99999999999L, wide);
            Assert.IsFalse(FlockJsonValues.TryConvert(JToken.Parse("1e40"), out long _), "Past long's range");
            Assert.IsFalse(FlockJsonValues.TryConvert(-1L, out byte _), "Below byte's range");
        }

        [Test]
        public void ANumberWithinTheType_StillConverts()
        {
            Assert.IsTrue(FlockJsonValues.TryConvert(7777L, out int port));
            Assert.AreEqual(7777, port);
            Assert.IsTrue(FlockJsonValues.TryConvert("7777", out int fromText));
            Assert.AreEqual(7777, fromText);
        }
    }
}
