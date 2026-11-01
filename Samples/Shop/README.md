# Flock Shop Sample

One screen, one component (**FlockShopSample**): every item in the game's shops, ready to buy.

- **Prices**: an item paid with a game currency shows its Flock price; a real-money item shows the store's own price on a phone,
  in the player's currency and with their taxes, and its Flock price through a web checkout.
- **What the player owns**: each item shows how many unused entries the player holds, and a one-time item already owned reads
  **Owned**.
- **What the player can afford**: an item the wallet cannot pay for reads **Not enough** and its currency, and cannot be tapped.
- **Every outcome in plain words**: bought, not enough currency, cancelled, payment declined, a checkout left unpaid, waiting
  for the payment, already owned, the store or Flock not answering. When Flock does not answer a wallet purchase, the sample reads the wallet and
  inventory again to see whether it went through.
- **Restore purchases** (on a phone) asks the store for the one-time items this store account owns and has Flock deliver them.
- **Check store setup** says, without buying anything, which real-money items the store has no product for, which have no
  product id for this store, and whether Flock's server can ask the store about purchases with the game's keys. Through a web
  checkout it says how to check the gateway: buy an item with the gateway's test card.

Real-money items are sold through FlockStore: on Android and iOS through the store, which needs Unity IAP 5, and on every other
build target through a web checkout with the game's gateway (Stripe, Tap or Moyasar), whose payment page opens in the browser.

## Setup

1. **Configure Flock**: open **Flock > Settings**, fill in your API URL, API Key, Game ID and Game Version, and click **Add
   Flock Bootstrap to Scene**.
2. **Make a shop** in the Flock dashboard, under Shops, and give the player template that holds the wallet the tag
   `currency`.
3. **For real-money items on a phone**, install Unity IAP 5 (`com.unity.purchasing` 5.x, Package Manager), create each item's
   product in Google Play Console or App Store Connect, and enter its product id on the shop item in the Flock dashboard. **On
   other build targets**, connect Stripe, Tap or Moyasar under Monetization > Payments in the Flock dashboard.
4. **Add the sample**: create an empty GameObject, add **FlockShopSample**, and press Play. The build target picks the way to
   pay; to try Unity IAP's fake store in the editor on any build target, enter `google_play` or `apple_app_store` in the
   component's **Payment Provider**.

Tap **Log in (device)**, then **Open the shop**.
