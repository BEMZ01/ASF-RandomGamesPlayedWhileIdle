# Random Games Played While Idle Plugin for ASF

An [ArchiSteamFarm](https://github.com/JustArchiNET/ArchiSteamFarm) plugin that automatically rotates your idle games on a configurable timer, cycling through your entire library in random order.

## How it works

1. When a bot logs on, the plugin fetches the full games library for that Steam account.
2. Any blacklisted app IDs are removed, then the remaining games are shuffled into a queue.
3. The first batch is applied immediately; a timer then applies the next batch every N minutes.
4. Once all games have been cycled through, the library is re-fetched and the queue is rebuilt.

All activity is logged per-bot and is visible directly in the ASF UI.

## Configuration

Add any of the following properties to the bot's JSON config file (e.g. `config/MyBot.json`).  
All properties are optional — omit any you don't need and the defaults will be used.

```json
{
  "SteamLogin": "MyLogin",
  "SteamPassword": "MyPassword",
  "RandomGamesPlayedWhileIdleCycleIntervalMinutes": 30,
  "RandomGamesPlayedWhileIdleMaxGamesPlayed": 1,
  "RandomGamesPlayedWhileIdleBlacklist": []
}
```

| Property | Type | Default | Description |
|---|---|---|---|
| `RandomGamesPlayedWhileIdleCycleIntervalMinutes` | `number` | `30` | How often (in minutes) to rotate to the next batch. Must be greater than 0. |
| `RandomGamesPlayedWhileIdleMaxGamesPlayed` | `number` | `32` | How many games to play per batch (1–32). Set to `1` to play a single game at a time. |
| `RandomGamesPlayedWhileIdleBlacklist` | `array of numbers` | `[]` | App IDs to permanently exclude from rotation (e.g. `[440, 730]`). |
