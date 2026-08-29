namespace GameInterface.Services.MapEvents.TroopSupply;

/// <summary>
/// Which of this client's own parties fill a wave first.
/// </summary>
public enum TroopDeploymentPreference
{
    /// <summary>
    /// Every party this client supplies gives up a share of each wave, in proportion to what it has left.
    /// </summary>
    AllPartyTroops = 0,

    /// <summary>
    /// The player's own party fills each wave first; the other lords, the garrison and the militia only
    /// contribute once it has nothing left to give.
    /// </summary>
    MyTroopsFirst = 1,
}

/// <summary>
/// This client's deployment preference. Local to the machine and never sent anywhere.
/// </summary>
/// <remarks>
/// Deliberately client-local, and it is safe to be so. What a client CONTRIBUTES to a wave is its slice of a
/// partition computed from each party's <c>SideOffset</c>, and those slices sum to the side's allocation
/// across every owner without the owners talking to each other. This preference only decides which of a
/// client's OWN parties fill that slice - never how big it is - so two players can hold different settings,
/// or both hold "my troops first", and neither can spend the other's capacity.
///
/// Read fresh on every wave rather than captured when a battle starts, so the deployment screen can change it
/// and have the next wave honour it.
/// </remarks>
public static class LocalTroopDeploymentPreference
{
    private static volatile TroopDeploymentPreference current = TroopDeploymentPreference.AllPartyTroops;

    /// <summary>
    /// The proportional split stays the default: "my troops first" deliberately re-creates the shape of a
    /// player fighting a wave alone while allied lords trickle in behind, which is a choice worth making
    /// explicitly rather than one to inherit by accident.
    /// </summary>
    public static TroopDeploymentPreference Current
    {
        get => current;
        set => current = value;
    }
}
