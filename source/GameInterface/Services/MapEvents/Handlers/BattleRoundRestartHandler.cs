using Common;
using Common.Logging;
using Common.Messaging;
using GameInterface.Services.MapEvents.Messages;
using GameInterface.Services.ObjectManager;
using Serilog;
using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using TaleWorlds.CampaignSystem.MapEvents;
using TaleWorlds.Core;

namespace GameInterface.Services.MapEvents.Handlers;

/// <summary>
/// [Server] Watches live battles for reinforcements large enough to be worth re-forming the round around, and
/// asks for a restart when one arrives.
/// </summary>
/// <remarks>
/// The engine sizes a battle once. <c>InitWithSinglePhase</c> splits the battle size between the sides in
/// proportion to the totals it is handed at the start, and every wave afterwards is drawn against that split -
/// so a side that opened 100 against 300 keeps feeding at its opening rate however much relief arrives behind
/// it. Restarting the round is what re-derives the split; this decides when that is warranted.
///
/// Only the totals a round was actually SIZED FROM are remembered, not the totals at the last check. Comparing
/// against the last check would let a side grow without limit in small steps, each one under the threshold, and
/// never trigger anything - which is the same trickle the restart exists to fix.
/// </remarks>
internal class BattleRoundRestartHandler : IHandler
{
    private static readonly ILogger Logger = LogManager.GetLogger<BattleRoundRestartHandler>();

    /// <summary>
    /// The least time between two restarts of the same battle.
    /// </summary>
    /// <remarks>
    /// Reinforcements do not arrive in one lump - they trickle, and each arrival that clears the growth
    /// threshold is on its own terms a reason to re-size. Without a floor the battle re-forms every time a
    /// lord rides up: observed live restarting nine times, roughly once a minute, which is far more disruptive
    /// than the stale sizing it was fixing. One restart absorbs everyone who arrived since the last one, so
    /// waiting costs nothing but a minute of slightly-off proportions.
    /// </remarks>
    internal static readonly TimeSpan MinimumInterval = TimeSpan.FromMinutes(3);

    private sealed class SizedAt
    {
        public int Defender;
        public int Attacker;
        public DateTime LastRestartUtc = DateTime.MinValue;
    }

    /// <summary>Pure so the pacing rule is testable without a clock.</summary>
    internal static bool IsRestartDue(DateTime lastRestartUtc, DateTime nowUtc)
        => nowUtc - lastRestartUtc >= MinimumInterval;

    // Keyed by the event INSTANCE and weak, so a finished battle's entry evicts itself rather than growing a
    // ledger of every battle the session has ever fought.
    private readonly ConditionalWeakTable<MapEvent, SizedAt> sizedAt = new ConditionalWeakTable<MapEvent, SizedAt>();

    private readonly IMessageBroker messageBroker;
    private readonly IObjectManager objectManager;

    public BattleRoundRestartHandler(IMessageBroker messageBroker, IObjectManager objectManager)
    {
        this.messageBroker = messageBroker;
        this.objectManager = objectManager;

        messageBroker.Subscribe<MapEventInvolvedPartiesAdded>(Handle_InvolvedPartiesAdded);
    }

    public void Dispose()
    {
        messageBroker.Unsubscribe<MapEventInvolvedPartiesAdded>(Handle_InvolvedPartiesAdded);
    }

    private void Handle_InvolvedPartiesAdded(MessagePayload<MapEventInvolvedPartiesAdded> payload)
    {
        if (!ModInformation.IsServer) return;
        if (payload.Who is not MapEvent mapEvent) return;
        if (mapEvent.IsFinalized) return;

        var defenderNow = SideTotal(mapEvent, BattleSideEnum.Defender);
        var attackerNow = SideTotal(mapEvent, BattleSideEnum.Attacker);

        // First sighting is the baseline: this IS the sizing the round started from, so it can never be a
        // reason to restart.
        if (!sizedAt.TryGetValue(mapEvent, out var baseline))
        {
            sizedAt.Add(mapEvent, new SizedAt { Defender = defenderNow, Attacker = attackerNow });
            return;
        }

        if (!BattleRoundRestartPolicy.ShouldRestart(
                baseline.Defender, defenderNow, baseline.Attacker, attackerNow))
            return;

        // Re-form at most once every MinimumInterval. Re-baseline anyway, so the growth that was refused is
        // absorbed into the next comparison rather than re-triggering the moment the cooldown lapses.
        if (!IsRestartDue(baseline.LastRestartUtc, DateTime.UtcNow))
        {
            baseline.Defender = defenderNow;
            baseline.Attacker = attackerNow;
            return;
        }

        if (!objectManager.TryGetId(mapEvent, out var mapEventId)) return;

        baseline.LastRestartUtc = DateTime.UtcNow;

        // Re-baseline BEFORE announcing: the restart is now what the round is sized from, so further arrivals
        // are measured against the new figures rather than re-triggering against the old ones.
        baseline.Defender = defenderNow;
        baseline.Attacker = attackerNow;

        Logger.Information(
            "[BattleSync] Battle {MapEventId} reinforced to defender {Defender} / attacker {Attacker} (re-baselined; balancing handles the field)",
            mapEventId, defenderNow, attackerNow);

        // Superseded by BattleFieldBalancer. The restart got the proportions right by clearing the field and
        // re-forming, and every one of its defects came from the clearing: retreats mis-filed, the local player
        // dropped to spectator, the engine's wave racing the fielder, and health and horses reset. Trimming the
        // over-strength side achieves the same balance without any of that, so no restart is requested. Left
        // wired up rather than deleted so the comparison is one line to restore if the balancer disappoints.
        // messageBroker.Publish(this, new BattleRoundRestartRequested(mapEventId, defenderNow, attackerNow));
    }

    private static int SideTotal(MapEvent mapEvent, BattleSideEnum side)
        => mapEvent.GetMapEventSide(side)?.TroopCount ?? 0;
}
