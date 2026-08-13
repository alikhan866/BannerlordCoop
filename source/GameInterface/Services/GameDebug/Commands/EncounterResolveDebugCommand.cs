using Common;
using System.Collections.Generic;
using System.Text;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.Encounters;
using TaleWorlds.CampaignSystem.Party;
using TaleWorlds.CampaignSystem.Roster;
using TaleWorlds.Core;
using static TaleWorlds.Library.CommandLineFunctionality;

namespace GameInterface.Services.GameDebug.Commands;

/// <summary>
/// C15 - resolve an encounter: attack, leave, send troops, join a side, break in.
/// </summary>
/// <remarks>
/// UNBLOCKED BY C12, FOR THE SAME REASON
/// This was carried as "menus are inert on a headless client". Encounter menus are game menus, so it rested on
/// the same premise C12 disproved - and it turns out not to need menus at all. Every action C15 names is a
/// public static on <c>PlayerEncounter</c> in TaleWorlds.CampaignSystem: StartBattle, JoinBattle,
/// EnterSettlement, Finish, InitSimulation. The menu option is the UI's way of reaching them, not the only way.
///
/// MAP-LEVEL, NOT MISSION-LEVEL
/// PlayerEncounter also offers StartAttackMission, StartVillageBattleMission and StartSiegeAmbushMission. Those
/// put the player into a SCENE and need a renderer, so they are deliberately not exposed here. The distinction
/// is the whole point of the headless client: the campaign outcome of a fight is testable render-free, the
/// fight itself is not. Anything wanting the mission path belongs on a rendered client via C53.
/// </remarks>
public class EncounterResolveDebugCommand
{
    [CommandLineArgumentFunction("state", "coop.debug.encounter")]
    public static string State(List<string> args)
    {
        if (args.Count != 0) return "Usage: coop.debug.encounter.state";
        if (Campaign.Current == null) return "ENCOUNTER_STATE campaign=false";
        if (!PlayerEncounter.IsActive) return "ENCOUNTER_STATE active=false";

        var result = new StringBuilder();
        result.AppendLine(
            $"ENCOUNTER_STATE active=true state={PlayerEncounter.Current?.EncounterState} " +
            $"with={PlayerEncounter.EncounteredMobileParty?.StringId ?? "none"} " +
            $"settlement={PlayerEncounter.EncounterSettlement?.StringId ?? "none"} " +
            $"insideSettlement={Lower(PlayerEncounter.InsideSettlement)}");
        result.AppendLine(
            $"battleState={PlayerEncounter.BattleState} " +
            $"playerIsAttacker={Lower(PlayerEncounter.PlayerIsAttacker)} " +
            $"playerIsDefender={Lower(PlayerEncounter.PlayerIsDefender)} " +
            $"joinedBattle={Lower(PlayerEncounter.Current?.IsJoinedBattle ?? false)} " +
            $"battle={(PlayerEncounter.Battle != null).ToString().ToLowerInvariant()}");
        return result.ToString();
    }

    [CommandLineArgumentFunction("attack", "coop.debug.encounter")]
    public static string Attack(List<string> args)
    {
        if (args.Count != 0) return "Usage: coop.debug.encounter.attack";
        if (!TryRequireEncounter(out var error)) return error;

        // StartBattle, not StartAttackMission: this creates the map-level battle a headless client can resolve.
        // StartAttackMission would load a scene and there is nothing to render it with.
        PlayerEncounter.StartBattle();

        return $"ENCOUNTER_ATTACK battle={(PlayerEncounter.Battle != null).ToString().ToLowerInvariant()} " +
               $"battleState={PlayerEncounter.BattleState}";
    }

    [CommandLineArgumentFunction("join_side", "coop.debug.encounter")]
    public static string JoinSide(List<string> args)
    {
        if (args.Count != 1) return "Usage: coop.debug.encounter.join_side <attacker|defender>";
        if (!TryRequireEncounter(out var error)) return error;

        BattleSideEnum side;
        switch (args[0].ToLowerInvariant())
        {
            case "attacker": side = BattleSideEnum.Attacker; break;
            case "defender": side = BattleSideEnum.Defender; break;
            default: return "The side must be 'attacker' or 'defender'.";
        }

        PlayerEncounter.JoinBattle(side);

        return $"ENCOUNTER_JOINED side={side} joinedBattle={Lower(PlayerEncounter.Current?.IsJoinedBattle ?? false)} " +
               $"playerSide={PlayerEncounter.Current?.PlayerSide}";
    }

    /// <summary>
    /// Sends the party's troops into a simulated resolution rather than a fought one.
    /// </summary>
    /// <remarks>
    /// Vanilla's "send troops" opens a selection screen. The campaign-level equivalent is InitSimulation with
    /// flattened rosters, which is what this uses - so the whole party is committed rather than a chosen subset.
    /// Selecting WHICH troops is the UI part and stays a rendered-client capability; committing them and
    /// resolving the outcome is what a headless scenario actually needs.
    /// </remarks>
    [CommandLineArgumentFunction("send_troops", "coop.debug.encounter")]
    public static string SendTroops(List<string> args)
    {
        if (args.Count != 0) return "Usage: coop.debug.encounter.send_troops";
        if (!TryRequireEncounter(out var error)) return error;

        var playerParty = MobileParty.MainParty;
        if (playerParty == null) return "There is no main party to send.";

        var opponent = PlayerEncounter.EncounteredParty;
        if (opponent == null) return "The encounter has no opposing party to simulate against.";

        PlayerEncounter.InitSimulation(
            playerParty.Party.MemberRoster.ToFlattenedRoster(),
            opponent.MemberRoster.ToFlattenedRoster());

        return $"ENCOUNTER_SEND_TROOPS sent={playerParty.MemberRoster.TotalManCount} " +
               $"against={opponent.MemberRoster.TotalManCount} " +
               $"simulation={(PlayerEncounter.CurrentBattleSimulation != null).ToString().ToLowerInvariant()}";
    }

    /// <summary>Break in - entering the settlement the encounter is standing at.</summary>
    [CommandLineArgumentFunction("break_in", "coop.debug.encounter")]
    public static string BreakIn(List<string> args)
    {
        if (args.Count != 0) return "Usage: coop.debug.encounter.break_in";
        if (!TryRequireEncounter(out var error)) return error;
        if (PlayerEncounter.EncounterSettlement == null)
            return "This encounter is not at a settlement, so there is nothing to break into.";
        if (PlayerEncounter.InsideSettlement)
            return $"ENCOUNTER_BREAK_IN alreadyInside=true settlement={PlayerEncounter.EncounterSettlement.StringId}";

        PlayerEncounter.EnterSettlement();

        return $"ENCOUNTER_BREAK_IN settlement={PlayerEncounter.EncounterSettlement?.StringId ?? "none"} " +
               $"insideSettlement={Lower(PlayerEncounter.InsideSettlement)}";
    }

    [CommandLineArgumentFunction("leave", "coop.debug.encounter")]
    public static string Leave(List<string> args)
    {
        if (args.Count != 0) return "Usage: coop.debug.encounter.leave";
        if (!PlayerEncounter.IsActive) return "ENCOUNTER_LEAVE wasActive=false";

        var was = PlayerEncounter.EncounteredMobileParty?.StringId ?? "none";

        // Finish rather than setting LeaveEncounter and hoping a tick notices. forcePlayerOutFromSettlement is
        // false so leaving an encounter does not also eject a party that legitimately went inside.
        PlayerEncounter.Finish(false);

        return $"ENCOUNTER_LEAVE wasActive=true wasWith={was} " +
               $"stillActive={Lower(PlayerEncounter.IsActive)}";
    }

    private static bool TryRequireEncounter(out string error)
    {
        error = null;
        if (Campaign.Current == null) { error = "No campaign is loaded."; return false; }
        if (!PlayerEncounter.IsActive) { error = "No encounter is active."; return false; }

        return true;
    }

    private static string Lower(bool value) => value.ToString().ToLowerInvariant();
}
