using Common.Logging;
using Serilog;
using System;
using System.Collections.Generic;
using System.Linq;

namespace GameInterface.Services.Headless
{
    /// <summary>
    /// C23 - decides how a driven client answers a modal it cannot show.
    /// </summary>
    /// <remarks>
    /// A modal has to be answered or the flow behind it never continues, and a render-free client has nobody
    /// to answer it. That leaves two failure modes, and they are not symmetrical:
    ///
    ///   - Answer nothing, and the client hangs. Loud, but it stops the run.
    ///   - Accept everything, and the run continues while making campaign decisions nobody asked for -
    ///     marriages, wars, fiefs given away - and every result after that point is quietly worthless.
    ///
    /// The second is far worse, because it produces a green run built on a world the test never intended. So
    /// the rule is: a popup the scenario DECLARED gets the answer it declared, and anything else gets the
    /// negative answer AND fails the run. An unexpected modal is a finding, not an inconvenience.
    ///
    /// There is deliberately no accept-all, not behind a flag, not "just for this scenario". A switch like
    /// that exists to be turned on at 2am by someone who wants the run to go green, which is the exact moment
    /// the rule is protecting.
    /// </remarks>
    public static class PopupPolicy
    {
        private static readonly ILogger Logger = LogManager.GetLogger(typeof(PopupPolicy));

        public enum Answer
        {
            /// <summary>The negative option - decline, cancel, refuse.</summary>
            Negative = 0,

            /// <summary>The affirmative option. Only ever reached through an explicit declaration.</summary>
            Affirmative = 1,
        }

        public sealed class Declaration
        {
            public string Pattern { get; set; }
            public Answer Answer { get; set; }
            public int Matched { get; set; }
        }

        private static readonly object Gate = new object();
        private static readonly List<Declaration> Declarations = new List<Declaration>();
        private static readonly List<string> Failures = new List<string>();

        /// <summary>True once an undeclared modal has been seen. A run in this state has already failed.</summary>
        public static bool RunFailed
        {
            get { lock (Gate) { return Failures.Count > 0; } }
        }

        public static IReadOnlyList<string> FailureReasons
        {
            get { lock (Gate) { return Failures.ToList(); } }
        }

        public static IReadOnlyList<Declaration> Declared
        {
            get { lock (Gate) { return Declarations.Select(d => new Declaration
            {
                Pattern = d.Pattern, Answer = d.Answer, Matched = d.Matched,
            }).ToList(); } }
        }

        /// <summary>
        /// Declares that popups whose title or text contains <paramref name="pattern"/> get a given answer.
        /// </summary>
        public static void Declare(string pattern, Answer answer)
        {
            if (string.IsNullOrWhiteSpace(pattern)) throw new ArgumentException("A declaration needs a pattern.");

            lock (Gate)
            {
                Declarations.RemoveAll(d => string.Equals(d.Pattern, pattern, StringComparison.OrdinalIgnoreCase));
                Declarations.Add(new Declaration { Pattern = pattern, Answer = answer });
            }
            Logger.Information("[Popup] declared '{Pattern}' -> {Answer}", pattern, answer);
        }

        public static int ClearDeclarations()
        {
            lock (Gate)
            {
                int count = Declarations.Count;
                Declarations.Clear();
                return count;
            }
        }

        /// <summary>Clears the failed state. Separate from clearing declarations, and never automatic.</summary>
        public static int ClearFailures()
        {
            lock (Gate)
            {
                int count = Failures.Count;
                Failures.Clear();
                return count;
            }
        }

        /// <summary>
        /// The answer for a popup, and whether anything actually declared it.
        /// </summary>
        /// <remarks>
        /// Pure and side-effect free apart from the match counter, so the rule can be tested without a game.
        /// The default is Negative for the undeclared case, which is the whole point: getting this wrong in
        /// the safe direction stops a run, getting it wrong in the unsafe direction corrupts one.
        /// </remarks>
        public static Answer Decide(string title, string text, out bool wasDeclared)
        {
            string haystack = ((title ?? string.Empty) + "\n" + (text ?? string.Empty));

            lock (Gate)
            {
                foreach (var declaration in Declarations)
                {
                    if (haystack.IndexOf(declaration.Pattern, StringComparison.OrdinalIgnoreCase) < 0) continue;

                    declaration.Matched++;
                    wasDeclared = true;
                    return declaration.Answer;
                }
            }

            wasDeclared = false;
            return Answer.Negative;
        }

        /// <summary>Records that an undeclared modal was answered negatively, which fails the run.</summary>
        internal static void FailRun(string title, string text)
        {
            string reason = $"undeclared popup answered negatively: title='{title}' text='{Shorten(text)}'";
            lock (Gate)
            {
                Failures.Add(reason);
            }
            Logger.Error("[Popup] RUN FAILED - {Reason}", reason);
        }

        private static string Shorten(string value)
        {
            if (string.IsNullOrEmpty(value)) return string.Empty;
            return value.Length <= 160 ? value : value.Substring(0, 160) + "...";
        }
    }
}
