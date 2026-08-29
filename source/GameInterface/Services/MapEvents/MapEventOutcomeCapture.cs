using Common;
using Common.Logging;
using GameInterface.Services.MapEvents.TroopSupply;
using GameInterface.Services.ObjectManager;
using Serilog;
using System;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.MapEvents;
using TaleWorlds.CampaignSystem.Party;

namespace GameInterface.Services.MapEvents;

/// <summary>
/// Plan 2 C8 - records what a battle actually cost, at the one moment the numbers still exist.
/// </summary>
/// <remarks>
/// WHY IT HANGS OFF FinalizeEventAux
/// The obvious hook, <c>MapEvent.FinalizeEvent</c>, is the vanilla wrapper - and coop does not use it. Battles
/// here are torn down through <c>FinalizeEventAux</c>, called directly by BattleFinalizeHandler, so a patch on
/// the wrapper captures precisely nothing and says so by reporting every battle as "never finalized". Measured
/// exactly that way before this moved.
///
/// It is also called from INSIDE the existing MapEventPatches prefix rather than from a second Harmony prefix
/// of its own. Two prefixes on one method with the same priority have no defined order, and the existing one
/// returns false on a client - which would skip a lower-ordered prefix entirely and lose the client-side
/// capture at random. Being one line inside the patch that is already there removes the race instead of
/// tuning it.
///
/// CAPTURED ON THE WAY IN, CONFIRMED ON THE WAY OUT
/// Finalize is what tears the rosters down, so reading them afterwards gives zeros and reports every battle as
/// bloodless. The separate "committed" mark records that the original actually ran, which is the difference
/// between "the defenders lost 40 men" and "the defenders lost 40 men and none of it reached the campaign".
/// That distinction is not hypothetical here: on a CLIENT the existing prefix deliberately skips the original,
/// so a client legitimately captures an outcome it never applies, and only the mark tells them apart.
///
/// IT MUST NEVER BREAK FINALIZE
/// Everything is wrapped and swallowed. A diagnostic that throws inside finalize would forfeit rosters and
/// destroy the campaign state it exists to describe; being silently absent is its only acceptable failure.
/// </remarks>
internal static class MapEventOutcomeCapture
{
    private static readonly ILogger Logger = LogManager.GetLogger(typeof(MapEventOutcomeCapture));

    public static void Capture(MapEvent mapEvent)
    {
        try
        {
            if (mapEvent == null || mapEvent.IsFinalized) return;
            BattleObservationLedger.RecordOutcome(Build(mapEvent));
        }
        catch (Exception failure)
        {
            Logger.Warning(failure, "[C8] Could not capture the outcome of a finalizing map event");
        }
    }

    public static void MarkCommitted(MapEvent mapEvent)
    {
        try
        {
            if (mapEvent == null) return;
            BattleObservationLedger.MarkOutcomeCommitted(Identify(mapEvent));
        }
        catch (Exception failure)
        {
            Logger.Warning(failure, "[C8] Could not confirm a finalized map event");
        }
    }

    private static BattleObservationLedger.BattleOutcome Build(MapEvent mapEvent)
    {
        var outcome = new BattleObservationLedger.BattleOutcome
        {
            MapEventId = Identify(mapEvent),
            EventType = mapEvent.EventType.ToString(),
            WinningSide = mapEvent.WinningSide.ToString(),
            BattleState = mapEvent.BattleState.ToString(),
            EndedByRetreat = mapEvent.EndedByRetreat,
            RetreatingSide = mapEvent.RetreatingSide.ToString(),
            WasEverInLootingPhase = mapEvent.WasEverInLootingPhase,
            HasWinner = mapEvent.HasWinner,
            Settlement = mapEvent.MapEventSettlement?.StringId ?? "none",
            DurationHours = Read(() => (CampaignTime.Now - mapEvent.BattleStartTime).ToHours, -1.0),
            CapturedUtc = DateTime.UtcNow,
        };

        AppendSide(outcome, mapEvent.DefenderSide, "Defender");
        AppendSide(outcome, mapEvent.AttackerSide, "Attacker");
        return outcome;
    }

    private static void AppendSide(
        BattleObservationLedger.BattleOutcome outcome, MapEventSide side, string label)
    {
        if (side?.Parties == null) return;

        foreach (var party in side.Parties)
        {
            if (party?.Party == null) continue;

            // Every field is read independently. One party with a torn-down roster must not cost the report
            // the other nine, which is what a single try around the whole loop would do.
            outcome.Parties.Add(new BattleObservationLedger.OutcomeParty
            {
                PartyId = Identify(party.Party),
                Name = Read(() => party.Party.Name?.ToString(), "<unnamed>"),
                Side = label,
                HealthyAtStart = Read(() => party.HealthyManCountAtStart, 0),
                Died = Read(() => party.DiedInBattle?.TotalManCount ?? 0, 0),
                Wounded = Read(() => party.WoundedInBattle?.TotalManCount ?? 0, 0),
                Routed = Read(() => party.RoutedInBattle?.TotalManCount ?? 0, 0),
                PrisonersToReceive = Read(() => party.RosterToReceiveLootPrisoners?.TotalManCount ?? 0, 0),
                LootMembersToReceive = Read(() => party.RosterToReceiveLootMembers?.TotalManCount ?? 0, 0),
                LootItemStacksToReceive = Read(() => party.RosterToReceiveLootItems?.Count ?? 0, 0),
                GainedRenown = Read(() => party.GainedRenown, 0f),
                GainedInfluence = Read(() => party.GainedInfluence, 0f),
                PlunderedGold = Read(() => party.PlunderedGold, 0),
                GoldLost = Read(() => party.GoldLost, 0),
                Contribution = Read(() => party.ContributionToBattle, 0),
            });
        }
    }

    private static string Identify(object gameObject)
    {
        try
        {
            if (ContainerProvider.TryResolve<IObjectManager>(out var objectManager) &&
                objectManager.TryGetId(gameObject, out string id))
                return id;
        }
        catch
        {
            // Falls through to the engine id below - an unregistered object is still worth naming.
        }

        return (gameObject as MapEvent)?.StringId
            ?? (gameObject as PartyBase)?.Id.ToString()
            ?? "<unregistered>";
    }

    private static T Read<T>(Func<T> read, T fallback)
    {
        try { return read(); }
        catch { return fallback; }
    }
}
