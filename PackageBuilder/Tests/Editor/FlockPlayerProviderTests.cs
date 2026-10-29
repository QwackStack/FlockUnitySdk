using System.Collections;
using System.Collections.Generic;
using System.Threading.Tasks;
using Flock.Exceptions;
using Flock.Http;
using Flock.Models;
using Flock.Tests.Support;
using NUnit.Framework;
using UnityEngine.TestTools;

namespace Flock.Tests.Editor
{
    // PlayerProvider reads: by-id happy/not-found/validation, template-scoped fetch, and the never-cached
    // ban path (SNAP-15). CRUD writes go through FlockCommandProvider (covered in FlockCommandProviderTests).
    public class FlockPlayerProviderTests
    {
        // ---- PLYR-01 ----
        [Test]
        public void GetDataById_Success_ReturnsRow()
        {
            FlockFakeTransport transport = new FlockFakeTransport();
            transport.On(FlockEndpoints.PlayerDataById("pd-1"),
                FlockFakeTransport.Ok("{\"result\":{\"id\":\"pd-1\",\"player_id\":\"player-a\"}}"));
            using (FlockTestClient h = FlockTestClient.Create(transport))
            {
                h.LoginAs("player-a");

                PlayerData r = h.Run(() => h.Client.Player.GetDataByIdAsync("pd-1"));

                Assert.AreEqual("pd-1", r.Id);
            }
        }

        // ---- PLYR-02 ----
        [Test]
        public void GetDataById_NotFound_Throws()
        {
            FlockFakeTransport transport = new FlockFakeTransport();
            transport.On(FlockEndpoints.PlayerDataById("pd-x"), FlockFakeTransport.Coded(404, "player.not_found"));
            using (FlockTestClient h = FlockTestClient.Create(transport))
            {
                h.LoginAs("player-a");

                Assert.Catch<FlockException>(() => h.Run(() => h.Client.Player.GetDataByIdAsync("pd-x")));
            }
        }

        // ---- PLYR (validation) ----
        [Test]
        public void GetDataById_EmptyId_ThrowsValidation_NoNetwork()
        {
            FlockFakeTransport transport = new FlockFakeTransport();
            using (FlockTestClient h = FlockTestClient.Create(transport))
            {
                h.LoginAs("player-a");

                Assert.Throws<FlockValidationException>(() => h.Run(() => h.Client.Player.GetDataByIdAsync("")));
                Assert.AreEqual(0, transport.Requests.Count, "Validation short-circuits before any request.");
            }
        }

        // ---- PLYR-06 ----
        [Test]
        public void GetMyDataByTemplate_Success_ReturnsRow()
        {
            FlockFakeTransport transport = new FlockFakeTransport();
            transport.On("player_data?page=",
                FlockFakeTransport.Ok("{\"items\":[{\"id\":\"pd-1\",\"player_template_id\":\"tpl-1\",\"player_id\":\"player-a\",\"data\":[]}],\"total\":1,\"page\":1,\"limit\":100}"));
            using (FlockTestClient h = FlockTestClient.Create(transport))
            {
                h.LoginAs("player-a");

                PlayerData r = h.Run(() => h.Client.Player.GetMyDataByTemplateAsync("tpl-1"));

                Assert.IsNotNull(r);
                Assert.AreEqual("pd-1", r.Id);
            }
        }

        // ---- SNAP-15 (ban never cached): offline -> throws, never served from a snapshot ----
        [Test]
        public void GetBan_Offline_Throws_NeverServedFromCache()
        {
            FlockFakeTransport transport = new FlockFakeTransport();
            using (FlockTestClient h = FlockTestClient.Create(transport))
            {
                h.LoginAs("player-a");
                transport.GoOffline();

                Assert.Catch<FlockNetworkException>(() => h.Run(() => h.Client.Player.GetBanAsync("player-a")));
            }
        }

        // ---- PLYR (validation) ----
        [Test]
        public void GetBan_EmptyPlayerId_ThrowsValidation()
        {
            FlockFakeTransport transport = new FlockFakeTransport();
            using (FlockTestClient h = FlockTestClient.Create(transport))
            {
                h.LoginAs("player-a");

                Assert.Throws<FlockValidationException>(() => h.Run(() => h.Client.Player.GetBanAsync("")));
            }
        }

        // ---- The wallet: balances by currency code, from the template tagged "currency" ----
        // Both answers as the local backend sent them: the template list enveloped, each wallet category an object of integer fields.
        private const string TemplateList =
            "{\"error\":{\"code\":null},\"response\":{\"message\":null,\"code\":null},\"result\":[" +
            "{\"id\":\"tpl-progress\",\"name\":\"progress\",\"game_version_id\":\"gv-1\",\"schema\":[],\"data\":[],\"tag\":\"progress\"}," +
            "{\"id\":\"tpl-wallet\",\"name\":\"currencies\",\"game_version_id\":\"gv-1\",\"schema\":[],\"data\":[],\"tag\":\"currency\"}]}";

        private static string Currency(string code, string value)
            => "{\"type\":\"integer\",\"value\":" + value + ",\"type_name\":\"currency\",\"field_name\":\"" + code + "\"}";

        private static readonly string WalletRow =
            "{\"id\":\"pd-wallet\",\"player_template_id\":\"tpl-wallet\",\"game_id\":\"game-1\",\"player_id\":\"player-a\",\"data\":[" +
            "{\"type\":\"object\",\"value\":[" + Currency("Gold", "250") + "," + Currency("Gems", "0") + "],\"type_name\":\"wallet\",\"field_name\":\"game_currencies\"}," +
            "{\"type\":\"object\",\"value\":[" + Currency("SAR", "1200") + "],\"type_name\":\"wallet\",\"field_name\":\"real_currencies\"}]}";

        private const string ProgressRow = "{\"id\":\"pd-progress\",\"player_template_id\":\"tpl-progress\",\"player_id\":\"player-a\",\"data\":[]}";

        private static FlockFakeTransport WithRows(params string[] rows)
        {
            FlockFakeTransport transport = new FlockFakeTransport();
            transport.On("player_data?page=", FlockFakeTransport.Ok("{\"items\":[" + string.Join(",", rows) + "],\"total\":" + rows.Length + ",\"page\":1,\"limit\":100}"));
            transport.On(FlockEndpoints.PlayerTemplate, FlockFakeTransport.Ok(TemplateList));
            return transport;
        }

        private static ShopItem WalletItem(string currency, int price) => new ShopItem { Id = "item", Currency = currency, Price = price };

        [Test]
        public void GetWallet_ReadsEachCurrencysBalance_FromTheCurrencyTemplatesRow()
        {
            using (FlockTestClient h = FlockTestClient.Create(WithRows(ProgressRow, WalletRow)))
            {
                h.LoginAs("player-a");
                h.SetReachable(true);

                FlockWallet wallet = h.Run(() => h.Client.Player.GetWalletAsync());

                Assert.AreEqual(250, wallet.BalanceOf("Gold"));
                Assert.AreEqual(0, wallet.BalanceOf("Gems"));
                Assert.IsTrue(wallet.Holds("Gems"), "A zero balance is still held.");
                Assert.AreEqual(1200, wallet.RealCurrencies["SAR"]);
                Assert.IsFalse(wallet.GameCurrencies.ContainsKey("SAR"), "Real currencies are kept apart from the game's own.");
                Assert.AreEqual(0, wallet.BalanceOf("Shard"));
                Assert.IsFalse(wallet.Holds("Shard"));
                Assert.IsFalse(wallet.Holds("gold"), "Codes are matched exactly, as the server matches them.");
            }
        }

        // A read sent before the player's data was dropped may answer with data from before a change: it is handed back but never kept.
        [UnityTest]
        public IEnumerator AReadOnItsWay_WhenThePlayersDataIsDropped_DoesNotPutTheOldDataBack()
        {
            FlockFakeTransport transport = new FlockFakeTransport();
            transport.On(FlockEndpoints.PlayerTemplate, FlockFakeTransport.Ok(TemplateList));
            transport.Default(request =>
            {
                bool sentFirst = transport.AllTo("player_data?page=").IndexOf(request) == 0;
                string row = WalletRow.Replace(Currency("Gold", "250"), Currency("Gold", sentFirst ? "250" : "80"));
                return FlockFakeTransport.Ok("{\"items\":[" + row + "],\"total\":1,\"page\":1,\"limit\":100}");
            });
            using (FlockTestClient h = FlockTestClient.Create(transport))
            {
                h.LoginAs("player-a");
                h.SetReachable(true);
                h.Run(() => h.Client.Player.GetTemplatesAsync());
                transport.GateNext("player_data?page=");

                Task<FlockWallet> before = h.Client.Player.GetWalletAsync();
                yield return FlockTestWait.Until(() => transport.CountTo("player_data?page=") == 1, "The first read is on its way");
                h.Client.Player.ForgetPlayerData("player-a");
                transport.ReleaseGate();
                yield return FlockTestWait.Until(() => before.IsCompleted, "The read from before the drop answered");

                Assert.AreEqual(250, before.Result.BalanceOf("Gold"), "Control: the earlier read answered with the earlier balance, to its caller.");
                Assert.AreEqual(80, h.Run(() => h.Client.Player.GetWalletAsync()).BalanceOf("Gold"), "The earlier answer was not kept: the next read asked the server.");
                Assert.AreEqual(2, transport.CountTo("player_data?page="));
            }
        }

        [Test]
        public void Wallet_CanAfford_OnlyAGameCurrencyItemItsBalanceCovers()
        {
            using (FlockTestClient h = FlockTestClient.Create(WithRows(WalletRow)))
            {
                h.LoginAs("player-a");
                h.SetReachable(true);
                FlockWallet wallet = h.Run(() => h.Client.Player.GetWalletAsync());

                Assert.IsTrue(wallet.CanAfford(WalletItem("Gold", 250)), "Exactly the balance is enough.");
                Assert.IsFalse(wallet.CanAfford(WalletItem("Gold", 251)));
                Assert.IsFalse(wallet.CanAfford(WalletItem("Shard", 0)), "A currency the wallet does not hold buys nothing, even at no cost.");
                Assert.IsFalse(wallet.CanAfford(new ShopItem { Id = "pack", Currency = "Gold", Price = 1, IsRealMoney = true }), "A real-money item is bought through the store, never the wallet, whatever the wallet holds.");
                Assert.IsFalse(wallet.CanAfford(WalletItem("SAR", 1)), "A real currency is never spent from the wallet.");
                Assert.IsFalse(wallet.CanAfford(null));
            }
        }

        [Test]
        public void GetWallet_PlayerWithoutAWalletRow_ThrowsWithAHint()
        {
            using (FlockTestClient h = FlockTestClient.Create(WithRows(ProgressRow)))
            {
                h.LoginAs("player-a");
                h.SetReachable(true);

                FlockValidationException ex = Assert.Throws<FlockValidationException>(() => h.Run(() => h.Client.Player.GetWalletAsync()));

                StringAssert.Contains("currency template", ex.Hint);
            }
        }

        [Test]
        public void GetWallet_NotSignedIn_ThrowsBeforeReadingPlayerData()
        {
            FlockFakeTransport transport = WithRows(WalletRow);
            using (FlockTestClient h = FlockTestClient.Create(transport))
            {
                h.SetReachable(true);

                Assert.Catch<FlockException>(() => h.Run(() => h.Client.Player.GetWalletAsync()));
                Assert.AreEqual(0, transport.CountTo("player_data?page="));
            }
        }

        [Test]
        public void Wallet_FromAPurchasesWallet_ReadsTheSameShape()
        {
            PlayerData row = Newtonsoft.Json.JsonConvert.DeserializeObject<PlayerData>(WalletRow);

            FlockWallet wallet = FlockWallet.From(row);

            Assert.AreEqual(250, wallet.BalanceOf("Gold"));
            Assert.AreEqual(0, FlockWallet.From(null).GameCurrencies.Count, "A purchase that returned no wallet reads as an empty one.");
        }
    }
}
