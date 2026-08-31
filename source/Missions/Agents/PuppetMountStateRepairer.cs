using TaleWorlds.Core;
using TaleWorlds.MountAndBlade;

namespace Missions.Agents;

public interface IPuppetMountStateRepairer
{
    void PrepareForAiControl(Agent mount);

    void PreserveRiderlessPuppet(Agent mount);

    void RepairAfterRiderDeath(Agent mount);

    /// <summary>Guarantees every riderless mount carries a CommonAIComponent; returns how many needed it.</summary>
    int EnsureRiderlessMountsHaveAi(Mission mission);
}

public class PuppetMountStateRepairer : IPuppetMountStateRepairer
{
    /// <summary>
    /// Drops a puppet mount's existing <see cref="CommonAIComponent"/> immediately BEFORE its controller is
    /// set to <see cref="AgentControllerType.AI"/>.
    /// </summary>
    /// <remarks>
    /// This is de-duplication, not a teardown: the engine adds a fresh <see cref="CommonAIComponent"/> of
    /// its own whenever an active agent's controller becomes AI, so without this the mount would end up
    /// carrying two. It is therefore only ever correct paired with that controller change - callers must
    /// not use it to strip the component on its own, because a riderless mount without one crashes
    /// <c>HumanAIComponent.FindClosestMountAvailable</c> (see <see cref="PreserveRiderlessPuppet"/>).
    /// </remarks>
    public void PrepareForAiControl(Agent mount)
    {
        if (mount == null
            || mount.Controller == AgentControllerType.AI
            || mount.CommonAIComponent == null)
        {
            return;
        }

        mount.RemoveComponent(mount.CommonAIComponent);
    }

    /// <summary>
    /// Guarantees the engine's invariant that an active, riderless mount always carries a
    /// <see cref="CommonAIComponent"/> — whatever controls it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This deliberately does NOT filter on <see cref="Agent.Controller"/>. It used to require
    /// <c>AgentControllerType.None</c>, which crashed clients outright in cavalry battles.
    /// </para>
    /// <para>
    /// <c>HumanAIComponent.FindClosestMountAvailable</c> walks <c>Mission.Current.MountsWithoutRiders</c>
    /// and reads <c>agent2.CommonAIComponent.ReservedRiderAgentIndex</c> with NO null check, so the engine
    /// treats "riderless mount" and "has a CommonAIComponent" as the same thing. Meanwhile
    /// <see cref="PrepareForAiControl"/> REMOVES that component when handing a puppet mount to the AI. A
    /// horse that went through that path and later lost its rider therefore sat in MountsWithoutRiders with
    /// no component, and the controller filter here meant the repair skipped it — because by then its
    /// controller was AI, not None.
    /// </para>
    /// <para>
    /// The next AI agent looking for a horse then threw a NullReferenceException inside
    /// <c>Mission.TickAgentsAndTeams</c>, a native-driven callback, so the game thread died with nothing in
    /// the log and the client hung. Confirmed from a dump of a hung client: the pending exception was
    /// <c>NullReferenceException</c> at <c>FindClosestMountAvailable + 0x1e3</c>. It needs cavalry to
    /// happen at all, and grows likelier the longer a battle runs, because every mounted death adds another
    /// riderless horse.
    /// </para>
    /// </remarks>
    public void PreserveRiderlessPuppet(Agent mount)
    {
        if (mount == null
            || !mount.IsActive()
            || mount.RiderAgent != null)
        {
            return;
        }

        if (mount.CommonAIComponent == null)
            mount.AddComponent(new CommonAIComponent(mount));
    }

    /// <summary>
    /// Restores the engine's invariant across every currently riderless mount: each must carry a
    /// <see cref="CommonAIComponent"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A sweep rather than another call site, because the invariant is broken by a STATE TRANSITION that
    /// happens in many places: the engine removes an agent's <see cref="CommonAIComponent"/> whenever its
    /// controller leaves <see cref="AgentControllerType.AI"/>, so any code path that hands a mount back from
    /// AI control while it has no rider leaves it in the fatal state. Relying on each of those paths to
    /// remember a repair call is what let this through in the first place.
    /// </para>
    /// <para>
    /// The cost is bounded and small: this walks <c>MountsWithoutRiders</c>, which is exactly the collection
    /// the engine is about to walk anyway in <c>FindClosestMountAvailable</c>, and touches only entries that
    /// are already missing their component.
    /// </para>
    /// </remarks>
    /// <returns>How many mounts had to be repaired, for diagnostics.</returns>
    public int EnsureRiderlessMountsHaveAi(Mission mission)
    {
        if (mission?.MountsWithoutRiders == null) return 0;

        int repaired = 0;
        foreach (var entry in mission.MountsWithoutRiders)
        {
            Agent mount = entry.Key;
            if (mount == null || !mount.IsActive() || mount.RiderAgent != null) continue;
            if (mount.CommonAIComponent != null) continue;

            mount.AddComponent(new CommonAIComponent(mount));
            repaired++;
        }

        return repaired;
    }

    public void RepairAfterRiderDeath(Agent mount)
    {
        PreserveRiderlessPuppet(mount);
        if (mount?.CommonAIComponent == null
            || !mount.IsActive()
            || mount.RiderAgent != null)
        {
            return;
        }

        mount.CommonAIComponent.OnMountUnreserved();
    }
}
