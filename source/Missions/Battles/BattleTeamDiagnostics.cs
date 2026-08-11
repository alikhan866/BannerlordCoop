using Common.Logging;
using Serilog;
using System.Collections.Generic;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.Party;
using TaleWorlds.Core;
using TaleWorlds.MountAndBlade;

namespace Missions.Battles;

/// <summary>
/// TEMP diagnostic: a few seconds into the battle (after spawns settle), dump the team/agent/player state once
/// so we can see why the player's side may be absent (no Defender team? team but no agents? hero spawned but
/// not the MainAgent?). Remove once the spawn/attach is solid.
/// </summary>
public class BattleTeamDiagnostics
{
    private static readonly ILogger Logger = LogManager.GetLogger<BattleTeamDiagnostics>();

    private float timer;
    private bool logged;

    // Separate, RECURRING sampler for the battle-size question. The one-shot dump above fires four seconds in
    // and is therefore useless for "how big did the field get later" - reading it as if it were current is
    // exactly the mistake that made an over-populated field look fine twice.
    private float sizeTimer;

    public void Tick(float dt)
    {
        // Guarded because this is a DIAGNOSTIC and must never be the reason a battle stops. It reads
        // DefenderActivePhase/AttackerActivePhase, which this codebase already documents as throwing when a
        // side has no phases, and it walks mission.Agents where an agent mid-removal is not guaranteed to
        // answer. An exception on the game tick does not skip a log line - it takes Game.OnTick down and
        // freezes the client, which is exactly how two evenings were lost.
        try
        {
            TickBattleSizeSample(dt);
        }
        catch (System.Exception e)
        {
            Logger.Error(e, "[BattleSize] sampler failed; diagnostics only, the battle is unaffected");
        }

        if (logged) return;
        timer += dt;
        if (timer < 4f) return;
        logged = true;

        var mission = Mission.Current;
        if (mission == null) return;

        foreach (var team in mission.Teams)
        {
            // List the HERO agents on each team — pinpoints where the player's own hero, the host hero and the
            // AI-lord heroes actually land (PlayerTeam = controllable by us; ally team = not).
            var heroes = new List<string>();
            foreach (var agent in team.ActiveAgents)
                if (agent.Character != null && agent.Character.IsHero)
                    heroes.Add(agent.Character.StringId);

            Logger.Information("[BattleDiag] Team side={Side} isPlayerTeam={IsPlayer} isPlayerAlly={IsAlly} activeAgents={Count} heroes=[{Heroes}]",
                team.Side, team == mission.PlayerTeam, team == mission.PlayerAllyTeam, team.ActiveAgents.Count, string.Join(", ", heroes));
        }

        var heroChar = Hero.MainHero?.CharacterObject;
        bool heroOnField = false;
        foreach (var agent in mission.Agents)
            if (agent.Character == heroChar)
            {
                heroOnField = true;
                Logger.Information("[BattleDiag] Player hero {Char} is on team side={Side} isPlayerTeam={IsPlayer} (controller={Ctrl})",
                    heroChar.StringId, agent.Team?.Side, agent.Team == mission.PlayerTeam, agent.Controller);
                break;
            }

        var spawnLogic = mission.GetMissionBehavior<DefaultBattleMissionAgentSpawnLogic>();
        Logger.Information("[BattleDiag] AttackerTeam={Atk} DefenderTeam={Def} PlayerTeam={Player} MainAgent={Main} heroOnField={Hero} spawnEnabled(Def={SDef},Atk={SAtk}) playerSide={PSide}",
            mission.AttackerTeam != null, mission.DefenderTeam != null,
            mission.PlayerTeam?.Side.ToString() ?? "null",
            mission.MainAgent != null,
            heroOnField,
            spawnLogic?.IsSideSpawnEnabled(BattleSideEnum.Defender),
            spawnLogic?.IsSideSpawnEnabled(BattleSideEnum.Attacker),
            PartyBase.MainParty?.Side);
    }

    /// <summary>
    /// Every few seconds, the three numbers needed to judge whether the battle size is being honoured: what the
    /// engine thinks the size is, how many agents are actually standing, and what the spawn phases still intend
    /// to put out. Logged together so they cannot be compared across different moments by mistake.
    /// </summary>
    private void TickBattleSizeSample(float dt)
    {
        sizeTimer += dt;
        if (sizeTimer < 5f) return;
        sizeTimer = 0f;

        var mission = Mission.Current;
        var spawnLogic = mission?.GetMissionBehavior<DefaultBattleMissionAgentSpawnLogic>();
        if (spawnLogic == null) return;

        int defenders = 0, attackers = 0;
        foreach (var agent in mission.Agents)
        {
            if (agent == null || !agent.IsActive() || !agent.IsHuman) continue;
            var side = agent.Team?.Side ?? BattleSideEnum.None;
            if (side == BattleSideEnum.Defender) defenders++;
            else if (side == BattleSideEnum.Attacker) attackers++;
        }

        var defPhase = spawnLogic.DefenderActivePhase;
        var atkPhase = spawnLogic.AttackerActivePhase;

        Logger.Information(
            "[BattleSize] battleSize={BattleSize} onField(def={Def},atk={Atk},total={Total}) initialSpawnOver={InitOver} " +
            "defPhase(total={DT},remaining={DR},initial={DI}) atkPhase(total={AT},remaining={AR},initial={AI})",
            spawnLogic.BattleSize, defenders, attackers, defenders + attackers, spawnLogic.IsInitialSpawnOver,
            defPhase?.TotalSpawnNumber, defPhase?.RemainingSpawnNumber, defPhase?.InitialSpawnNumber,
            atkPhase?.TotalSpawnNumber, atkPhase?.RemainingSpawnNumber, atkPhase?.InitialSpawnNumber);
    }
}
