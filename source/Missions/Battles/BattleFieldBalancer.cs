using Common.Logging;
using GameInterface.Configuration;
using GameInterface.Services.Heroes.Extensions;
using GameInterface.Services.MapEvents;
using GameInterface.Services.MapEvents.TroopSupply;
using GameInterface.Services.MobileParties.Extensions;
using GameInterface.Services.ObjectManager;
using Serilog;
using System.Collections.Generic;
using System.Linq;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.MapEvents;
using TaleWorlds.Core;
using TaleWorlds.MountAndBlade;

namespace Missions.Battles;

/// <summary>
/// Keeps the number of troops actually standing on each side in line with what the battle is sized for, as
/// reinforcements change the balance of the fight.
/// </summary>
/// <remarks>
/// Replaces the round restart. The restart got the numbers right by clearing the field and re-forming, which
/// cost four separate defects: every cleared agent was filed as having RETREATED, the local player lost their
/// agent and dropped to the spectator view, the engine's opening wave and the reinforcement fielder both raced
/// the emptied field, and every troop came back at full health on a fresh horse.
///
/// All four came from clearing the field, none from re-sizing. So this trims instead: the under-strength side
/// fills through the ordinary reinforcement path, and the over-strength side stands men down until it is within
/// its share. Nobody is teleported, nobody is re-spawned, and the men who stay keep the health and the horse
/// they had.
///
/// Withdrawn troops go back to the reserve rather than out of the battle, so they return as casualties make
/// room - exactly like troops that had not been fielded yet.
/// </remarks>
public interface IBattleFieldBalancer
{
    void Tick(float dt);
}

public class BattleFieldBalancer : IBattleFieldBalancer
{
    private static readonly ILogger Logger = LogManager.GetLogger<BattleFieldBalancer>();

    /// <summary>
    /// How often the balance is re-checked.
    /// </summary>
    /// <remarks>
    /// Short, because this is currently CORRECTIVE rather than preventive: something can still put men on the
    /// field faster than the target allows, and this is what brings the side back down. Every second of
    /// interval is a second the field can sit over the limit - which is exactly what a scoreboard opened at the
    /// wrong moment shows. One second is frequent enough that an overshoot is momentary without re-walking
    /// every agent each frame.
    /// </remarks>
    internal const float IntervalSeconds = 1f;

    private readonly IObjectManager objectManager;
    private readonly ICoopMissionComponent coopMissionComponent;
    private readonly IBattleSession session;

    private float sinceLastCheck;

    public BattleFieldBalancer(IObjectManager objectManager, ICoopMissionComponent coopMissionComponent,
        IBattleSession session)
    {
        this.objectManager = objectManager;
        this.coopMissionComponent = coopMissionComponent;
        this.session = session;
    }

    public void Tick(float dt)
    {
        if (!ModConfigProvider.ModOptions.TrimFieldToBattleSize) return;

        sinceLastCheck += dt;
        if (sinceLastCheck < IntervalSeconds) return;
        sinceLastCheck = 0f;

        // Everything below runs on the game tick, where an escaping exception does not merely skip a balance
        // pass - it takes Game.OnTick with it and the client freezes. Balancing is an improvement on the
        // battle, never a prerequisite for it, so any failure is logged and the fight carries on unbalanced.
        try
        {
            TickCore();
        }
        catch (System.Exception e)
        {
            Logger.Error(e, "[BattleBalance] balance pass failed; the battle continues unbalanced");
        }
    }

    private void TickCore()
    {

        var mission = Mission.Current;
        var spawnLogic = mission?.GetMissionBehavior<DefaultBattleMissionAgentSpawnLogic>();
        if (spawnLogic == null) return;

        // Nothing to balance until the engine has finished putting out its opening wave - the field is not yet
        // what it is about to be, and trimming against a half-filled side would stand men down only for the
        // engine to spawn replacements a moment later.
        if (!spawnLogic.IsInitialSpawnOver) return;
        if (!session.HasInstance) return;
        if (!objectManager.TryGetObject<MapEvent>(session.InstanceId, out var mapEvent)) return;

        var settings = spawnLogic.SpawnSettings;
        var targets = ReinforcementFielder.RecoveryTargets.Calculate(
            mapEvent.GetMapEventSide(BattleSideEnum.Defender)?.TroopCount ?? 0,
            mapEvent.GetMapEventSide(BattleSideEnum.Attacker)?.TroopCount ?? 0,
            spawnLogic.BattleSize,
            settings.MaximumBattleSideRatio,
            settings.DefenderAdvantageFactor);

        TrimSide(mission, BattleSideEnum.Defender, targets.Defenders);
        TrimSide(mission, BattleSideEnum.Attacker, targets.Attackers);
    }

    private void TrimSide(Mission mission, BattleSideEnum side, int sideTarget)
    {
        CountTroops(mission, side, out var sideTroops, out var ownedTroops);

        var surplus = BattleFieldBalance.Surplus(sideTroops, sideTarget);
        if (surplus <= 0) return;

        var mine = BattleFieldBalance.OwnedShareOfSurplus(surplus, ownedTroops.Count, sideTroops);
        if (mine <= 0) return;

        // Ordered by distance from the enemy team's centre so trimming reads as the rear ranks falling back
        // rather than the front line evaporating mid-melee. Both the ordering and the withdrawal run inside a
        // guard: this executes on the game tick, and an exception here does not just skip a trim - it escapes
        // into Game.OnTick and wedges the loop. Seen live as a NullReferenceException at 02:25:32 followed by
        // "a blocking Run action was not processed by the game loop" and a frozen client.
        var withdrawn = 0;
        try
        {
            foreach (var agent in OrderedForWithdrawal(mission, ownedTroops, side).Take(mine))
            {
                Withdraw(agent);
                withdrawn++;
            }
        }
        catch (System.Exception e)
        {
            Logger.Error(e, "[BattleBalance] {Side}: trimming failed after {Withdrawn}; the battle continues", side, withdrawn);
        }

        Logger.Information(
            "[BattleBalance] {Side}: {OnField} troops on a target of {Target}; stood down {Withdrawn} of my {Owned}",
            side, sideTroops, sideTarget, withdrawn, ownedTroops.Count);
    }

    /// <summary>
    /// Counts the side's troops and picks out the ones this client may withdraw.
    /// </summary>
    /// <remarks>
    /// Players are excluded entirely - not counted against the target, and never returned as candidates. That
    /// is what lets a client join a full battle and still spawn: their hero adds to the field without
    /// displacing a troop, so a 200-v-200 fight becomes 200 v 201 rather than pushing one of their own men out.
    /// </remarks>
    private void CountTroops(Mission mission, BattleSideEnum side, out int sideTroops, out List<Agent> ownedTroops)
    {
        sideTroops = 0;
        ownedTroops = new List<Agent>();

        // Keep the registry id, not just the agent: a withdrawn man must be DE-REGISTERED as well as faded.
        // Fading alone leaves him in the registry, and AgentMovementHandler.PollMovement walks that registry
        // every tick building AgentEquipmentData - which calls Agent.GetPrimaryWieldedItemIndex() on a faded
        // agent and throws. That escaped into Game.OnTick and froze the client
        // (NullReferenceException at 02:45:41). BattleAuthorityMigrator has always paired the two calls;
        // this did not, and that omission was the whole bug.
        var ownedIds = new HashSet<Agent>();
        foreach (var info in coopMissionComponent.AgentRegistry.GetAgents(session.OwnControllerId))
        {
            if (info.Agent == null) continue;
            ownedIds.Add(info.Agent);
        }

        foreach (var agent in mission.Agents)
        {
            if (agent == null || !agent.IsActive() || !agent.IsHuman) continue;
            if ((agent.Team?.Side ?? BattleSideEnum.None) != side) continue;
            if (IsPlayerAgent(agent)) continue;

            sideTroops++;

            // Owning an agent is not the same as being entitled to withdraw it. As battle host this client
            // owns the AI it drives - and, after adoption, parties belonging to a player who joined late. Those
            // are somebody else's men: standing them down leaves that player in a battle with an army they
            // cannot command, which is exactly what a second client reported. Only troops from a party no
            // player owns may be withdrawn.
            if (!ownedIds.Contains(agent)) continue;
            if (agent == mission.MainAgent) continue;
            if (BelongsToAPlayersParty(agent)) continue;

            ownedTroops.Add(agent);
        }
    }

    /// <summary>Any player's hero, not just this client's - no player is ever stood down.</summary>
    internal static bool IsPlayerAgent(Agent agent)
    {
        if (agent == null) return false;
        if (Mission.Current != null && agent == Mission.Current.MainAgent) return true;

        return agent.Character is CharacterObject character
            && character.IsHero
            && character.HeroObject != null
            && character.HeroObject.IsPlayerHero();
    }

    private void Withdraw(Agent agent)
    {
        // The same scope the restart used: FadeOut is the engine's only "take this agent off the field" call
        // and it reports Routed, so without this every man stood down is recorded as having fled.
        using (BattleRoundRestartScope.Enter())
        {
            if (agent.HasMount) agent.MountAgent?.FadeOut(false, false);
            agent.FadeOut(false, true);
        }

        // No manual de-registration here. FadeOut reports Routed, which drives AgentRoutReporter's existing
        // path: broadcast the removal to peers, forget the casualty, de-register the agent. That is the
        // COMPLETE despawn, and reproducing half of it by hand is what left peers holding ownerless puppets.

        // Deliberately NOT handed back to the reserve. Doing that produced a feedback loop: the man returns to
        // the pool, the engine's reinforcement logic sees an available troop and room on the field, spawns him
        // again, the next pass trims him again. Observed live as the defender side climbing 105 -> 123 and
        // being cut back every second, which is the "troops rapidly spawning in" this was meant to prevent.
        //
        // Nothing is lost by leaving him out: the phases still hold hundreds of unspawned troops
        // (remaining=319 at the time of writing), so casualties are replaced from those instead. He is not a
        // casualty either - he survives the battle and returns to the party roster afterwards.
    }

    /// <summary>
    /// Rear ranks first, without asking the engine anything that can fail.
    /// </summary>
    /// <remarks>
    /// The obvious ordering - distance from the enemy team's median position - reads team and agent state that
    /// is not guaranteed valid for an agent mid-removal, and it threw on the game tick. Formation index is a
    /// plain field, and troops deeper in a formation are the ones further back, so it answers the same question
    /// without touching anything that can be null underneath us. Any agent that cannot answer at all sorts
    /// first, since it is in no state to be fighting.
    /// </remarks>
    private static System.Collections.Generic.IEnumerable<Agent> OrderedForWithdrawal(
        Mission mission, List<Agent> candidates, BattleSideEnum side)
        => candidates.OrderByDescending(SafeFormationDepth);

    private static int SafeFormationDepth(Agent agent)
    {
        try
        {
            return agent?.Formation?.CountOfUnits ?? int.MaxValue;
        }
        catch
        {
            return int.MaxValue;
        }
    }

    /// <summary>
    /// Whether this agent came from a party some player commands - theirs to lose, not ours to withdraw.
    /// </summary>
    /// <remarks>
    /// Checked against the party the troop was SUPPLIED from rather than against current agent ownership,
    /// because the two diverge: the battle host owns every AI party it drives, and adopts the parties of any
    /// player who joined after the reserves were built. Trimming by ownership alone therefore reaches into
    /// other players' armies.
    /// </remarks>
    private static bool BelongsToAPlayersParty(Agent agent)
    {
        try
        {
            if (agent.Origin is not CoopAgentOrigin origin) return false;

            var party = origin.Party?.MobileParty;
            return party != null && party.IsPlayerParty();
        }
        catch
        {
            // Unsure means leave it alone: wrongly withdrawing a player's troop is far worse than wrongly
            // keeping an AI one.
            return true;
        }
    }
}
