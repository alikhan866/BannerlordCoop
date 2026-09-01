using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using GameInterface;
using HarmonyLib;
using Missions;
using Xunit;

namespace E2E.Tests.Services.Missions;

/// <summary>
/// Every Harmony patch in the Missions assembly must be installed, or be knowingly listed as dormant.
/// </summary>
/// <remarks>
/// Nothing calls PatchAllUncategorized on this assembly - MissionModule registers CATEGORIES, and only
/// patches carrying a registered category are ever installed. So a patch class without one compiles,
/// deploys, and silently does nothing, with no error anywhere.
///
/// That cost real time twice. A Postfix written to fix puppet swing animation ran a full live test round
/// before its all-zero counters revealed it had never been installed. Worse, RegisterBlowPatch - the
/// suppression that stops a node applying a blow to an agent it does not own - had been dormant while
/// BattleDamageRouter was written around it being live, so every hit was applied locally AND routed to the
/// owner. Damage landed twice and one side's troops killed at roughly double the rate.
///
/// Adding a patch is therefore two steps, and this test fails if only the first is done.
/// </remarks>
public class MissionsPatchInstallationTests
{
    /// <summary>
    /// Patches deliberately left uninstalled. Each needs a reason; delete the entry to install one.
    /// </summary>
    private static readonly Dictionary<string, string> KnownDormant = new()
    {
        // --- Superseded damage path -------------------------------------------------------------------
        // These belong to the damage system that BattleDamageRouter replaced. Installing them now would
        // double-handle hits alongside the router. RegisterBlowPatch, in AgentDamagePatch.cs, is the one
        // piece the router DOES depend on, and it is installed.
        ["AgentDamagePatch"] = "Old Mission.RegisterBlow path; superseded by BattleDamageRouter.",
        ["ChargeDamageCallbackPatch"] = "SkipPatches region of the old damage path. Unreviewed.",
        ["FallDamageCallbackPatch"] = "SkipPatches region of the old damage path. Unreviewed.",
        ["MeleeHitCallbackPatch"] = "SkipPatches region of the old damage path. Unreviewed.",
        ["MissileAreaDamageCallbackPatch"] = "SkipPatches region of the old damage path. Unreviewed.",
        ["MissileHitCallbackPatch"] = "SkipPatches region of the old damage path. Unreviewed.",
        ["AgentKilledPatch"] = "Kill routing is handled elsewhere. Unreviewed.",

        // --- Never wired up ---------------------------------------------------------------------------
        ["RobustnessPatches"] = "Dormant since before this test. Unreviewed - worth a look given the name.",

        // --- Arena / board games ----------------------------------------------------------------------
        // Board games are not synchronised and the arena patches are cosmetic.
        ["DisableMainAgentCheerBarkMultiplayer"] = "Arena-only cosmetic.",
        ["WarPartyComponentFix"] = "Arena-only.",
        ["RemoveBoardGameBehaviorOfAgentPatch"] = "Board games are not synchronised.",
        ["StartConversationAfterGamePatch"] = "Board games are not synchronised.",
        ["HandlePlayerInputPatch"] = "Board games are not synchronised.",
        ["ForfeitGamePatch"] = "Board games are not synchronised.",
        ["Board"] = "Board games are not synchronised.",
        ["CalculateMovePatch"] = "Board games are not synchronised.",
        ["HandlePreMovementStagePatch"] = "Board games are not synchronised.",
        ["FocusBlockingPawnsPatch"] = "Board games are not synchronised.",
        ["SetPawnCapturedPatch"] = "Board games are not synchronised.",
        ["PreplaceUnitsPatch"] = "Board games are not synchronised.",
    };

    private static IEnumerable<Type> PatchClasses() =>
        typeof(MissionModule).Assembly
            .GetTypes()
            .Where(t => t.GetCustomAttributes<HarmonyPatch>().Any());

    private static HashSet<string> RegisteredCategories()
    {
        var categories = new HashSet<string>(StringComparer.Ordinal);
        foreach (HarmonyPatchCategoryRegistration registration in
                 MissionModule.CreatePatchCategoryRegistrations())
        {
            categories.Add(registration.Category);
        }
        return categories;
    }

    [Fact]
    public void EveryPatch_IsEitherInstalled_OrKnowinglyDormant()
    {
        HashSet<string> registered = RegisteredCategories();
        var uninstalled = new List<string>();

        foreach (Type patch in PatchClasses())
        {
            string category = patch.GetCustomAttribute<HarmonyPatchCategory>()?.info?.category;
            bool installed = category != null && registered.Contains(category);
            if (installed) continue;
            if (KnownDormant.ContainsKey(patch.Name)) continue;
            uninstalled.Add(category == null
                ? patch.Name + " (no [HarmonyPatchCategory] - never installed)"
                : patch.Name + " (category '" + category + "' is not registered in MissionModule)");
        }

        Assert.True(
            uninstalled.Count == 0,
            "These Harmony patches in the Missions assembly will never be installed. Give each a "
            + "[HarmonyPatchCategory] and register it in MissionModule.CreatePatchCategoryRegistrations(), "
            + "or add it to KnownDormant with a reason:\n  " + string.Join("\n  ", uninstalled));
    }

    /// <summary>The suppression BattleDamageRouter depends on. Dormant, damage is applied twice.</summary>
    [Fact]
    public void RegisterBlowPatch_IsInstalled()
    {
        Type patch = PatchClasses().Single(t => t.Name == "RegisterBlowPatch");
        string category = patch.GetCustomAttribute<HarmonyPatchCategory>()?.info?.category;

        Assert.NotNull(category);
        Assert.Contains(category, RegisteredCategories());
    }

    /// <summary>A dormant entry naming a class that no longer exists is stale and hides the next one.</summary>
    [Fact]
    public void KnownDormantList_HasNoStaleEntries()
    {
        HashSet<string> present = PatchClasses().Select(t => t.Name).ToHashSet(StringComparer.Ordinal);
        string[] stale = KnownDormant.Keys.Where(name => !present.Contains(name)).ToArray();

        Assert.True(stale.Length == 0, "KnownDormant names classes that no longer exist: "
            + string.Join(", ", stale));
    }
}
