//     _                _      _  ____   _                           _____
//    / \    _ __  ___ | |__  (_)/ ___| | |_  ___   __ _  _ __ ___  |  ___|__ _  _ __  _ __ ___
//   / _ \  | '__|/ __|| '_ \ | |\___ \ | __|/ _ \ / _` || '_ ` _ \ | |_  / _` || '__|| '_ ` _ \
//  / ___ \ | |  | (__ | | | || | ___) || |_|  __/| (_| || | | | | ||  _|| (_| || |   | | | | | |
// /_/   \_\|_|   \___||_| |_||_||____/  \__|\___| \__,_||_| |_| |_||_|   \__,_||_|   |_| |_| |_|
// |
// Copyright 2015-2022 Łukasz "JustArchi" Domeradzki
// Contact: JustArchi@JustArchi.net
// |
// Licensed under the Apache License, Version 2.0 (the "License");
// you may not use this file except in compliance with the License.
// You may obtain a copy of the License at
// |
// http://www.apache.org/licenses/LICENSE-2.0
// |
// Unless required by applicable law or agreed to in writing, software
// distributed under the License is distributed on an "AS IS" BASIS,
// WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
// See the License for the specific language governing permissions and
// limitations under the License.

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using ArchiSteamFarm.Core;
using ArchiSteamFarm.Localization;
using ArchiSteamFarm.OfficialPlugins.ItemsMatcher.Data;
using ArchiSteamFarm.Steam;
using ArchiSteamFarm.Steam.Cards;
using ArchiSteamFarm.Steam.Data;
using ArchiSteamFarm.Steam.Exchange;
using ArchiSteamFarm.Steam.Security;
using ArchiSteamFarm.Steam.Storage;
using ArchiSteamFarm.Storage;

namespace ArchiSteamFarm.OfficialPlugins.ItemsMatcher;

internal sealed class RemoteCommunication : IAsyncDisposable, IDisposable {
	private const byte MaxAnnouncementTTL = 60; // Maximum amount of minutes we can wait before the next Announcement
	private const byte MinAnnouncementTTL = 5; // Minimum amount of minutes we must wait before the next Announcement
	private const byte MinHeartBeatTTL = 10; // Minimum amount of minutes we must wait before sending next HeartBeat
	private const byte MinItemsCount = 100; // Minimum amount of items to be eligible for public listing
	private const byte MinPersonaStateTTL = 5; // Minimum amount of minutes we must wait before requesting persona state update

	private static readonly ImmutableHashSet<Asset.EType> AcceptedMatchableTypes = ImmutableHashSet.Create(
		Asset.EType.Emoticon,
		Asset.EType.FoilTradingCard,
		Asset.EType.ProfileBackground,
		Asset.EType.TradingCard
	);

	private readonly Bot Bot;
	private readonly Timer? HeartBeatTimer;
	private readonly SemaphoreSlim MatchActivelySemaphore = new(1, 1);
	private readonly Timer? MatchActivelyTimer;
	private readonly SemaphoreSlim RequestsSemaphore = new(1, 1);

	private DateTime LastAnnouncement;
	private DateTime LastHeartBeat;
	private DateTime LastPersonaStateRequest;
	private bool ShouldSendAnnouncementEarlier;
	private bool ShouldSendHeartBeats;

	internal RemoteCommunication(Bot bot) {
		ArgumentNullException.ThrowIfNull(bot);

		Bot = bot;

		if (Bot.BotConfig.RemoteCommunication.HasFlag(BotConfig.ERemoteCommunication.PublicListing)) {
			HeartBeatTimer = new Timer(
				HeartBeat,
				null,
				TimeSpan.FromMinutes(1) + TimeSpan.FromSeconds(ASF.LoadBalancingDelay * Bot.Bots?.Count ?? 0),
				TimeSpan.FromMinutes(1)
			);
		}

		if (Bot.BotConfig.TradingPreferences.HasFlag(BotConfig.ETradingPreferences.MatchActively)) {
			if ((ASF.GlobalConfig?.LicenseID != null) && (ASF.GlobalConfig.LicenseID != Guid.Empty)) {
				MatchActivelyTimer = new Timer(
					MatchActively,
					null,
					TimeSpan.FromHours(1) + TimeSpan.FromSeconds(ASF.LoadBalancingDelay * Bot.Bots?.Count ?? 0),
					TimeSpan.FromHours(6)
				);
			} else {
				bot.ArchiLogger.LogGenericError(string.Format(CultureInfo.CurrentCulture, Strings.WarningNoLicense, nameof(BotConfig.ETradingPreferences.MatchActively)));
			}
		}
	}

	public void Dispose() {
		// Those are objects that are always being created if constructor doesn't throw exception
		MatchActivelySemaphore.Dispose();
		RequestsSemaphore.Dispose();

		// Those are objects that might be null and the check should be in-place
		HeartBeatTimer?.Dispose();
		MatchActivelyTimer?.Dispose();
	}

	public async ValueTask DisposeAsync() {
		// Those are objects that are always being created if constructor doesn't throw exception
		MatchActivelySemaphore.Dispose();
		RequestsSemaphore.Dispose();

		// Those are objects that might be null and the check should be in-place
		if (HeartBeatTimer != null) {
			await HeartBeatTimer.DisposeAsync().ConfigureAwait(false);
		}

		if (MatchActivelyTimer != null) {
			await MatchActivelyTimer.DisposeAsync().ConfigureAwait(false);
		}
	}

	internal void OnNewItemsNotification() => ShouldSendAnnouncementEarlier = true;

	internal async Task OnPersonaState(string? nickname = null, string? avatarHash = null) {
		if (!Bot.BotConfig.RemoteCommunication.HasFlag(BotConfig.ERemoteCommunication.PublicListing)) {
			return;
		}

		if ((DateTime.UtcNow < LastAnnouncement.AddMinutes(ShouldSendAnnouncementEarlier ? MinAnnouncementTTL : MaxAnnouncementTTL)) && ShouldSendHeartBeats) {
			return;
		}

		await RequestsSemaphore.WaitAsync().ConfigureAwait(false);

		try {
			if ((DateTime.UtcNow < LastAnnouncement.AddMinutes(ShouldSendAnnouncementEarlier ? MinAnnouncementTTL : MaxAnnouncementTTL)) && ShouldSendHeartBeats) {
				return;
			}

			// Don't announce if we don't meet conditions
			bool? eligible = await IsEligibleForListing().ConfigureAwait(false);

			if (!eligible.HasValue) {
				// This is actually network failure, so we'll stop sending heartbeats but not record it as valid check
				ShouldSendHeartBeats = false;

				return;
			}

			if (!eligible.Value) {
				// We're not eligible, record this as a valid check
				LastAnnouncement = DateTime.UtcNow;
				ShouldSendAnnouncementEarlier = ShouldSendHeartBeats = false;

				return;
			}

			HashSet<Asset.EType> acceptedMatchableTypes = Bot.BotConfig.MatchableTypes.Where(AcceptedMatchableTypes.Contains).ToHashSet();

			if (acceptedMatchableTypes.Count == 0) {
				throw new InvalidOperationException(nameof(acceptedMatchableTypes));
			}

			string? tradeToken = await Bot.ArchiHandler.GetTradeToken().ConfigureAwait(false);

			if (string.IsNullOrEmpty(tradeToken)) {
				// This is actually a network failure, so we'll stop sending heartbeats but not record it as valid check
				ShouldSendHeartBeats = false;

				Bot.ArchiLogger.LogGenericWarning(string.Format(CultureInfo.CurrentCulture, Strings.WarningFailedWithError, nameof(tradeToken)));

				return;
			}

			List<Asset> inventory;

			try {
				inventory = await Bot.ArchiWebHandler.GetInventoryAsync().ToListAsync().ConfigureAwait(false);
			} catch (HttpRequestException e) {
				// This is actually a network failure, so we'll stop sending heartbeats but not record it as valid check
				ShouldSendHeartBeats = false;

				Bot.ArchiLogger.LogGenericWarningException(e);

				return;
			} catch (Exception e) {
				// This is actually a network failure, so we'll stop sending heartbeats but not record it as valid check
				ShouldSendHeartBeats = false;

				Bot.ArchiLogger.LogGenericException(e);

				return;
			}

			// This is actual inventory
			if (inventory.Count(item => item.Tradable && acceptedMatchableTypes.Contains(item.Type)) < MinItemsCount) {
				// We're not eligible, record this as a valid check
				LastAnnouncement = DateTime.UtcNow;
				ShouldSendAnnouncementEarlier = ShouldSendHeartBeats = false;

				return;
			}

			Bot.ArchiLogger.LogGenericInfo(string.Format(CultureInfo.CurrentCulture, Localization.Strings.ListingAnnouncing, Bot.SteamID, nickname, inventory.Count));

			// ReSharper disable once RedundantSuppressNullableWarningExpression - required for .NET Framework
			HttpStatusCode? response = await Backend.AnnounceForListing(Bot, inventory, acceptedMatchableTypes, tradeToken!, nickname, avatarHash).ConfigureAwait(false);

			if (!response.HasValue) {
				// This is actually a network failure, so we'll stop sending heartbeats but not record it as valid check
				ShouldSendHeartBeats = false;

				return;
			}

			// We've got the response, regardless what happened, we've succeeded in a valid check
			LastAnnouncement = DateTime.UtcNow;
			ShouldSendAnnouncementEarlier = false;

			if (response.Value.IsClientErrorCode()) {
				// ArchiNet told us that we've sent a bad request, so the process should restart from the beginning at later time
				ShouldSendHeartBeats = false;

				Bot.ArchiLogger.LogGenericWarning(string.Format(CultureInfo.CurrentCulture, Strings.WarningFailedWithError, response));

				switch (response) {
					case HttpStatusCode.Forbidden:
						// ArchiNet told us to stop submitting data for now
						LastAnnouncement = DateTime.UtcNow.AddYears(1);

						break;
#if NETFRAMEWORK
					case (HttpStatusCode) 429:
#else
					case HttpStatusCode.TooManyRequests:
#endif

						// ArchiNet told us to try again later
						LastAnnouncement = DateTime.UtcNow.AddDays(1);

						break;
				}

				return;
			}

			LastHeartBeat = DateTime.UtcNow;
			ShouldSendHeartBeats = true;

			Bot.ArchiLogger.LogGenericInfo(Strings.Success);
		} finally {
			RequestsSemaphore.Release();
		}
	}

	private async void HeartBeat(object? state = null) {
		if (!Bot.IsConnectedAndLoggedOn || (Bot.HeartBeatFailures > 0)) {
			return;
		}

		// Request persona update if needed
		if ((DateTime.UtcNow > LastPersonaStateRequest.AddMinutes(MinPersonaStateTTL)) && (DateTime.UtcNow > LastAnnouncement.AddMinutes(ShouldSendAnnouncementEarlier ? MinAnnouncementTTL : MaxAnnouncementTTL))) {
			LastPersonaStateRequest = DateTime.UtcNow;
			Bot.RequestPersonaStateUpdate();
		}

		if (!ShouldSendHeartBeats || (DateTime.UtcNow < LastHeartBeat.AddMinutes(MinHeartBeatTTL))) {
			return;
		}

		if (!await RequestsSemaphore.WaitAsync(0).ConfigureAwait(false)) {
			return;
		}

		try {
			HttpStatusCode? response = await Backend.HeartBeatForListing(Bot).ConfigureAwait(false);

			if (!response.HasValue) {
				return;
			}

			if (response.Value.IsClientErrorCode()) {
				ShouldSendHeartBeats = false;

				return;
			}

			LastHeartBeat = DateTime.UtcNow;
		} finally {
			RequestsSemaphore.Release();
		}
	}

	private async Task<bool?> IsEligibleForListing() {
		// Bot must be eligible for matching first
		bool? isEligibleForMatching = await IsEligibleForMatching().ConfigureAwait(false);

		if (isEligibleForMatching != true) {
			return isEligibleForMatching;
		}

		// Bot must have STM enabled in TradingPreferences
		if (!Bot.BotConfig.TradingPreferences.HasFlag(BotConfig.ETradingPreferences.SteamTradeMatcher)) {
			Bot.ArchiLogger.LogGenericTrace(string.Format(CultureInfo.CurrentCulture, Strings.WarningFailedWithError, $"{nameof(Bot.BotConfig.TradingPreferences)}: {Bot.BotConfig.TradingPreferences}"));

			return false;
		}

		// Bot must have public inventory
		bool? hasPublicInventory = await Bot.HasPublicInventory().ConfigureAwait(false);

		if (hasPublicInventory != true) {
			Bot.ArchiLogger.LogGenericTrace(string.Format(CultureInfo.CurrentCulture, Strings.WarningFailedWithError, $"{nameof(Bot.HasPublicInventory)}: {hasPublicInventory?.ToString() ?? "null"}"));

			return hasPublicInventory;
		}

		return true;
	}

	private async Task<bool?> IsEligibleForMatching() {
		// Bot must have ASF 2FA
		if (!Bot.HasMobileAuthenticator) {
			Bot.ArchiLogger.LogGenericTrace(string.Format(CultureInfo.CurrentCulture, Strings.WarningFailedWithError, $"{nameof(Bot.HasMobileAuthenticator)}: {Bot.HasMobileAuthenticator}"));

			return false;
		}

		// Bot must have at least one accepted matchable type set
		if ((Bot.BotConfig.MatchableTypes.Count == 0) || Bot.BotConfig.MatchableTypes.All(static type => !AcceptedMatchableTypes.Contains(type))) {
			Bot.ArchiLogger.LogGenericTrace(string.Format(CultureInfo.CurrentCulture, Strings.WarningFailedWithError, $"{nameof(Bot.BotConfig.MatchableTypes)}: {string.Join(", ", Bot.BotConfig.MatchableTypes)}"));

			return false;
		}

		// Bot must have valid API key (e.g. not being restricted account)
		bool? hasValidApiKey = await Bot.ArchiWebHandler.HasValidApiKey().ConfigureAwait(false);

		if (hasValidApiKey != true) {
			Bot.ArchiLogger.LogGenericTrace(string.Format(CultureInfo.CurrentCulture, Strings.WarningFailedWithError, $"{nameof(Bot.ArchiWebHandler.HasValidApiKey)}: {hasValidApiKey?.ToString() ?? "null"}"));

			return hasValidApiKey;
		}

		return true;
	}

	private async void MatchActively(object? state = null) {
		if (ASF.GlobalConfig == null) {
			throw new InvalidOperationException(nameof(ASF.GlobalConfig));
		}

		if (!ASF.GlobalConfig.LicenseID.HasValue || (ASF.GlobalConfig.LicenseID == Guid.Empty)) {
			throw new InvalidOperationException(nameof(ASF.GlobalConfig.LicenseID));
		}

		if (!Bot.IsConnectedAndLoggedOn || Bot.BotConfig.TradingPreferences.HasFlag(BotConfig.ETradingPreferences.MatchEverything) || !Bot.BotConfig.TradingPreferences.HasFlag(BotConfig.ETradingPreferences.MatchActively) || (await IsEligibleForMatching().ConfigureAwait(false) != true)) {
			Bot.ArchiLogger.LogGenericTrace(Strings.ErrorAborted);

			return;
		}

		HashSet<Asset.EType> acceptedMatchableTypes = Bot.BotConfig.MatchableTypes.Where(AcceptedMatchableTypes.Contains).ToHashSet();

		if (acceptedMatchableTypes.Count == 0) {
			Bot.ArchiLogger.LogNullError(acceptedMatchableTypes);

			return;
		}

		if (!await MatchActivelySemaphore.WaitAsync(0).ConfigureAwait(false)) {
			Bot.ArchiLogger.LogGenericTrace(Strings.ErrorAborted);

			return;
		}

		try {
			Bot.ArchiLogger.LogGenericInfo(Strings.Starting);

			string? tradeToken = await Bot.ArchiHandler.GetTradeToken().ConfigureAwait(false);

			if (string.IsNullOrEmpty(tradeToken)) {
				Bot.ArchiLogger.LogGenericWarning(string.Format(CultureInfo.CurrentCulture, Strings.WarningFailedWithError, nameof(tradeToken)));

				return;
			}

			HashSet<Asset> ourInventory;

			try {
				ourInventory = await Bot.ArchiWebHandler.GetInventoryAsync().Where(item => acceptedMatchableTypes.Contains(item.Type) && !Bot.BotDatabase.MatchActivelyBlacklistAppIDs.Contains(item.RealAppID)).ToHashSetAsync().ConfigureAwait(false);
			} catch (HttpRequestException e) {
				Bot.ArchiLogger.LogGenericWarningException(e);
				Bot.ArchiLogger.LogGenericWarning(string.Format(CultureInfo.CurrentCulture, Strings.WarningFailedWithError, nameof(ourInventory)));

				return;
			} catch (Exception e) {
				Bot.ArchiLogger.LogGenericException(e);
				Bot.ArchiLogger.LogGenericWarning(string.Format(CultureInfo.CurrentCulture, Strings.WarningFailedWithError, nameof(ourInventory)));

				return;
			}

			if (ourInventory.Count == 0) {
				Bot.ArchiLogger.LogGenericInfo(string.Format(CultureInfo.CurrentCulture, Strings.ErrorIsEmpty, nameof(ourInventory)));

				return;
			}

			// ReSharper disable once RedundantSuppressNullableWarningExpression - required for .NET Framework
			(HttpStatusCode StatusCode, ImmutableHashSet<ListedUser> Users)? response = await Backend.GetListedUsersForMatching(ASF.GlobalConfig.LicenseID.Value, Bot, ourInventory, acceptedMatchableTypes, tradeToken!).ConfigureAwait(false);

			if (response == null) {
				Bot.ArchiLogger.LogGenericWarning(string.Format(CultureInfo.CurrentCulture, Strings.WarningFailedWithError, nameof(response)));

				return;
			}

			if (!response.Value.StatusCode.IsSuccessCode()) {
				Bot.ArchiLogger.LogGenericWarning(string.Format(CultureInfo.CurrentCulture, Strings.WarningFailedWithError, response.Value.StatusCode));

				return;
			}

			if (response.Value.Users.IsEmpty) {
				Bot.ArchiLogger.LogGenericWarning(string.Format(CultureInfo.CurrentCulture, Strings.WarningFailedWithError, nameof(response.Value.Users)));

				return;
			}

#pragma warning disable CA2000 // False positive, we're actually wrapping it in the using clause below exactly for that purpose
			using (await Bot.Actions.GetTradingLock().ConfigureAwait(false)) {
#pragma warning restore CA2000 // False positive, we're actually wrapping it in the using clause below exactly for that purpose
				Bot.ArchiLogger.LogGenericInfo(Strings.Starting);
				await MatchActivelyRound(response.Value.Users, ourInventory, acceptedMatchableTypes).ConfigureAwait(false);
			}

			Bot.ArchiLogger.LogGenericInfo(Strings.Done);
		} finally {
			MatchActivelySemaphore.Release();
		}
	}

	private async Task MatchActivelyRound(IReadOnlyCollection<ListedUser> listedUsers, IReadOnlyCollection<Asset> ourInventory, IReadOnlyCollection<Asset.EType> acceptedMatchableTypes) {
		if ((listedUsers == null) || (listedUsers.Count == 0)) {
			throw new ArgumentNullException(nameof(listedUsers));
		}

		if ((ourInventory == null) || (ourInventory.Count == 0)) {
			throw new ArgumentNullException(nameof(ourInventory));
		}

		if ((acceptedMatchableTypes == null) || (acceptedMatchableTypes.Count == 0)) {
			throw new ArgumentNullException(nameof(acceptedMatchableTypes));
		}

		(Dictionary<(uint RealAppID, Asset.EType Type, Asset.ERarity Rarity), Dictionary<ulong, uint>> ourFullState, Dictionary<(uint RealAppID, Asset.EType Type, Asset.ERarity Rarity), Dictionary<ulong, uint>> ourTradableState) = Trading.GetDividedInventoryState(ourInventory);

		if (Trading.IsEmptyForMatching(ourFullState, ourTradableState)) {
			// User doesn't have any more dupes in the inventory
			Bot.ArchiLogger.LogGenericTrace(string.Format(CultureInfo.CurrentCulture, Strings.ErrorIsEmpty, $"{nameof(ourFullState)} || {nameof(ourTradableState)}"));

			return;
		}

		HashSet<ulong> triedSteamIDs = new();
		HashSet<(uint RealAppID, Asset.EType Type, Asset.ERarity Rarity)> skippedSetsThisRound = new();

		byte maxTradeHoldDuration = ASF.GlobalConfig?.MaxTradeHoldDuration ?? GlobalConfig.DefaultMaxTradeHoldDuration;

		foreach (ListedUser listedUser in listedUsers.Where(listedUser => (listedUser.SteamID != Bot.SteamID) && !triedSteamIDs.Contains(listedUser.SteamID) && acceptedMatchableTypes.Any(listedUser.MatchableTypes.Contains) && !Bot.IsBlacklistedFromTrades(listedUser.SteamID)).OrderByDescending(static listedUser => listedUser.MatchEverything).ThenBy(static listedUser => listedUser.Assets.Count)) {
			HashSet<(uint RealAppID, Asset.EType Type, Asset.ERarity Rarity)> wantedSets = ourTradableState.Keys.Where(set => !skippedSetsThisRound.Contains(set) && listedUser.MatchableTypes.Contains(set.Type)).ToHashSet();

			if (wantedSets.Count == 0) {
				continue;
			}

			Bot.ArchiLogger.LogGenericTrace($"{listedUser.SteamID}...");

			byte? tradeHoldDuration = await Bot.ArchiWebHandler.GetCombinedTradeHoldDurationAgainstUser(listedUser.SteamID, listedUser.TradeToken).ConfigureAwait(false);

			switch (tradeHoldDuration) {
				case null:
					Bot.ArchiLogger.LogGenericTrace(string.Format(CultureInfo.CurrentCulture, Strings.ErrorIsEmpty, nameof(tradeHoldDuration)));

					continue;
				case > 0 when (tradeHoldDuration.Value > maxTradeHoldDuration) || (tradeHoldDuration.Value > listedUser.MaxTradeHoldDuration):
					Bot.ArchiLogger.LogGenericTrace($"{tradeHoldDuration.Value} > {maxTradeHoldDuration} || {listedUser.MaxTradeHoldDuration}");

					continue;
			}

			HashSet<Asset> theirInventory = listedUser.Assets.Where(item => (!listedUser.MatchEverything || item.Tradable) && wantedSets.Contains((item.RealAppID, item.Type, item.Rarity)) && ((tradeHoldDuration.Value == 0) || !(item.Type is Asset.EType.FoilTradingCard or Asset.EType.TradingCard && CardsFarmer.SalesBlacklist.Contains(item.RealAppID)))).Select(static asset => asset.ToAsset()).ToHashSet();

			HashSet<(uint RealAppID, Asset.EType Type, Asset.ERarity Rarity)> skippedSetsThisUser = new();

			Dictionary<(uint RealAppID, Asset.EType Type, Asset.ERarity Rarity), Dictionary<ulong, uint>> theirTradableState = Trading.GetTradableInventoryState(theirInventory);
			Dictionary<(uint RealAppID, Asset.EType Type, Asset.ERarity Rarity), Dictionary<ulong, uint>> inventoryStateChanges = new();

			for (byte i = 0; i < Trading.MaxTradesPerAccount; i++) {
				byte itemsInTrade = 0;
				HashSet<(uint RealAppID, Asset.EType Type, Asset.ERarity Rarity)> skippedSetsThisTrade = new();

				Dictionary<ulong, uint> classIDsToGive = new();
				Dictionary<ulong, uint> classIDsToReceive = new();
				Dictionary<ulong, uint> fairClassIDsToGive = new();
				Dictionary<ulong, uint> fairClassIDsToReceive = new();

				foreach (((uint RealAppID, Asset.EType Type, Asset.ERarity Rarity) set, Dictionary<ulong, uint> ourFullItems) in ourFullState.Where(set => !skippedSetsThisUser.Contains(set.Key) && listedUser.MatchableTypes.Contains(set.Key.Type) && set.Value.Values.Any(static count => count > 1))) {
					if (!ourTradableState.TryGetValue(set, out Dictionary<ulong, uint>? ourTradableItems) || (ourTradableItems.Count == 0)) {
						continue;
					}

					if (!theirTradableState.TryGetValue(set, out Dictionary<ulong, uint>? theirTradableItems) || (theirTradableItems.Count == 0)) {
						continue;
					}

					// Those 2 collections are on user-basis since we can't be sure that the trade passes through (and therefore we need to keep original state in case of failure)
					Dictionary<ulong, uint> ourFullSet = new(ourFullItems);
					Dictionary<ulong, uint> ourTradableSet = new(ourTradableItems);

					// We also have to take into account changes that happened in previous trades with this user, so this block will adapt to that
					if (inventoryStateChanges.TryGetValue(set, out Dictionary<ulong, uint>? pastChanges) && (pastChanges.Count > 0)) {
						foreach ((ulong classID, uint amount) in pastChanges) {
							if (!ourFullSet.TryGetValue(classID, out uint fullAmount) || (fullAmount == 0) || (fullAmount < amount)) {
								Bot.ArchiLogger.LogNullError(fullAmount);

								return;
							}

							if (fullAmount > amount) {
								ourFullSet[classID] = fullAmount - amount;
							} else {
								ourFullSet.Remove(classID);
							}

							if (!ourTradableSet.TryGetValue(classID, out uint tradableAmount) || (tradableAmount == 0) || (tradableAmount < amount)) {
								Bot.ArchiLogger.LogNullError(tradableAmount);

								return;
							}

							if (fullAmount > amount) {
								ourTradableSet[classID] = fullAmount - amount;
							} else {
								ourTradableSet.Remove(classID);
							}
						}

						if (Trading.IsEmptyForMatching(ourFullSet, ourTradableSet)) {
							continue;
						}
					}

					bool match;

					do {
						match = false;

						foreach ((ulong ourItem, uint ourFullAmount) in ourFullSet.Where(static item => item.Value > 1).OrderByDescending(static item => item.Value)) {
							if (!ourTradableSet.TryGetValue(ourItem, out uint ourTradableAmount) || (ourTradableAmount == 0)) {
								continue;
							}

							foreach ((ulong theirItem, uint theirTradableAmount) in theirTradableItems.OrderBy(item => ourFullSet.TryGetValue(item.Key, out uint ourAmountOfTheirItem) ? ourAmountOfTheirItem : 0)) {
								if (ourFullSet.TryGetValue(theirItem, out uint ourAmountOfTheirItem) && (ourFullAmount <= ourAmountOfTheirItem + 1)) {
									continue;
								}

								if (!listedUser.MatchEverything) {
									// We have a potential match, let's check fairness for them
									fairClassIDsToGive.TryGetValue(ourItem, out uint fairGivenAmount);
									fairClassIDsToReceive.TryGetValue(theirItem, out uint fairReceivedAmount);
									fairClassIDsToGive[ourItem] = ++fairGivenAmount;
									fairClassIDsToReceive[theirItem] = ++fairReceivedAmount;

									// Filter their inventory for the sets we're trading or have traded with this user
									HashSet<Asset> fairFiltered = theirInventory.Where(item => ((item.RealAppID == set.RealAppID) && (item.Type == set.Type) && (item.Rarity == set.Rarity)) || skippedSetsThisTrade.Contains((item.RealAppID, item.Type, item.Rarity))).Select(static item => item.CreateShallowCopy()).ToHashSet();

									// Copy list to HashSet<Steam.Asset>
									HashSet<Asset> fairItemsToGive = Trading.GetTradableItemsFromInventory(ourInventory.Where(item => ((item.RealAppID == set.RealAppID) && (item.Type == set.Type) && (item.Rarity == set.Rarity)) || skippedSetsThisTrade.Contains((item.RealAppID, item.Type, item.Rarity))).Select(static item => item.CreateShallowCopy()).ToHashSet(), fairClassIDsToGive.ToDictionary(static classID => classID.Key, static classID => classID.Value));
									HashSet<Asset> fairItemsToReceive = Trading.GetTradableItemsFromInventory(fairFiltered.Select(static item => item.CreateShallowCopy()).ToHashSet(), fairClassIDsToReceive.ToDictionary(static classID => classID.Key, static classID => classID.Value));

									// Actual check:
									if (!Trading.IsTradeNeutralOrBetter(fairFiltered, fairItemsToReceive, fairItemsToGive)) {
										if (fairGivenAmount > 1) {
											fairClassIDsToGive[ourItem] = fairGivenAmount - 1;
										} else {
											fairClassIDsToGive.Remove(ourItem);
										}

										if (fairReceivedAmount > 1) {
											fairClassIDsToReceive[theirItem] = fairReceivedAmount - 1;
										} else {
											fairClassIDsToReceive.Remove(theirItem);
										}

										continue;
									}
								}

								// Skip this set from the remaining of this round
								skippedSetsThisTrade.Add(set);

								// Update our state based on given items
								classIDsToGive[ourItem] = classIDsToGive.TryGetValue(ourItem, out uint ourGivenAmount) ? ourGivenAmount + 1 : 1;
								ourFullSet[ourItem] = ourFullAmount - 1; // We don't need to remove anything here because we can guarantee that ourItem.Value is at least 2

								if (inventoryStateChanges.TryGetValue(set, out Dictionary<ulong, uint>? currentChanges)) {
									currentChanges[ourItem] = currentChanges.TryGetValue(ourItem, out uint amount) ? amount + 1 : 1;
								} else {
									inventoryStateChanges[set] = new Dictionary<ulong, uint> {
										{ ourItem, 1 }
									};
								}

								// Update our state based on received items
								classIDsToReceive[theirItem] = classIDsToReceive.TryGetValue(theirItem, out uint ourReceivedAmount) ? ourReceivedAmount + 1 : 1;
								ourFullSet[theirItem] = ourAmountOfTheirItem + 1;

								if (ourTradableAmount > 1) {
									ourTradableSet[ourItem] = ourTradableAmount - 1;
								} else {
									ourTradableSet.Remove(ourItem);
								}

								// Update their state based on taken items
								if (theirTradableAmount > 1) {
									theirTradableItems[theirItem] = theirTradableAmount - 1;
								} else {
									theirTradableItems.Remove(theirItem);
								}

								itemsInTrade += 2;

								match = true;

								break;
							}

							if (match) {
								break;
							}
						}
					} while (match && (itemsInTrade < Trading.MaxItemsPerTrade - 1));

					if (itemsInTrade >= Trading.MaxItemsPerTrade - 1) {
						break;
					}
				}

				if (skippedSetsThisTrade.Count == 0) {
					Bot.ArchiLogger.LogGenericTrace(string.Format(CultureInfo.CurrentCulture, Strings.ErrorIsEmpty, nameof(skippedSetsThisTrade)));

					break;
				}

				// Remove the items from inventories
				HashSet<Asset> itemsToGive = Trading.GetTradableItemsFromInventory(ourInventory, classIDsToGive);
				HashSet<Asset> itemsToReceive = Trading.GetTradableItemsFromInventory(theirInventory, classIDsToReceive, true);

				if ((itemsToGive.Count != itemsToReceive.Count) || !Trading.IsFairExchange(itemsToGive, itemsToReceive)) {
					// Failsafe
					Bot.ArchiLogger.LogGenericError(string.Format(CultureInfo.CurrentCulture, Strings.WarningFailedWithError, Strings.ErrorAborted));

					return;
				}

				triedSteamIDs.Add(listedUser.SteamID);

				Bot.ArchiLogger.LogGenericInfo(string.Format(CultureInfo.CurrentCulture, Localization.Strings.MatchingFound, itemsToReceive.Count, listedUser.SteamID, listedUser.Nickname));

				Bot.ArchiLogger.LogGenericTrace($"{Bot.SteamID} <- {string.Join(", ", itemsToReceive.Select(static item => $"{item.RealAppID}/{item.Type}/{item.Rarity}/{item.ClassID} #{item.Amount}"))} | {string.Join(", ", itemsToGive.Select(static item => $"{item.RealAppID}/{item.Type}/{item.Rarity}/{item.ClassID} #{item.Amount}"))} -> {listedUser.SteamID}");

				(bool success, HashSet<ulong>? mobileTradeOfferIDs) = await Bot.ArchiWebHandler.SendTradeOffer(listedUser.SteamID, itemsToGive, itemsToReceive, listedUser.TradeToken, true).ConfigureAwait(false);

				if ((mobileTradeOfferIDs?.Count > 0) && Bot.HasMobileAuthenticator) {
					(bool twoFactorSuccess, _, _) = await Bot.Actions.HandleTwoFactorAuthenticationConfirmations(true, Confirmation.EType.Trade, mobileTradeOfferIDs, true).ConfigureAwait(false);

					if (!twoFactorSuccess) {
						Bot.ArchiLogger.LogGenericWarning(string.Format(CultureInfo.CurrentCulture, Strings.WarningFailedWithError, nameof(twoFactorSuccess)));

						return;
					}
				}

				if (!success) {
					Bot.ArchiLogger.LogGenericWarning(string.Format(CultureInfo.CurrentCulture, Localization.Strings.TradeOfferFailed, listedUser.SteamID, listedUser.Nickname));

					break;
				}

				// Add itemsToGive to theirInventory to reflect their current state if we're over MaxItemsPerTrade
				theirInventory.UnionWith(itemsToGive);

				skippedSetsThisUser.UnionWith(skippedSetsThisTrade);
				Bot.ArchiLogger.LogGenericInfo(Strings.Success);
			}

			if (skippedSetsThisUser.Count == 0) {
				continue;
			}

			skippedSetsThisRound.UnionWith(skippedSetsThisUser);

			foreach ((uint RealAppID, Asset.EType Type, Asset.ERarity Rarity) skippedSet in skippedSetsThisUser) {
				ourFullState.Remove(skippedSet);
				ourTradableState.Remove(skippedSet);
			}

			if (Trading.IsEmptyForMatching(ourFullState, ourTradableState)) {
				// User doesn't have any more dupes in the inventory
				break;
			}

			ourFullState.TrimExcess();
			ourTradableState.TrimExcess();
		}

		Bot.ArchiLogger.LogGenericInfo(string.Format(CultureInfo.CurrentCulture, Localization.Strings.ActivelyMatchingItemsRound, skippedSetsThisRound.Count));
	}
}