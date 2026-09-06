namespace Missions.Agents;

/// <summary>
/// Runtime switches for the mounted-sync fixes, so a before/after pair on the duel rig is one build with the fix
/// turned off for the "before" run (<c>coop.debug.movement.mounted_fixes off</c>). Both default to on; nothing in
/// normal play turns them off.
/// </summary>
public static class MountedSyncSwitches
{
    /// <summary>Lead the puppet horse by its owner's velocity over frame age, network delay and the ease lag (PVP-SYNC-PLAN 13.3).</summary>
    public static volatile bool LeadEnabled = true;

    /// <summary>Latch the wielded usage index against the couched-lance per-frame flicker before it goes on the wire (13.3).</summary>
    public static volatile bool UsageLatchEnabled = true;

    /// <summary>Re-assert a weapon usage the puppet's engine dropped (couched lance) every tick until it sticks (13.3).</summary>
    public static volatile bool UsageReassertEnabled = true;
}
