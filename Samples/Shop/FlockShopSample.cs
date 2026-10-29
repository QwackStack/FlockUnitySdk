using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using UnityEngine;
using Flock.Exceptions;
using Flock.Models;
#if FLOCK_UNITY_IAP_5
using System.Threading;
using Flock.Providers;
#endif

namespace Flock.Samples
{
    /// A shop screen: every item of the game's shops with its price (the store's own for a real-money item), what the player
    /// owns, wallet items the player cannot pay for greyed out, and each purchase's outcome in plain words. Real-money items
    /// are sold when Unity IAP 5 is installed, with Restore purchases and Check store setup beside them. Drop it on any
    /// GameObject beside a FlockBootstrap and press Play; in the editor, Unity IAP's fake store answers for the store below.
    public class FlockShopSample : MonoBehaviour
    {
        [Tooltip("The store to test with in the editor, where Unity IAP's fake store answers; a phone finds its own.")]
        [SerializeField] private string editorStore = PaymentProviders.GooglePlay;

        private readonly List<ShopItem> _items = new List<ShopItem>();
        private readonly List<string> _lines = new List<string>();
        private IReadOnlyDictionary<string, int> _owned = new Dictionary<string, int>();
        private FlockWallet _wallet;
        private bool _opened;
        private bool _busy;
        private Vector2 _scroll;
#if FLOCK_UNITY_IAP_5
        private FlockStorePurchases _store;
        private readonly Dictionary<string, FlockStorePrice> _storePrices = new Dictionary<string, FlockStorePrice>();
#endif

        private void OnGUI()
        {
            // Sized by the shorter side, so a phone turned sideways keeps the same text size and scrolls.
            float scale = Mathf.Max(1f, Mathf.Min(Screen.width, Screen.height) / 540f);
            GUI.matrix = Matrix4x4.Scale(new Vector3(scale, scale, 1f));
            GUILayout.BeginArea(new Rect(12f, 12f, Screen.width / scale - 24f, Screen.height / scale - 24f), GUI.skin.box);
            GUILayout.Label("Flock - Shop");

            if (!FlockClient.IsInitialized)
            {
                GUILayout.Label("Flock is not initialized. Add a FlockBootstrap to the scene (Flock > Settings), fill in your FlockConfig, then press Play.");
                GUILayout.EndArea();
                return;
            }

            _scroll = GUILayout.BeginScrollView(_scroll);
            GUI.enabled = !_busy;
            if (!FlockClient.Instance.IsAuthenticated)
            {
                if (GUILayout.Button("Log in (device)", GUILayout.Height(40f)))
                    Run(SignInAsync);
            }
            else if (!_opened)
            {
                if (GUILayout.Button("Open the shop", GUILayout.Height(40f)))
                    Run(OpenAsync);
            }
            else
            {
                GUILayout.Label("Wallet: " + DescribeWallet());
                foreach (ShopItem item in _items)
                    DrawItem(item);
                DrawStoreButtons();
            }
            GUI.enabled = true;

            for (int i = _lines.Count - 1; i >= 0; i--)
                GUILayout.Label(_lines[i]);
            GUILayout.EndScrollView();
            GUILayout.EndArea();
        }

        private void OnDestroy()
        {
#if FLOCK_UNITY_IAP_5
            _store?.Dispose();
#endif
        }

        // One button's work: the buttons are off while it runs, and a failure is shown (a Flock failure's message ends with its fix).
        private async void Run(Func<Task> action)
        {
            _busy = true;
            try
            {
                await action();
            }
            catch (FlockException failed)
            {
                Say(failed.Message);
            }
            catch (Exception unexpected)
            {
                Say($"{unexpected.GetType().Name}: {unexpected.Message}");
            }
            finally
            {
                _busy = false;
            }
        }

        // A device signs in once registered; the first launch on a device registers it.
        private async Task SignInAsync()
        {
            string deviceId = SystemInfo.deviceUniqueIdentifier;
            try
            {
                await FlockClient.Instance.Authentication.LoginWithDeviceAsync(deviceId);
                Say("Logged in.");
            }
            catch (FlockException notRegistered) when (notRegistered.ErrorCode == FlockErrorCode.PlayerInvalidLoginCredentials)
            {
                await FlockClient.Instance.Authentication.RegisterWithDeviceAsync(deviceId);
                Say("Registered this device and logged in.");
            }
        }

        private async Task OpenAsync()
        {
            _items.Clear();
            _items.AddRange(await ReadItemsAsync());
#if FLOCK_UNITY_IAP_5
            if (_store == null && _items.Exists(item => item.IsRealMoney))
                await ConnectTheStoreAsync();
#endif
            await RefreshAsync();
            _opened = true;
            Say($"The shop has {_items.Count} items.");
        }

        private static async Task<List<ShopItem>> ReadItemsAsync()
        {
            List<ShopItem> items = new List<ShopItem>();
            for (int page = 1; ; page++)
            {
                PaginatedResponse<Shop> shops = await FlockClient.Instance.Shop.GetAllAsync(page, 100);
                if (shops?.Items == null || shops.Items.Length == 0)
                    return items;
                foreach (Shop shop in shops.Items)
                    if (shop?.ShopItems != null)
                        items.AddRange(shop.ShopItems);
                if (page * 100 >= shops.Total)
                    return items;
            }
        }

        // The wallet and the inventory, read again after every purchase so the screen shows what the server holds.
        private async Task RefreshAsync()
        {
#if !FLOCK_NO_PLAYER
            try
            {
                _wallet = await FlockClient.Instance.Player.GetWalletAsync();
            }
            catch (FlockValidationException noWallet)
            {
                _wallet = null;
                Say(noWallet.Message);
            }
#endif
            _owned = await FlockClient.Instance.Shop.GetOwnedItemCountsAsync();
        }

        private async Task RefreshSafelyAsync()
        {
            try
            {
                await RefreshAsync();
            }
            catch (FlockException failed)
            {
                Say("Could not read the wallet and inventory again: " + failed.Message);
            }
        }

        private void DrawItem(ShopItem item)
        {
            _owned.TryGetValue(item.Id, out int owned);
            string ownedText = owned > 0 ? $"  (owned: {owned})" : string.Empty;
            string notBuyable = WhyNotBuyable(item, owned);
            GUILayout.BeginHorizontal();
            GUILayout.Label($"{item.Name}  {PriceOf(item)}{ownedText}", GUILayout.Width(300f));
            GUI.enabled = !_busy && notBuyable == null;
            if (GUILayout.Button(notBuyable ?? "Buy", GUILayout.Height(40f)))
                Run(() => BuyAsync(item));
            GUI.enabled = !_busy;
            GUILayout.EndHorizontal();
        }

        // Null when the item can be bought now, else what the button says instead.
        private string WhyNotBuyable(ShopItem item, int owned)
        {
            if (item.Type == ShopItemTypes.Standard && owned > 0)
                return "Owned";
            if (item.IsRealMoney)
            {
#if FLOCK_UNITY_IAP_5
                if (_store == null)
                    return "Store not connected";
                return _storePrices.TryGetValue(item.Id, out FlockStorePrice price) && price != null ? null : "Not in the store";
#else
                return "Needs Unity IAP 5";
#endif
            }
            // Without a wallet read the server decides, and says why when it refuses.
            if (_wallet != null && !_wallet.CanAfford(item))
                return $"Not enough {item.Currency}";
            return null;
        }

        private string PriceOf(ShopItem item)
        {
            if (!item.IsRealMoney)
                return $"{item.Price} {item.Currency}";
#if FLOCK_UNITY_IAP_5
            // The store's price is in the player's currency with their taxes, so it is the one to show.
            if (_storePrices.TryGetValue(item.Id, out FlockStorePrice price) && price != null)
                return price.Text;
#endif
            return string.Empty;
        }

        private string DescribeWallet()
        {
            if (_wallet == null)
                return "not read";
            List<string> balances = new List<string>();
            foreach (KeyValuePair<string, long> currency in _wallet.GameCurrencies)
                balances.Add($"{currency.Value} {currency.Key}");
            return balances.Count > 0 ? string.Join(", ", balances) : "empty";
        }

        private async Task BuyAsync(ShopItem item)
        {
#if FLOCK_UNITY_IAP_5
            if (item.IsRealMoney)
            {
                await BuyFromTheStoreAsync(item);
                return;
            }
#endif
            await BuyWithTheWalletAsync(item);
        }

        private async Task BuyWithTheWalletAsync(ShopItem item)
        {
            Say($"Buying {item.Name} for {item.Price} {item.Currency}...");
            try
            {
                PurchaseResult bought = await FlockClient.Instance.Shop.PurchaseAsync(item.Id);
                Say($"Bought {item.Name}{Granted(bought)}.");
            }
            catch (FlockException refused) when (refused.ErrorCode == FlockErrorCode.ShopInsufficientFunds)
            {
                Say($"Not enough {item.Currency} for {item.Name}: nothing was bought.");
            }
            catch (FlockNetworkException noAnswer) when (noAnswer.StatusCode == null || noAnswer.StatusCode >= 500)
            {
                // The answer never came, so the purchase may have gone through: what the player holds, read below, says which.
                Say($"Flock did not answer, so {item.Name} may have been bought: reading the wallet and inventory again.");
            }
            catch (FlockException refused)
            {
                Say(refused.Message);
            }
            await RefreshSafelyAsync();
        }

        private void DrawStoreButtons()
        {
#if FLOCK_UNITY_IAP_5
            if (_store == null)
                return;
            GUILayout.BeginHorizontal();
            if (GUILayout.Button("Restore purchases", GUILayout.Height(40f)))
                Run(RestoreAsync);
            if (GUILayout.Button("Check store setup", GUILayout.Height(40f)))
                Run(CheckSetupAsync);
            GUILayout.EndHorizontal();
#endif
        }

        private static string Granted(PurchaseResult result)
        {
            if (result?.Granted == null || result.Granted.Count == 0)
                return string.Empty;
            List<string> parts = new List<string>();
            foreach (ShopItemReward reward in result.Granted)
                parts.Add($"{reward.Amount} {reward.Code}");
            return ", got " + string.Join(", ", parts);
        }

        private void Say(string line)
        {
            _lines.Add($"{DateTime.Now:HH:mm:ss} {line}");
            Debug.Log("[Flock Shop Sample] " + line);
        }

#if FLOCK_UNITY_IAP_5
        // A store that never answers is given up on after a while, so the wallet items still sell.
        private async Task ConnectTheStoreAsync()
        {
            try
            {
                using (CancellationTokenSource wait = new CancellationTokenSource(TimeSpan.FromSeconds(30)))
                    _store = await FlockStorePurchases.ConnectAsync(Application.isEditor ? editorStore : null, wait.Token);
            }
            catch (FlockException failed)
            {
                Say("Real-money items cannot be bought: " + failed.Message);
                return;
            }
            catch (OperationCanceledException)
            {
                Say("The store did not answer in 30 seconds, so real-money items cannot be bought now.");
                return;
            }
            _store.PurchaseDelivered += purchase =>
            {
                Say($"Delivered {purchase.Item.Name}, paid for earlier{Granted(purchase.Delivery)}.");
                RefreshInTheBackgroundAsync();
            };
            _store.PurchaseNeedsAttention += problem => Say($"A purchase of {problem.ProductId} was not delivered: {problem.Error.Message}");
            foreach (ShopItem item in _items)
                if (item.IsRealMoney)
                    _storePrices[item.Id] = StorePriceOf(item);
            Say($"Connected to {_store.Store}.");
        }

        private FlockStorePrice StorePriceOf(ShopItem item)
        {
            try
            {
                return _store.GetStorePrice(item.Id);
            }
            catch (FlockValidationException notSoldHere)
            {
                Say($"{item.Name}: {notSoldHere.Message}");
                return null;
            }
        }

        private async void RefreshInTheBackgroundAsync() => await RefreshSafelyAsync();

        private async Task BuyFromTheStoreAsync(ShopItem item)
        {
            Say($"Buying {item.Name} from the store...");
            try
            {
                FlockStorePurchase purchase = await _store.BuyAsync(item.Id);
                Say(Outcome(purchase));
            }
            catch (FlockException refused)
            {
                // The player paid and Flock refused: the store keeps the purchase and hands it back at the next launch.
                Say(refused.Message);
            }
            await RefreshSafelyAsync();
        }

        private static string Outcome(FlockStorePurchase purchase)
        {
            switch (purchase.Outcome)
            {
                case FlockStorePurchaseOutcome.Delivered:
                    return $"Bought {purchase.Item.Name}{Granted(purchase.Delivery)}.";
                case FlockStorePurchaseOutcome.Pending:
                    return $"{purchase.Item.Name} waits for its payment to clear, and is delivered then.";
                case FlockStorePurchaseOutcome.Cancelled:
                    return "Cancelled: nothing was bought.";
            }
            switch (purchase.StoreFailureReason)
            {
                case FlockStoreFailureReasons.AlreadyOwned:
                    return $"This store account already owns {purchase.Item.Name}: tap Restore purchases.";
                case FlockStoreFailureReasons.AlreadyPending:
                    return $"A purchase of {purchase.Item.Name} is already waiting for its payment.";
                case FlockStoreFailureReasons.PaymentDeclined:
                    return "The payment was declined: nothing was charged.";
                case FlockStoreFailureReasons.StoreUnavailable:
                    return "The store could not be reached: try again later.";
                case FlockStoreFailureReasons.NotSignedInToTheStore:
                    return "Sign in to the store on this device, then buy again.";
                default:
                    return "The store did not take the payment: " + purchase.StoreFailure;
            }
        }

        private async Task RestoreAsync()
        {
            Say("Restoring purchases...");
            FlockStoreRestore restore = await _store.RestoreAsync();
            Say($"Restored {restore.Restored.Count} purchase(s).");
            foreach (FlockStorePurchaseProblem problem in restore.Problems)
                Say($"Not restored, {problem.ProductId}: {problem.Error.Message}");
            await RefreshSafelyAsync();
        }

        // For the developer: what the store and Flock's server say about the setup, without buying anything.
        private async Task CheckSetupAsync()
        {
            FlockStoreSetupCheck check = await _store.CheckSetupAsync();
            if (check.AllGood)
                Say("The store setup is complete: the store has every real-money item, and Flock's server can ask it about purchases.");
            foreach (ShopItem missing in check.ProductsTheStoreDidNotFind)
                Say($"The store has no product '{missing.StoreProductIds[_store.Store]}' for {missing.Name}: create it in the store, or correct the id on the item in the Flock dashboard.");
            foreach (ShopItem noId in check.ItemsWithoutAProductId)
                Say($"{noId.Name} has no {_store.Store} product id: enter it on the item in the Flock dashboard.");
            if (check.StoreProblem != null)
                Say("Flock's server cannot ask the store about purchases: " + check.StoreProblem.Message);
        }
#endif
    }
}
