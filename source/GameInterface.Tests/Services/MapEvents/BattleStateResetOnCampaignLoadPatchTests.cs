using System;
using GameInterface.Services.MapEvents;
using GameInterface.Services.MapEvents.Patches;
using GameInterface.Services.MapEvents.TroopSupply;
using TaleWorlds.Core;
using Xunit;

namespace GameInterface.Tests.Services.MapEvents;

/// <summary>
/// A battle abandoned to the main menu never ends, so the process-global state it set up outlives its campaign.
/// Loading again must start from nothing.
/// </summary>
/// <remarks>
/// Reported symptom: after leaving an ongoing battle to the main menu and loading again, attacking produced a
/// battle with no troops and no Ready button. All three pieces reset here feed that: the gate left engaged keeps
/// vanilla troop supply suppressed and the deployment patches misdirected, while the reserve registry - keyed by
/// per-campaign map event ids that restart on load - can hand a dead battle's reserve to a new one.
/// </remarks>
public class BattleStateResetOnCampaignLoadPatchTests : IDisposable
{
    private const string AbandonedBattle = "MapEvent_Created_102626";

    public void Dispose()
    {
        BattleSpawnGate.EndBattle();
        CoopTroopSupplierRegistry.ClearAll();
    }

    [Fact]
    public void CampaignLoad_ReleasesASpawnGateLeftEngagedByAnAbandonedBattle()
    {
        BattleSpawnGate.BeginBattle(AbandonedBattle);
        Assert.True(BattleSpawnGate.IsCoopBattleActive);

        BattleStateResetOnCampaignLoadPatch.ResetBattleState();

        Assert.False(BattleSpawnGate.IsCoopBattleActive);
        Assert.Null(BattleSpawnGate.ActiveMapEventId);
    }

    [Fact]
    public void CampaignLoad_DropsTroopReservesLeftBehindByAnAbandonedBattle()
    {
        var supplier = new CoopTroopSupplier(
            AbandonedBattle,
            BattleSideEnum.Attacker,
            objectManager: null,
            agentBudget: null);
        CoopTroopSupplierRegistry.Register(supplier);
        Assert.Single(CoopTroopSupplierRegistry.GetSuppliers(AbandonedBattle));

        BattleStateResetOnCampaignLoadPatch.ResetBattleState();

        // A new campaign can reuse this id, and inheriting the old battle's supplier is how a fresh battle
        // ends up being fed by something that no longer exists.
        Assert.Empty(CoopTroopSupplierRegistry.GetSuppliers(AbandonedBattle));
    }

}
