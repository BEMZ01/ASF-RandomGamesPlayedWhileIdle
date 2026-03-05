# Random Games Played While Idle Plugin for ASF

An [ArchiSteamFarm](https://github.com/JustArchiNET/ArchiSteamFarm) plugin that automatically rotates your idle games every 30 minutes (configurable), cycling through your entire library in random order.

## How it works

1. When a bot logs on, the plugin fetches the full games library for that Steam account.
2. The library is shuffled into a queue. The first batch of up to 32 games is applied immediately.
3. A timer fires every **N** minutes (default: 30) and applies the next batch of 32 games.
4. Once all games have been cycled through, the library is re-fetched and the queue is rebuilt.

All activity is logged per-bot and is visible directly in the ASF UI.

## Configuration

The rotation interval can be configured **per-bot** by adding an extra property to the bot's JSON config file (e.g. `config/MyBot.json`):

```json
{
  "SteamLogin": "MyLogin",
  "SteamPassword": "MyPassword",
  "RandomGamesPlayedWhileIdle_RotationIntervalMinutes": 15
}
```

| Property | Type | Default | Description |
|---|---|---|---|
| `RandomGamesPlayedWhileIdle_RotationIntervalMinutes` | `number` | `30` | How often (in minutes) to rotate to the next batch of 32 games. Must be greater than 0. |

If the property is absent or invalid the default of **30 minutes** is used.
