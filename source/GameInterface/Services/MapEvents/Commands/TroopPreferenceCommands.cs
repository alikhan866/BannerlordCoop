using GameInterface.Services.MapEvents.TroopSupply;
using System;
using System.Collections.Generic;
using static TaleWorlds.Library.CommandLineFunctionality;

namespace GameInterface.Services.MapEvents.Commands;

/// <summary>
/// Reads and sets this client's troop deployment preference.
/// </summary>
/// <remarks>
/// Exists so the behaviour can be exercised before the deployment-screen buttons are built - the mechanism
/// and the UI are separate risks, and proving the mechanism first means a UI bug and a supply bug can never
/// be confused for each other.
///
/// Deliberately NOT server-only. The preference is local to whichever process runs it and is never sent
/// anywhere, so on a two-client rig it has to be set on each client independently - which is also exactly the
/// behaviour being tested.
/// </remarks>
public class TroopPreferenceCommands
{
    private const string Usage =
        "Usage: coop.debug.battle.troop_preference [all|mine]  " +
        "(all = every party shares each wave; mine = own party fills it first)";

    [CommandLineArgumentFunction("troop_preference", "coop.debug.battle")]
    public static string TroopPreference(List<string> args)
    {
        if (args == null || args.Count == 0) return Describe();
        if (args.Count > 1) return Usage;

        string requested = args[0];
        if (string.Equals(requested, "all", StringComparison.OrdinalIgnoreCase))
        {
            LocalTroopDeploymentPreference.Current = TroopDeploymentPreference.AllPartyTroops;
            return Describe();
        }
        if (string.Equals(requested, "mine", StringComparison.OrdinalIgnoreCase))
        {
            LocalTroopDeploymentPreference.Current = TroopDeploymentPreference.MyTroopsFirst;
            return Describe();
        }

        return $"'{requested}' is not a preference.\n{Usage}";
    }

    private static string Describe()
    {
        TroopDeploymentPreference current = LocalTroopDeploymentPreference.Current;
        string meaning = current == TroopDeploymentPreference.MyTroopsFirst
            ? "own party fills each wave first; other lords, garrison and militia only once it is empty"
            : "every party this client supplies shares each wave in proportion to what it has left";

        // Says outright that this is local, because the obvious first mistake on a two-client rig is to set it
        // once and expect both sides to change.
        return $"TROOP_PREFERENCE {current} ({meaning}) | local to this client, applies from the next wave";
    }
}
