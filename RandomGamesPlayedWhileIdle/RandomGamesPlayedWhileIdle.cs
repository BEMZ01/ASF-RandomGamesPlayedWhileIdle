using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Composition;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using ArchiSteamFarm.Core;
using ArchiSteamFarm.Plugins.Interfaces;
using ArchiSteamFarm.Steam;
using ArchiSteamFarm.Steam.Integration;
using SteamKit2;

namespace RandomGamesPlayedWhileIdle {
	[Export(typeof(IPlugin))]
	public sealed class RandomGamesPlayedWhileIdlePlugin : IBotConnection, IBotModules {
		private const int AbsoluteMaxGamesPlayed = 32;
		private const double DefaultCycleIntervalMinutes = 30;
		private const int DefaultMaxGamesPlayed = AbsoluteMaxGamesPlayed;

		// Config keys used in each bot's JSON config file.
		private const string CycleIntervalConfigKey = "RandomGamesPlayedWhileIdleCycleIntervalMinutes";
		private const string MaxGamesPlayedConfigKey = "RandomGamesPlayedWhileIdleMaxGamesPlayed";
		private const string BlacklistConfigKey = "RandomGamesPlayedWhileIdleBlacklist";

		// Cached reflection members – looked up once per process lifetime.
		private static readonly PropertyInfo? GamesPlayedWhileIdleProperty =
			typeof(Bot).Assembly
				.GetType("ArchiSteamFarm.Steam.Storage.BotConfig")
				?.GetProperty("GamesPlayedWhileIdle");

		private static readonly MethodInfo? ResetGamesPlayedMethod =
			typeof(Bot).GetMethod("ResetGamesPlayed", BindingFlags.NonPublic | BindingFlags.Instance);

		// Per-bot settings read from the bot config file.
		private readonly ConcurrentDictionary<Bot, PluginBotConfig> BotConfigs = new();
		private readonly ConcurrentDictionary<Bot, BotState> BotStates = new();

		public string Name => nameof(RandomGamesPlayedWhileIdle);
		public Version Version => typeof(RandomGamesPlayedWhileIdlePlugin).Assembly.GetName().Version!;

		public Task OnLoaded() {
			ASF.ArchiLogger.LogGenericInfo($"[{Name}] Plugin loaded (v{Version}). Configurable per-bot via \"{CycleIntervalConfigKey}\", \"{MaxGamesPlayedConfigKey}\", \"{BlacklistConfigKey}\" in each bot config.");

			return Task.CompletedTask;
		}

		/// <summary>
		///     Called by ASF right after the bot config is initialised. Reads the three optional
		///     plugin properties from the bot's JSON config file.
		/// </summary>
		public Task OnBotInitModules(Bot bot, IReadOnlyDictionary<string, JsonElement>? additionalConfigProperties = null) {
			ArgumentNullException.ThrowIfNull(bot);

			double cycleIntervalMinutes = DefaultCycleIntervalMinutes;
			int maxGamesPlayed = DefaultMaxGamesPlayed;
			HashSet<uint> blacklist = [];

			if (additionalConfigProperties != null) {
				// --- CycleIntervalMinutes ---
				if (additionalConfigProperties.TryGetValue(CycleIntervalConfigKey, out JsonElement intervalElem) &&
					intervalElem.ValueKind == JsonValueKind.Number &&
					intervalElem.TryGetDouble(out double parsedInterval) &&
					parsedInterval > 0) {
					cycleIntervalMinutes = parsedInterval;
				}

				// --- MaxGamesPlayed ---
				if (additionalConfigProperties.TryGetValue(MaxGamesPlayedConfigKey, out JsonElement maxElem) &&
					maxElem.ValueKind == JsonValueKind.Number &&
					maxElem.TryGetInt32(out int parsedMax) &&
					parsedMax > 0) {
					maxGamesPlayed = Math.Min(parsedMax, AbsoluteMaxGamesPlayed);
				}

				// --- Blacklist ---
				if (additionalConfigProperties.TryGetValue(BlacklistConfigKey, out JsonElement blacklistElem) &&
					blacklistElem.ValueKind == JsonValueKind.Array) {
					foreach (JsonElement entry in blacklistElem.EnumerateArray()) {
						if (entry.ValueKind == JsonValueKind.Number && entry.TryGetUInt32(out uint appID)) {
							blacklist.Add(appID);
						}
					}
				}
			}

			PluginBotConfig config = new(cycleIntervalMinutes, maxGamesPlayed, blacklist);
			BotConfigs[bot] = config;

			bot.ArchiLogger.LogGenericInfo($"[{Name}] Config loaded – interval: {cycleIntervalMinutes} min, max games: {maxGamesPlayed}, blacklisted: {blacklist.Count}.");

			return Task.CompletedTask;
		}

		public Task OnBotDisconnected(Bot bot, EResult reason) {
			ArgumentNullException.ThrowIfNull(bot);

			if (BotStates.TryRemove(bot, out BotState? state)) {
				state.Dispose();
				bot.ArchiLogger.LogGenericInfo($"[{Name}] Bot disconnected (reason: {reason}), rotation timer stopped.");
			}

			return Task.CompletedTask;
		}

		public async Task OnBotLoggedOn(Bot bot) {
			ArgumentNullException.ThrowIfNull(bot);

			if (BotStates.TryRemove(bot, out BotState? existingState)) {
				existingState.Dispose();
				bot.ArchiLogger.LogGenericInfo($"[{Name}] Re-initialising rotation after re-login.");
			}

			bot.ArchiLogger.LogGenericInfo($"[{Name}] Bot logged on, fetching games library...");

			try {
				List<uint>? allGames = await FetchGamesAsync(bot).ConfigureAwait(false);

				if (allGames == null || allGames.Count == 0) {
					bot.ArchiLogger.LogGenericWarning($"[{Name}] No games found in library. Plugin will not rotate games.");

					return;
				}

				PluginBotConfig botConfig = BotConfigs.GetValueOrDefault(bot, PluginBotConfig.Default);

				// Apply blacklist.
				if (botConfig.Blacklist.Count > 0) {
					int before = allGames.Count;
					allGames.RemoveAll(id => botConfig.Blacklist.Contains(id));
					bot.ArchiLogger.LogGenericInfo($"[{Name}] Blacklist removed {before - allGames.Count} game(s). {allGames.Count} remain.");
				}

				if (allGames.Count == 0) {
					bot.ArchiLogger.LogGenericWarning($"[{Name}] All games were blacklisted. Plugin will not rotate games.");

					return;
				}

				bot.ArchiLogger.LogGenericInfo($"[{Name}] Found {allGames.Count} game(s) in library. Building rotation queue...");

				TimeSpan rotationInterval = TimeSpan.FromMinutes(botConfig.CycleIntervalMinutes);
				BotState state = new(allGames, botConfig.MaxGamesPlayed);
				BotStates[bot] = state;

				ApplyNextBatch(bot, state);

				state.RotationTimer = new Timer(
					_ => { _ = RotateAsync(bot, state); },
					null,
					rotationInterval,
					rotationInterval
				);

				bot.ArchiLogger.LogGenericInfo($"[{Name}] Rotation timer started. Next rotation in {botConfig.CycleIntervalMinutes} minutes.");
			} catch (Exception e) {
				bot.ArchiLogger.LogGenericException(e);
			}
		}

		private async Task RotateAsync(Bot bot, BotState state) {
			// Bail out if this state has already been superseded (re-login / disconnect).
			if (!BotStates.TryGetValue(bot, out BotState? currentState) || !ReferenceEquals(currentState, state)) {
				return;
			}

			bot.ArchiLogger.LogGenericInfo($"[{Name}] Rotation timer fired.");

			try {
				await state.RotationLock.WaitAsync().ConfigureAwait(false);

				try {
					if (state.Queue.Count == 0) {
						bot.ArchiLogger.LogGenericInfo($"[{Name}] Queue is empty, re-fetching games library...");

						List<uint>? freshGames = await FetchGamesAsync(bot).ConfigureAwait(false);

						if (freshGames == null || freshGames.Count == 0) {
							bot.ArchiLogger.LogGenericWarning($"[{Name}] Re-fetch returned no games. Skipping rotation.");

							return;
						}

						// Re-apply blacklist when rebuilding.
						PluginBotConfig botConfig = BotConfigs.GetValueOrDefault(bot, PluginBotConfig.Default);

						if (botConfig.Blacklist.Count > 0) {
							freshGames.RemoveAll(id => botConfig.Blacklist.Contains(id));
						}

						if (freshGames.Count == 0) {
							bot.ArchiLogger.LogGenericWarning($"[{Name}] Re-fetch returned no games after blacklist filter. Skipping rotation.");

							return;
						}

						bot.ArchiLogger.LogGenericInfo($"[{Name}] Re-fetched {freshGames.Count} game(s). Rebuilding queue.");
						state.RebuildQueue(freshGames);
					}

					ApplyNextBatch(bot, state);
				} finally {
					state.RotationLock.Release();
				}
			} catch (Exception e) {
				bot.ArchiLogger.LogGenericException(e);
			}
		}

		private void ApplyNextBatch(Bot bot, BotState state) {
			List<uint> batch = new(state.MaxGamesPlayed);

			while (batch.Count < state.MaxGamesPlayed && state.Queue.Count > 0) {
				batch.Add(state.Queue.Dequeue());
			}

			if (batch.Count == 0) {
				bot.ArchiLogger.LogGenericWarning($"[{Name}] Batch is empty, nothing to apply.");

				return;
			}

			ImmutableList<uint> gamesList = batch.ToImmutableList();

			if (GamesPlayedWhileIdleProperty != null) {
				GamesPlayedWhileIdleProperty.SetValue(bot.BotConfig, gamesList);
			} else {
				// Fallback: use reflection on the runtime type (handles obfuscated assemblies).
				bot.BotConfig.GetType().GetProperty("GamesPlayedWhileIdle")?.SetValue(bot.BotConfig, gamesList);
			}

			bot.ArchiLogger.LogGenericInfo($"[{Name}] Applying {batch.Count} game(s): [{string.Join(", ", batch)}]. Queue remaining: {state.Queue.Count}.");

			if (ResetGamesPlayedMethod != null) {
				if (ResetGamesPlayedMethod.Invoke(bot, null) is Task task) {
					task.ContinueWith(
						// t.Exception is guaranteed non-null inside OnlyOnFaulted continuations.
						t => bot.ArchiLogger.LogGenericException(t.Exception!.InnerException ?? t.Exception!),
						CancellationToken.None,
						TaskContinuationOptions.OnlyOnFaulted,
						TaskScheduler.Default
					);
				}
			} else {
				bot.ArchiLogger.LogGenericWarning($"[{Name}] ResetGamesPlayed not found via reflection; game list updated but immediate apply skipped.");
			}
		}

		private static async Task<List<uint>?> FetchGamesAsync(Bot bot) {
			Dictionary<uint, string>? ownedGames = await bot.ArchiHandler.GetOwnedGames(bot.SteamID).ConfigureAwait(false);

			if (ownedGames == null || ownedGames.Count == 0) {
				return null;
			}

			return ownedGames.Keys.ToList();
		}

		private static Queue<uint> BuildShuffledQueue(List<uint> games) {
			List<uint> copy = new(games);

			// CA5394: Random.Shared is intentionally used here – game order is not a security concern.
#pragma warning disable CA5394
			for (int i = copy.Count - 1; i > 0; i--) {
				int j = Random.Shared.Next(i + 1);
				(copy[i], copy[j]) = (copy[j], copy[i]);
			}
#pragma warning restore CA5394

			return new Queue<uint>(copy);
		}

		/// <summary>Per-bot plugin settings parsed from the bot JSON config.</summary>
		private sealed record PluginBotConfig(double CycleIntervalMinutes, int MaxGamesPlayed, HashSet<uint> Blacklist) {
			public static readonly PluginBotConfig Default = new(DefaultCycleIntervalMinutes, DefaultMaxGamesPlayed, []);
		}

		private sealed class BotState : IDisposable {
			public int MaxGamesPlayed { get; }
			public Queue<uint> Queue { get; private set; }
			public Timer? RotationTimer { get; set; }

			// Ensures only one rotation runs at a time even if a timer fires while a re-fetch is in progress.
			public SemaphoreSlim RotationLock { get; } = new(1, 1);

			public BotState(List<uint> allGames, int maxGamesPlayed) {
				MaxGamesPlayed = maxGamesPlayed;
				Queue = BuildShuffledQueue(allGames);
			}

			public void RebuildQueue(List<uint> freshGames) {
				Queue = BuildShuffledQueue(freshGames);
			}

			public void Dispose() {
				RotationTimer?.Dispose();
				RotationLock.Dispose();
			}
		}
	}
}

