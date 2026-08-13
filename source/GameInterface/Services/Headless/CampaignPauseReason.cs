using System;

namespace GameInterface.Services.Headless
{
    /// <summary>
    /// C30 - why the campaign clock is not moving, published so a watchdog can consult it.
    /// </summary>
    /// <remarks>
    /// A paused campaign and a stuck one look identical from outside: campaign time stops in both. A watchdog
    /// that cannot tell them apart reports a stall every time the server pauses itself, and a watchdog that
    /// cries wolf stops being read - which is worse than not having one.
    ///
    /// The server already pauses for three distinct reasons and logs each, but a log line is not something a
    /// watchdog can consult at the moment it needs to decide. This holds the latest reason so the decision can
    /// be made from state rather than by grepping.
    ///
    /// Deliberately last-writer-wins with no stack. The question being answered is "why is it paused right
    /// now", and a reason that outlives its cause is worse than none - it would explain away a real stall.
    /// Whoever resumes clears it.
    /// </remarks>
    public static class CampaignPauseReason
    {
        private static readonly object Gate = new object();
        private static string reason;
        private static DateTime sinceUtc;

        /// <summary>The current reason, or null when nothing has claimed one.</summary>
        public static string Current
        {
            get { lock (Gate) { return reason; } }
        }

        /// <summary>How long the current reason has been in force, in seconds; 0 when there is none.</summary>
        public static double HeldForSeconds
        {
            get
            {
                lock (Gate)
                {
                    return reason == null ? 0d : Math.Max(0d, (DateTime.UtcNow - sinceUtc).TotalSeconds);
                }
            }
        }

        public static void Set(string value)
        {
            lock (Gate)
            {
                if (string.Equals(reason, value, StringComparison.Ordinal)) return;
                reason = value;
                sinceUtc = DateTime.UtcNow;
            }
        }

        public static void Clear()
        {
            lock (Gate)
            {
                reason = null;
                sinceUtc = default;
            }
        }
    }
}
