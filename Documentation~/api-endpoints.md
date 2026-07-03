# Flock SDK — Backend Wire Reference (maintainer-only)

> **Internal SDK-development reference. Not shipped to consumers.**
> This file lives in `Documentation~/`, which Unity ignores (`~`-suffixed folders
> are never imported via git UPM, and `FlockPackageBuilder` excludes them from the
> `.unitypackage`). Consumers call the SDK through `FlockClient.Instance.*`; they
> never need raw REST paths or header names, so keep these out of `README.md` and
> the in-editor guide. When you change anything here, update the **Unreal SDK** to
> match (Unity ↔ Unreal parity).

## Headers

Every API request includes these headers:

| Header | Source | Description |
|--------|--------|-------------|
| `X-Flock-API-Key` | `FlockInitConfig.ApiKey` | Required. Identifies the game. |
| `X-Game-Version-ID` | `FlockInitConfig.GameVersionId` | Resolved from `GameVersion` (name) during `FlockClient.CreateAsync`. |
| `Authorization` | Bearer token from login | Added after authentication. |

## Endpoints

| Service | Endpoint | Auth |
|---------|----------|------|
| Email / Facebook / Discord Login | `POST /v1/player/login` | API Key |
| Device Login | `POST /v1/player/login/device` | API Key |
| Google Login | `POST /v1/player/login/google` | API Key |
| Apple Login | `POST /v1/player/login/apple` | API Key |
| Steam Login | `POST /v1/player/login/steam` | API Key |
| Email Register | `POST /v1/player/register` | API Key |
| Device Register | `POST /v1/player/register/device` | API Key |
| Google Register | `POST /v1/player/register/google` | API Key |
| Apple Register | `POST /v1/player/register/apple` | API Key |
| Steam Register | `POST /v1/player/register/steam` | API Key |
| Refresh Token | `POST /v1/player/token/refresh` | API Key |
| Game Configs | `GET /v1/game_patch` | API Key |
| Config by ID | `GET /v1/game_patch/{id}` | API Key |
| Configs by Schema | `GET /v1/game_patch/config/{id}` | API Key |
| Config Schemas | `GET /v1/game_config` | API Key |
| Schemas by Version | `GET /v1/game_config/version` | API Key |
| Schema by ID | `GET /v1/game_config/{id}` | API Key |
| Schema by Name | `GET /v1/game_config/by-name/{name}` | API Key |
| Schema Configs | `GET /v1/game_config/{id}/patches` | API Key |
| Player Feature Config | `GET /v1/game_config/player/{player_id}/features` | API Key |
| Game Info | `GET /v1/game` | API Key |
| Game Version | `GET /v1/game_version` | API Key |
| Game Version by Name | `GET /v1/game_version/by-name/{name}` | API Key |
| Player Data | `GET /v1/player_data` | API Key |
| Player Data by ID | `GET /v1/player_data/{id}` | API Key |
| Player Templates | `GET /v1/player_template` | API Key |
| Player Template by ID | `GET /v1/player_template/{id}` | API Key |
| Player Template by Name | `GET /v1/player_template/by-name/{name}` | API Key |
| Template Player Data | `GET /v1/player_template/{id}/player-data` | API Key |
| Game Configs by Tag | `GET /v1/game_config?tag=` | API Key |
| Game Configs by Version/Tag | `GET /v1/game_config/version?tag=` | API Key |
| Update Player Data | `POST /v1/game_command/update_player_data` | API Key |
| Update Player Data Field | `POST /v1/game_command/update_player_data_key` | API Key |
| Add Game Funds | `POST /v1/game_command/add_game_funds` | API Key |
| Unlock Achievement | `POST /v1/game_command/unlock_achievement` | API Key |
| List Shops | `GET /v1/shop` | API Key |
| Get Shop | `GET /v1/shop/{shop_id}` | API Key |
| Get Shop by Name | `GET /v1/shop/by-name/{name}` | API Key |
| Shop Transaction | `POST /v1/shop/transaction` | API Key |
| Get Shop Item | `GET /v1/shop_item/{shop_item_id}` | API Key |
| Shop Items by Shop | `GET /v1/shop_item/shop/{shop_id}` | API Key |
| Player Inventory | `GET /v1/player_inventory/player/{player_id}` | API Key |
| Player Ban | `GET /v1/player-ban` | API Key |
| List Assets | `GET /v1/asset` | API Key |
| Get Asset | `GET /v1/asset/{asset_id}` | API Key |
| Start Session | `POST /v1/analytics/sessions` | Bearer |
| End Session | `PATCH /v1/analytics/sessions/{session_id}` | Bearer |
| Track Event | `POST /v1/analytics/events/single` | Bearer |
| Track Events Batch | `POST /v1/analytics/events` | Bearer |
| Record Transaction | `POST /v1/analytics/transactions` | Bearer |
| Log Events Batch | `POST /v1/log_event` | API Key |
| Log Event | `POST /v1/log_event/single` | API Key |

Reserved event names on the events endpoints (no dedicated route): `app_termination` (next-launch
dirty-exit report, category `session`, dedupe on `previous_session_id`), `sdk_heartbeat` (category
`system`).
