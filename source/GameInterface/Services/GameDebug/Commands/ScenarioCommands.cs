using Common;
using Common.Logging;
using Common.Messaging;
using GameInterface.Services.Kingdoms;
using GameInterface.Services.MobileParties.Extensions;
using GameInterface.Services.MobileParties.Messages.Behavior;
using GameInterface.Services.ObjectManager;
using GameInterface.Services.ObjectManager.Extensions;
using GameInterface.Services.Players;
using GameInterface.Services.Players.Data;
using HarmonyLib;
using Helpers;
using System.Reflection;
using Serilog;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.Actions;
using TaleWorlds.CampaignSystem.CharacterDevelopment;
using TaleWorlds.CampaignSystem.Party;
using TaleWorlds.CampaignSystem.Settlements;
using TaleWorlds.Core;
using TaleWorlds.Localization;
using TaleWorlds.ObjectSystem;
using static TaleWorlds.Library.CommandLineFunctionality;

namespace GameInterface.Services.GameDebug.Commands;

/// <summary>
/// Named world scenarios: one command builds an entire starting situation so it can be rebuilt on demand
/// rather than reproduced by hand.
/// </summary>
/// <remarks>
/// Written as ONE server-side command rather than a script driving the granular debug commands. The world
/// rearrangement alone is hundreds of operations, and each one over the live-test channel is a round trip
/// that can half-finish; doing it in-process makes the scenario atomic from the caller's point of view and
/// lets it report what it actually did.
///
/// Every mutation goes through the same campaign action the equivalent hand command uses
/// (<c>ChangeOwnerOfSettlementAction</c>, <c>ChangeKingdomAction</c>, <c>DeclareWarAction</c>), so the
/// changes replicate to clients and persist through a save exactly as if a person had typed them.
/// </remarks>
internal class ScenarioCommands
{
    private static readonly ILogger Logger = LogManager.GetLogger<ScenarioCommands>();

    /// <summary>Bannerlord's highest clan tier.</summary>
    private const int MaxClanTier = 6;

    /// <summary>Comfortably past the tier-6 renown threshold, so the tier holds rather than drifting back.</summary>
    private const float MaxTierRenown = 50000f;

    private const int ScenarioGold = 500000;

    /// <summary>Ostican: a Vlandian port, so the players keep one town inside what becomes enemy territory.</summary>
    private const string OsticanId = "town_V8";

    private const string BattaniaId = "battania";
    private const string VlandiaId = "vlandia";
    private const string FianId = "battanian_fian_champion";
    private const string LegionaryId = "imperial_legionary";

    private const string DefaultAlifreezeController = "76561198876156674";
    private const string DefaultOmarController = "76561199074278663";

    private const string Usage =
        "Usage: coop.debug.scenario.build_qin [alifreezeControllerId] [omarControllerId]";

    /// <summary>
    /// Builds the "Qin" scenario: two players ruling one Battanian kingdom from Ostican, against a world
    /// that is entirely Vlandian and entirely at war with them.
    /// </summary>
    /// <remarks>
    /// Both players are resolved from the coop player registry by controller (platform) id, NOT from
    /// <c>Hero.MainHero</c>. On a dedicated server the main hero is a vestigial <c>Hero_main_hero</c> with no
    /// party at all - the registry is where the characters people actually play live, and it survives a save
    /// through the coop session sidecar, so this can run against a loaded save with nobody connected.
    /// </remarks>
    [CommandLineArgumentFunction("build_qin", "coop.debug.scenario")]
    public static string BuildQin(List<string> args)
    {
        if (!ModInformation.IsServer) return "Command can only be run on the server.";
        if (Campaign.Current == null) return "No campaign is loaded.";
        // "nodissolve" leaves the drained kingdoms standing instead of destroying them. Kept as a switch
        // because dissolving is the cosmetic half of the scenario - the world is Vlandia's and at war either
        // way - while being the only step that rewrites campaign structure rather than playing the game, so
        // it is the first thing to take away when a built world will not load.
        var positional = (args ?? new List<string>())
            .Where(a => !string.Equals(a, "nodissolve", StringComparison.OrdinalIgnoreCase)).ToList();
        bool dissolve = positional.Count == (args?.Count ?? 0);
        if (positional.Count > 2) return Usage;

        string alifreezeController = positional.Count >= 1 ? positional[0] : DefaultAlifreezeController;
        string omarController = positional.Count >= 2 ? positional[1] : DefaultOmarController;
        if (alifreezeController == omarController) return "The two players need different controller ids.\n" + Usage;

        if (!ContainerProvider.TryResolve<IObjectManager>(out var objectManager))
            return "Unable to resolve ObjectManager.";
        if (!ContainerProvider.TryResolve<IPlayerManager>(out var playerManager))
            return "Unable to resolve PlayerManager.";
        if (!ContainerProvider.TryResolve<IKingdomCreator>(out var kingdomCreator))
            return "Unable to resolve KingdomCreator.";

        var report = new StringBuilder();

        // An alive hero with no HeroDeveloper is the shape that kills a load: CampaignObjectManager.AfterLoad
        // runs Hero.AfterLoad over every hero, and that path dereferences the developer for the living ones.
        // Dead heroes legitimately have none, so only the living are counted. Probed after every phase because
        // the corruption is silent until the save is read back, and "which phase" is otherwise a bisect.
        int AliveWithNoDeveloper() => Campaign.Current.CampaignObjectManager
            .GetAllHeroes().Count(h => h != null && h.IsAlive && h.HeroDeveloper == null);
        void Probe(string phase) => report.AppendLine($"  probe[{phase}] aliveNoDeveloper={AliveWithNoDeveloper()}");


        // --- principals ---------------------------------------------------------------------------------
        if (!TryResolvePlayerHero(playerManager, objectManager, alifreezeController, out Hero alifreeze, out string error))
            return "Alifreeze: " + error;
        if (!TryResolvePlayerHero(playerManager, objectManager, omarController, out Hero omar, out error))
            return "Omar: " + error;

        Clan alifreezeClan = alifreeze.Clan;
        Clan omarClan = omar.Clan;
        if (alifreezeClan == null) return $"{alifreeze.StringId} has no clan.";
        if (omarClan == null) return $"{omar.StringId} has no clan.";
        if (alifreezeClan == omarClan) return "Both players are in one clan; the scenario needs two clans in one kingdom.";

        var battania = MBObjectManager.Instance.GetObject<CultureObject>(BattaniaId);
        if (battania == null) return $"Culture '{BattaniaId}' not found.";

        Kingdom vlandia = Kingdom.All.FirstOrDefault(k => k.StringId == VlandiaId);
        if (vlandia == null) return $"Kingdom '{VlandiaId}' not found; there is nobody to hand the world to.";

        Hero vlandiaOwner = vlandia.Leader ?? vlandia.RulingClan?.Leader;
        if (vlandiaOwner == null) return "Vlandia has no leader to receive the world.";

        Settlement ostican = Settlement.All.FirstOrDefault(s => s.StringId == OsticanId);
        if (ostican == null) return $"Settlement '{OsticanId}' (Ostican) not found.";

        var ourClans = new HashSet<Clan> { alifreezeClan, omarClan };

        report.AppendLine($"principals: Alifreeze={alifreeze.StringId}/{alifreezeClan.StringId} ({alifreezeController}) | " +
                          $"Omar={omar.StringId}/{omarClan.StringId} ({omarController})");

        // Who is dead BEFORE anything is touched. Restored at the end - see the repair step for why.
        var wasDead = new HashSet<Hero>(Campaign.Current.CampaignObjectManager
            .GetAllHeroes().Where(h => h != null && !h.IsAlive));

        Probe("before-registry");
        // --- drop stale player registrations ------------------------------------------------------------
        // A registration makes its party PLAYER-controlled, which means no AI drives it. Left behind from
        // earlier test clients, those parties just sit there. Deregistering returns the hero, party and clan
        // to the campaign as ordinary AI lords; the game objects themselves are untouched.
        var stale = playerManager.Players
            .Where(p => p.ControllerId != alifreezeController && p.ControllerId != omarController)
            .ToList();
        int dropped = stale.Count(playerManager.RemovePlayer);
        report.AppendLine($"registry: kept 2 players, dropped {dropped}/{stale.Count} stale registration(s)" +
                          (stale.Count > 0 ? " [" + string.Join(",", stale.Select(p => p.ControllerId)) + "]" : ""));

        Probe("before-identity");
        // --- identity -----------------------------------------------------------------------------------
        Rename(alifreeze, "Alifreeze");
        Rename(omar, "Omar");

        // The kingdom inherits clan.Culture (see KingdomDebugCommand.CreateKingdomCommand), so the clans have
        // to be Battanian BEFORE Qin is founded or it takes whatever culture they happened to carry.
        SetCulture(alifreeze, alifreezeClan, battania);
        SetCulture(omar, omarClan, battania);
        report.AppendLine($"identity: renamed, culture={battania.StringId} on both heroes and clans");

        Probe("before-tiers");
        // --- clan standing ------------------------------------------------------------------------------
        report.AppendLine("tiers: " + RaiseToMaxTier(alifreezeClan) + " | " + RaiseToMaxTier(omarClan));

        Probe("before-consolidate");
        // --- hand the world to Vlandia ------------------------------------------------------------------
        // BEFORE our clans leave their kingdoms, and that order is the whole point.
        //
        // Omar's clan RULES its kingdom. A ruling clan that walks out triggers a succession, and the
        // succession promoted a long-dead lord to lead the clan it picked - leaving a hero flagged alive with
        // no HeroDeveloper. Nothing complains at the time: the world plays on, saves happily, and only dies on
        // the way back in, where CampaignObjectManager.AfterLoad dereferences that developer for every living
        // hero and takes the whole load down with a bare NullReferenceException (it was lord_1_75, Thephilos).
        //
        // Emptying the other kingdoms first means there is no candidate left to promote when our clans go, so
        // the succession never runs. Repairing the hero afterwards would work too; not breaking him is better.
        // By moving CLANS rather than gifting settlements one by one. Gifting ~300 settlements to Derthert
        // would leave every other lord landless and make one hero personally own the map; moving their clans
        // instead means Vlandia holds everything through its members, and its lords keep their own fiefs.
        // Minor factions are mercenary companies and bandits are not a kingdom's to take, so both are left.
        int joined = 0;
        foreach (var clan in Clan.All.ToList())
        {
            if (ourClans.Contains(clan) || clan.IsEliminated) continue;
            if (clan.IsBanditFaction || clan.IsMinorFaction) continue;
            if (clan.Kingdom == vlandia) continue;

            LeaveKingdom(clan);
            ChangeKingdomAction.ApplyByJoinToKingdom(clan, vlandia);
            if (clan.Kingdom == vlandia) joined++;
        }

        Probe("before-kingdom");
        // --- kingdom ------------------------------------------------------------------------------------
        LeaveKingdom(alifreezeClan);
        LeaveKingdom(omarClan);

        Kingdom qin = Kingdom.All.FirstOrDefault(k => !k.IsEliminated && k.Name?.ToString() == "Qin");
        if (qin == null)
        {
            // Empty controller id on purpose: it makes the creation notification's settlement-restore steps a
            // no-op on the server and every client, which is what a debug-built kingdom wants.
            if (!kingdomCreator.TryCreateKingdom(alifreezeClan, "Qin", battania, string.Empty, out string qinId, out string createError))
                return report + $"\nUnable to create kingdom Qin: {createError}.";
            if (!objectManager.TryGetObject(qinId, out qin))
                return report + $"\nKingdom Qin was created ({qinId}) but could not be resolved back.";
        }

        if (omarClan.Kingdom != qin) ChangeKingdomAction.ApplyByJoinToKingdom(omarClan, qin);
        report.AppendLine($"kingdom: Qin ({qin.StringId}) ruled by {qin.Leader?.StringId}, clans=" +
                          string.Join(",", qin.Clans.Select(c => c.StringId)));

        // Our own fiefs go too - the players keep exactly one town, and both start holding a pile of them.
        int surrendered = 0;
        foreach (var settlement in Settlement.All.Where(s => s != ostican && ourClans.Contains(s.OwnerClan)).ToList())
        {
            ChangeOwnerOfSettlementAction.ApplyByGift(settlement, vlandiaOwner);
            surrendered++;
        }

        if (ostican.OwnerClan != alifreezeClan) ChangeOwnerOfSettlementAction.ApplyByGift(ostican, alifreeze);

        // The consolidation leaves the drained kingdoms standing as shells - no clans, no land, still able to
        // hold a war. One of them is the old player kingdom, which keeps naming a player as its ruler even
        // after he has moved to Qin, so a player ends up leading a ghost realm at war with his own.
        //
        // Membership is counted from the CLANS rather than from Kingdom.Clans: coop maintains a kingdom's
        // runtime collections itself (see KingdomRegistry.EnsureRuntimeCollections), and an emptied one can
        // still list members whose own Kingdom now points elsewhere. The clan is the side that was actually
        // moved, so it is the side to believe.
        int destroyed = 0;
        var undissolved = new List<string>();
        foreach (var kingdom in dissolve ? Kingdom.All.ToList() : new List<Kingdom>())
        {
            if (kingdom == qin || kingdom == vlandia || kingdom.IsEliminated) continue;

            // Every kingdom that is not Qin or Vlandia is empty by construction here - the consolidation above
            // moved the clans and the surrender moved the land - so this attempts them all rather than
            // re-deciding emptiness from state that has proven unreliable. An earlier revision guarded on
            // Settlements.Count and on the clan count, and the old player kingdom slipped through both while
            // being neither: its caches disagreed with the clans themselves. Attempt, then report the truth.
            kingdom._clans?.RemoveAll(c => c == null || c.Kingdom != kingdom);
            if (kingdom.RulingClan != null && kingdom.RulingClan.Kingdom != kingdom) kingdom._rulingClan = null;

            int members = Clan.All.Count(c => !c.IsEliminated && c.Kingdom == kingdom);
            int fiefs = kingdom.Settlements.Count;

            DestroyKingdomAction.Apply(kingdom);

            if (kingdom.IsEliminated) destroyed++;
            else undissolved.Add($"{kingdom.StringId}(members={members},fiefs={fiefs}," +
                                 $"clans={kingdom.Clans.Count},ruler={kingdom.RulingClan?.StringId ?? "none"})");
        }
        if (undissolved.Count > 0)
            report.AppendLine("WARNING undissolved kingdoms: " + string.Join(" ", undissolved));

        report.AppendLine($"world: {joined} clan(s) moved into Vlandia, {surrendered} of our fief(s) surrendered, " +
                          $"{(dissolve ? destroyed + " emptied kingdom(s) dissolved" : "dissolve skipped")}, Ostican held by {ostican.OwnerClan?.StringId}");

        Probe("before-outfit");
        // --- purse, troops, footing ---------------------------------------------------------------------
        report.AppendLine("Alifreeze: " + Outfit(alifreeze, FianId));
        report.AppendLine("Omar:      " + Outfit(omar, LegionaryId));

        report.AppendLine("staging:   " + PlaceOutside(alifreeze.PartyBelongedTo, ostican) +
                          " | " + PlaceOutside(omar.PartyBelongedTo, ostican));

        Probe("before-war");
        // --- war ----------------------------------------------------------------------------------------
        // Last, because a kingdom emptied by the step above can be eliminated by it, and declaring war on a
        // dead faction is noise. Whoever is still standing gets to be an enemy.
        var enemies = new List<string>();
        foreach (var kingdom in Kingdom.All.ToList())
        {
            if (kingdom == qin || kingdom.IsEliminated) continue;
            if (!kingdom.IsAtWarWith(qin)) DeclareWarAction.ApplyByDefault(kingdom, qin);
            if (kingdom.IsAtWarWith(qin)) enemies.Add(kingdom.StringId);
        }
        report.AppendLine($"war: Qin is at war with {enemies.Count} kingdom(s): {string.Join(",", enemies)}");

        // Put the resurrected back in their graves.
        //
        // Emptying a kingdom moves its clans out one at a time, and a departing RULING clan runs a succession
        // that can promote a long-dead lord to lead the clan it picks - flagging him alive again. A hero who
        // has been dead for years is missing the state a living one is assumed to have, and Hero.AfterLoad
        // dereferences exactly that for the living: the world saves without complaint and then dies on the way
        // back in, inside ClearChangedPerks, naming no hero (it was lord_1_75, Thephilos).
        //
        // No ordering avoids it - the kingdoms have to be emptied, and any order empties a ruling clan
        // eventually - and handing the revived hero a blank HeroDeveloper does not help, because that is not
        // the only thing a corpse is missing. So the prior truth is restored instead: whoever was dead when
        // this started is dead when it finishes. ChangeState rather than a kill action, which would run
        // another succession and revive somebody else.
        int reburied = 0;
        foreach (var hero in wasDead.Where(h => h.IsAlive).ToList())
        {
            hero.ChangeState(Hero.CharacterStates.Dead);
            reburied++;
        }
        if (reburied > 0) report.AppendLine($"repair: reburied {reburied} hero(es) a succession had revived");

        // Last word before the operator saves. A world in this state writes a .sav quite happily and only
        // fails on the way back in, so the check belongs HERE, where it can still say "do not save this",
        // rather than six minutes later in a load trace.
        int broken = AliveWithNoDeveloper();
        report.AppendLine(broken == 0
            ? "integrity: ok (no living hero is missing its HeroDeveloper)"
            : $"WARNING integrity: {broken} living hero(es) have no HeroDeveloper - this world will NOT reload. " +
              "Do not save it; rerun the scenario from a clean load.");

        Logger.Information("[Scenario] build_qin complete\n{Report}", report.ToString());
        return "QIN_SCENARIO_BUILT\n" + report;
    }

    /// <summary>
    /// Reports what the scenario would act on, changing nothing.
    /// </summary>
    /// <remarks>
    /// Exists because every failure mode of the builder is "it picked the wrong character": the registry is
    /// keyed by controller id, a save can carry registrations from clients that no longer exist, and the
    /// main hero is not one of them. Reading that back first is cheaper than a wrong world.
    /// </remarks>
    [CommandLineArgumentFunction("state", "coop.debug.scenario")]
    public static string State(List<string> args)
    {
        if (!ModInformation.IsServer) return "Command can only be run on the server.";
        if (Campaign.Current == null) return "No campaign is loaded.";
        if (!ContainerProvider.TryResolve<IObjectManager>(out var objectManager)) return "Unable to resolve ObjectManager.";
        if (!ContainerProvider.TryResolve<IPlayerManager>(out var playerManager)) return "Unable to resolve PlayerManager.";

        var sb = new StringBuilder();
        Hero main = Hero.MainHero;
        sb.AppendLine($"SCENARIO_STATE mainHero={main?.StringId ?? "none"} " +
                      $"name='{main?.Name}' clan={main?.Clan?.StringId ?? "none"} " +
                      $"party={main?.PartyBelongedTo?.StringId ?? "none"}");

        foreach (var player in playerManager.Players)
        {
            objectManager.TryGetObject(player.HeroId, out Hero hero);
            sb.AppendLine($"  player controller={player.ControllerId} hero={player.HeroId} " +
                          $"name='{hero?.Name}' clan={hero?.Clan?.StringId ?? "none"} tier={hero?.Clan?.Tier} " +
                          $"culture={hero?.Culture?.StringId ?? "none"} gold={hero?.Gold} " +
                          $"kingdom={hero?.Clan?.Kingdom?.StringId ?? "none"} " +
                          $"party={hero?.PartyBelongedTo?.StringId ?? "none"} " +
                          $"men={hero?.PartyBelongedTo?.MemberRoster?.TotalManCount} " +
                          $"fiefs={hero?.Clan?.Settlements?.Count}");
        }

        var ostican = Settlement.All.FirstOrDefault(s => s.StringId == OsticanId);
        sb.AppendLine($"  Ostican({OsticanId}) owner={ostican?.OwnerClan?.StringId ?? "none"} " +
                      $"kingdom={ostican?.OwnerClan?.Kingdom?.StringId ?? "none"}");
        sb.AppendLine($"  kingdoms alive={Kingdom.All.Count(k => !k.IsEliminated)} " +
                      $"[{string.Join(",", Kingdom.All.Where(k => !k.IsEliminated).Select(k => k.StringId + ":" + k.Settlements.Count))}]");
        return sb.ToString();
    }

    /// <summary>
    /// Brings every member of both player clans to one settlement.
    /// </summary>
    /// <remarks>
    /// After the world is handed to Vlandia, the clans' companions are left sitting in whatever towns they
    /// happened to be in - which are now all enemy ground, scattered across the map and unreachable. This
    /// walks them home.
    ///
    /// <c>ApplyForCharacterOnly</c> rather than <c>ApplyForParty</c>, because these heroes have no party of
    /// their own: they are loose members waiting in a settlement, and the party overload has nothing to move.
    /// A member who leads a party is moved by their party instead, so the party goes with them rather than
    /// being orphaned somewhere else. A captive is freed first - leaving one clan member in an enemy dungeon
    /// while gathering the rest is not "the clan is at Ostican".
    /// </remarks>
    /// <param name="args">optional settlement id; defaults to Ostican</param>
    [CommandLineArgumentFunction("gather_clan", "coop.debug.scenario")]
    public static string GatherClan(List<string> args)
    {
        if (!ModInformation.IsServer) return "Command can only be run on the server.";
        if (Campaign.Current == null) return "No campaign is loaded.";
        if (args != null && args.Count > 1) return "Usage: coop.debug.scenario.gather_clan [settlementId]";

        if (!ContainerProvider.TryResolve<IObjectManager>(out var objectManager))
            return "Unable to resolve ObjectManager.";
        if (!ContainerProvider.TryResolve<IPlayerManager>(out var playerManager))
            return "Unable to resolve PlayerManager.";

        string settlementId = args != null && args.Count == 1 ? args[0] : OsticanId;
        Settlement home = Settlement.All.FirstOrDefault(x => x.StringId == settlementId);
        if (home == null) return $"Settlement '{settlementId}' not found.";

        var players = new List<Hero>();
        foreach (var controllerId in new[] { DefaultAlifreezeController, DefaultOmarController })
            if (TryResolvePlayerHero(playerManager, objectManager, controllerId, out var playerHero, out _))
                players.Add(playerHero);
        if (players.Count == 0) return "No player heroes are registered in this world.";

        var clans = new HashSet<Clan>(players.Select(h => h.Clan).Where(c => c != null));
        var members = new HashSet<Hero>();
        foreach (var clan in clans)
        {
            foreach (var hero in clan.Heroes ?? Enumerable.Empty<Hero>()) members.Add(hero);
            foreach (var hero in clan.Companions ?? Enumerable.Empty<Hero>()) members.Add(hero);
        }
        foreach (var player in players) members.Remove(player);

        int moved = 0, freed = 0, withParty = 0, skipped = 0;
        var notes = new List<string>();

        foreach (var hero in members.ToList())
        {
            if (hero == null || !hero.IsAlive) { skipped++; continue; }

            if (hero.IsPrisoner)
            {
                EndCaptivityAction.ApplyByReleasedByChoice(hero);
                freed++;
                notes.Add($"freed {hero.StringId}");
            }

            try
            {
                MobileParty own = hero.PartyBelongedTo;
                if (own != null && own.LeaderHero == hero)
                {
                    // Move the party, not the person: leaving the party behind strands its troops.
                    if (own.CurrentSettlement != home) EnterSettlementAction.ApplyForParty(own, home);
                    withParty++;
                }
                else
                {
                    if (hero.CurrentSettlement != home) EnterSettlementAction.ApplyForCharacterOnly(hero, home);
                    moved++;
                }
            }
            catch (Exception e)
            {
                skipped++;
                notes.Add($"{hero.StringId} refused ({e.GetType().Name})");
                Logger.Warning(e, "[Scenario] could not bring {Hero} to {Settlement}", hero.StringId, home.StringId);
            }
        }

        int atHome = members.Count(h => h != null && h.IsAlive && h.CurrentSettlement == home);
        string report = $"GATHER_CLAN home={home.StringId}({home.Name}) members={members.Count} " +
                        $"moved={moved} withParty={withParty} freed={freed} skipped={skipped} atHome={atHome}";
        if (notes.Count > 0)
            report += Environment.NewLine + "  " + string.Join(Environment.NewLine + "  ", notes);

        Logger.Information("[Scenario] {Report}", report);
        return report;
    }

    /// <summary>
    /// Replays the per-hero step of <c>CampaignObjectManager.AfterLoad</c> and names any hero that throws.
    /// </summary>
    /// <remarks>
    /// A corrupted world does not announce itself: the campaign saves happily and only fails on the way back
    /// IN, inside <c>Hero.AfterLoad -&gt; ClearChangedPerks</c>, with a bare NullReferenceException and no clue
    /// which of ~1600 heroes carried it. Calling that step here, one hero at a time, turns "the save will not
    /// load" into "this hero, this null field", without the six-minute save-and-reload cycle in between.
    ///
    /// Diagnostic only. It runs the real method, so use it on a scratch world, not on one about to be saved.
    /// </remarks>
    [CommandLineArgumentFunction("audit", "coop.debug.scenario")]
    public static string Audit(List<string> args)
    {
        if (!ModInformation.IsServer) return "Command can only be run on the server.";
        if (Campaign.Current == null) return "No campaign is loaded.";

        var clearChangedPerks = AccessTools.Method(typeof(Hero), "ClearChangedPerks");
        if (clearChangedPerks == null) return "Hero.ClearChangedPerks not found; cannot replay the load step.";

        var heroes = Campaign.Current.CampaignObjectManager.GetAllHeroes().ToList();
        var sb = new StringBuilder();
        int threw = 0;

        foreach (var hero in heroes)
        {
            try
            {
                clearChangedPerks.Invoke(hero, null);
            }
            catch (Exception e)
            {
                threw++;
                if (threw > 8) continue;
                var inner = e is TargetInvocationException tie && tie.InnerException != null ? tie.InnerException : e;
                sb.AppendLine($"  THREW hero={hero.StringId} name='{hero.Name}' " +
                              $"clan={hero.Clan?.StringId ?? "NULL"} culture={hero.Culture?.StringId ?? "NULL"} " +
                              $"character={hero.CharacterObject?.StringId ?? "NULL"} " +
                              $"developer={(hero.HeroDeveloper == null ? "NULL" : "ok")} " +
                              $"alive={hero.IsAlive} occupation={hero.Occupation} :: {inner.GetType().Name}");
            }
        }

        // Broad null sweep too: the throw names one victim, but a scenario usually breaks a whole class of
        // heroes at once, and the counts say which class.
        int noClan = heroes.Count(h => h.Clan == null);
        int noCulture = heroes.Count(h => h.Culture == null);
        int noCharacter = heroes.Count(h => h.CharacterObject == null);
        int noDeveloper = heroes.Count(h => h.HeroDeveloper == null);

        return $"SCENARIO_AUDIT heroes={heroes.Count} threw={threw} " +
               $"nullClan={noClan} nullCulture={noCulture} nullCharacter={noCharacter} nullDeveloper={noDeveloper}\n" + sb;
    }

    private static bool TryResolvePlayerHero(
        IPlayerManager playerManager, IObjectManager objectManager,
        string controllerId, out Hero hero, out string error)
    {
        hero = null;
        error = null;

        if (!playerManager.TryGetPlayer(controllerId, out Player player))
        {
            error = $"no player registered for controller id {controllerId}. " +
                    "Registrations come from the save's coop session or from a client joining; " +
                    "run coop.debug.scenario.state to see which ids this world has.";
            return false;
        }

        if (!objectManager.TryGetObject(player.HeroId, out hero))
        {
            error = $"controller {controllerId} is registered but hero {player.HeroId} could not be resolved.";
            return false;
        }

        return true;
    }

    private static void Rename(Hero hero, string name)
    {
        var text = new TextObject(name);
        // Hero.SetName is Harmony-patched to publish HeroNameChanged and to run the original on the server,
        // so this both applies locally and replicates; no separate broadcast is needed.
        hero.SetName(text, text);
    }

    /// <summary>
    /// Sets hero and clan culture, and tells the clients about a change the game itself does not announce.
    /// </summary>
    /// <remarks>
    /// Nothing publishes <see cref="Heroes.Messages.CultureChanged"/> - the Culture setter is not patched -
    /// even though the server handler that forwards it and the client handler that applies it both exist. A
    /// save carries the culture regardless, so the publish matters only to a session watching it happen.
    /// </remarks>
    private static void SetCulture(Hero hero, Clan clan, CultureObject culture)
    {
        hero.Culture = culture;
        clan.Culture = culture;
        MessageBroker.Instance.Publish(typeof(ScenarioCommands), new Heroes.Messages.CultureChanged(culture, hero));
    }

    private static string RaiseToMaxTier(Clan clan)
    {
        // AddRenown rather than assigning Renown: the tier is recomputed as renown is ADDED, so a direct
        // assignment can leave a clan sitting at its old tier with a large renown behind it.
        float missing = MaxTierRenown - clan.Renown;
        if (missing > 0) clan.AddRenown(missing, false);

        // Renown is the honest way in, but the tier is what the scenario promises - so say it outright if
        // the model still disagrees.
        if (clan.Tier < MaxClanTier) clan._tier = MaxClanTier;

        return $"{clan.StringId} tier={clan.Tier} renown={(int)clan.Renown}";
    }

    private static void LeaveKingdom(Clan clan)
    {
        if (clan.Kingdom == null) return;
        if (clan.IsUnderMercenaryService) ChangeKingdomAction.ApplyByLeaveKingdomAsMercenary(clan);
        else ChangeKingdomAction.ApplyByLeaveKingdom(clan);
    }

    /// <summary>
    /// Gives a player their purse and fills their party to its size limit with one elite troop.
    /// </summary>
    /// <remarks>
    /// "Max" is the party's OWN limit rather than a chosen number: a roster over the limit bleeds men back
    /// out on the next party tick, so a larger figure would not survive the first day.
    /// </remarks>
    private static string Outfit(Hero hero, string troopId)
    {
        hero.Gold = ScenarioGold;
        hero.HitPoints = hero.MaxHitPoints;

        MobileParty party = hero.PartyBelongedTo;
        if (party == null) return $"gold={hero.Gold}, but {hero.StringId} has no party to fill";

        var troop = MBObjectManager.Instance.GetObject<CharacterObject>(troopId);
        if (troop == null) return $"gold={hero.Gold}, troop '{troopId}' not found";

        // Snapshot first: removing entries mutates the roster being read.
        foreach (var element in party.MemberRoster.GetTroopRoster().Where(e => e.Character?.IsHero == false).ToList())
            party.MemberRoster.AddToCounts(element.Character, -element.Number);

        int room = party.Party.PartySizeLimit - party.MemberRoster.TotalManCount;
        if (room > 0) party.MemberRoster.AddToCounts(troop, room);

        // A party with no food starves and haemorrhages men, which would quietly undo the roster above.
        var grain = MBObjectManager.Instance.GetObject<ItemObject>("grain");
        if (grain != null) party.ItemRoster.AddToCounts(grain, 600);

        // ...and morale, or the roster deserts before it is ever used. These parties come out of the source
        // save after months of starvation sitting at zero, and a party at zero morale sheds men on the first
        // tick - the army handed over above would quietly melt on day one. Morale itself is computed, so the
        // recent-events term is what can be given; the achieved value is reported rather than assumed.
        party.RecentEventsMorale = 100f;

        return $"gold={hero.Gold}, {party.MemberRoster.TotalManCount}/{party.Party.PartySizeLimit} men " +
               $"({troop.StringId}), morale={party.Morale:F1}, party={party.StringId}";
    }

    /// <summary>
    /// Stands a party on the map just outside a settlement's gate, and rebuilds its navigation.
    /// </summary>
    /// <remarks>
    /// Outside rather than ON the settlement: a party sharing a settlement's exact position reads as being
    /// there rather than standing next to it, and both parties dropped on one point overlap each other.
    /// The gate is the anchor because it is the spot the game itself spawns things at when they belong
    /// outside the walls - the deserter spawner uses the same one.
    ///
    /// Setting the position alone is what leaves a party frozen: its behaviour keeps the path it had for
    /// where it used to be. Holding, resetting navigation and announcing the change is the sequence
    /// coop.debug.fixture.set_position uses, and it is why that command does not freeze parties.
    /// </remarks>
    private static string PlaceOutside(MobileParty party, Settlement settlement)
    {
        if (party == null || settlement == null) return "no party to place";

        party.Position = OutsideGate(settlement);
        party.SetMoveModeHold();
        party.ResetNavigationToHold();
        MessageBroker.Instance.Publish(typeof(ScenarioCommands),
            new PartyBehaviorChangeAttempted(
                party,
                forcePosition: true,
                isCurrentlyAtSea: party.IsCurrentlyAtSea,
                resetMovementToHold: true));

        return $"{party.StringId}@{party.Position.X:F2},{party.Position.Y:F2} " +
               $"({party.Position.Distance(settlement.GatePosition):F2} from the gate)";
    }

    /// <summary>
    /// A point a land party can actually stand on, a short walk outside the settlement's gate.
    /// </summary>
    /// <remarks>
    /// Sampled rather than computed by offset, because an offset in a fixed direction can put a party in the
    /// sea or on unreachable ground - and a land party that believes it is standing on water cannot path
    /// anywhere, which is the frozen-party fault manufactured by the fixing rather than found by it. Each
    /// call samples afresh, so two parties staged here do not land on the same spot.
    /// </remarks>
    private static CampaignVec2 OutsideGate(Settlement settlement)
    {
        const float MinRadius = 1.0f;
        const float MaxRadius = 2.5f;

        CampaignVec2 gate = settlement.GatePosition;

        for (int attempt = 0; attempt < 15; attempt++)
        {
            CampaignVec2 candidate = NavigationHelper.FindReachablePointAroundPosition(
                gate, MobileParty.NavigationType.Default, MaxRadius, MinRadius, false);

            if (candidate.IsValid() &&
                NavigationHelper.IsPositionValidForNavigationType(candidate, MobileParty.NavigationType.Default))
                return candidate;
        }

        // Never silently drop the party somewhere it cannot be: standing on the gate is worse staging than
        // outside it, but it is a place a party can legitimately be, which a failed sample is not.
        return gate;
    }

}
