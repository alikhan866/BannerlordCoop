using Common;
using Common.Logging;
using SandBox;
using Serilog;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using TaleWorlds.CampaignSystem;
using TaleWorlds.Core;
using TaleWorlds.MountAndBlade;
using static TaleWorlds.Library.CommandLineFunctionality;

namespace GameInterface.Services.Save.Commands
{
    public class SaveDebugCommand
    {
        private static readonly ILogger Logger = LogManager.GetLogger<SaveDebugCommand>();

        private static readonly Regex SafeSaveName = new("^[A-Za-z0-9_-]{1,64}$");

#if DEBUG
        private static int evidenceHoldMilliseconds;
#endif

        [CommandLineArgumentFunction("save_as", "coop.debug.save")]
        public static string SaveAs(List<string> args)
        {
            if (!ModInformation.IsServer)
                return "Command can only be run on the server.";
            if (args.Count != 1 || !SafeSaveName.IsMatch(args[0]))
                return "Usage: coop.debug.save.save_as <1-64 letters, digits, underscores, or hyphens>";

            SaveHandler saveHandler = Campaign.Current?.SaveHandler;
            if (saveHandler == null)
                return "No active campaign / SaveHandler.";
            if (saveHandler.IsSaving)
                return "A save is already queued.";

            saveHandler.SaveAs(args[0]);
            return $"Enqueued save as {args[0]} on the server.";
        }

        [CommandLineArgumentFunction("state", "coop.debug.save")]
        public static string State(List<string> args)
        {
            if (args.Count != 0)
                return "Usage: coop.debug.save.state";

            SaveHandler saveHandler = Campaign.Current?.SaveHandler;
            return saveHandler == null
                ? "saveHandler=unavailable"
                : $"saveHandler=ready|isSaving={saveHandler.IsSaving}";
        }

        /// <summary>
        /// Enqueues a native autosave (the same path SaveHandler uses on a timer) so the
        /// client save block can be exercised on demand. On a client SetSaveArgs is blocked, so
        /// nothing is enqueued and no file is written; on the host a save file appears.
        /// </summary>
        [CommandLineArgumentFunction("force_autosave", "coop.debug.save")]
        public static string ForceAutoSave(List<string> args)
        {
            if (Campaign.Current?.SaveHandler == null) return "No active campaign / SaveHandler.";

            int holdMilliseconds = 0;
            if (args.Count > 1 ||
                (args.Count == 1 &&
                 (int.TryParse(args[0], out holdMilliseconds) == false ||
                  holdMilliseconds < 1 ||
                  holdMilliseconds > 5000)))
            {
                return "Usage: coop.debug.save.force_autosave [evidence hold milliseconds from 1 to 5000]";
            }

#if DEBUG
            if (args.Count == 1)
            {
                if (ModInformation.IsClient)
                {
                    return "The evidence hold is server-only.";
                }

                if (Campaign.Current.SaveHandler.IsSaving)
                {
                    return "Cannot add an evidence hold while a save is already queued.";
                }
            }
#else
            if (args.Count == 1)
            {
                return "The evidence hold is only available in DEBUG builds.";
            }
#endif

            Campaign.Current.SaveHandler.ForceAutoSave();

#if DEBUG
            if (args.Count == 1)
            {
                if (Campaign.Current.SaveHandler.IsSaving == false)
                {
                    return "Autosaves are disabled; no save was enqueued.";
                }

                Interlocked.Exchange(ref evidenceHoldMilliseconds, holdMilliseconds);
            }
#endif

            string side = ModInformation.IsClient ? "client (save should be BLOCKED)" : "host (save should succeed)";
            return $"Enqueued autosave on {side}. Check the Saves folder.";
        }

#if DEBUG
        /// <summary>
        /// Loads a save from disk into THIS process, the way the dedicated server loads its own.
        /// </summary>
        /// <remarks>
        /// A diagnostic, not a play feature. A headless client dies with a native access violation while
        /// loading a campaign it received over the wire, and that path differs from the server's in two ways
        /// at once - it uses <c>CoopInMemSaveDriver</c> over transferred bytes, and it passes
        /// <c>loadAsLateInitialize: true</c>. Both are load-path differences rather than role differences, so
        /// the way to tell them apart from a crash with no managed stack is to make the SAME process type
        /// perform the OTHER kind of load. Survive here and the fault is in the transfer path; die here and a
        /// client-role campaign load is render-free-unsafe in general.
        ///
        /// Deliberately not routed through IGameStateInterface: a client that has not joined has no container
        /// to resolve it from, and requiring a join would reintroduce the very path under test.
        ///
        /// Not blocking. The engine load never returns to the caller in the ordinary way, and holding the
        /// control channel's request open for it would time out the caller and tell us nothing.
        /// </remarks>
        [CommandLineArgumentFunction("load_local", "coop.debug.save")]
        public static string LoadLocal(List<string> args)
        {
            if (args.Count != 1 || !SafeSaveName.IsMatch(args[0]))
                return "Usage: coop.debug.save.load_local <1-64 letters, digits, underscores, or hyphens>";

            if (Campaign.Current != null)
                return "A campaign is already loaded; this diagnostic starts from an empty process.";

            string saveName = args[0];
            GameThread.Run(() =>
            {
                var save = MBSaveLoad.GetSaveFiles(null).SingleOrDefault(file => file.Name == saveName);
                if (save == null)
                {
                    Logger.Error("[LoadLocal] no save named {SaveName}", saveName);
                    return;
                }

                Logger.Information("[LoadLocal] loading {SaveName} from disk in this process", saveName);
                SandBoxSaveHelper.LoadGameAction(
                    save,
                    loadResult => MBGameManager.StartNewGame(new SandBoxGameManager(loadResult)),
                    null);
            });

            return $"Requested a local disk load of '{saveName}'. Watch the log.";
        }
#endif

        internal static void HoldForEvidenceIfRequested()
        {
#if DEBUG
            int milliseconds = Interlocked.Exchange(ref evidenceHoldMilliseconds, 0);
            if (milliseconds > 0)
            {
                Thread.Sleep(milliseconds);
            }
#endif
        }
    }
}
