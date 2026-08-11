using System;
using System.Collections.Generic;
using Common;
using Common.Logging;
using Common.Messaging;
using Common.Network;
using Common.Util;
using GameInterface.Services.Clans.Messages;
using GameInterface.Services.ObjectManager;
using Serilog;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.Party.PartyComponents;

namespace GameInterface.Services.Clans.Handlers;

internal class ClanCachesHandler : IHandler
{
    private static readonly ILogger Logger = LogManager.GetLogger<ClanCachesHandler>();

    private readonly IMessageBroker messageBroker;
    private readonly IObjectManager objectManager;
    private readonly INetwork network;

    public ClanCachesHandler(
        IMessageBroker messageBroker,
        IObjectManager objectManager,
        INetwork network)
    {
        this.messageBroker = messageBroker;
        this.objectManager = objectManager;
        this.network = network;

        messageBroker.Subscribe<WarPartyAdded>(Handle_WarPartyAdded);
        messageBroker.Subscribe<NetworkAddWarParty>(Handle_AddWarParty);
        messageBroker.Subscribe<WarPartyRemoved>(Handle_WarPartyRemoved);
        messageBroker.Subscribe<NetworkRemoveWarParty>(Handle_RemoveWarParty);
        messageBroker.Subscribe<SupporterNotableAdded>(Handle_SupporterNotableAdded);
        messageBroker.Subscribe<NetworkAddSupporterNotable>(Handle_AddSupporterNotable);
        messageBroker.Subscribe<SupporterNotableRemoved>(Handle_SupporterNotableRemoved);
        messageBroker.Subscribe<NetworkRemoveSupporterNotable>(Handle_RemoveSupporterNotable);
    }

    public void Dispose()
    {
        messageBroker.Unsubscribe<WarPartyAdded>(Handle_WarPartyAdded);
        messageBroker.Unsubscribe<NetworkAddWarParty>(Handle_AddWarParty);
        messageBroker.Unsubscribe<WarPartyRemoved>(Handle_WarPartyRemoved);
        messageBroker.Unsubscribe<NetworkRemoveWarParty>(Handle_RemoveWarParty);
        messageBroker.Unsubscribe<SupporterNotableAdded>(Handle_SupporterNotableAdded);
        messageBroker.Unsubscribe<NetworkAddSupporterNotable>(Handle_AddSupporterNotable);
        messageBroker.Unsubscribe<SupporterNotableRemoved>(Handle_SupporterNotableRemoved);
        messageBroker.Unsubscribe<NetworkRemoveSupporterNotable>(Handle_RemoveSupporterNotable);
    }

    private void Handle_WarPartyAdded(MessagePayload<WarPartyAdded> obj)
    {
        if (!objectManager.TryGetIdWithLogging(obj.What.Clan, out var clanId)) return;
        if (!objectManager.TryGetIdWithLogging(obj.What.WarPartyComponent, out var warPartyComponentId)) return;
        
        // Update changed cache on all clients
        network.SendAll(new NetworkAddWarParty(clanId, warPartyComponentId));
    }

    /// <summary>
    /// War-party registrations whose clan or component had not arrived yet, retried until they can be applied.
    /// </summary>
    /// <remarks>
    /// This used to give up. Both lookups returned on failure, so a registration that arrived before the party
    /// it names was dropped and never retried - the party existed and worked, but was never added to its clan's
    /// war-party cache.
    ///
    /// That cache is what Army Management lists, so the party became uncallable and INVISIBLE there: a player
    /// could see his companion's party on the map and had no row for it in the army screen. Rejoining fixed it,
    /// because a fresh save transfer rebuilds the cache from scratch - which is exactly the shape of a lost
    /// one-shot message rather than a corrupted one.
    ///
    /// The order is a race, not an error: the server sends this the moment the war party is added, and the
    /// client may not have registered the component yet. So an unresolved message is now held and retried
    /// rather than discarded.
    /// </remarks>
    private static readonly List<PendingWarParty> pendingWarParties = new List<PendingWarParty>();

    private readonly struct PendingWarParty
    {
        public readonly string ClanId;
        public readonly string ComponentId;
        public readonly DateTime FirstSeen;

        public PendingWarParty(string clanId, string componentId, DateTime firstSeen)
        {
            ClanId = clanId;
            ComponentId = componentId;
            FirstSeen = firstSeen;
        }
    }

    /// <summary>How long an unresolvable registration is retried before it is reported and dropped.</summary>
    internal static readonly TimeSpan PendingWarPartyTimeout = TimeSpan.FromSeconds(60);

    /// <summary>Whether a still-unresolvable registration should keep being retried.</summary>
    /// <remarks>
    /// Bounded on purpose. A component that never arrives - because its party was destroyed in the meantime,
    /// say - must not be retried for the rest of the session, and the give-up has to be loud rather than the
    /// silent return this replaces.
    /// </remarks>
    internal static bool ShouldKeepRetrying(TimeSpan waited, TimeSpan timeout) => waited < timeout;

    private void Handle_AddWarParty(MessagePayload<NetworkAddWarParty> obj)
    {
        var clanId = obj.What.ClanId;
        var componentId = obj.What.WarPartyComponentId;

        GameThread.RunSafe(() =>
        {
            if (TryApplyWarParty(clanId, componentId)) { DrainPendingWarParties(); return; }

            // Not resolvable yet - hold it and try again as later objects register.
            pendingWarParties.Add(new PendingWarParty(clanId, componentId, DateTime.UtcNow));
            Logger.Information(
                "[ClanCache] War party {Component} for clan {Clan} cannot be resolved yet; holding it for retry rather than dropping it",
                componentId, clanId);
        });
    }

    /// <summary>Applies a war-party registration, or reports that it still cannot be resolved.</summary>
    private bool TryApplyWarParty(string clanId, string componentId)
    {
        if (!objectManager.TryGetObject<Clan>(clanId, out var clan)) return false;
        if (!objectManager.TryGetObject<WarPartyComponent>(componentId, out var warPartyComponent)) return false;

        using (new AllowedThread())
        {
            clan.OnWarPartyAdded(warPartyComponent);
        }

        return true;
    }

    /// <summary>
    /// Retries every held registration. Called whenever another one arrives, which is the cheapest signal that
    /// object registration has progressed.
    /// </summary>
    private void DrainPendingWarParties()
    {
        if (pendingWarParties.Count == 0) return;

        for (int i = pendingWarParties.Count - 1; i >= 0; i--)
        {
            var pending = pendingWarParties[i];

            if (TryApplyWarParty(pending.ClanId, pending.ComponentId))
            {
                pendingWarParties.RemoveAt(i);
                Logger.Information("[ClanCache] Held war party {Component} resolved and was added to clan {Clan}",
                    pending.ComponentId, pending.ClanId);
                continue;
            }

            if (!ShouldKeepRetrying(DateTime.UtcNow - pending.FirstSeen, PendingWarPartyTimeout))
            {
                pendingWarParties.RemoveAt(i);
                Logger.Error(
                    "[ClanCache] War party {Component} for clan {Clan} never resolved after {Seconds}s; it will be missing from that clan's army list until a rejoin",
                    pending.ComponentId, pending.ClanId, PendingWarPartyTimeout.TotalSeconds);
            }
        }
    }

    /// <summary>Clears held registrations; used by tests and on teardown so nothing leaks between sessions.</summary>
    internal static void ResetPendingWarParties() => pendingWarParties.Clear();

    private void Handle_WarPartyRemoved(MessagePayload<WarPartyRemoved> obj)
    {
        if (!objectManager.TryGetIdWithLogging(obj.What.Clan, out var clanId)) return;
        if (!objectManager.TryGetIdWithLogging(obj.What.WarPartyComponent, out var warPartyComponentId)) return;

        // Update changed cache on all clients
        network.SendAll(new NetworkRemoveWarParty(clanId, warPartyComponentId));
    }

    private void Handle_RemoveWarParty(MessagePayload<NetworkRemoveWarParty> obj)
    {
        // Resolve on the game-loop thread, in queue order with deferred component lifecycle applies.
        GameThread.RunSafe(() =>
        {
            if (!objectManager.TryGetObjectWithLogging<Clan>(obj.What.ClanId, out var clan)) return;
            if (!objectManager.TryGetObjectWithLogging<WarPartyComponent>(obj.What.WarPartyComponentId, out var warPartyComponent)) return;

            using (new AllowedThread())
            {
                clan.OnWarPartyRemoved(warPartyComponent);
            }
        });
    }

    private void Handle_SupporterNotableAdded(MessagePayload<SupporterNotableAdded> obj)
    {
        if (!objectManager.TryGetIdWithLogging(obj.What.Clan, out var clanId)) return;
        if (!objectManager.TryGetIdWithLogging(obj.What.Hero, out var heroId)) return;

        // Update changed cache on all clients
        network.SendAll(new NetworkAddSupporterNotable(clanId, heroId));
    }

    private void Handle_AddSupporterNotable(MessagePayload<NetworkAddSupporterNotable> obj)
    {
        // Resolve on the game-loop thread, in queue order with deferred object-creation applies.
        GameThread.RunSafe(() =>
        {
            if (!objectManager.TryGetObjectWithLogging<Clan>(obj.What.ClanId, out var clan)) return;
            if (!objectManager.TryGetObjectWithLogging<Hero>(obj.What.HeroId, out var hero)) return;

            using (new AllowedThread())
            {
                clan.OnSupporterNotableAdded(hero);
            }
        });
    }

    private void Handle_SupporterNotableRemoved(MessagePayload<SupporterNotableRemoved> obj)
    {
        if (!objectManager.TryGetIdWithLogging(obj.What.Clan, out var clanId)) return;
        if (!objectManager.TryGetIdWithLogging(obj.What.Hero, out var heroId)) return;

        // Update changed cache on all clients
        network.SendAll(new NetworkRemoveSupporterNotable(clanId, heroId));
    }

    private void Handle_RemoveSupporterNotable(MessagePayload<NetworkRemoveSupporterNotable> obj)
    {
        // Resolve on the game-loop thread, in queue order with deferred object-creation applies.
        GameThread.RunSafe(() =>
        {
            if (!objectManager.TryGetObjectWithLogging<Clan>(obj.What.ClanId, out var clan)) return;
            if (!objectManager.TryGetObjectWithLogging<Hero>(obj.What.HeroId, out var hero)) return;

            using (new AllowedThread())
            {
                clan.OnSupporterNotableRemoved(hero);
            }
        });
    }
}
