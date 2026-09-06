using System.Collections.Generic;
using TaleWorlds.MountAndBlade;

namespace Missions.Agents;

/// <summary>
/// Puppets whose engine did not keep the weapon usage their owner reported (a couched lance: the puppet's engine
/// re-evaluates the couch every frame and falls back to the plain usage). <see cref="Handlers.AgentEquipmentApplier"/>
/// adds an agent when an applied usage does not read back; the movement handler re-asserts it every tick after the
/// native tick until it matches or the authoritative usage changes. Game thread only.
/// </summary>
public static class PuppetUsageWatch
{
    private static readonly HashSet<Agent> Watched = new HashSet<Agent>();

    public static int Count => Watched.Count;

    public static void Add(Agent agent)
    {
        if (agent != null) Watched.Add(agent);
    }

    public static void Remove(Agent agent)
    {
        Watched.Remove(agent);
    }

    public static void CopyTo(List<Agent> target)
    {
        target.AddRange(Watched);
    }

    public static void Clear()
    {
        Watched.Clear();
    }
}
