using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Composition;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using AngleSharp.Dom;
using ArchiSteamFarm.Core;
using ArchiSteamFarm.Plugins.Interfaces;
using ArchiSteamFarm.Steam;
using ArchiSteamFarm.Steam.Integration;
using ArchiSteamFarm.Web.Responses;
using SteamKit2;

namespace RandomGamesPlayedWhileIdle {
	[Export(typeof(IPlugin))]
	public sealed partial class RandomGamesPlayedWhileIdlePlugin : IBotConnection, IBotModules {
		private const int MaxGamesPlayedConcurrently = 32;
		private const double DefaultRotationIntervalMinutes = 30;

		// Config key used in the bot JSON config file:
		//   "RandomGamesPlayedWhileIdle_RotationIntervalMinutes": <number>
		private const string RotationIntervalConfigKey = $"{nameof(RandomGamesPlayedWhileIdle)}_RotationIntervalMinutes";

		// Cached reflection members – looked up once per process lifetime.
		private static readonly PropertyInfo? GamesPlayedWhileIdleProperty =
			typeof(Bot).Assembly
				.GetType("ArchiSteamFarm.Steam.Storage.BotConfig")
				?.GetProperty("GamesPlayedWhileIdle");

		private static readonly MethodInfo? ResetGamesPlayedMethod =
			typeof(Bot).GetMethod("ResetGamesPlayed", BindingFlags.NonPublic | BindingFlags.Instance);

		// Per-bot rotation intervals read from the bot config file.
		private readonly ConcurrentDictionary<Bot, double> BotRotationIntervals = new();
		private readonly ConcurrentDictionary<Bot, BotState> BotStates = new();

		public string Name => nameof(RandomGamesPlayedWhileIdle);
		public Version Version => typeof(RandomGamesPlayedWhileIdlePlugin).Assembly.GetName().Version!;

		public Task OnLoaded() {
			ASF.ArchiLogger.LogGenericInfo($"[{Name}] Plugin loaded (v{Version}). Default rotation interval: {DefaultRotationIntervalMinutes} minutes (configurable per-bot via \"{RotationIntervalConfigKey}\" in the bot config).");

			return Task.CompletedTask;
		}

		/// <summary>
		///     Called by ASF right after the bot config is initialised. Reads the optional
		///     <c>RandomGamesPlayedWhileIdle_RotationIntervalMinutes</c> property from the bot's JSON
		///     config file and stores it for use when the bot logs on.
		/// </summary>
		public Task OnBotInitModules(Bot bot, IReadOnlyDictionary<string, JsonElement>? additionalConfigProperties = null) {
			ArgumentNullException.ThrowIfNull(bot);

			double intervalMinutes = DefaultRotationIntervalMinutes;

			if (additionalConfigProperties != null &&
				additionalConfigProperties.TryGetValue(RotationIntervalConfigKey, out JsonElement configValue) &&
				configValue.ValueKind == JsonValueKind.Number &&
				configValue.TryGetDouble(out double parsedMinutes) &&
				parsedMinutes > 0) {
				intervalMinutes = parsedMinutes;
				bot.ArchiLogger.LogGenericInfo($"[{Name}] Config: rotation interval set to {intervalMinutes} minutes.");
			}

			BotRotationIntervals[bot] = intervalMinutes;

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

				bot.ArchiLogger.LogGenericInfo($"[{Name}] Found {allGames.Count} game(s) in library. Building rotation queue...");

				double intervalMinutes = BotRotationIntervals.GetValueOrDefault(bot, DefaultRotationIntervalMinutes);
				TimeSpan rotationInterval = TimeSpan.FromMinutes(intervalMinutes);

				BotState state = new(allGames);
				BotStates[bot] = state;

				ApplyNextBatch(bot, state);

				state.RotationTimer = new Timer(
					_ => { _ = RotateAsync(bot, state); },
					null,
					rotationInterval,
					rotationInterval
				);

				bot.ArchiLogger.LogGenericInfo($"[{Name}] Rotation timer started. Next rotation in {intervalMinutes} minutes.");
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
			List<uint> batch = new(MaxGamesPlayedConcurrently);

			while (batch.Count < MaxGamesPlayedConcurrently && state.Queue.Count > 0) {
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
			using HtmlDocumentResponse? response = await bot.ArchiWebHandler
				.UrlGetToHtmlDocumentWithSession(new Uri(ArchiWebHandler.SteamCommunityURL,
					$"profiles/{bot.SteamID}/games")).ConfigureAwait(false);

			if (response?.Content?.QuerySelector("#gameslist_config") is not IElement element) {
				return null;
			}

			return GamesListRegex()
				.Matches(element.OuterHtml)
				.Select(static x => uint.Parse(x.Groups[1].Value, CultureInfo.InvariantCulture))
				.ToList();
		}

		private static Queue<uint> BuildShuffledQueue(List<uint> games) {
			List<uint> copy = new(games);

			// CA5394: Random.Shared is intentionally used here – game order is not a security concern.
#pragma warning disable CA5394
			Random.Shared.Shuffle(System.Runtime.InteropServices.CollectionsMarshal.AsSpan(copy));
#pragma warning restore CA5394

			return new Queue<uint>(copy);
		}

		[GeneratedRegex(@"{&quot;appid&quot;:(\d+),&quot;name&quot;:&quot;")]
		private static partial Regex GamesListRegex();

		private sealed class BotState : IDisposable {
			public Queue<uint> Queue { get; private set; }
			public Timer? RotationTimer { get; set; }

			// Ensures only one rotation runs at a time even if a timer fires while a re-fetch is in progress.
			public SemaphoreSlim RotationLock { get; } = new(1, 1);

			public BotState(List<uint> allGames) {
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
