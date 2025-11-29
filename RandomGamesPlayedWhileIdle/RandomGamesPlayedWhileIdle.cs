using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Composition;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using AngleSharp.Dom;
using ArchiSteamFarm.Core;
using ArchiSteamFarm.Plugins.Interfaces;
using ArchiSteamFarm.Steam;
using ArchiSteamFarm.Steam.Integration;
using ArchiSteamFarm.Web.Responses;
using Newtonsoft.Json.Linq;
using SteamKit2;

namespace RandomGamesPlayedWhileIdle {
	[Export(typeof(IPlugin))]
	public sealed partial class RandomGamesPlayedWhileIdlePlugin : IBotConnection, IBotModules, IBot {
		private const int DefaultMaxGamesPlayedConcurrently = 32;
		private const int DefaultCycleIntervalMinutes = 0; // 0 means disabled

		private static readonly ConcurrentDictionary<Bot, BotSettings> BotConfigs = new();
		private static readonly ConcurrentDictionary<Bot, CancellationTokenSource> BotTimers = new();
		private static readonly ConcurrentDictionary<Bot, ImmutableList<uint>> BotGameLists = new();

		public string Name => nameof(RandomGamesPlayedWhileIdle);
		public Version Version => typeof(RandomGamesPlayedWhileIdlePlugin).Assembly.GetName().Version!;

		public Task OnLoaded() {
			ASF.ArchiLogger.LogGenericInfo($"{Name} has been loaded!");
			return Task.CompletedTask;
		}

		public Task OnBotInitModules(Bot bot, IReadOnlyDictionary<string, JToken>? additionalConfigProperties = null) {
			ArgumentNullException.ThrowIfNull(bot);

			BotSettings settings = new();

			if (additionalConfigProperties != null) {
				if (additionalConfigProperties.TryGetValue("RandomGamesPlayedWhileIdleCycleIntervalMinutes", out JToken? cycleIntervalToken)) {
					int cycleInterval = cycleIntervalToken.Value<int>();
					if (cycleInterval >= 0) {
						settings.CycleIntervalMinutes = cycleInterval;
					}
				}

				if (additionalConfigProperties.TryGetValue("RandomGamesPlayedWhileIdleBlacklist", out JToken? blacklistToken)) {
					try {
						IEnumerable<uint>? blacklist = blacklistToken.ToObject<IEnumerable<uint>>();
						if (blacklist != null) {
							settings.BlacklistedAppIds = blacklist.ToImmutableHashSet();
						}
					} catch (Exception e) {
						ASF.ArchiLogger.LogGenericException(e);
					}
				}

				if (additionalConfigProperties.TryGetValue("RandomGamesPlayedWhileIdleMaxGamesPlayed", out JToken? maxGamesToken)) {
					int maxGames = maxGamesToken.Value<int>();
					if (maxGames > 0 && maxGames <= 32) {
						settings.MaxGamesPlayedConcurrently = maxGames;
					}
				}
			}

			BotConfigs[bot] = settings;

			ASF.ArchiLogger.LogGenericInfo($"[{bot.BotName}] Config: CycleInterval={settings.CycleIntervalMinutes}min, MaxGames={settings.MaxGamesPlayedConcurrently}, Blacklist={settings.BlacklistedAppIds.Count} apps");

			return Task.CompletedTask;
		}

		public Task OnBotInit(Bot bot) => Task.CompletedTask;

		public Task OnBotDestroy(Bot bot) {
			ArgumentNullException.ThrowIfNull(bot);

			StopCycleTimer(bot);
			BotGameLists.TryRemove(bot, out _);
			BotConfigs.TryRemove(bot, out _);

			return Task.CompletedTask;
		}

		public Task OnBotDisconnected(Bot bot, EResult reason) {
			ArgumentNullException.ThrowIfNull(bot);

			StopCycleTimer(bot);

			return Task.CompletedTask;
		}

		public async Task OnBotLoggedOn(Bot bot) {
			ArgumentNullException.ThrowIfNull(bot);

			try {
				if (!BotConfigs.TryGetValue(bot, out BotSettings? settings)) {
					ASF.ArchiLogger.LogGenericWarning($"[{bot.BotName}] No configuration found, using defaults");
					settings = new BotSettings();
					BotConfigs[bot] = settings;
				}

				ImmutableList<uint>? gamesList = await FetchGamesList(bot, settings).ConfigureAwait(false);

				if (gamesList != null && gamesList.Count > 0) {
					BotGameLists[bot] = gamesList;
					SetRandomGames(bot, gamesList, settings);

					if (settings.CycleIntervalMinutes > 0) {
						StartCycleTimer(bot, settings);
					}
				}
			} catch (Exception e) {
				ASF.ArchiLogger.LogGenericException(e);
			}
		}

		private static async Task<ImmutableList<uint>?> FetchGamesList(Bot bot, BotSettings settings) {
			using HtmlDocumentResponse? response = await bot.ArchiWebHandler
				.UrlGetToHtmlDocumentWithSession(new Uri(ArchiWebHandler.SteamCommunityURL,
					$"profiles/{bot.SteamID}/games")).ConfigureAwait(false);

			IDocument? document = response?.Content;
			if (document == null) {
				return null;
			}

			INode? node = document.SelectSingleNode("""//*[@id="gameslist_config"]""");
			if (node is not IElement element) {
				return null;
			}

			List<uint> list = GamesListRegex()
				.Matches(element.OuterHtml)
				.Select(static x => uint.Parse(x.Groups[1].Value, CultureInfo.InvariantCulture))
				.Where(appId => !settings.BlacklistedAppIds.Contains(appId))
				.ToList();

			return list.Count > 0 ? list.ToImmutableList() : null;
		}

		private static void SetRandomGames(Bot bot, ImmutableList<uint> gamesList, BotSettings settings) {
			ImmutableList<uint> randomGames = gamesList
				.OrderBy(static _ => Guid.NewGuid())
				.Take(Math.Min(settings.MaxGamesPlayedConcurrently, gamesList.Count))
				.ToImmutableList();

			System.Reflection.PropertyInfo? property = bot.BotConfig.GetType().GetProperty("GamesPlayedWhileIdle");
			if (property == null) {
				ASF.ArchiLogger.LogGenericWarning($"[{bot.BotName}] Could not find GamesPlayedWhileIdle property - ASF version may be incompatible");
				return;
			}

			property.SetValue(bot.BotConfig, randomGames);
			ASF.ArchiLogger.LogGenericInfo($"[{bot.BotName}] Set {randomGames.Count} random games");
		}

		private static void StartCycleTimer(Bot bot, BotSettings settings) {
			StopCycleTimer(bot);

			CancellationTokenSource cts = new();
			BotTimers[bot] = cts;

			// Fire-and-forget is intentional - CycleGamesAsync handles all exceptions internally
			_ = CycleGamesAsync(bot, settings, cts.Token);
		}

		private static void StopCycleTimer(Bot bot) {
			if (BotTimers.TryRemove(bot, out CancellationTokenSource? cts)) {
				cts.Cancel();
				cts.Dispose();
			}
		}

		private static async Task CycleGamesAsync(Bot bot, BotSettings settings, CancellationToken cancellationToken) {
			try {
				while (!cancellationToken.IsCancellationRequested) {
					await Task.Delay(TimeSpan.FromMinutes(settings.CycleIntervalMinutes), cancellationToken).ConfigureAwait(false);

					if (BotGameLists.TryGetValue(bot, out ImmutableList<uint>? gamesList) && gamesList.Count > 0) {
						SetRandomGames(bot, gamesList, settings);
					}
				}
			} catch (OperationCanceledException) {
				// Expected when timer is stopped
			} catch (Exception e) {
				ASF.ArchiLogger.LogGenericException(e);
			}
		}

		[GeneratedRegex(@"{&quot;appid&quot;:(\d+),&quot;name&quot;:&quot;")]
		private static partial Regex GamesListRegex();

		private sealed class BotSettings {
			public int CycleIntervalMinutes { get; set; } = DefaultCycleIntervalMinutes;
			public int MaxGamesPlayedConcurrently { get; set; } = DefaultMaxGamesPlayedConcurrently;
			public ImmutableHashSet<uint> BlacklistedAppIds { get; set; } = ImmutableHashSet<uint>.Empty;
		}
	}
}
