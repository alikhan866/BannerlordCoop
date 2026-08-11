using ProtoBuf;
using TaleWorlds.CampaignSystem;
using TaleWorlds.Library;

namespace GameInterface.Services.MobileParties.Data;

/// <summary>
/// Contains the complete physical and behavior state needed to align a joining client.
/// </summary>
[ProtoContract(SkipConstructor = true)]
public struct MobilePartyJoinState
{
    [ProtoMember(1)]
    public PartyBehaviorUpdateData Behavior { get; set; }

    [ProtoMember(2)]
    public Vec2 EventPositionAdder { get; set; }

    [ProtoMember(3)]
    public Vec2 ArmyPositionAdder { get; set; }

    [ProtoMember(4)]
    public Vec2 Bearing { get; set; }

    [ProtoMember(5)]
    public bool IsCurrentlyAtSea { get; set; }

    [ProtoMember(6)]
    public CampaignVec2 EndPositionForNavigationTransition { get; set; }

    [ProtoMember(7)]
    public long NavigationTransitionStartTimeTicks { get; set; }

    [ProtoMember(8)]
    public bool StartTransitionNextFrameToExitFromPort { get; set; }

    [ProtoMember(9)]
    public bool ForceAiNoPathMode { get; set; }

    /// <summary>
    /// Whether the server has this party active.
    /// </summary>
    /// <remarks>
    /// Activation is world state, not cosmetic: <c>CampaignObjectManager.MobileParties</c> contains only
    /// ACTIVE parties, so a party the joiner holds but has deactivated is invisible to every count and
    /// every lookup that walks that collection. Measured live as baseline=1538 / client=1537 with nothing
    /// missing, nothing extra and nothing aliased - the one party was simply <c>IsActive=false</c> on the
    /// joiner while the server had it active, and the join was refused forever over it.
    /// </remarks>
    [ProtoMember(10)]
    public bool IsActive { get; set; }
}
