using Common;
using Common.Logging;
using Common.Messaging;
using GameInterface.Policies;
using GameInterface.Services.PartyComponents.Messages;
using HarmonyLib;
using Serilog;
using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.Party;
using TaleWorlds.CampaignSystem.Party.PartyComponents;
using TaleWorlds.CampaignSystem.Settlements;

namespace GameInterface.Services.PartyComponents.Patches;

[HarmonyPatch(typeof(PartyComponent))]
internal class PartyComponentPatches
{
    private static readonly ILogger Logger = LogManager.GetLogger<PartyComponentPatches>();

    [HarmonyPatch(nameof(PartyComponent.MobileParty), MethodType.Setter)]
    [HarmonyPrefix]
    private static void PrefixMobileParty(PartyComponent __instance, MobileParty value)
    {
        if (CallOriginalPolicy.IsOriginalAllowed())
            return;
        
        if (ModInformation.IsClient)
        {
            Logger.Error("Client called managed PartyComponent.MobileParty setter");
            return;
        }

        var message = new PartyComponentMobilePartyUpdated(__instance, value);
        MessageBroker.Instance.Publish(__instance, message);
    }
}

[HarmonyPatch(typeof(PartyComponent))]
public class PartyComponentTranspilers
{
    private static readonly ILogger Logger = LogManager.GetLogger<PartyComponentTranspilers>();

    public static IEnumerable<MethodBase> TargetMethods()
    {
        yield return AccessTools.Method(typeof(PartyComponent), nameof(PartyComponent.ChangePartyLeader));
    }

    [HarmonyTranspiler]
    private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
    {
        var onChangeMethod = AccessTools.Method(typeof(PartyComponent), nameof(PartyComponent.OnChangePartyLeader));
        var onChangeIntercept = AccessTools.Method(typeof(PartyComponentTranspilers), nameof(OnChangePartyLeaderIntercept));

        foreach (var instruction in instructions)
        {
            if (instruction.Calls(onChangeMethod))
            {
                yield return new CodeInstruction(OpCodes.Call, onChangeIntercept);
            }
            else
            {
                yield return instruction;
            }
        }
    }

    public static void OnChangePartyLeaderIntercept(PartyComponent instance, Hero newLeader)
    {
        // A lord party that loses its leader and never gets one back is invisible trouble: the size limit
        // collapses to the base value, Army Management cannot build a row for it so it vanishes from the list,
        // and ChangePartyLeader parks it on Hold. Vanilla always follows the removal with a destroy, a disband
        // or a replacement leader; when one of those does not land here the party just rots.
        //
        // Seven vanilla paths call RemovePartyLeader, so guessing which one fired from the wreckage afterwards
        // is hopeless. Record the caller at the moment it happens instead.
        if (newLeader == null && instance is LordPartyComponent lordParty && lordParty.Owner != null)
        {
            Logger.Warning(
                "Lord party {Party} (owner {Owner}) lost its leader. Caller:\n{Stack}",
                instance.MobileParty?.StringId ?? "<no party>",
                lordParty.Owner.Name?.ToString() ?? "<unnamed>",
                Environment.StackTrace);
        }

        if (CallOriginalPolicy.IsOriginalAllowed())
        {
            instance.OnChangePartyLeader(newLeader);
            return;
        }

        if (ModInformation.IsClient)
        {
            Logger.Error("Client ran managed {type}", "PartyComponent.OnChangePartyLeader");
            instance.OnChangePartyLeader(newLeader);
            return;
        }

        MessageBroker.Instance.Publish(instance, new PartyComponentLeaderChanged(instance, newLeader));

        instance.OnChangePartyLeader(newLeader);
    }
}