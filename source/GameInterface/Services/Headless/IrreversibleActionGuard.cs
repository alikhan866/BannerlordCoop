using Common.Logging;
using Serilog;
using System;
using System.Collections.Generic;
using System.Linq;

namespace GameInterface.Services.Headless
{
    /// <summary>
    /// C24 - a refusal layer for actions that cannot be undone, enforced where the rig cannot reach it.
    /// </summary>
    /// <remarks>
    /// C23 keeps a driven client from ACCEPTING something irreversible by accident. This is the second layer,
    /// and it exists because the first one is enforced by the same process it is protecting against: a rig
    /// that misbehaves, or a scenario written in a hurry, disables its own safeguard by definition. This one
    /// runs on the server, which no client can reconfigure.
    ///
    /// The denylist is the set whose damage outlives the run. A fief given away, a war declared, a hero
    /// executed, a clan destroyed - none of these are undone by restarting the client, and on a save that
    /// matters they are not undone at all. Everything else is allowed: the point is not to make the world
    /// read-only, it is to keep an unattended run from doing the handful of things nobody would sanction.
    ///
    /// Refusals are recorded rather than silent. A refusal that leaves no trace turns into "why did the
    /// scenario not work", which costs more than the action would have.
    /// </remarks>
    public static class IrreversibleActionGuard
    {
        private static readonly ILogger Logger = LogManager.GetLogger(typeof(IrreversibleActionGuard));

        /// <summary>
        /// Command fragments that name an irreversible act. Matched as substrings, so a family is covered by
        /// its prefix rather than by naming every member and missing the one added next month.
        /// </summary>
        private static readonly string[] Denied =
        {
            "kingdom.declare_war",
            "kingdom.make_peace",
            "kingdom.end_alliance",
            "kingdom.resolve_decision",
            "kingdom.vote_decision",
            "kingdom.force_player_join_kingdom",
            "kingdom.force_player_vassalage",
            "mobileparty.declare_war",
            "clan.destroy_clan",
            "clan.change_clan_kingdom",
            "clan.change_clan_leader",
            "clan.join_kingdom",
            "kingdom.join_kingdom",
            "clan.leave_kingdom",
            "romance.marry",
            "romance.divorce",
            "settlementComponent.set_owner",
            "settlements.set_ownerclan",
            "settlements.capture_by_siege",
            "town.set_governor",
            "town.start_rebellion",
            "hero.createHero",
            "player_captivity.capture_player",
            "mobileparty.destroyParty",
            "mobileparty.destroyAllBanditParties",
            // The migrated command classes use snake_case prefixes and names; the entries above keep the old spellings.
            "mobile_party.declare_war",
            "mobile_party.destroy_party",
            "mobile_party.destroy_all_bandit_parties",
            "hero.create_hero",
            "settlement_component.set_owner",
            "settlements.set_owner_clan",
        };

        private static readonly object Gate = new object();
        private static readonly List<string> Refusals = new List<string>();
        private static bool armed;

        /// <summary>Whether this process refuses irreversible actions. Off by default; a server opts in.</summary>
        public static bool IsArmed
        {
            get { lock (Gate) { return armed; } }
        }

        public static IReadOnlyList<string> DeniedPatterns => Denied.ToList();

        public static IReadOnlyList<string> RefusalLog
        {
            get { lock (Gate) { return Refusals.ToList(); } }
        }

        public static void Arm(bool value)
        {
            lock (Gate) { armed = value; }
            Logger.Information("[Guard] irreversible-action refusal {State}", value ? "ARMED" : "disarmed");
        }

        public static int ClearRefusals()
        {
            lock (Gate)
            {
                int count = Refusals.Count;
                Refusals.Clear();
                return count;
            }
        }

        /// <summary>
        /// Whether a command names a denied act. Pure, so the list can be tested without a game.
        /// </summary>
        public static bool IsDenied(string command)
        {
            if (string.IsNullOrWhiteSpace(command)) return false;

            return Denied.Any(pattern =>
                command.IndexOf(pattern, StringComparison.OrdinalIgnoreCase) >= 0);
        }

        /// <summary>
        /// The decision for one command, recording the refusal when it refuses.
        /// </summary>
        public static bool TryRefuse(string command, out string reason)
        {
            reason = null;
            if (!IsArmed || !IsDenied(command)) return false;

            reason = $"'{command}' is on the irreversible-action denylist and this server is armed against it.";
            lock (Gate) { Refusals.Add(command); }
            Logger.Warning("[Guard] REFUSED {Command}", command);
            return true;
        }
    }
}
