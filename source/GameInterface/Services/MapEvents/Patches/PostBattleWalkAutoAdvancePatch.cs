using Common.Logging;
using HarmonyLib;
using Serilog;
using System.Collections.Generic;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.Encounters;
using TaleWorlds.CampaignSystem.GameState;
using TaleWorlds.CampaignSystem.MapEvents;
using TaleWorlds.CampaignSystem.Party;
using TaleWorlds.CampaignSystem.Roster;
using TaleWorlds.Core;
using TaleWorlds.MountAndBlade;

namespace GameInterface.Services.MapEvents.Patches;

/// <summary>
/// Carries the post-battle walk from one screen to the next, so the player clicks "Capture the enemy" once
/// instead of once per lord and once per loot screen.
/// </summary>
/// <remarks>
/// Every blocking step of the walk - a lord's conversation, the troop and prisoner screen, the item screen -
/// ends by setting <c>_stateHandled</c> and returning. Something has to call <see cref="PlayerEncounter.Update"/>
/// again once the player has finished with it. Single-player resumes through the encounter menu's
/// <c>on_init</c>, which ends in exactly that call - but <c>on_init</c> only runs when something calls
/// SwitchToMenu or ActivateGameMenu, and in co-op the encounter menu is already up when the walk begins and
/// nothing ever switches to it again. So that resume point never comes round, and the menu's remaining option
/// became a manual "next step" button. Measured live: eight clicks for one battle.
///
/// Driving it from the menu tick is easy; knowing whether the previous step has FINISHED is the whole problem,
/// and two earlier attempts got it wrong in opposite directions - one re-opened the same lord's conversation
/// every frame, the next refused to resume at all. The answer is not one rule but a distinction:
///
/// A step that opens a SCREEN pushes a game state - PartyScreenHelper, InventoryScreenHelper and
/// PortStateHelper all end in GameStateManager.PushState, which activates synchronously. MapState stops
/// ticking, so this patch is not called at all while the screen is up. If the tick runs, the screen is closed.
/// Nothing more is needed, and anything more is harmful: DoLootMembersAndPrisonersOfParty advances the state
/// to LootInventory ITSELF before returning, so a "has anything changed" test sees the same thing before the
/// screen opens and after it closes, and blocks the resume forever.
///
/// A step that opens a CONVERSATION pushes nothing. CampaignMapConversation.OpenConversation leaves MapState
/// active and raises no Mission, which is why the walk could re-open the same lord frame after frame. But each
/// of those steps works off a QUEUE, and the queue is exact: the lord stays on it until he answers. So for
/// those states, and only those, the walk refuses to drive again until the queue moves.
///
/// Queue per state: PlayerVictory works off <c>_helpedHeroes</c>, CaptureHeroes off <c>_capturedHeroes</c>,
/// FreeHeroes off <c>_capturedAlreadyPrisonerHeroes</c> - though that last one is not measured by length,
/// because vanilla does not shorten it; see <see cref="PendingRescues"/>.
///
/// Deliberately NOT used: ConversationManager's flags. IsConversationInProgress is false during the window
/// NeedsToActivateForMapConversation exists to describe, and a flag that fails to clear stops the walk dead -
/// which is the more likely of the two ways this can fail. The queue needs no such trust.
///
/// It stays dormant until the player takes the option once, and is armed against THAT encounter: the decision
/// to walk the post-battle screens stays theirs, and a later encounter starts from the same place rather than
/// inheriting a walk it never asked for.
/// </remarks>
[HarmonyPatch(typeof(MapState), "OnMenuModeTick")]
internal static class PostBattleWalkAutoAdvancePatch
{
    private static readonly ILogger Logger = LogManager.GetLogger(typeof(PostBattleWalkAutoAdvancePatch));

    private static PlayerEncounter armed;
    private static string lastLogged;
    private static PlayerEncounterState waitingIn;
    private static int? waitingOn;

    /// <summary>
    /// Starts the walk on the player's own click, and arms this to carry it the rest of the way.
    /// </summary>
    internal static void StartWalk()
    {
        armed = PlayerEncounter.Current;
        lastLogged = null;
        waitingOn = null;

        if (armed != null) Drive(armed, armed.EncounterState);
    }

    [HarmonyPostfix]
    private static void Postfix()
    {
        if (armed == null) return;

        var encounter = PlayerEncounter.Current;
        if (encounter == null || !ReferenceEquals(encounter, armed))
        {
            // Finished, or replaced by a different encounter entirely. Either way this one is done with.
            armed = null;
            return;
        }

        // A live map event means a battle is still being fought, and vanilla owns the encounter while it is.
        if (MapEvent.PlayerMapEvent != null) return;

        // Both belt and braces. A pushed state stops this tick being called at all, and a conversation held
        // inside a settlement runs as a Mission rather than on the map - neither should ever be driven under.
        if (Mission.Current != null) return;
        if (!(Game.Current?.GameStateManager?.ActiveState is MapState)) return;

        var state = encounter.EncounterState;
        if (!IsPostBattle(state)) return;

        // Waiting on somebody to answer, and they have not. Driving now would call them back.
        var queue = QueueFor(encounter, state);
        if (queue != null && state == waitingIn && queue == waitingOn) return;

        // One line per thing actually dealt with - each lord who answers moves the queue - and none at all
        // while the walk is waiting. A drive that repeated without the queue moving would be silent under a
        // per-state line, which is exactly how an earlier misfire hid itself.
        var mark = state + "/" + (queue?.ToString() ?? "-");
        if (mark != lastLogged)
        {
            Logger.Information("[Encounter] Carrying the post-battle walk on from {State} (waiting on {Queued})",
                state, queue?.ToString() ?? "nobody");
            lastLogged = mark;
        }

        Drive(encounter, state);
    }

    private static void Drive(PlayerEncounter encounter, PlayerEncounterState state)
    {
        // End closes the encounter, so this is the last step either way. Disarming first means that even if
        // closing it leaves PlayerEncounter.Current standing, the walk is not asked to close it again on
        // every following frame.
        if (state == PlayerEncounterState.End) armed = null;

        PlayerEncounter.Update();

        // Where it came to rest, which is not where it set off from: the first call into CaptureHeroes is
        // what lifts the heroes out of the loot roster, so the queue goes from "not yet built" to six waiting
        // lords as a RESULT of driving. Reading the queue beforehand would score that as somebody having
        // answered, and the next tick would call the first lord straight back.
        var settled = PlayerEncounter.Current;
        if (settled == null)
        {
            waitingOn = null;
            return;
        }

        waitingIn = settled.EncounterState;
        waitingOn = QueueFor(settled, waitingIn);
    }

    /// <summary>
    /// How many people this state is still waiting on, or null when it does not wait on people at all.
    /// </summary>
    /// <remarks>
    /// Null is the important half. A state that opens a screen must never be measured this way - its step
    /// moves the encounter on by itself, so the measurement cannot change and the walk would never resume.
    /// </remarks>
    private static int? QueueFor(PlayerEncounter encounter, PlayerEncounterState state)
    {
        switch (state)
        {
            // Thanking the lords who fought alongside you, one conversation each.
            case PlayerEncounterState.PlayerVictory:
                return encounter._helpedHeroes?.Count ?? -1;

            // "You are my prisoner now", one conversation each, popped as each answers.
            case PlayerEncounterState.CaptureHeroes:
                return encounter._capturedHeroes?.Count ?? -1;

            case PlayerEncounterState.FreeHeroes:
                return PendingRescues(encounter);

            default:
                return null;
        }
    }

    /// <summary>
    /// How many rescued lords still need dealing with, by vanilla's own test rather than by list length.
    /// </summary>
    /// <remarks>
    /// <c>DoFreeOrCapturePrisonerHeroes</c> does not shorten its list when a lord is dealt with - it re-asks
    /// <c>AnyQ(list, h =&gt; h.IsPrisoner &amp;&amp; not already mine)</c> and stops when nobody answers to that any
    /// more. Counting the list itself would never change, and the walk would sit at this state forever.
    ///
    /// Both endings move this number: releasing him clears IsPrisoner, keeping him makes the main party his
    /// captor. On a client that can do neither - captivity is the server's to change - the number holds still
    /// and the walk stops here rather than looping, which costs the player one click and never a lockup.
    /// </remarks>
    private static int PendingRescues(PlayerEncounter encounter)
    {
        List<TroopRosterElement> rescued = encounter._capturedAlreadyPrisonerHeroes;
        if (rescued == null) return -1;

        int pending = 0;
        foreach (var element in rescued)
        {
            var hero = element.Character?.HeroObject;
            if (hero == null) continue;

            if (hero.IsPrisoner && hero.PartyBelongedToAsPrisoner != PartyBase.MainParty) pending++;
        }

        return pending;
    }

    /// <summary>
    /// The states of the walk, in the order <see cref="PlayerEncounterState"/> lists them. Deliberately not
    /// the states before it: Begin and Wait have no step to run once the server has finalized the battle, so
    /// driving them would repeat every frame and get nowhere.
    /// </summary>
    private static bool IsPostBattle(PlayerEncounterState state)
    {
        switch (state)
        {
            case PlayerEncounterState.PlayerVictory:
            case PlayerEncounterState.PlayerTotalDefeat:
            case PlayerEncounterState.CaptureHeroes:
            case PlayerEncounterState.FreeHeroes:
            case PlayerEncounterState.LootParty:
            case PlayerEncounterState.LootInventory:
            case PlayerEncounterState.LootShips:
            case PlayerEncounterState.End:
                return true;
            default:
                return false;
        }
    }
}
