using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;

namespace Flock.Models
{
    /// <summary>A player's balances by currency code, read from their wallet (the player data of the template tagged "currency"). Game currencies are spent through <c>Shop.PurchaseAsync</c>; real currencies are never spent from the wallet.</summary>
    public sealed class FlockWallet
    {
        /// <summary>Balances of the game's own currencies, by code.</summary>
        public IReadOnlyDictionary<string, long> GameCurrencies { get; }

        /// <summary>Balances of real currencies, by code.</summary>
        public IReadOnlyDictionary<string, long> RealCurrencies { get; }

        private FlockWallet(Dictionary<string, long> gameCurrencies, Dictionary<string, long> realCurrencies)
        {
            GameCurrencies = gameCurrencies;
            RealCurrencies = realCurrencies;
        }

        /// <summary>The balance of a currency, or 0 when the wallet does not hold it (<see cref="Holds"/> tells the two apart).</summary>
        public long BalanceOf(string currencyCode)
        {
            if (string.IsNullOrEmpty(currencyCode))
                return 0;
            if (GameCurrencies.TryGetValue(currencyCode, out long game))
                return game;
            return RealCurrencies.TryGetValue(currencyCode, out long real) ? real : 0;
        }

        /// <summary>Whether the wallet holds a balance in this currency at all.</summary>
        public bool Holds(string currencyCode)
            => !string.IsNullOrEmpty(currencyCode) && (GameCurrencies.ContainsKey(currencyCode) || RealCurrencies.ContainsKey(currencyCode));

        /// <summary>Whether <c>Shop.PurchaseAsync</c> can pay for this item from the wallet: a game-currency item whose price the wallet covers. A real-money item is never bought from the wallet, so it reads false; buy it through the store.</summary>
        public bool CanAfford(ShopItem item)
            => item != null && !item.IsRealMoney && GameCurrencies.TryGetValue(item.Currency ?? string.Empty, out long balance) && balance >= item.Price;

        /// <summary>Reads a wallet row (a purchase's <c>Wallet</c>, or the player's currency data): a currency object per category, the shape the server answers with.</summary>
        public static FlockWallet From(PlayerData wallet)
        {
            JObject flat = wallet?.Data != null ? wallet.Data.ToFlatObject() : new JObject();
            return new FlockWallet(Category(flat["game_currencies"]), Category(flat["real_currencies"]));
        }

        // The server brings an older wallet row to this shape before any read answers with it.
        private static Dictionary<string, long> Category(JToken category)
        {
            Dictionary<string, long> balances = new Dictionary<string, long>(StringComparer.Ordinal);
            if (category is JObject codes)
                foreach (JProperty code in codes.Properties())
                    if (code.Value.Type == JTokenType.Integer || code.Value.Type == JTokenType.Float)
                        balances[code.Name] = code.Value.Value<long>();
            return balances;
        }
    }
}
