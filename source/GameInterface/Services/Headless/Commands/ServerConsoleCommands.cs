using Common;
using Common.Logging;
using Serilog;
using System.Collections.Generic;
using TaleWorlds.CampaignSystem;
using static TaleWorlds.Library.CommandLineFunctionality;

namespace GameInterface.Services.Headless.Commands
{
    /// <summary>
    /// Commands for operating a dedicated server from its console.
    /// </summary>
    /// <remarks>
    /// Registered with the engine's own console rather than read from stdin directly. The headless engine
    /// already owns the console - it loads consoles.xml and reads input itself - so a second reader in this
    /// process simply never sees a typed line. Going through the engine also puts these commands in the
    /// same place as every other one the server understands.
    ///
    /// Typed as <c>coop.server.save</c>.
    /// </remarks>
    public static class ServerConsoleCommands
    {
        private static readonly ILogger Logger = LogManager.GetLogger(typeof(ServerConsoleCommands));

        /// <summary>
        /// Saves the world, under the hosted save's name unless another is given.
        /// </summary>
        /// <remarks>
        /// SaveHandler.SaveAs is used rather than writing a file directly, because a coop save is a PAIR:
        /// the .sav holds the world, and a matching .json records which player owns which hero. Going
        /// through the game's own save path runs Game.Save, which is what the coop save handler listens for
        /// to write that .json. A save produced any other way loads back with every player sent to
        /// character creation.
        ///
        /// This runs on the game thread already - the engine dispatches console commands from its tick - so
        /// the campaign is not being walked while it is mid-update.
        /// </remarks>
        [CommandLineArgumentFunction("save", "coop.server")]
        public static string Save(List<string> arguments)
        {
            var saveName = arguments != null && arguments.Count > 0 && !string.IsNullOrWhiteSpace(arguments[0])
                ? arguments[0].Trim()
                : HeadlessServices.HostedSaveName;

            if (string.IsNullOrEmpty(saveName))
            {
                return "No save name given, and none configured. Usage: coop.server.save [name]";
            }

            if (!ModInformation.IsServer)
            {
                return "Only the server can save the world.";
            }

            var handler = Campaign.Current?.SaveHandler;
            if (handler == null)
            {
                return "No campaign is loaded; nothing to save.";
            }

            if (handler.IsSaving)
            {
                return "A save is already in progress.";
            }

            Logger.Information("[Server] saving '{SaveName}' from the console", saveName);
            handler.SaveAs(saveName);

            return $"Saving '{saveName}'...";
        }

        /// <summary>Reports what the server is doing, for a console with no other view of it.</summary>
        [CommandLineArgumentFunction("status", "coop.server")]
        public static string Status(List<string> arguments)
        {
            var campaign = Campaign.Current;
            if (campaign == null) return "No campaign loaded.";

            return $"save='{HeadlessServices.HostedSaveName}' date={CampaignTime.Now} " +
                   $"saving={campaign.SaveHandler?.IsSaving} headless={ModInformation.IsHeadless}";
        }
    }
}
