using Common;
using Common.Logging;
using Coop.Core.Common.Session;
using Serilog;
using System;
using System.IO;
using System.Linq;
using System.Threading;
using TaleWorlds.CampaignSystem;
using TaleWorlds.Engine;
using TaleWorlds.Library;

namespace Coop
{

    /// <summary>
    /// Supports running a coop server in a console, with no window and no second game instance.
    /// </summary>
    /// <remarks>
    /// There is deliberately no game loop and no hosting code here, because neither is needed:
    ///
    ///   - The headless engine ticks its modules on its own thread, exactly like the windowed one. The
    ///     engine-tick counter below is what established that. An earlier version of this class ran its own
    ///     pump thread on the assumption that nothing else would, and driving GameThread from that thread
    ///     while the engine drove campaign loading from its own crashed the process natively.
    ///   - Hosting is already handled by CoopMod.TryManagedServerAutoStart, which waits for InitialState on
    ///     the engine thread and starts the server from the /coopsave arguments this process was launched
    ///     with. Publishing HostSaveGame from here as well only raced it, from the wrong thread.
    ///
    /// What remains is the one thing a windowless process genuinely changes: the loading-window patches
    /// have no window to patch, and CoopMod needs to know not to let them take the server down with them.
    /// The heartbeat exists because a server with no window has no other sign of life.
    /// </remarks>
    internal static class HeadlessServerBootstrap
    {
        private static readonly ILogger Logger = LogManager.GetLogger(typeof(HeadlessServerBootstrap));

        internal const string HeadlessArgument = "/coopheadless";

        /// <summary>How often the tick counter reports in.</summary>
        private static readonly TimeSpan HeartbeatInterval = TimeSpan.FromSeconds(30);

        private static bool consoleStarted;
        private static long engineTicks;
        private static DateTime lastHeartbeat = DateTime.UtcNow;

        internal static bool IsRequested()
        {
            var args = Utilities.GetFullCommandLineString().Split(' ');
            return args.Any(a => a.Equals(HeadlessArgument, StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>
        /// Runs any command written to the command file, then clears it.
        /// </summary>
        /// <remarks>
        /// A second way in, alongside the console, and the one that always works. The engine takes the
        /// console over for itself on a windowless process, so a reader in here can be left holding a
        /// handle nothing is ever typed into - which is exactly what happened. Dropping a line in a file
        /// depends on none of that, and it lets the server be driven from another window or a script.
        ///
        ///     echo save &gt; "%USERPROFILE%\Documents\Mount and Blade II Bannerlord\CoopData\DedicatedServer\command.txt"
        ///
        /// Polled from the module tick, so commands run on the game thread, between frames, rather than
        /// while the world is mid-update.
        /// </remarks>
        private static void PollCommandFile(long ticks)
        {
            // Roughly once a second at 64 Hz. Checking every frame would stat a file 64 times a second for
            // something a human types every few minutes.
            if (ticks % 64 != 0) return;

            try
            {
                var path = CommandFilePath();
                if (path == null || !File.Exists(path)) return;

                var lines = File.ReadAllLines(path);

                // Cleared first: a command that throws must not be run again on the next poll, forever.
                File.Delete(path);

                foreach (var line in lines)
                {
                    Execute(line.Trim());
                }
            }
            catch (Exception e)
            {
                Logger.Warning(e, "[Headless] could not read the command file");
            }
        }

        /// <summary>
        /// Documents\Mount and Blade II Bannerlord\CoopData\DedicatedServer\command.txt - the coop
        /// team's own location for dedicated-server files, so this sits beside server-config.json rather
        /// than somewhere only this script knows about.
        /// </summary>
        private static string CommandFilePath()
        {
            var documents = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
            if (string.IsNullOrEmpty(documents)) return null;

            return System.IO.Path.Combine(
                documents, "Mount and Blade II Bannerlord", "CoopData", "DedicatedServer", "command.txt");
        }

        /// <summary>
        /// Starts reading server commands from the console.
        /// </summary>
        /// <remarks>
        /// A dedicated server with no window needs some way to be told to do things, and stdin is the one
        /// it already has. Its own thread, because reading a line blocks until someone types one, and the
        /// game loop cannot wait for that.
        /// </remarks>
        internal static void StartConsole()
        {
            if (!ModInformation.IsHeadless || consoleStarted) return;
            consoleStarted = true;

            new Thread(ReadCommands)
            {
                Name = "CoopHeadlessConsole",
                IsBackground = true,
            }.Start();
        }

        private static void ReadCommands()
        {
            Logger.Information("[Headless] console ready - type 'help' for commands");

            while (true)
            {
                string line;
                try
                {
                    line = Console.ReadLine();
                }
                catch (Exception e)
                {
                    Logger.Warning(e, "[Headless] console closed; commands are unavailable");
                    return;
                }

                // Null means the input stream ended - the server was started without an attached console,
                // so there is nobody to read from and looping would just spin.
                if (line == null)
                {
                    Logger.Information("[Headless] no console attached; commands are unavailable");
                    return;
                }

                Execute(line.Trim());
            }
        }

        private static void Execute(string line)
        {
            if (line.Length == 0) return;

            var parts = line.Split(new[] { ' ' }, 2, StringSplitOptions.RemoveEmptyEntries);
            var command = parts[0].ToLowerInvariant();
            var argument = parts.Length > 1 ? parts[1].Trim() : null;

            switch (command)
            {
                case "save":
                    Save(argument);
                    break;

                case "help":
                    // Logged in the same "<command> -> <result>" shape the console path uses, so a caller
                    // reading replies out of the log can match every command the same way instead of
                    // special-casing the built-ins.
                    Logger.Information("[Headless] {Command} -> {Result}", line,
                        "save [name] - save the world; help - this list; anything else is passed to the " +
                        "game's own console (e.g. coop.debug.mobileparty.list_stuck_ai)");
                    break;

                default:
                    RunGameConsoleCommand(line);
                    break;
            }
        }

        /// <summary>
        /// Hands a line to the game's own console, so every registered coop command works from the file.
        /// </summary>
        /// <remarks>
        /// Without this the file understands only 'save', which leaves a headless host unable to run any of
        /// the coop.debug commands - and several of them refuse to run anywhere ELSE, because they mutate
        /// authoritative state and start with "this command can only be run on the server". On a windowed
        /// host you would just type them; a headless one has no console to type into, so the file is the only
        /// way in.
        ///
        /// Marshalled onto the game thread: these commands walk and mutate campaign objects, and the file is
        /// polled from the engine tick, which is not where that work belongs.
        /// </remarks>
        private static void RunGameConsoleCommand(string line)
        {
            GameThread.RunSafe(() =>
            {
                try
                {
                    // CallFunction wants the command and its arguments SEPARATELY; passing the whole line as
                    // the name makes every command that takes an argument fail with "Could not find the command".
                    var split = line.IndexOf(' ');
                    var name = split < 0 ? line : line.Substring(0, split);
                    var arguments = split < 0 ? string.Empty : line.Substring(split + 1).Trim();

                    var result = CommandLineFunctionality.CallFunction(name, arguments, out _);
                    Logger.Information("[Headless] {Command} -> {Result}", line,
                        string.IsNullOrWhiteSpace(result) ? "(no output)" : result);
                }
                catch (Exception e)
                {
                    Logger.Error(e, "[Headless] command '{Command}' failed", line);
                }
            });
        }

        /// <summary>
        /// Saves the world, under the hosted save's name unless another is given.
        /// </summary>
        /// <remarks>
        /// Marshalled onto the game thread: saving walks the whole campaign, and doing that from the
        /// console thread while the world ticks would read half-updated state.
        ///
        /// SaveHandler.SaveAs is used rather than a direct write because a coop save is a PAIR - the .sav
        /// holds the world, a matching .json holds which player owns which hero. Going through the game's
        /// own save path means Game.Save runs, which is what the coop save handler listens for to write
        /// that .json. A save written any other way loads back with every player sent to character
        /// creation.
        /// </remarks>
        private static void Save(string name)
        {
            var saveName = string.IsNullOrWhiteSpace(name) ? ManagedServerConfig.SaveName : name.Trim();

            if (string.IsNullOrEmpty(saveName))
            {
                Logger.Error("[Headless] no save name given and none configured");
                return;
            }

            GameThread.RunSafe(() =>
            {
                var handler = Campaign.Current?.SaveHandler;
                if (handler == null)
                {
                    Logger.Error("[Headless] no campaign is loaded; nothing to save");
                    return;
                }

                if (handler.IsSaving)
                {
                    Logger.Warning("[Headless] a save is already in progress");
                    return;
                }

                Logger.Information("[Headless] saving '{SaveName}'...", saveName);
                handler.SaveAs(saveName);
            });
        }

        /// <summary>
        /// Called from the engine's module tick. Establishes that the engine drives a loop here, and
        /// periodically says so, so an unattended server shows whether it is running or wedged.
        /// </summary>
        internal static void NoteEngineTick()
        {
            // Guarded, because this is called from CoopMod's tick on EVERY process - client, windowed
            // server and headless alike. Without it a client polls the command file once a second and
            // would run whatever a stray command.txt contained, and every log gained a "[Headless] alive"
            // line every thirty seconds on machines that are not headless at all.
            if (!ModInformation.IsHeadless) return;

            var ticks = Interlocked.Increment(ref engineTicks);

            PollCommandFile(ticks);

            var now = DateTime.UtcNow;
            if (now - lastHeartbeat < HeartbeatInterval) return;
            lastHeartbeat = now;

            Logger.Information("[Headless] alive: engineTicks={EngineTicks}", ticks);
        }
    }
}
