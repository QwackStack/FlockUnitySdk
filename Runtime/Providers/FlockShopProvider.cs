using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Flock.Exceptions;
using Flock.Models;
using Flock.Http;

namespace Flock.Providers
{
    public enum PurchaseStatus
    {
        Started,
        Purchased,
        Failed
    }
    public enum TransactionType
    {
        //Status will change once validation/flow changes
        Purchase
    }
    // The store connection a real-money PurchaseAsync goes through (FlockStore, in the Unity IAP assembly), set while it is connected.
    internal interface IFlockStoreCheckout
    {
        // The item as the store connection read it from the game's shops, or null when it was not there.
        ShopItem Find(string shopItemId);

        Task<PurchaseResult> PurchaseAsync(ShopItem item, CancellationToken cancellationToken);
    }

    public class FlockShopProvider : FlockProviderBase
    {
        private const string SnapshotCategory = "shop";

        // Set by FlockStore when it connects and cleared when it is disposed.
        internal IFlockStoreCheckout ConnectedStore { get; set; }

        // Full paginated response cache — page metadata (total/page/limit) isn't part of the Shop entity, so it isn't folded into _shopsById.
        private readonly Dictionary<string, PaginatedResponse<Shop>> _shopPages = new Dictionary<string, PaginatedResponse<Shop>>();

        // Canonical stores — every shop/item lives here exactly once, keyed by id.
        private readonly Dictionary<string, Shop> _shopsById = new Dictionary<string, Shop>();
        private readonly Dictionary<string, ShopItem> _itemsById = new Dictionary<string, ShopItem>();

        // Indexes into the canonical stores above — id (or id-list) per query, not duplicate objects.
        private readonly Dictionary<string, string> _shopIdByName = new Dictionary<string, string>();
        private readonly Dictionary<string, List<string>> _itemIdsByShop = new Dictionary<string, List<string>>();

        // The store account token and the sign-in it belongs to.
        private string _accountToken;
        private int? _accountTokenSignIn;

        public FlockShopProvider(FlockClient client) : base(client) { }

        public void ClearCache()
        {
            _shopPages.Clear();
            _shopsById.Clear();
            _shopIdByName.Clear();
            _itemsById.Clear();
            _itemIdsByShop.Clear();
            DeleteSnapshotCategory(SnapshotCategory);
        }

        public async Task<PaginatedResponse<Shop>> GetAllAsync(
            int page = 1, int limit = 100, CancellationToken cancellationToken = default)
        {
            string pageKey = $"all_p{page}_l{limit}";
            if (_shopPages.TryGetValue(pageKey, out PaginatedResponse<Shop> cached))
                return cached;

            PaginatedResponse<Shop> shops = await FetchWithSnapshotAsync(
                SnapshotCategory, pageKey, async () =>
                {
                    return await FlockHttpClient.GetAsync<PaginatedResponse<Shop>>(
                        $"{Client.GetVersionedApiUrl()}/{FlockEndpoints.Shop}?page={page}&limit={limit}", Client.GetBaseHeaders(), cancellationToken);
                }, "Fetch shops", cancellationToken);

            if (shops != null)
            {
                _shopPages[pageKey] = shops;
                if (shops.Items != null)
                {
                    foreach (Shop shop in shops.Items)
                        IndexShop(shop);
                }
            }
            return shops;
        }

        public async Task<Shop> GetByIdAsync(
            string shopId, CancellationToken cancellationToken = default)
        {
            RequireNotEmpty(shopId, "Shop ID");
            if (_shopsById.TryGetValue(shopId, out Shop cached))
                return cached;

            Shop shop = await FetchWithSnapshotAsync(
                SnapshotCategory, $"shop_{shopId}",
                async () => await FlockHttpClient.GetAsync<Shop>(
                    $"{Client.GetVersionedApiUrl()}/{FlockEndpoints.ShopById(shopId)}", Client.GetBaseHeaders(), cancellationToken),
                "Fetch shop", cancellationToken);

            // Keyed by the requested id (not shop.Id) so a hit is guaranteed on the next call with this same shopId.
            if (shop != null)
                _shopsById[shopId] = shop;
            return shop;
        }

        public async Task<Shop> GetByNameAsync(
            string name, CancellationToken cancellationToken = default)
        {
            RequireNotEmpty(name, "Shop Name");
            if (_shopIdByName.TryGetValue(name, out string cachedId) && _shopsById.TryGetValue(cachedId, out Shop cached))
                return cached;

            Shop shop = await FetchWithSnapshotAsync(
                SnapshotCategory, $"shop_name_{name}",
                async () => await FlockHttpClient.GetAsync<Shop>(
                    $"{Client.GetVersionedApiUrl()}/{FlockEndpoints.ShopByName(name)}", Client.GetBaseHeaders(), cancellationToken),
                "Fetch shop by name", cancellationToken);

            IndexShop(shop);
            if (shop != null && !string.IsNullOrEmpty(shop.Id))
                _shopIdByName[name] = shop.Id;
            return shop;
        }

        private void IndexShop(Shop shop)
        {
            if (shop == null || string.IsNullOrEmpty(shop.Id)) return;
            _shopsById[shop.Id] = shop;
        }

        public async Task<ShopItem> GetItemAsync(
            string shopItemId, CancellationToken cancellationToken = default)
        {
            RequireNotEmpty(shopItemId, "Shop Item ID");
            if (_itemsById.TryGetValue(shopItemId, out ShopItem cached))
                return cached;

            ShopItem item = await FetchWithSnapshotAsync(
                SnapshotCategory, $"item_{shopItemId}", async () =>
                {
                    return await FlockHttpClient.GetAsync<ShopItem>(
                        $"{Client.GetVersionedApiUrl()}/{FlockEndpoints.ShopItemById(shopItemId)}", Client.GetBaseHeaders(), cancellationToken);
                }, "Fetch shop item", cancellationToken);

            if (item != null)
                _itemsById[shopItemId] = item;
            return item;
        }

        public async Task<List<ShopItem>> GetItemsByShopAsync(
            string shopId, string patchId = null, CancellationToken cancellationToken = default)
        {
            RequireNotEmpty(shopId, "Shop ID");

            string cacheKey = $"items_shop_{shopId}_{(string.IsNullOrEmpty(patchId) ? "current" : patchId)}";
            if (_itemIdsByShop.TryGetValue(cacheKey, out List<string> cachedIds))
                return ResolveItems(cachedIds);

            List<ShopItem> items = await FetchWithSnapshotAsync(
                SnapshotCategory, cacheKey, async () =>
                {
                    string url = $"{Client.GetVersionedApiUrl()}/{FlockEndpoints.ShopItemsByShop(shopId)}{(!string.IsNullOrEmpty(patchId) ? $"?patch_id={patchId}" : "")}";

                    GenericResponse<List<ShopItem>> response = await FlockHttpClient.GetAsync<GenericResponse<List<ShopItem>>>(
                        url, Client.GetBaseHeaders(), cancellationToken);
                    ValidateResponse(response);
                    return response.Result;
                }, "Fetch shop items", cancellationToken);

            List<string> ids = new List<string>(items.Count);
            foreach (ShopItem item in items)
            {
                if (item != null && !string.IsNullOrEmpty(item.Id))
                {
                    _itemsById[item.Id] = item;
                    ids.Add(item.Id);
                }
            }
            _itemIdsByShop[cacheKey] = ids;
            return ResolveItems(ids);
        }

        private List<ShopItem> ResolveItems(List<string> ids)
        {
            List<ShopItem> result = new List<ShopItem>(ids.Count);
            foreach (string id in ids)
                if (_itemsById.TryGetValue(id, out ShopItem item))
                    result.Add(item);
            return result;
        }

        /// <summary>Buys a shop item for the signed-in player: one priced in a game currency from the wallet, a real-money one through the store connected with FlockStore.ConnectAsync. Returns what Flock delivered (Granted, Inventory, Balances), the wallet also written into the cached player data; a real-money purchase the store is still waiting to be paid for answers with Status pending and is reported by FlockStore.PurchaseCompleted once paid. Throws when nothing was bought: the server's refusal (such as ShopInsufficientFunds), or FlockStoreException when the store did not take the payment. A failure with <see cref="FlockException.OutcomeUnknown"/> set may have bought it. A wallet purchase whose answer never came is sent again under the same key, so it is never charged twice.</summary>
        public async Task<PurchaseResult> PurchaseAsync(string shopItemId, CancellationToken cancellationToken = default)
        {
            RequireNotEmpty(shopItemId, "Shop Item ID");
            string buyer = Client.CurrentPlayerId;
            RequireNotEmpty(buyer, "Player ID", FlockErrorHints.SignInFirst);
            // Taken before the item is read, so the purchase and its records never go out as a player who signed in meanwhile.
            int? purchaser = SignInToActFor;
            IFlockStoreCheckout store = ConnectedStore;
            ShopItem item = store?.Find(shopItemId) ?? await GetItemAsync(shopItemId, cancellationToken);
            if (item == null || !item.IsRealMoney)
                return await BuyWithTheWalletAsync(shopItemId, buyer, purchaser, item, cancellationToken);

            if (store == null)
                throw new FlockValidationException("This shop item costs real money, and no store is connected")
                {
                    Hint = "Connect the store once at start with FlockStore.ConnectAsync (it needs Unity IAP 5, on a phone or with the editor's fake store); real-money items are then bought through this same call."
                };
            return await store.PurchaseAsync(item, cancellationToken);
        }

        /// <summary>Buys a shop item with the wallet of <paramref name="playerId"/>, or of the signed-in player when it is null.</summary>
        [Obsolete("Use PurchaseAsync(shopItemId): a purchase is always for the signed-in player, and the server refuses another player's id.")]
        public Task<PurchaseResult> PurchaseAsync(string shopItemId, string playerId, CancellationToken cancellationToken = default)
            => BuyWithTheWalletAsync(shopItemId, string.IsNullOrEmpty(playerId) ? Client.CurrentPlayerId : playerId, SignInToActFor, null, cancellationToken);

        // The purchaser is the sign-in taken with the player id, so the purchase and its records never go out as a player who signed in after.
        private async Task<PurchaseResult> BuyWithTheWalletAsync(string shopItemId, string playerId, int? purchaser, ShopItem knownItem, CancellationToken cancellationToken)
        {
            RequireNotEmpty(shopItemId, "Shop Item ID");
            RequireNotEmpty(playerId, "Player ID", FlockErrorHints.SignInFirst);

            ShopItem shopItem = knownItem ?? await GetItemAsync(shopItemId, cancellationToken);
            RecordPurchaseInTheBackground(shopItem, shopItemId, nameof(PurchaseStatus.Started), purchaser);

            // One key for every attempt of this call: the server answers a resend with the first purchase, so an answer that never came is sent again.
            string purchaseKey = Guid.NewGuid().ToString("N");
            PurchaseResult result;
            try
            {
                result = await ExecuteAsync(async () =>
                {
                    ShopTransactionRequest request = new ShopTransactionRequest
                    {
                        ShopItemId = shopItemId,
                        PlayerId = playerId,
                        IdempotencyKey = purchaseKey
                    };

                    return await FlockHttpClient.PostAsync<PurchaseResult>(
                        $"{Client.GetVersionedApiUrl()}/{FlockEndpoints.ShopTransaction}", request, Client.GetBaseHeaders(), cancellationToken);
                }, "Purchase shop item", cancellationToken, idempotent: true, actsForSignIn: purchaser, spendsOrGrants: true);
            }
            catch (FlockException unknown) when (unknown.OutcomeUnknown)
            {
                // Neither delivered nor refused: the usual "make the call again" would be a second purchase.
                unknown.Hint = "The purchase may have been made: no answer said whether it was. " + ReadBeforeBuyingAgain;
                RecordPurchaseInTheBackground(shopItem, shopItemId, nameof(PurchaseStatus.Failed), purchaser);
                throw;
            }
            catch
            {
                RecordPurchaseInTheBackground(shopItem, shopItemId, nameof(PurchaseStatus.Failed), purchaser);
                throw;
            }

            RequirePurchaseNamed(result, ReadBeforeBuyingAgain);
            RecordPurchaseInTheBackground(shopItem, shopItemId, nameof(PurchaseStatus.Purchased), purchaser);
            ApplyWalletToPlayerCache(result.Wallet);
            return result;
        }

        /// <summary>Redeems a purchase a store made for a shop item, as the signed-in player: Flock asks the store and delivers the item. Safe to send again: one store purchase makes one Flock purchase, and a repeat returns what the first delivered. The wallet is also written into the cached player data.</summary>
        public async Task<PurchaseResult> RedeemStoreReceiptAsync(
            string shopItemId, StoreReceipt receipt,
            CancellationToken cancellationToken = default)
        {
            RequireNotEmpty(shopItemId, "Shop Item ID");
            Dictionary<string, string> ids = ReceiptIds(receipt);
            int? redeemer = SignInToActFor;
            if (!redeemer.HasValue)
                throw new FlockAuthException("No player is signed in") { Hint = FlockErrorHints.SignInFirst };

            // A store purchase makes one Flock purchase, so this one may be sent again (payment plan D-P3), unlike a wallet purchase.
            PurchaseResult result = await ExecuteAsync(async () =>
            {
                StoreReceiptRedemption request = new StoreReceiptRedemption
                {
                    ShopItemId = shopItemId,
                    Provider = receipt.Store,
                    Receipt = ids
                };
                return await FlockHttpClient.PostAsync<PurchaseResult>(
                    $"{Client.GetVersionedApiUrl()}/{FlockEndpoints.ShopValidateReceipt}", request, Client.GetBaseHeaders(), cancellationToken);
            }, "Redeem store receipt", cancellationToken, idempotent: true, actsForSignIn: redeemer, spendsOrGrants: true);

            RequirePurchaseNamed(result, "Leave the store's purchase unfinished and send the same receipt again: it cannot be paid for twice.");
            ApplyWalletToPlayerCache(result.Wallet);
            return result;
        }

        /// <summary>The value to attach to a store purchase for the signed-in player: Apple's appAccountToken (a UUID) and Google Play's obfuscated account id. The receipt check then refuses a purchase the store says is another player's. Stable per player, so it is read once per sign-in.</summary>
        public async Task<string> GetStoreAccountTokenAsync(CancellationToken cancellationToken = default)
        {
            int? reader = SignInToActFor;
            if (!reader.HasValue)
                throw new FlockAuthException("No player is signed in") { Hint = FlockErrorHints.SignInFirst };
            if (_accountTokenSignIn == reader && !string.IsNullOrEmpty(_accountToken))
                return _accountToken;

            StoreAccountTokenAnswer answer = await ExecuteAsync(async () =>
                await FlockHttpClient.GetAsync<StoreAccountTokenAnswer>(
                    $"{Client.GetVersionedApiUrl()}/{FlockEndpoints.ShopAccountToken}", Client.GetBaseHeaders(), cancellationToken),
                "Get store account token", cancellationToken, actsForSignIn: reader);
            if (answer == null || string.IsNullOrEmpty(answer.AccountToken))
                throw new FlockSerializationException("The server's answer had no account token") { Hint = FlockErrorHints.NotFlocksAnswer };

            // Kept only for the sign-in it was read for, so the next player never gets this one.
            if (SignInToActFor == reader)
            {
                _accountToken = answer.AccountToken;
                _accountTokenSignIn = reader;
            }
            return answer.AccountToken;
        }

        // The receipt's ids as the route takes them, each exactly as the store issued it.
        private Dictionary<string, string> ReceiptIds(StoreReceipt receipt)
        {
            if (receipt == null)
                throw new FlockValidationException("No store receipt was given") { Hint = "Pass the store's purchase as StoreReceipt.FromAppleAppStore or StoreReceipt.FromGooglePlay." };
            if (receipt.Store == PaymentProviders.AppleAppStore)
                return new Dictionary<string, string> { { "transaction_id", AsTheStoreIssuedIt(receipt.TransactionId, "App Store transaction id") } };
            return new Dictionary<string, string>
            {
                { "product_id", AsTheStoreIssuedIt(receipt.ProductId, "Google Play product id") },
                { "purchase_token", AsTheStoreIssuedIt(receipt.PurchaseToken, "Google Play purchase token") }
            };
        }

        // A store id with spaces around it is a bug where it was copied; trimming it here would hide that bug.
        private string AsTheStoreIssuedIt(string id, string name)
        {
            RequireNotEmpty(id, name, $"Pass the {name} the store gave for this purchase.");
            if (char.IsWhiteSpace(id[0]) || char.IsWhiteSpace(id[id.Length - 1]))
                throw new FlockValidationException($"The {name} starts or ends with a space") { Hint = $"Pass the {name} exactly as the store gave it; spaces around it come from wherever it was copied or stored." };
            return id;
        }

        // A 2xx that names no purchase is not Flock's answer about one: without this, PurchaseResult's default status would read it as paid.
        private static void RequirePurchaseNamed(PurchaseResult result, string fix)
        {
            if (result == null || string.IsNullOrEmpty(result.PurchaseId))
                throw new FlockSerializationException("The server's answer named no purchase, so whether it went through is unknown") { Hint = fix, OutcomeUnknown = true };
        }

        // The purchase never waits on its records: they are analytics, and their sending and retries must not hold up or fail
        // a purchase. A record goes to its purchaser or nowhere, since the transaction names the player signed in when it is sent.
        private async void RecordPurchaseInTheBackground(ShopItem shopItem, string shopItemId, string status, int? purchaser)
        {
            if (Client.Analytics == null || shopItem == null || purchaser != SignInToActFor)
                return;
            try
            {
                await Client.Analytics.RecordTransactionAsync(
                    new AnalyticsTransactionRequest
                    {
                        Amount = shopItem.Price,
                        CurrencyCode = shopItem.Currency,
                        ShopItemId = shopItemId,
                        TransactionType = nameof(TransactionType.Purchase),
                        Status = status
                    });
            }
            catch (Exception ex)
            {
                Client.Logger.LogWarning($"Could not record the purchase's {status} transaction: {ex.Message}");
            }
        }

        // The wallet a purchase or consume reports is the player's balance now. Without this the cached row keeps the old
        // one, and a read-modify-write would put it back over the purchase. No wallet means no currency moved: nothing to write.
        private void ApplyWalletToPlayerCache(PlayerData wallet)
        {
#if !FLOCK_NO_PLAYER
            if (wallet != null && !string.IsNullOrEmpty(wallet.Id))
                Client.Player?.ApplyServerPlayerData(wallet);
#endif
        }

        /// <summary>Consumes an owned inventory entry, granting its rewards. Returns the updated row plus what was granted; the wallet is also written into the cached player data. To use one of a shop item without reading the inventory, see <see cref="ConsumeOneAsync"/>.</summary>
        public Task<ConsumeResult> ConsumeAsync(
            string inventoryId, CancellationToken cancellationToken = default)
            => ConsumeEntryAsync(inventoryId, SignInToActFor, cancellationToken);

        /// <summary>Uses up one unused entry of a shop item the signed-in player holds, granting what it holds (a consumable's rewards; a standard item just becomes used). Reads the inventory to find one; an entry used meanwhile on another device is passed over for the next. Refused when the player holds none.</summary>
        public async Task<ConsumeResult> ConsumeOneAsync(string shopItemId, CancellationToken cancellationToken = default)
        {
            RequireNotEmpty(shopItemId, "Shop Item ID");
            // Taken before the read, so an entry read for one player is never used as the player who signed in next.
            int? consumer = SignInToActFor;
            if (!consumer.HasValue)
                throw new FlockAuthException("No player is signed in") { Hint = FlockErrorHints.SignInFirst };

            foreach (PlayerInventory entry in await ReadUnusedEntriesAsync(cancellationToken))
            {
                if (entry.ShopItemId != shopItemId || string.IsNullOrEmpty(entry.Id))
                    continue;
                try
                {
                    return await ConsumeEntryAsync(entry.Id, consumer, cancellationToken);
                }
                // Used by another call or device since the read: the next one is still the player's to use. Any other failure stops, so one call never uses two.
                catch (FlockException usedMeanwhile) when (usedMeanwhile.ErrorCode == FlockErrorCode.PlayerInventoryAlreadyUsed)
                {
                }
            }
            throw new FlockValidationException("The signed-in player holds no unused entry of that shop item")
            {
                Hint = "Offer to use it only while GetOwnedItemCountsAsync counts it; it may have been used on another device."
            };
        }

        private async Task<ConsumeResult> ConsumeEntryAsync(string inventoryId, int? consumer, CancellationToken cancellationToken)
        {
            RequireNotEmpty(inventoryId, "Inventory ID");

            // Consuming grants currency, so it carries the same double-spend risk as a purchase: an ambiguous failure surfaces rather than being re-sent. Only 408/429 (provably not processed) retry.
            ConsumeResult result = await ExecuteAsync(async () =>
            {
                // Route takes no request body — an empty object keeps the POST well-formed.
                return await FlockHttpClient.PostAsync<ConsumeResult>(
                    $"{Client.GetVersionedApiUrl()}/{FlockEndpoints.PlayerInventoryConsume(inventoryId)}", new { }, Client.GetBaseHeaders(), cancellationToken);
            }, "Consume inventory item", cancellationToken, idempotent: false, actsForSignIn: consumer, spendsOrGrants: true);
            // A 2xx that names no entry is not Flock's answer about one, so whether it was used is unknown.
            if (result?.Inventory == null || string.IsNullOrEmpty(result.Inventory.Id))
                throw new FlockSerializationException("The server's answer named no inventory entry, so whether it was used is unknown")
                {
                    Hint = "Read the player's inventory to see whether it was used before offering to use it again.",
                    OutcomeUnknown = true
                };
            ApplyWalletToPlayerCache(result.Wallet);
            return result;
        }

        public async Task<PaginatedResponse<PlayerInventory>> GetPlayerInventoryAsync(
            string playerId = null, int page = 1, int limit = 100,
            CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrEmpty(playerId))
                playerId = Client.CurrentPlayerId;
            RequireNotEmpty(playerId, "Player ID", FlockErrorHints.SignInFirst);

            // Inventory changes on every purchase — intentionally never cached (no in-memory dict / snapshot); always fresh. Same rule as bans.
            return await ExecuteAsync(async () =>
            {
                return await FlockHttpClient.GetAsync<PaginatedResponse<PlayerInventory>>(
                    $"{Client.GetVersionedApiUrl()}/{FlockEndpoints.PlayerInventoryByPlayer(playerId)}?page={page}&limit={limit}",
                    Client.GetBaseHeaders(), cancellationToken);
            }, "Get player inventory", cancellationToken);
        }

        /// <summary>How many unused entries of each shop item the signed-in player holds, by shop item id, from every page of the inventory; used and refunded entries do not count. The inventory is never cached, so each call reads it afresh: for a screen of items, read this once.</summary>
        public async Task<IReadOnlyDictionary<string, int>> GetOwnedItemCountsAsync(CancellationToken cancellationToken = default)
        {
            Dictionary<string, int> counts = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (PlayerInventory entry in await ReadUnusedEntriesAsync(cancellationToken))
                counts[entry.ShopItemId] = counts.TryGetValue(entry.ShopItemId, out int held) ? held + 1 : 1;
            return counts;
        }

        // The signed-in player's unused entries from every page of the inventory, in the server's order.
        private async Task<List<PlayerInventory>> ReadUnusedEntriesAsync(CancellationToken cancellationToken)
        {
            List<PlayerInventory> unused = new List<PlayerInventory>();
            int page = 1;
            while (true)
            {
                PaginatedResponse<PlayerInventory> entries = await GetPlayerInventoryAsync(page: page, limit: InventoryPageSize, cancellationToken: cancellationToken);
                if (entries?.Items == null || entries.Items.Length == 0)
                    return unused;
                foreach (PlayerInventory entry in entries.Items)
                    if (entry != null && !string.IsNullOrEmpty(entry.ShopItemId) && entry.Status == PlayerInventoryStatuses.Unused)
                        unused.Add(entry);
                if (page * InventoryPageSize >= entries.Total)
                    return unused;
                page++;
            }
        }

        /// <summary>Whether the signed-in player holds an unused entry of this shop item, such as a standard item bought and not used up. Reads the whole inventory.</summary>
        public async Task<bool> OwnsAsync(string shopItemId, CancellationToken cancellationToken = default)
        {
            RequireNotEmpty(shopItemId, "Shop Item ID");
            IReadOnlyDictionary<string, int> counts = await GetOwnedItemCountsAsync(cancellationToken);
            return counts.ContainsKey(shopItemId);
        }

        private const int InventoryPageSize = 100;
        private const string ReadBeforeBuyingAgain = "Read the player's inventory and wallet to see whether it went through before offering it again; a new PurchaseAsync call is a new purchase.";
    }
}
