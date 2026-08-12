using System.Collections.Generic;

namespace GameInterface.Services.MapEvents.Loot;

/// <summary>How much room the receiving party actually has, measured at apply time.</summary>
/// <remarks>
/// Measured on the server immediately before applying, never taken from the client. The player's screen
/// showed them a party that may since have changed - another player donating troops, a garrison handover -
/// and a limit the client asserted would be a limit the client could lie about.
/// </remarks>
public readonly struct BattleLootCapacity
{
    public readonly int MemberSlotsFree;
    public readonly int PrisonerSlotsFree;

    public BattleLootCapacity(int memberSlotsFree, int prisonerSlotsFree)
    {
        MemberSlotsFree = memberSlotsFree;
        PrisonerSlotsFree = prisonerSlotsFree;
    }

    /// <summary>Room enough that nothing will be clamped. Used where a limit does not apply.</summary>
    public static BattleLootCapacity Unlimited => new BattleLootCapacity(int.MaxValue, int.MaxValue);
}

/// <summary>A quantity of one thing to hand over.</summary>
public readonly struct BattleLootGrant
{
    public readonly BattleLootLineKind Kind;
    public readonly string ObjectId;
    public readonly string ModifierId;
    public readonly int Count;
    public readonly int WoundedNumber;
    public readonly int Xp;

    public BattleLootGrant(BattleLootLineKind kind, string objectId, string modifierId, int count, int woundedNumber, int xp)
    {
        Kind = kind;
        ObjectId = objectId;
        ModifierId = modifierId;
        Count = count;
        WoundedNumber = woundedNumber;
        Xp = xp;
    }
}

/// <summary>A hero to imprison or to set free, which are actions rather than roster edits.</summary>
public readonly struct BattleLootHeroAction
{
    public readonly string CharacterId;
    public readonly BattleLootDisposition Disposition;

    public BattleLootHeroAction(string characterId, BattleLootDisposition disposition)
    {
        CharacterId = characterId;
        Disposition = disposition;
    }
}

/// <summary>Exactly what the server intends to do, worked out before it touches anything.</summary>
public sealed class BattleLootApplicationPlan
{
    public List<BattleLootGrant> Items { get; } = new List<BattleLootGrant>();
    public List<BattleLootGrant> Members { get; } = new List<BattleLootGrant>();
    public List<BattleLootGrant> Prisoners { get; } = new List<BattleLootGrant>();
    public List<BattleLootHeroAction> Heroes { get; } = new List<BattleLootHeroAction>();

    /// <summary>Troops the party had no room for. Reported so the echo can tell the player the truth.</summary>
    public int ClampedMembers { get; internal set; }
    public int ClampedPrisoners { get; internal set; }

    /// <summary>Heroes that could not be imprisoned for lack of room, and so were not taken.</summary>
    public List<string> HeroesWithoutRoom { get; } = new List<string>();

    public bool IsEmpty =>
        Items.Count == 0 && Members.Count == 0 && Prisoners.Count == 0 && Heroes.Count == 0;
}

/// <summary>
/// Turns validated claims into a concrete plan, clamped to the room the party really has.
/// </summary>
/// <remarks>
/// Pure, for the same reason the validator is: the interesting decisions here are arithmetic and priority,
/// and they are worth testing without a campaign. Executing the plan against live game objects is a separate,
/// deliberately dull step.
///
/// Heroes are allocated prisoner room BEFORE ordinary troops. A captured lord is the thing a player actually
/// came for - ransom, leverage, a chance to recruit him - and losing one to a cart of looters filling the last
/// slot would be a poor trade the player never got to make. Ordinary prisoners are fungible; a named lord is not.
///
/// A hero that will not fit is NOT quietly released. Release is an action with consequences the player did not
/// ask for - captivity ends, relations move, the lord walks. Leaving him where he is means the server simply
/// did not hand him over, which is recoverable; releasing him is not.
/// </remarks>
public static class BattleLootPlanner
{
    public static BattleLootApplicationPlan Plan(
        IReadOnlyList<BattleLootResolvedClaim> claims,
        BattleLootCapacity capacity)
    {
        var plan = new BattleLootApplicationPlan();
        if (claims == null) return plan;

        int memberRoom = capacity.MemberSlotsFree < 0 ? 0 : capacity.MemberSlotsFree;
        int prisonerRoom = capacity.PrisonerSlotsFree < 0 ? 0 : capacity.PrisonerSlotsFree;

        // Pass one: heroes. They take priority for prisoner room, and releases cost nothing.
        foreach (var claim in claims)
        {
            var line = claim.Line;
            if (!line.IsHero) continue;

            if (claim.Disposition == BattleLootDisposition.Release)
            {
                plan.Heroes.Add(new BattleLootHeroAction(line.ObjectId, BattleLootDisposition.Release));
                continue;
            }

            // A hero joining as a MEMBER - a rescued companion - costs member room, not prisoner room.
            if (line.Kind == BattleLootLineKind.Member)
            {
                if (memberRoom <= 0)
                {
                    plan.HeroesWithoutRoom.Add(line.ObjectId);
                    continue;
                }

                memberRoom--;
                plan.Heroes.Add(new BattleLootHeroAction(line.ObjectId, BattleLootDisposition.Keep));
                continue;
            }

            if (prisonerRoom <= 0)
            {
                plan.HeroesWithoutRoom.Add(line.ObjectId);
                continue;
            }

            prisonerRoom--;
            plan.Heroes.Add(new BattleLootHeroAction(line.ObjectId, BattleLootDisposition.Keep));
        }

        // Pass two: everything ordinary, into whatever room the heroes left.
        foreach (var claim in claims)
        {
            var line = claim.Line;
            if (line.IsHero) continue;

            switch (line.Kind)
            {
                case BattleLootLineKind.Item:
                    // Items are limited by weight, which the party carries as a penalty rather than a refusal,
                    // so there is no slot count to clamp against here.
                    plan.Items.Add(Grant(line, claim.Count));
                    break;

                case BattleLootLineKind.Member:
                {
                    int given = claim.Count <= memberRoom ? claim.Count : memberRoom;
                    if (given > 0)
                    {
                        plan.Members.Add(Grant(line, given));
                        memberRoom -= given;
                    }

                    plan.ClampedMembers += claim.Count - given;
                    break;
                }

                case BattleLootLineKind.Prisoner:
                {
                    int given = claim.Count <= prisonerRoom ? claim.Count : prisonerRoom;
                    if (given > 0)
                    {
                        plan.Prisoners.Add(Grant(line, given));
                        prisonerRoom -= given;
                    }

                    plan.ClampedPrisoners += claim.Count - given;
                    break;
                }
            }
        }

        return plan;
    }

    /// <summary>
    /// Scales wounded and xp to the portion actually granted.
    /// </summary>
    /// <remarks>
    /// Handing over half a line must not hand over all of its wounded, or the party receives more casualties
    /// than troops - a roster with more wounded than present reports negative healthy troops, which is the
    /// same shape of corruption the duplicate-hero repair had to clamp.
    /// </remarks>
    private static BattleLootGrant Grant(BattleLootOfferLine line, int count)
    {
        int wounded = line.Count > 0 ? line.WoundedNumber * count / line.Count : 0;
        if (wounded > count) wounded = count;
        if (wounded < 0) wounded = 0;

        int xp = line.Count > 0 ? line.Xp * count / line.Count : 0;

        return new BattleLootGrant(line.Kind, line.ObjectId, line.ModifierId, count, wounded, xp);
    }
}
