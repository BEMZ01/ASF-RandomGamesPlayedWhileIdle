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
	public sealed partial class RandomGamesPlayedWhileIdlePlugin : IBotConnection, IASF, IBot {
		private const int MaxGamesPlayedConcurrently = 32;
		private const int DefaultCycleIntervalMinutes = 0; // 0 means disabled

		private static int CycleIntervalMinutes = DefaultCycleIntervalMinutes;
		private static ImmutableHashSet<uint> BlacklistedAppIds = ImmutableHashSet<uint>.Empty;

		private static readonly ConcurrentDictionary<Bot, CancellationTokenSource> BotTimers = new();
		private static readonly ConcurrentDictionary<Bot, ImmutableList<uint>> BotGameLists = new();

		public string Name => nameof(RandomGamesPlayedWhileIdle);
		public Version Version => typeof(RandomGamesPlayedWhileIdlePlugin).Assembly.GetName().Version!;

		public Task OnLoaded() {
			ASF.ArchiLogger.LogGenericInfo($"{Name} has been loaded!");
			return Task.CompletedTask;
		}

		public Task OnASFInit(IReadOnlyDictionary<string, JToken>? additionalConfigProperties = null) {
			if (additionalConfigProperties == null) {
				return Task.CompletedTask;
			}

			if (additionalConfigProperties.TryGetValue("RandomGamesPlayedWhileIdleCycleIntervalMinutes", out JToken? cycleIntervalToken)) {
				int cycleInterval = cycleIntervalToken.Value<int>();
				if (cycleInterval >= 0) {
					CycleIntervalMinutes = cycleInterval;
					ASF.ArchiLogger.LogGenericInfo($"Game cycle interval set to {CycleIntervalMinutes} minutes" + (CycleIntervalMinutes == 0 ? " (disabled)" : ""));
				}
			}

			if (additionalConfigProperties.TryGetValue("RandomGamesPlayedWhileIdleBlacklist", out JToken? blacklistToken)) {
				try {
					IEnumerable<uint>? blacklist = blacklistToken.ToObject<IEnumerable<uint>>();
					if (blacklist != null) {
						BlacklistedAppIds = blacklist.ToImmutableHashSet();
						ASF.ArchiLogger.LogGenericInfo($"Loaded {BlacklistedAppIds.Count} blacklisted app IDs");
					}
				} catch (Exception e) {
					ASF.ArchiLogger.LogGenericException(e);
				}
			}

			return Task.CompletedTask;
		}

		public Task OnBotInit(Bot bot) => Task.CompletedTask;

		public Task OnBotDestroy(Bot bot) {
			ArgumentNullException.ThrowIfNull(bot);

			StopCycleTimer(bot);
			BotGameLists.TryRemove(bot, out _);

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
				ImmutableList<uint>? gamesList = await FetchGamesList(bot).ConfigureAwait(false);

				if (gamesList != null && gamesList.Count > 0) {
					BotGameLists[bot] = gamesList;
					await SetRandomGames(bot, gamesList).ConfigureAwait(false);

					if (CycleIntervalMinutes > 0) {
						StartCycleTimer(bot);
					}
				}
			} catch (Exception e) {
				ASF.ArchiLogger.LogGenericException(e);
			}
		}

		private static async Task<ImmutableList<uint>?> FetchGamesList(Bot bot) {
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
				.Where(static appId => !BlacklistedAppIds.Contains(appId))
				.ToList();

			return list.Count > 0 ? list.ToImmutableList() : null;
		}

		private static async Task SetRandomGames(Bot bot, ImmutableList<uint> gamesList) {
			ImmutableList<uint> randomGames = gamesList
				.OrderBy(static _ => Guid.NewGuid())
				.Take(Math.Min(MaxGamesPlayedConcurrently, gamesList.Count))
				.ToImmutableList();

			bot.BotConfig.GetType().GetProperty("GamesPlayedWhileIdle")?.SetValue(bot.BotConfig, randomGames);

			ASF.ArchiLogger.LogGenericInfo($"Set {randomGames.Count} random games for {bot.BotName}");

			await Task.CompletedTask.ConfigureAwait(false);
		}

		private static void StartCycleTimer(Bot bot) {
			StopCycleTimer(bot);

			CancellationTokenSource cts = new();
			BotTimers[bot] = cts;

			_ = CycleGamesAsync(bot, cts.Token);
		}

		private static void StopCycleTimer(Bot bot) {
			if (BotTimers.TryRemove(bot, out CancellationTokenSource? cts)) {
				cts.Cancel();
				cts.Dispose();
			}
		}

		private static async Task CycleGamesAsync(Bot bot, CancellationToken cancellationToken) {
			try {
				while (!cancellationToken.IsCancellationRequested) {
					await Task.Delay(TimeSpan.FromMinutes(CycleIntervalMinutes), cancellationToken).ConfigureAwait(false);

					if (cancellationToken.IsCancellationRequested) {
						break;
					}

					if (BotGameLists.TryGetValue(bot, out ImmutableList<uint>? gamesList) && gamesList.Count > 0) {
						await SetRandomGames(bot, gamesList).ConfigureAwait(false);
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
	}
}
