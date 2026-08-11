using System;
using TaleWorlds.Core;

namespace GameInterface.Services.MapEvents;

/// <summary>
/// How many men each side may have standing at once, splitting the battle size between them in proportion to
/// their real strength - the same allocation the engine's own <c>Init</c> derives.
/// </summary>
/// <remarks>
/// Lives here, in GameInterface, rather than beside its callers in Missions, because three different things now
/// need the same answer and they must not disagree: the reinforcement fielder deciding whether it may spawn, the
/// field balancer deciding whether a side is over strength, and <c>CoopTroopSupplier</c> deciding how large a
/// wave the engine may actually be handed. Two of those live in Missions and one does not, so the shared rule
/// has to sit in the assembly both can see.
/// </remarks>
public readonly struct BattleSizeTargets
{
    public readonly int Defenders;
    public readonly int Attackers;

    public BattleSizeTargets(int defenders, int attackers)
    {
        Defenders = defenders;
        Attackers = attackers;
    }

    public int For(BattleSideEnum side)
        => side == BattleSideEnum.Defender ? Defenders : Attackers;

    public static BattleSizeTargets Calculate(int defenderTotal, int attackerTotal, int battleSize,
        float maximumSideRatio, float defenderAdvantageFactor)
    {
        int combined = defenderTotal + attackerTotal;
        if (combined <= 0 || battleSize <= 0)
            return new BattleSizeTargets(0, 0);

        float defenderRatio = (float)defenderTotal / combined;
        float attackerRatio = (float)attackerTotal / combined;
        defenderRatio = Math.Min(maximumSideRatio, defenderRatio * defenderAdvantageFactor);
        attackerRatio = 1f - defenderRatio;

        bool defenderIsLarger = defenderRatio >= attackerRatio;
        if (defenderIsLarger && defenderRatio > maximumSideRatio)
        {
            defenderRatio = maximumSideRatio;
            attackerRatio = 1f - maximumSideRatio;
        }
        else if (!defenderIsLarger && attackerRatio > maximumSideRatio)
        {
            attackerRatio = maximumSideRatio;
            defenderRatio = 1f - maximumSideRatio;
        }

        int defenderTarget;
        int attackerTarget;
        if (defenderRatio < attackerRatio)
        {
            defenderTarget = Math.Min((int)Math.Ceiling(defenderRatio * battleSize), defenderTotal);
            attackerTarget = Math.Min(battleSize - defenderTarget, attackerTotal);
        }
        else
        {
            attackerTarget = Math.Min((int)Math.Ceiling(attackerRatio * battleSize), attackerTotal);
            defenderTarget = Math.Min(battleSize - attackerTarget, defenderTotal);
        }

        return new BattleSizeTargets(defenderTarget, attackerTarget);
    }
}
