using Common;
using Common.Logging;
using Common.Messaging;
using GameInterface.Services.Heroes;
using GameInterface.Services.Heroes.Messages;
using GameInterface.Services.Save.Commands;
using GameInterface.Services.Save.Messages;
using HarmonyLib;
using Serilog;
using TaleWorlds.CampaignSystem;
using TaleWorlds.Core;
using TaleWorlds.SaveSystem;

namespace GameInterface.Services.Save.Patches;

[HarmonyPatch(typeof(Game), "Save")]
class SavePatches
{
    private static readonly ILogger Logger = LogManager.GetLogger<SavePatches>();

    static bool Prefix(Game __instance, ref string saveName, ISaveDriver driver)
    {
        if (ModInformation.IsServer)
        {
            bool publish = ShouldPublishGameSaved(driver);

            // Always say which driver was seen and what was decided. The failure this replaced was silent
            // on both sides - the engine logged "Successfully saved" and the sidecar simply never appeared -
            // and a save that has lost its player bindings looks identical to one that has not.
            Logger.Information(
                "[Save] {SaveName}: driver={Driver} sidecar={Decision}",
                saveName,
                driver?.GetType().Name ?? "null",
                publish ? "written" : "skipped");

            if (publish) MessageBroker.Instance.Publish(__instance, new GameSaved(saveName));
        }

        return true;
    }

    /// <summary>
    /// Whether this save should also write the co-op session sidecar.
    /// </summary>
    /// <remarks>
    /// Asked as "is this NOT an in-memory save", not as "is this a FileDriver".
    ///
    /// The sidecar is the &lt;name&gt;.json holding every controller's hero, party and clan. Gating it on
    /// <c>driver is FileDriver</c> also excluded the DEDICATED SERVER's ordinary campaign save, whose driver
    /// is not that type - so every save the server wrote lost its bindings, and the next load sent both
    /// players off to build new characters. Nothing reported it: the .sav is complete and the engine calls
    /// the save a success.
    ///
    /// The in-memory drivers are the ones that must be excluded - the bug reporter's and the join
    /// transfer's - and they are ours, so they can be named directly. Anything else is treated as a real
    /// save, which fails towards keeping player bindings rather than silently dropping them.
    /// </remarks>
    internal static bool ShouldPublishGameSaved(ISaveDriver driver)
    {
        return !(driver is CoopInMemSaveDriver) && !(driver is CoopFileInMemSaveDriver);
    }
}

[HarmonyPatch(typeof(SaveHandler), "OnSaveStarted")]
internal class SaveStartedPatch
{
    static void Prefix(SaveHandler __instance)
    {
        if (ModInformation.IsServer)
        {
            MessageBroker.Instance.Publish(__instance, new GameSaveStateChanged(true));
            SaveDebugCommand.HoldForEvidenceIfRequested();
        }
    }
}

[HarmonyPatch(typeof(SaveHandler), "OnSaveEnded")]
internal class SaveEndedPatch
{
    static void Postfix(SaveHandler __instance)
    {
        if (ModInformation.IsServer)
        {
            MessageBroker.Instance.Publish(__instance, new GameSaveStateChanged(false));
        }
    }
}
