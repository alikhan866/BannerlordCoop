using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using TaleWorlds.ModuleManager;

namespace GameInterface.Services.Modules;

public interface ICoopModulePathResolver
{
    string GetXmlPath(string xmlName);
}

internal sealed class CoopModulePathResolver : ICoopModulePathResolver
{
    internal const string StableModuleId = "Coop";
    internal const string NightlyModuleId = "CoopNightly";

    private readonly Func<string, bool> isModuleActive;
    private readonly Func<string, string, string> getXmlPath;
    private readonly Func<IEnumerable<string>> getActiveModuleIds;
    private readonly Func<string, bool> fileExists;

    public CoopModulePathResolver()
        : this(
            ModuleHelper.IsModuleActive,
            ModuleHelper.GetXmlPath,
            () => ModuleHelper.GetActiveModules().Select(module => module.Id),
            File.Exists)
    {
    }

    internal CoopModulePathResolver(
        Func<string, bool> isModuleActive,
        Func<string, string, string> getXmlPath,
        Func<IEnumerable<string>> getActiveModuleIds = null,
        Func<string, bool> fileExists = null)
    {
        if (isModuleActive == null) throw new ArgumentNullException(nameof(isModuleActive));
        if (getXmlPath == null) throw new ArgumentNullException(nameof(getXmlPath));

        this.isModuleActive = isModuleActive;
        this.getXmlPath = getXmlPath;
        this.getActiveModuleIds = getActiveModuleIds;
        this.fileExists = fileExists;
    }

    public string GetXmlPath(string xmlName)
    {
        if (isModuleActive(StableModuleId))
            return getXmlPath(StableModuleId, xmlName);

        if (isModuleActive(NightlyModuleId))
            return getXmlPath(NightlyModuleId, xmlName);

        return FindInAnyActiveModule(xmlName);
    }

    /// <summary>
    /// Looks for the file under whatever module folder is actually loaded, when neither known id is active.
    /// </summary>
    /// <remarks>
    /// The module id is not a constant. A share or test build installs the same module under a different
    /// folder - CoopFixes, CoopDebug - and the two ids above then both miss, so the caller is handed null and
    /// the content silently never loads. Worse, <see cref="ModuleHelper.GetXmlPath"/> throws
    /// KeyNotFoundException for an id that is not loaded rather than returning a path that does not exist,
    /// and that threw out of OnSessionLaunched - which the engine does not guard - so a renamed folder killed
    /// the campaign during load with nothing in the log but an exit code.
    ///
    /// Asking the loaded modules removes the assumption. The scan is opt-in through the injected delegates so
    /// that a unit test constructing the resolver without them keeps the strict two-id behaviour; production
    /// goes through the parameterless constructor, which wires both.
    /// </remarks>
    private string FindInAnyActiveModule(string xmlName)
    {
        if (getActiveModuleIds == null || fileExists == null) return null;

        foreach (var moduleId in getActiveModuleIds())
        {
            if (string.IsNullOrEmpty(moduleId)) continue;

            string path;
            // Defensive: this is the call that threw before, and one unreadable module must not stop the scan
            // from reaching the one that actually holds the file.
            try { path = getXmlPath(moduleId, xmlName); }
            catch (Exception) { continue; }

            if (!string.IsNullOrEmpty(path) && fileExists(path)) return path;
        }

        return null;
    }
}
