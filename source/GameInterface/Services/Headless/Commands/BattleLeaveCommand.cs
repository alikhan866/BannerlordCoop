using System;
using System.Collections.Generic;
using System.Linq;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.Conversation;
using TaleWorlds.CampaignSystem.Encounters;
using TaleWorlds.CampaignSystem.GameState;
using TaleWorlds.CampaignSystem.Party;
using TaleWorlds.Core;
using TaleWorlds.MountAndBlade;
using static TaleWorlds.Library.CommandLineFunctionality;

namespace GameInterface.Services.Headless.Commands;

/// <summary>
/// Gets a driven client out of a battle and back onto the map, so the next scenario can start.
/// </summary>
/// <remarks>
/// WHY THIS HAD TO EXIST
/// A rig that can enter a battle but not leave one gets exactly one test per launch. Worse, this codebase has a
/// battle that does not end on its own: a field battle whose losing side is wiped out leaves the client in the
/// mission indefinitely - measured at onField=0 defenders with reserveRemoved=12 for over three minutes, while
/// the SERVER had already destroyed the map event. Waiting for the mission to conclude is therefore not a
/// strategy, and every restart to escape one costs a six-minute rig cycle.
///
/// IT UNWINDS IN STAGES, AND IS MEANT TO BE CALLED AGAIN
/// Ending a mission is not instantaneous: vanilla needs ticks to tear the scene down, run its result screens
/// and hand control back to the map. A command cannot wait for that without stopping the very thread doing it.
/// So each call does whatever is possible RIGHT NOW, reports what it did and what is left, and is safe to
/// repeat. The caller loops leave / blocker.state until the blocker reads NONE.
///
/// THE DETACH IS LOCAL ONLY
/// A client left holding a map event the server has already destroyed cannot be freed by the MapEventSide
/// setter: that runs RemovePartyInternal, whose client patch finalizes the event when the removed party leads
/// its side, which broadcasts a finalize to the server. The back-reference is nulled directly instead - the
/// same local-detach PvPInteractionClientHandler.CloseEncounter uses, and for the same reason.
///
/// END, NOT RETREAT, BY DEFAULT
/// RetreatMission is the player's own "flee" and carries campaign consequences - morale, casualties, a recorded
/// retreat. A harness leaving a battle it was only observing must not silently pay those, so the neutral exit
/// is the default and retreat is asked for by name.
/// </remarks>
public static class BattleLeaveCommand
{
    [CommandLineArgumentFunction("leave", "coop.debug.battle")]
    public static string Leave(List<string> args)
    {
        bool retreat = args.Count == 1 && string.Equals(args[0], "retreat", StringComparison.OrdinalIgnoreCase);
        if (args.Count > 1 || (args.Count == 1 && !retreat))
            return "Usage: coop.debug.battle.leave [retreat]";

        return Execute(retreat);
    }

    /// <summary>The same unwind, for the blocker's <c>advance leave</c> when a mission is what is blocking.</summary>
    internal static string Execute(bool retreat)
    {
        if (Campaign.Current == null) return "BATTLE_LEAVE action=none reason=no-campaign";

        var steps = new List<string>();

        var mission = Mission.Current ?? MissionState.Current?.CurrentMission;
        if (mission == null)
        {
            steps.Add("mission=none");
        }
        else if (Safe(() => mission.MissionEnded, false) || Safe(() => mission.IsMissionEnding, false))
        {
            // Already unwinding. Calling EndMission again here re-enters the teardown vanilla is midway
            // through, so this reports the wait instead of forcing it.
            steps.Add("mission=alreadyEnding");
        }
        else
        {
            steps.Add(retreat
                ? "mission=retreat:" + Lower(Safe(() => { mission.RetreatMission(); return true; }, false))
                : "mission=end:" + Lower(Safe(() => { mission.EndMission(); return true; }, false)));
        }

        // Everything below only applies once the mission is gone. Doing it while the scene is still tearing
        // down fights vanilla's own unwind, which is what made the first version of this leave the client on a
        // black screen with no map state.
        if (Mission.Current != null || MissionState.Current != null)
        {
            steps.Add("encounter=deferred");
            steps.Add("mapEvent=deferred");
            return Describe(steps, "callAgainWhenMissionIsGone");
        }

        var mainParty = MobileParty.MainParty;

        if (PlayerEncounter.Current == null)
        {
            steps.Add("encounter=none");
        }
        else
        {
            // forcePlayerOutFromSettlement stays false: leaving a battle must not also eject a party that
            // legitimately walked into a settlement.
            steps.Add("encounter=finish:" +
                      Lower(Safe(() => { PlayerEncounter.Finish(false); return true; }, false)));
        }

        var stale = mainParty?.Party;
        if (stale?.MapEventSide == null)
        {
            steps.Add("mapEvent=none");
        }
        else
        {
            steps.Add("mapEvent=detached:" +
                      Lower(Safe(() => { stale._mapEventSide = null; return true; }, false)));
        }

        // Finishing the encounter re-opens the parley that started it - measured: a client that had just
        // left a battle came back reading CONVERSATION_CHOICE, and on a map conversation that is a dead end it
        // cannot select its way out of. Leaving means leaving, so the conversation goes with it.
        var conversation = Campaign.Current.ConversationManager;
        if (conversation != null && Safe(() => conversation.IsConversationInProgress, false))
            steps.Add("conversation=" + BlockerVisionCommands.ForceEndConversation(conversation).Split(' ')[0]);
        else
            steps.Add("conversation=none");

        if (GameStateManager.Current?.ActiveState is MapState mapState && Safe(() => mapState.AtMenu, false))
            steps.Add("menu=exited:" + Lower(Safe(() => { mapState.ExitMenuMode(); return true; }, false)));

        return Describe(steps, "done");
    }

    private static string Describe(IEnumerable<string> steps, string outcome)
    {
        var mainParty = MobileParty.MainParty;
        return $"BATTLE_LEAVE outcome={outcome} steps=[{string.Join(" ", steps.ToArray())}] " +
               $"nowMission={Lower(Mission.Current != null || MissionState.Current != null)} " +
               $"nowEncounter={Lower(PlayerEncounter.Current != null)} " +
               $"nowMapEvent={Safe(() => mainParty?.MapEvent?.EventType.ToString()) ?? "none"} " +
               $"nowMenu={Safe(() => Campaign.Current?.CurrentMenuContext?.GameMenu?.StringId) ?? "none"}";
    }

    private static T Safe<T>(Func<T> read, T fallback = default)
    {
        try { return read(); }
        catch { return fallback; }
    }

    private static string Lower(bool value) => value.ToString().ToLowerInvariant();
}
