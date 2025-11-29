# Random Games Played While Idle Plugin for ASF

A plugin for [ArchiSteamFarm](https://github.com/JustArchiNET/ArchiSteamFarm) that randomly selects games from your library to play while idle.

## Features

- Automatically selects random games from your Steam library to play while idle
- Configurable game cycling interval (rotate games every X minutes)
- Configurable maximum number of games to play concurrently
- App ID blacklist to exclude specific games from being played
- Per-bot configuration (each bot can have different settings)

## Installation

1. Download the latest release from the [Releases](../../releases) page
2. Extract `RandomGamesPlayedWhileIdle.dll` to your ASF's `plugins` folder

## Configuration

Add the following properties to your **bot config file** (e.g., `YourBot.json`) to customize the plugin behavior for each bot:

```json
{
  "RandomGamesPlayedWhileIdleCycleIntervalMinutes": 30,
  "RandomGamesPlayedWhileIdleMaxGamesPlayed": 32,
  "RandomGamesPlayedWhileIdleBlacklist": [12345, 67890]
}
```

### Configuration Options

| Property | Type | Default | Description |
|----------|------|---------|-------------|
| `RandomGamesPlayedWhileIdleCycleIntervalMinutes` | `int` | `0` | Interval in minutes to rotate the random games. Set to `0` to disable cycling (games are only selected once on login). |
| `RandomGamesPlayedWhileIdleMaxGamesPlayed` | `int` | `32` | Maximum number of games to play concurrently. Must be between 1 and 32 (Steam limit). |
| `RandomGamesPlayedWhileIdleBlacklist` | `uint[]` | `[]` | Array of App IDs to exclude from random selection. |

## Building

1. Clone the repository with submodules:
   ```bash
   git clone --recursive https://github.com/BEMZ01/ASF-RandomGamesPlayedWhileIdle.git
   ```

2. Build the project:
   ```bash
   dotnet build --configuration Release
   ```

3. The plugin DLL will be located at `RandomGamesPlayedWhileIdle/bin/Release/net8.0/RandomGamesPlayedWhileIdle.dll`

## License

This project is licensed under the Apache-2.0 License - see the [LICENSE.txt](LICENSE.txt) file for details.
