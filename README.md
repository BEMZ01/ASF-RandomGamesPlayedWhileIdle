# Random Games Played While Idle Plugin for ASF

A plugin for [ArchiSteamFarm](https://github.com/JustArchiNET/ArchiSteamFarm) that randomly selects games from your library to play while idle.

## Features

- Automatically selects random games from your Steam library to play while idle
- Configurable game cycling interval (rotate games every X minutes)
- App ID blacklist to exclude specific games from being played
- Supports up to 32 games played concurrently (Steam limit)

## Installation

1. Download the latest release from the [Releases](../../releases) page
2. Extract `RandomGamesPlayedWhileIdle.dll` to your ASF's `plugins` folder

## Configuration

Add the following properties to your ASF global config (`ASF.json`) to customize the plugin behavior:

```json
{
  "RandomGamesPlayedWhileIdleCycleIntervalMinutes": 30,
  "RandomGamesPlayedWhileIdleBlacklist": [12345, 67890]
}
```

### Configuration Options

| Property | Type | Default | Description |
|----------|------|---------|-------------|
| `RandomGamesPlayedWhileIdleCycleIntervalMinutes` | `int` | `0` | Interval in minutes to rotate the random games. Set to `0` to disable cycling (games are only selected once on login). |
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
