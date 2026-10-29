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
        private FlockStore _store;
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
            // A consumable the player holds is used here; using it grants what it holds.
            if (item.Type == ShopItemTypes.Consumable && owned > 0 && GUILayout.Button("Use", GUILayout.Width(70f), GUILayout.Height(40f)))
                Run(() => UseAsync(item));
            GUILayout.EndHorizontal();
        }

        // Null when the item can be bought now, else what the button says instead.
        private string WhyNotBuyable(ShopItem item, int owned)
        {
            // A store sells a standard item once; the wallet would sell it again, so the game checks.
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

        // One call for every item: the wallet pays for a game-currency one, the store for a real-money one.
        private async Task BuyAsync(ShopItem item)
        {
            Say($"Buying {item.Name}...");
            try
            {
                PurchaseResult bought = await FlockClient.Instance.Shop.PurchaseAsync(item.Id);
                Say(bought.IsPaid ? $"Bought {item.Name}{Granted(bought.Granted)}." : $"{item.Name} waits for its payment to clear, and is delivered then.");
            }
            catch (FlockException refused) when (refused.ErrorCode == FlockErrorCode.ShopInsufficientFunds)
            {
                Say($"Not enough {item.Currency} for {item.Name}: nothing was bought.");
            }
#if FLOCK_UNITY_IAP_5
            catch (FlockStoreException notTaken)
            {
                Say(StoreAnswer(item, notTaken));
            }
#endif
            catch (FlockException unknown) when (unknown.OutcomeUnknown)
            {
                // No answer said whether it went through: what the player holds, read below, says which.
                Say($"Flock did not answer, so {item.Name} may have been bought: reading the wallet and inventory again.");
            }
            catch (FlockException refused)
            {
                Say(refused.Message);
            }
            await RefreshSafelyAsync();
        }

        private async Task UseAsync(ShopItem item)
        {
            try
            {
                ConsumeResult used = await FlockClient.Instance.Shop.ConsumeOneAsync(item.Id);
                Say($"Used {item.Name}{Granted(used.Granted)}.");
            }
            catch (FlockException unknown) when (unknown.OutcomeUnknown)
            {
                Say($"Flock did not answer, so {item.Name} may have been used: reading the inventory again.");
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

        private static string Granted(List<ShopItemReward> granted)
        {
            if (granted == null || granted.Count == 0)
                return string.Empty;
            List<string> parts = new List<string>();
            foreach (ShopItemReward reward in granted)
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
                    _store = await FlockStore.ConnectAsync(Application.isEditor ? editorStore : null, wait.Token);
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
            // Every delivered store purchase, this screen's own included: the place to refresh what the player holds.
            _store.PurchaseCompleted += (item, result) => RefreshInTheBackgroundAsync();
            _store.PurchaseNotDelivered += purchase => Say($"A purchase of {purchase.ProductId} was paid for but not delivered: {purchase.Error.Message}");
            foreach (ShopItem item in _items)
                if (item.IsRealMoney)
                    _storePrices[item.Id] = StorePriceOf(item);
            Say($"Connected to {_store.PaymentProvider}.");
        }

        private FlockStorePrice StorePriceOf(ShopItem item)
        {
            try
            {
                return _store.GetPrice(item.Id);
            }
            catch (FlockValidationException notSoldHere)
            {
                Say($"{item.Name}: {notSoldHere.Message}");
                return null;
            }
        }

        private async void RefreshInTheBackgroundAsync() => await RefreshSafelyAsync();

        private static string StoreAnswer(ShopItem item, FlockStoreException notTaken)
        {
            switch (notTaken.Reason)
            {
                case FlockStoreFailureReasons.Cancelled:
                    return "Cancelled: nothing was bought.";
                case FlockStoreFailureReasons.AlreadyOwned:
                    return $"This store account already owns {item.Name}: tap Restore purchases.";
                case FlockStoreFailureReasons.AlreadyPending:
                    return $"A purchase of {item.Name} is already waiting for its payment.";
                case FlockStoreFailureReasons.PaymentDeclined:
                    return "The payment was declined: nothing was charged.";
                case FlockStoreFailureReasons.StoreUnavailable:
                    return "The store could not take the payment, so nothing was charged: check the connection and the way to pay, then try again.";
                case FlockStoreFailureReasons.NotSignedInToTheStore:
                    return "Sign in to the store on this device, then buy again.";
                default:
                    return notTaken.Message;
            }
        }

        private async Task RestoreAsync()
        {
            Say("Restoring purchases...");
            FlockStoreRestore restore = await _store.RestoreAsync();
            Say($"Restored {restore.Restored.Count} purchase(s).");
            foreach (FlockUndeliveredPurchase notDelivered in restore.NotDelivered)
                Say($"Not restored, {notDelivered.ProductId}: {notDelivered.Error.Message}");
            await RefreshSafelyAsync();
        }

        // For the developer: what the store and Flock's server say about the setup, without buying anything.
        private async Task CheckSetupAsync()
        {
            FlockStoreSetupCheck check = await _store.CheckSetupAsync();
            if (check.IsReady)
                Say("The store setup is complete: the store has every real-money item, and Flock's server can ask it about purchases.");
            foreach (ShopItem missing in check.NotFoundInTheStore)
                Say($"The store has no product '{missing.StoreProductIds[_store.PaymentProvider]}' for {missing.Name}: create it in the store, or correct the id on the item in the Flock dashboard.");
            foreach (ShopItem noId in check.MissingProductId)
                Say($"{noId.Name} has no {_store.PaymentProvider} product id: enter it on the item in the Flock dashboard.");
            if (check.ServerProblem != null)
                Say("Flock's server cannot ask the store about purchases: " + check.ServerProblem.Message);
        }
#endif
    }
}
