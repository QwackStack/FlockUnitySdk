# Flock Shop Sample

One screen, one component (**FlockShopSample**): every item in the game's shops, ready to buy.

- **Prices**: an item paid with a game currency shows its Flock price; a real-money item shows the store's own price, in the
  player's currency and with their taxes.
- **What the player owns**: each item shows how many unused entries the player holds, and a one-time item already owned reads
  **Owned**.
- **What the player can afford**: an item the wallet cannot pay for reads **Not enough** and its currency, and cannot be tapped.
- **Every outcome in plain words**: bought, not enough currency, cancelled, payment declined, waiting for the payment, already
  owned, the store or Flock not answering. When Flock does not answer a wallet purchase, the sample reads the wallet and
  inventory again to see whether it went through.
- **Restore purchases** asks the store for the one-time items this store account owns and has Flock deliver them.
- **Check store setup** says, without buying anything, which real-money items the store has no product for, which have no
  product id for this store, and whether Flock's server can ask the store about purchases with the game's keys.

Real-money items need Unity IAP 5. Without it the sample still sells the items paid with game currencies, and real-money items
read **Needs Unity IAP 5**.

## Setup

1. **Configure Flock**: open **Flock > Settings**, fill in your API URL, API Key, Game ID and Game Version, and click **Add
   Flock Bootstrap to Scene**.
2. **Make a shop** in the Flock dashboard, under Shops, and give the player template that holds the wallet the tag
   `currency`.
3. **For real-money items**, install Unity IAP 5 (`com.unity.purchasing` 5.x, Package Manager), create each item's product in
   Google Play Console or App Store Connect, and enter its product id on the shop item in the Flock dashboard.
4. **Add the sample**: create an empty GameObject, add **FlockShopSample**, and press Play. In the editor Unity IAP's fake
   store answers for the store named in the component's **Editor Store** (Google Play by default); on a phone the store is
   found by itself.

Tap **Log in (device)**, then **Open the shop**.
