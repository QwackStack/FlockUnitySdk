# Player Data & Game Commands

[← Back to README](../README.md)

A player's saved data lives in **records**, one per player template set up in the Flock dashboard (progress, wallet,
achievements...). The client reads records directly and changes them through **game commands**, which the server checks
against the template.

## Reading with the generated methods

The easiest read is the method codegen writes for each template: run **Sync Schemas** on the **Code Generation** tab of
**Flock > Settings**, and a template named `PlayerProgress` gets `GetPlayerProgressAsync()`, returning a
`PlayerProgressTemplate` with typed properties. These names exist only after a sync, and yours follow your dashboard.
The method returns **null when the player has no record** of that template.

```csharp
using System.Threading.Tasks;
using Flock;
using Flock.Generated.Templates;
using UnityEngine;

public static class ProgressRead
{
    public static async Task ShowAsync()
    {
        PlayerProgressTemplate progress = await FlockClient.Instance.Player.GetPlayerProgressAsync();
        if (progress == null)
            Debug.Log("No progress record yet.");
        else
            Debug.Log($"Level {progress.Level}, {progress.Xp} XP");
    }
}
```

See [Codegen](codegen.md) for saving a generated record with `UpdateAsync`.

## Reading records directly

```csharp
using System.Threading.Tasks;
using Flock;
using Flock.Models;
using UnityEngine;

public static class RecordRead
{
    public static async Task ShowAsync(string playerDataId)
    {
        // One record, by its id.
        PlayerData record = await FlockClient.Instance.Player.GetDataByIdAsync(playerDataId);

        // A page of records; pass playerId: to read one player's.
        PaginatedResponse<PlayerData> page = await FlockClient.Instance.Player.GetAllDataAsync(
            playerId: FlockClient.Instance.CurrentPlayerId, page: 1, limit: 10);
        Debug.Log($"{page.Items.Length} of {page.Total} records");

        // A field read without codegen: JSON whole numbers arrive as long and decimals as double,
        // so read through GetValue<T>() rather than casting Value ((int)field.Value throws on a long).
        DataField scoreField = record.Data.Find(field => field.FieldName == "score");
        int score = scoreField != null ? scoreField.GetValue<int>() : 0;
        Debug.Log($"Score: {score}");
    }
}
```

## Changing records: game commands

Each command runs on the server and returns the record as it is afterwards.

```csharp
using System.Collections.Generic;
using System.Threading.Tasks;
using Flock;
using Flock.Models;

public static class RecordWrite
{
    // Several fields of one record at once.
    public static Task<PlayerData> SaveAsync(string playerDataId, int score, string playerClass)
    {
        List<DataField> fields = new List<DataField>
        {
            new DataField { FieldName = "score", Value = score },
            new DataField { FieldName = "class", Value = playerClass }
        };
        return FlockClient.Instance.Commands.UpdatePlayerDataAsync(playerDataId, fields);
    }

    // One field.
    public static Task<PlayerData> SaveScoreAsync(string playerDataId, int score)
    {
        return FlockClient.Instance.Commands.UpdatePlayerDataFieldAsync(playerDataId, "score", score);
    }
}
```

The field name is a **top-level** name, not a path: the server stores `"Level.Stage"` under that exact name, and the
template then refuses it (HTTP 422). To change a nested value, write the whole object: `UpdatePlayerDataFieldAsync(id,
"Level", theWholeLevelObject)`.

## Funds and achievements

Neither needs a record id: the SDK finds the player's **wallet** (the record of the template tagged `currency`) and
**achievements** record (the template tagged `achievement`).

```csharp
using System.Threading.Tasks;
using Flock;
using Flock.Exceptions;
using Flock.Generated.Achievements;
using Flock.Generated.Shops;
using Flock.Models;
using UnityEngine;

public static class Rewards
{
    public static async Task GiveDailyGoldAsync()
    {
        try
        {
            // The generated FlockFundId names your shop currencies; the string form is AddGameFundsAsync("Gold", 500).
            await FlockClient.Instance.Commands.AddGameFundsAsync(FlockFundId.Gold, 500);
        }
        catch (FlockException ex) when (ex.OutcomeUnknown)
        {
            // No answer said whether the gold was added: read the wallet before giving it again.
            FlockWallet wallet = await FlockClient.Instance.Player.GetWalletAsync();
            Debug.Log($"Gold now: {wallet.BalanceOf("Gold")}");
        }
    }

    // The generated FlockAchievementId names your achievements; the string form also exists.
    public static Task<PlayerData> UnlockFirstWinAsync()
    {
        return FlockClient.Instance.Commands.UnlockAchievementAsync(FlockAchievementId.FirstWin);
    }
}
```

`FlockFundId.Gold` and `FlockAchievementId.FirstWin` stand for a currency whose code is `Gold` and an achievement field
named `first_win`: [Codegen](codegen.md) says how the members are named.

`AddGameFundsAsync` retries only failures the server provably did not process (HTTP 408 and 429). A timeout or a server
error may already have added the funds, so it throws with `OutcomeUnknown` set rather than risk adding them twice. A
shop purchase is different: it carries a key and is sent again (see the [Shop guide](shop.md)).
`Player.GetWalletAsync()` reads the wallet's balances by currency (`FlockWallet`; see the Shop guide).

## Bans

```csharp
using System.Collections.Generic;
using System.Threading.Tasks;
using Flock;
using Flock.Models;
using UnityEngine;

public static class BanCheck
{
    public static async Task ShowAsync()
    {
        // Never null: a player who is not banned gets an empty record, so check IsBanned.
        PlayerBan ban = await FlockClient.Instance.Player.GetBanAsync(FlockClient.Instance.CurrentPlayerId);
        if (!ban.IsBanned || ban.Data == null)
            return;
        foreach (KeyValuePair<string, FeatureBan> entry in ban.Data)
            Debug.Log($"Banned from {entry.Key}: {entry.Value.Reason}");
        // A ban can cover one feature and not others: gate each action with IsBannedFrom.
        if (ban.IsBannedFrom("multiplayer"))
            Debug.Log("Banned from multiplayer: hide the online modes.");
    }
}
```

Bans are never cached: each call asks the server.

See also: [Codegen](codegen.md) for typed template accessors and `UpdateAsync` on generated classes, and [Shop](shop.md) for the same money-safety retry contract on purchases.
