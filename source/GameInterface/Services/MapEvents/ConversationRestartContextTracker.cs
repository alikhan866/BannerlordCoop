using System;
using System.Collections.Generic;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.Encounters;
using TaleWorlds.CampaignSystem.Party;

namespace GameInterface.Services.MapEvents;

internal enum ConversationRestartDecision
{
    Apply,
    Duplicate,
    Stale,
}

internal interface IConversationRestartContextTracker
{
    string Capture(PlayerEncounter encounter);
    ConversationRestartDecision Consume(string requestId, PlayerEncounter currentEncounter, PartyBase defender, PartyBase attacker);
    void Remove(string requestId);
}

internal class ConversationRestartContextTracker : IConversationRestartContextTracker
{
    private const int MaxPendingRequests = 32;

    private readonly object sync = new object();
    private readonly Dictionary<string, RestartContext> pendingEncounters = new Dictionary<string, RestartContext>();
    private readonly Queue<string> requestOrder = new Queue<string>();

    private readonly struct RestartContext
    {
        public readonly PlayerEncounter Encounter;
        public readonly PartyBase AttackerParty;
        public readonly PartyBase DefenderParty;
        public readonly PartyBase EncounteredParty;
        public readonly object MapEvent;
        public readonly object Settlement;
        public readonly object PlayerSide;
        public readonly string MenuId;

        public RestartContext(PlayerEncounter encounter)
        {
            Encounter = encounter;
            AttackerParty = encounter?._attackerParty;
            DefenderParty = encounter?._defenderParty;
            EncounteredParty = encounter?._encounteredParty;
            MapEvent = encounter?._mapEvent;
            Settlement = encounter?.EncounterSettlementAux;
            PlayerSide = encounter?.PlayerSide;
            MenuId = CurrentMenuId;
        }

        public bool StillMatches(PlayerEncounter encounter)
        {
            return ReferenceEquals(Encounter, encounter) &&
                ReferenceEquals(AttackerParty, encounter?._attackerParty) &&
                ReferenceEquals(DefenderParty, encounter?._defenderParty) &&
                ReferenceEquals(EncounteredParty, encounter?._encounteredParty) &&
                ReferenceEquals(MapEvent, encounter?._mapEvent) &&
                ReferenceEquals(Settlement, encounter?.EncounterSettlementAux) &&
                Equals(PlayerSide, encounter?.PlayerSide) &&
                string.Equals(MenuId, CurrentMenuId, StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// Where the player is standing in the menus, as part of what "unchanged" means.
    /// </summary>
    /// <remarks>
    /// Everything else here describes the ENCOUNTER, and an approval that arrives after the player has walked
    /// deeper into that same encounter passes every one of those checks - the parties, the map event, the
    /// settlement and the side are all still identical. Applying it then restarts the encounter, and
    /// PlayerEncounter.Init ends by activating the encounter menu, which throws away wherever the player had
    /// got to.
    ///
    /// Measured at Syronea: "Break in to help the defenders" opened its menu correctly and a conversation
    /// approval, still in flight from the click that began the encounter, put the siege menu straight back.
    /// From the player's side the option simply did nothing, several times over, with nothing in the log.
    /// </remarks>
    private static string CurrentMenuId =>
        Campaign.Current?.CurrentMenuContext?.GameMenu?.StringId;

    public string Capture(PlayerEncounter encounter)
    {
        var requestId = Guid.NewGuid().ToString("N");

        lock (sync)
        {
            pendingEncounters[requestId] = new RestartContext(encounter);
            requestOrder.Enqueue(requestId);

            while (requestOrder.Count > MaxPendingRequests)
            {
                pendingEncounters.Remove(requestOrder.Dequeue());
            }
        }

        return requestId;
    }

    public ConversationRestartDecision Consume(
        string requestId,
        PlayerEncounter currentEncounter,
        PartyBase defender,
        PartyBase attacker)
    {
        // Already face to face with this party, so there is nothing to restart. Rebuilding the encounter
        // would produce the same encounter again - and PlayerEncounter.Init ends by activating the encounter
        // menu, which throws away wherever the player had navigated to inside it.
        //
        // This is not a rare race. An AI party that keeps deciding to engage a player has each attempt turned
        // into a conversation request by EncounterManagerPatches, so the approvals arrive in a steady stream:
        // measured at Syronea, one per second for as long as the player sat beside a besieger camp. Every one
        // of them was fresh, every one matched the captured context, and every one rebuilt the encounter -
        // which is why "Break in to help the defenders" opened its menu and lost it again a second later,
        // over and over, with nothing in the log to say why.
        if (GetOccupiedDecision(currentEncounter, defender, attacker) == ConversationRestartDecision.Duplicate)
        {
            Remove(requestId);
            return ConversationRestartDecision.Duplicate;
        }

        if (string.IsNullOrEmpty(requestId))
        {
            return currentEncounter == null
                ? ConversationRestartDecision.Apply
                : GetOccupiedDecision(currentEncounter, defender, attacker);
        }

        RestartContext capturedContext;
        lock (sync)
        {
            if (!pendingEncounters.TryGetValue(requestId, out capturedContext))
                return GetOccupiedDecision(currentEncounter, defender, attacker);

            pendingEncounters.Remove(requestId);
        }

        if (capturedContext.StillMatches(currentEncounter))
            return ConversationRestartDecision.Apply;

        return GetOccupiedDecision(currentEncounter, defender, attacker);
    }

    public void Remove(string requestId)
    {
        if (string.IsNullOrEmpty(requestId)) return;

        lock (sync)
        {
            pendingEncounters.Remove(requestId);
        }
    }

    private static ConversationRestartDecision GetOccupiedDecision(
        PlayerEncounter currentEncounter,
        PartyBase defender,
        PartyBase attacker)
    {
        var encounteredParty = currentEncounter?._encounteredParty;
        return encounteredParty != null && (encounteredParty == defender || encounteredParty == attacker)
            ? ConversationRestartDecision.Duplicate
            : ConversationRestartDecision.Stale;
    }
}
