using System;
using System.Threading;
using System.Threading.Tasks;
using Flock.Exceptions;
using Flock.Http;
using Flock.Models;
using Flock.Tests.Support;
using NUnit.Framework;

namespace Flock.Tests
{
    // Locks the developer-facing error text: the server's own reason and an SDK "Fix:" line must reach
    // FlockException.Message, and a purely client-side throw must keep the plain message it was given.
    public class FlockErrorMessageTests
    {
        private const string Url = "https://test.invalid/v1/x";

        private sealed class FakeAdapter : IFlockHttpAdapter
        {
            private readonly FlockHttpResponse _response;
            public FakeAdapter(FlockHttpResponse response) { _response = response; }
            public Task<FlockHttpResponse> SendAsync(FlockHttpRequest request, CancellationToken cancellationToken)
                => Task.FromResult(_response);
        }

        private static FlockHttpResponse Body(int status, string body)
            => new FlockHttpResponse { Result = FlockHttpResult.Success, StatusCode = status, Body = body };

        private static FlockHttpResponse Coded(int status, string code, string message)
            => Body(status, "{\"detail\":{\"code\":\"" + code + "\",\"message\":\"" + message + "\"}}");

        // Sends the canned response off Unity's sync-context so blocking can't deadlock; rethrows the inner exception.
        private static void Send(FlockHttpResponse canned)
        {
            FlockHttpClient.Configure(new FakeAdapter(canned));
            Task.Run(() => FlockHttpClient.GetAsync<Shop>(Url, null, CancellationToken.None)).GetAwaiter().GetResult();
        }

        private static T Run<T>(Func<Task<T>> action) => action().GetAwaiter().GetResult();

        [TearDown]
        public void TearDown()
        {
            FlockHttpClient.Configure(TimeSpan.FromSeconds(30));
            if (FlockClient.IsInitialized)
                FlockClient.Shutdown();
        }

        [Test]
        public void CodedError_Message_CarriesServerReason_CodeAndStatus()
        {
            FlockValidationException ex = Assert.Throws<FlockValidationException>(
                () => Send(Coded(400, "player.invalid_login_credentials", "Invalid login credentials")));

            Assert.AreEqual("Invalid login credentials", ex.ServerMessage);
            StringAssert.Contains("Invalid login credentials", ex.Message);
            StringAssert.Contains("player.invalid_login_credentials", ex.Message);
            StringAssert.Contains("HTTP 400", ex.Message);
            // The generic placeholder must not survive once the server told us why.
            StringAssert.DoesNotContain("Validation failed", ex.Message);
        }

        [Test]
        public void CodedError_Message_CarriesActionableHint()
        {
            FlockNetworkException ex = Assert.Throws<FlockNetworkException>(
                () => Send(Coded(404, "player_template.not_found_by_name", "Template not found")));

            StringAssert.Contains("Codegen > Sync", ex.Hint);
            StringAssert.Contains("Fix: ", ex.Message);
            StringAssert.Contains("Codegen > Sync", ex.Message);
        }

        [Test]
        public void UncodedError_Message_KeepsGenericTextAndStatus()
        {
            FlockValidationException ex = Assert.Throws<FlockValidationException>(() => Send(Body(400, "plain text, not json")));

            Assert.IsNull(ex.ServerMessage);
            Assert.IsNull(ex.Hint);
            Assert.AreEqual("Validation failed [HTTP 400]", ex.Message);
        }

        [Test]
        public void FastApiFieldErrors_Message_NamesTheOffendingField()
        {
            FlockValidationException ex = Assert.Throws<FlockValidationException>(() => Send(Body(422,
                "{\"detail\":[{\"loc\":[\"body\",\"player_data\"],\"msg\":\"Input should be a valid dictionary\",\"type\":\"dict_type\"}]}")));

            Assert.AreEqual("body.player_data: Input should be a valid dictionary", ex.ServerMessage);
            StringAssert.Contains("HTTP 422", ex.Message);
        }

        [Test]
        public void ClientSideThrow_Message_IsUnchanged()
        {
            // No status, no code, no operation — nothing to decorate, so the plain text must survive verbatim.
            FlockException ex = new FlockException("deviceId cannot be null or empty");
            Assert.AreEqual("deviceId cannot be null or empty", ex.Message);
        }

        [Test]
        public void ToString_StillAppendsRawBody()
        {
            FlockException ex = new FlockException("Validation failed") { Body = "{\"detail\":\"raw\"}", StatusCode = 400 };
            StringAssert.Contains("Response body: {\"detail\":\"raw\"}", ex.ToString());
            StringAssert.Contains("Validation failed [HTTP 400]", ex.ToString());
        }

        [Test]
        public void Hints_AreKeyedOnCode_NotMessageText()
        {
            Assert.IsNull(FlockErrorHints.For(FlockErrorCode.Unknown));
            Assert.IsNotNull(FlockErrorHints.For(FlockErrorCode.ShopInsufficientFunds));
            Assert.IsNotNull(FlockErrorHints.For(FlockErrorCode.PlayerDeviceAlreadyRegistered));
        }

        [Test]
        public void AuthHint_SameCode_DiffersByCredential()
        {
            string device = FlockErrorHints.ForAuth(FlockErrorCode.PlayerInvalidLoginCredentials, FlockAuthMethod.Device);
            string email = FlockErrorHints.ForAuth(FlockErrorCode.PlayerInvalidLoginCredentials, FlockAuthMethod.Email);

            StringAssert.Contains("RegisterWithDeviceAsync", device);
            StringAssert.Contains("RegisterWithEmailAsync", email);
            Assert.AreNotEqual(device, email);
        }

        [Test]
        public void AuthHint_FacebookAndDiscord_NeverNameANonExistentRegisterMethod()
        {
            // The backend has no FB/Discord register route, so the hint must point at linking instead.
            foreach (FlockAuthMethod method in new[] { FlockAuthMethod.Facebook, FlockAuthMethod.Discord })
            {
                string hint = FlockErrorHints.ForAuth(FlockErrorCode.PlayerInvalidLoginCredentials, method);
                StringAssert.DoesNotContain($"RegisterWith{method}Async", hint);
                StringAssert.Contains($"Link{method}Async", hint);
            }
        }

        [Test]
        public void DeviceLogin_UnregisteredDevice_PointsAtRegisterWithDeviceAsync()
        {
            FlockFakeTransport transport = new FlockFakeTransport()
                .On("player/login/device", FlockFakeTransport.Status(400,
                    "{\"detail\":{\"code\":\"player.invalid_login_credentials\",\"message\":\"Invalid login credentials\"}}"));

            using (FlockTestClient harness = FlockTestClient.Create(transport))
            {
                FlockException ex = Assert.Throws<FlockValidationException>(
                    () => Run(() => harness.Client.Authentication.LoginWithDeviceAsync("device-1")));

                Assert.AreEqual("Device login", ex.Operation);
                StringAssert.Contains("Device login failed:", ex.Message);
                StringAssert.Contains("Invalid login credentials", ex.Message);
                StringAssert.Contains("RegisterWithDeviceAsync", ex.Message);
            }
        }
    }
}
