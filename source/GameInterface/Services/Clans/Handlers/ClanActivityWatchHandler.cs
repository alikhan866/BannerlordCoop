using Common;
using Common.Logging;
using Common.Messaging;
using GameInterface.Services.PlayerCaptivityService.Messages;
using Serilog;
using System;
using System.Collections.Generic;
using System.Linq;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.Party;

namespace GameInterface.Services.Clans.Handlers;

/// <summary>
/// Records, once an in-game hour, what a watched clan is actually doing.
/// </summary>
/// <remarks>
/// "That clan seems to be doing nothing" is a claim nobody can settle from inside the game: a clan with no
/// party on the map looks identical to one whose parties exist but never think, and to one whose parties think
/// but are held still. Those need different fixes, and by the time the difference matters the evening is over.
///
/// So this writes a line per member party per hour with the few facts that separate them - does the party
/// exist, is it active, has the AI been disabled on it, what behaviour did it choose, where is it going, and is
/// it moving. Read down a day of those and "were they active" answers itself.
///
/// Off unless asked for. A watch is set with <c>coop.debug.clan.watch &lt;name&gt;</c>, and the launcher's
/// PostStartCommands can re-apply it after every restart so a day's observation is not lost to a rebuild.
///
/// Server side only: the server owns AI parties, so it is the only machine whose answer means anything.
/// </remarks>
internal class ClanActivityWatchHandler : IHandler
{
    private static readonly ILogger Logger = LogManager.GetLogger<ClanActivityWatchHandler>();

    private static readonly HashSet<string> Watched = new(StringComparer.OrdinalIgnoreCase);
    private static readonly object Gate = new();

    private readonly IMessageBroker messageBroker;
    private CampaignTime nextReport = CampaignTime.Zero;

    public ClanActivityWatchHandler(IMessageBroker messageBroker)
    {
        this.messageBroker = messageBroker;
        messageBroker.Subscribe<CampaignTick>(Handle_CampaignTick);
    }

    public void Dispose() => messageBroker.Unsubscribe<CampaignTick>(Handle_CampaignTick);

    // ---- what is being watched ----------------------------------------------------------------------

    internal static bool Watch(string clanName)
    {
        if (string.IsNullOrWhiteSpace(clanName)) return false;

        lock (Gate) return Watched.Add(clanName.Trim());
    }

    internal static bool Unwatch(string clanName)
    {
        lock (Gate) return string.IsNullOrWhiteSpace(clanName) ? ClearAll() : Watched.Remove(clanName.Trim());
    }

    private static bool ClearAll()
    {
        var had = Watched.Count > 0;
        Watched.Clear();
        return had;
    }

    internal static IReadOnlyList<string> Watching
    {
        get { lock (Gate) return Watched.ToList(); }
    }

    // ---- reporting ----------------------------------------------------------------------------------

    private void Handle_CampaignTick(MessagePayload<CampaignTick> payload)
    {
        if (!ModInformation.IsServer) return;

        lock (Gate) { if (Watched.Count == 0) return; }

        var now = CampaignTime.Now;
        if (now < nextReport) return;

        nextReport = now + CampaignTime.Hours(1);

        ReportAll();
    }

    /// <summary>Writes the state of every watched clan right now.</summary>
    internal static void ReportAll()
    {
        List<string> names;
        lock (Gate) names = Watched.ToList();

        foreach (var name in names)
        {
            var clan = Campaign.Current?.Clans?.FirstOrDefault(
                candidate => string.Equals(candidate.Name?.ToString(), name, StringComparison.OrdinalIgnoreCase)
                          || string.Equals(candidate.StringId, name, StringComparison.OrdinalIgnoreCase));

            if (clan == null)
            {
                Logger.Information("[ClanWatch] {Clan}: no clan by that name exists", name);
                continue;
            }

            Report(clan);
        }
    }

    private static void Report(Clan clan)
    {
        Logger.Information(
            "[ClanWatch] {Clan} at {When}: tier={Tier} gold={Gold} influence={Influence} kingdom={Kingdom} " +
            "leader={Leader} fiefs={Fiefs} members={Members} parties={Parties} atWarWith={Wars}",
            clan.Name, CampaignTime.Now, clan.Tier, clan.Gold, (int)clan.Influence,
            clan.Kingdom?.Name?.ToString() ?? "<none>",
            clan.Leader?.Name?.ToString() ?? "<none>",
            (clan.Fiefs?.Count ?? 0),
            (clan.Heroes?.Count ?? 0),
            (clan.WarPartyComponents?.Count ?? 0),
            CountWars(clan));

        foreach (var hero in clan.Heroes ?? Enumerable.Empty<Hero>() as IEnumerable<Hero>)
        {
            if (hero == null) continue;

            var party = hero.PartyBelongedTo;

            if (party == null)
            {
                // The most common shape of "doing nothing": a lord with no party at all. Where they are
                // sitting says whether they are waiting to raise one or held somewhere they cannot.
                Logger.Information(
                    "[ClanWatch] {Clan}/{Hero}: NO PARTY (state={State} settlement={Settlement} prisonerOf={Captor})",
                    clan.Name, hero.Name, hero.HeroState,
                    hero.CurrentSettlement?.Name?.ToString() ?? "<none>",
                    hero.PartyBelongedToAsPrisoner?.Name?.ToString() ?? "<none>");

                continue;
            }

            Logger.Information(
                "[ClanWatch] {Clan}/{Hero}: party={Party} active={Active} aiDisabled={AiDisabled} " +
                "default={Default} shortTerm={ShortTerm} target={Target} settlement={Settlement} " +
                "army={Army} mapEvent={MapEvent} siege={Siege} men={Men} speed={Speed:0.00} food={Food:0.0}",
                clan.Name, hero.Name, party.StringId, party.IsActive, party.Ai?.IsDisabled,
                party.DefaultBehavior, party.ShortTermBehavior,
                Describe(party),
                party.CurrentSettlement?.Name?.ToString() ?? "<none>",
                party.Army?.Name?.ToString() ?? "<none>",
                party.MapEvent != null,
                party.SiegeEvent != null,
                party.MemberRoster?.TotalManCount ?? 0,
                party.Speed,
                party.Food);
        }
    }

    /// <summary>Where the party is headed, by whichever of the target fields is actually set.</summary>
    private static string Describe(MobileParty party)
    {
        if (party.TargetSettlement != null) return party.TargetSettlement.Name?.ToString();
        if (party.TargetParty != null) return party.TargetParty.Name?.ToString();
        if (party.ShortTermTargetSettlement != null) return party.ShortTermTargetSettlement.Name?.ToString();
        if (party.ShortTermTargetParty != null) return party.ShortTermTargetParty.Name?.ToString();

        return "<none>";
    }

    private static int CountWars(Clan clan)
    {
        if (clan?.MapFaction == null || Campaign.Current?.Factions == null) return 0;

        return Campaign.Current.Factions.Count(
            faction => faction != clan.MapFaction && clan.MapFaction.IsAtWarWith(faction));
    }
}
