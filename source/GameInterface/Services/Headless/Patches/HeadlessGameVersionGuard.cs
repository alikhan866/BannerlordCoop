using Common;
using Common.Logging;
using HarmonyLib;
using Serilog;
using System;
using System.IO;
using System.Xml;
using TaleWorlds.Core;
using TaleWorlds.Library;
using TaleWorlds.ModuleManager;

namespace GameInterface.Services.Headless.Patches
{
    /// <summary>
    /// Gives a headless server a usable game version, because it cannot read its own.
    /// </summary>
    /// <remarks>
    /// A windowless server resolves no application version: the engine logs "assert failed: Invalid version
    /// type" and then reports the version as <c>i-1.-1.-1.-1</c>. That is not cosmetic. Campaign loading is
    /// full of save-compatibility branches keyed on it - <c>Clan.PreAfterLoad</c> alone gates three of them
    /// on <c>LastLoadedGameVersion &lt; v1.2.2 / v1.3.0 / e1.8.0.0</c> - and an invalid version compares as
    /// older than everything, so a current save is run through years of migration paths meant for ancient
    /// ones. The first clan to hit it is player_faction, and it takes the process down with it.
    ///
    /// So any version that cannot be used is replaced with the one the Native module declares - the same
    /// value a client would have resolved. This changes nothing on a client, which reads its version fine.
    /// </remarks>
    [HarmonyPatch]
    internal static class HeadlessGameVersionGuard
    {
        private static readonly ILogger Logger = LogManager.GetLogger(typeof(HeadlessGameVersionGuard));

        private static ApplicationVersion fallback;
        private static bool applied;

        /// <summary>
        /// Applied by hand rather than by PatchAll, so the fallback is read once, up front, and the whole
        /// guard is skipped on a client.
        /// </summary>
        internal static void Apply(Harmony harmony)
        {
            if (!ModInformation.IsHeadless || applied) return;
            applied = true;

            fallback = ReadNativeModuleVersion();
            Logger.Information("[Headless] game version fallback: {Version}", fallback);

            Patch(harmony, AccessTools.Method(typeof(ApplicationVersion), nameof(ApplicationVersion.FromParametersFile)),
                nameof(ReplaceUnusableResult), prefix: false);

            Patch(harmony, AccessTools.Method(typeof(TaleWorlds.SaveSystem.MetaDataExtensions),
                    nameof(TaleWorlds.SaveSystem.MetaDataExtensions.GetApplicationVersion)),
                nameof(ReplaceUnusableResult), prefix: false);

            // A prefix on the SETTER, so a bad value never lands...
            Patch(harmony, AccessTools.PropertySetter(typeof(MBSaveLoad), nameof(MBSaveLoad.LastLoadedGameVersion)),
                nameof(ReplaceUnusableValue), prefix: true);

            // The version stamped INTO a save's metadata. Without this the server writes
            // "ApplicationVersion":"i-1.-1.-1.-1" into every save it makes, and that save then cannot be
            // loaded by anything - including this server - because the version cannot be parsed back.
            Patch(harmony, AccessTools.PropertyGetter(typeof(MBSaveLoad), nameof(MBSaveLoad.CurrentVersion)),
                nameof(ReplaceUnusableResult), prefix: false);

            // ...and a postfix on the GETTER, because guarding the setter alone assumes something sets it.
            // Nothing does here, so the property keeps its default - which is Empty, and Empty is exactly
            // the unusable value the guard exists to keep out of the compatibility branches.
            Patch(harmony, AccessTools.PropertyGetter(typeof(MBSaveLoad), nameof(MBSaveLoad.LastLoadedGameVersion)),
                nameof(ReplaceUnusableResult), prefix: false);
        }

        private static void Patch(Harmony harmony, System.Reflection.MethodBase target, string patchName, bool prefix)
        {
            if (target == null)
            {
                Logger.Warning("[Headless] version guard target missing for {Patch}; skipping", patchName);
                return;
            }

            var patch = new HarmonyMethod(typeof(HeadlessGameVersionGuard), patchName);
            harmony.Patch(target, prefix ? patch : null, prefix ? null : patch);
        }

        private static void ReplaceUnusableResult(ref ApplicationVersion __result)
        {
            if (!IsUnusable(__result)) return;

            __result = fallback;
        }

        private static void ReplaceUnusableValue(ref ApplicationVersion value)
        {
            if (!IsUnusable(value)) return;

            value = fallback;
        }

        /// <summary>
        /// Empty, or with no major version - which is what an unresolved version looks like, and what makes
        /// it compare as older than every compatibility check in the campaign.
        /// </summary>
        private static bool IsUnusable(ApplicationVersion version)
            => version.Equals(ApplicationVersion.Empty) || version.Major <= 0;

        /// <summary>
        /// The version the Native module declares. It is the same file the launcher reads, so the server
        /// ends up agreeing with the client about what game this is.
        /// </summary>
        private static ApplicationVersion ReadNativeModuleVersion()
        {
            try
            {
                var path = NativeSubModulePath();
                if (path == null || !File.Exists(path))
                {
                    Logger.Warning("[Headless] Native SubModule.xml not found; version checks may misfire");
                    return ApplicationVersion.Empty;
                }

                var document = new XmlDocument();
                document.Load(path);

                var value = document.SelectSingleNode("/Module/Version")?.Attributes?["value"]?.Value;
                if (string.IsNullOrEmpty(value)) return ApplicationVersion.Empty;

                return ApplicationVersion.FromString(value, ApplicationVersion.DefaultChangeSet);
            }
            catch (Exception e)
            {
                Logger.Warning(e, "[Headless] could not read the Native module version");
                return ApplicationVersion.Empty;
            }
        }

        private static string NativeSubModulePath()
        {
            var moduleRoot = ModuleHelper.GetModuleFullPath("Native");
            if (!string.IsNullOrEmpty(moduleRoot))
            {
                return Path.Combine(moduleRoot, "SubModule.xml");
            }

            // The working directory is the engine's bin folder, two levels below the game root.
            return Path.Combine(
                Directory.GetCurrentDirectory(), "..", "..", "Modules", "Native", "SubModule.xml");
        }
    }
}
