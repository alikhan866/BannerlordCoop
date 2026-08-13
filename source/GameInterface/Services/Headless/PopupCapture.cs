using Common;
using Common.Logging;
using HarmonyLib;
using Serilog;
using System;
using System.Collections.Generic;
using System.Linq;
using TaleWorlds.CampaignSystem;
using TaleWorlds.Core;
using TaleWorlds.Library;
using TaleWorlds.Localization;

namespace GameInterface.Services.Headless
{
    /// <summary>
    /// C22 - records the popups a render-free process cannot show.
    /// </summary>
    /// <remarks>
    /// A modal on a process with no screen is invisible in both directions: nobody sees it, and nothing in the
    /// log says it happened. That is the worst shape a fault can take for an unattended rig, because the
    /// symptom is a client that simply stops doing anything - which reads as a hang, a desync, or a dead
    /// process depending on who is guessing.
    ///
    /// So every inquiry and quick message is recorded with what it said, what it offered, and when - in BOTH
    /// clocks. Real time answers "how long has it been sitting there"; campaign time answers "where in the
    /// world was I", and the two diverge whenever the campaign is paused, which is exactly when a popup is
    /// most likely to be the reason.
    ///
    /// This records; it does not answer. Answering is C23, and it is deliberately separate: a capture that
    /// quietly accepted things would make campaign decisions nobody asked for, and would do it invisibly.
    /// </remarks>
    public static class PopupCapture
    {
        private static readonly ILogger Logger = LogManager.GetLogger(typeof(PopupCapture));

        /// <summary>Bounded so an unattended soak cannot grow it without limit.</summary>
        private const int Capacity = 200;

        private static readonly object Gate = new object();
        private static readonly List<CapturedPopup> Captured = new List<CapturedPopup>();
        private static long nextSequence = 1;

        public sealed class CapturedPopup
        {
            public long Sequence { get; set; }
            public string Kind { get; set; }
            public string Title { get; set; }
            public string Text { get; set; }
            public string[] Options { get; set; }
            public DateTime RealTimeUtc { get; set; }
            public string CampaignTime { get; set; }
            public double CampaignDays { get; set; }
            public bool Answered { get; set; }
            public string Answer { get; set; }
        }

        internal static CapturedPopup Record(string kind, string title, string text, params string[] options)
        {
            var popup = new CapturedPopup
            {
                Kind = kind,
                Title = title ?? string.Empty,
                Text = text ?? string.Empty,
                Options = options ?? Array.Empty<string>(),
                RealTimeUtc = DateTime.UtcNow,
                CampaignTime = Campaign.Current != null ? TaleWorlds.CampaignSystem.CampaignTime.Now.ToString() : null,
                CampaignDays = Campaign.Current != null ? TaleWorlds.CampaignSystem.CampaignTime.Now.ToDays : 0d,
            };

            lock (Gate)
            {
                popup.Sequence = nextSequence++;
                Captured.Add(popup);
                if (Captured.Count > Capacity) Captured.RemoveAt(0);
            }

            // Logged as well as stored: the log is what survives a process that dies before anyone asks.
            Logger.Information("[Popup] {Kind} #{Sequence} title='{Title}' options=[{Options}] text='{Text}'",
                kind, popup.Sequence, popup.Title, string.Join(" | ", popup.Options), Truncate(popup.Text, 300));

            return popup;
        }

        /// <summary>Everything captured, oldest first.</summary>
        public static IReadOnlyList<CapturedPopup> Snapshot()
        {
            lock (Gate) { return Captured.ToList(); }
        }

        public static int Clear()
        {
            lock (Gate)
            {
                int count = Captured.Count;
                Captured.Clear();
                return count;
            }
        }

        private static string Truncate(string value, int max)
        {
            if (string.IsNullOrEmpty(value) || value.Length <= max) return value;
            return value.Substring(0, max) + "...";
        }

        // ---- C27: the campaign's message feed -------------------------------------------------------
        //
        // Kept in its own buffer rather than mixed into the popup log above. The two have opposite shapes: a
        // modal is rare and demands an answer, a message is a stream and demands nothing. Sharing one bounded
        // list would let a few seconds of ordinary campaign chatter evict the inquiry that actually mattered,
        // which is the one record nobody can reconstruct afterwards.
        private const int MessageCapacity = 500;
        private static readonly List<CapturedMessage> Messages = new List<CapturedMessage>();
        private static long nextMessageSequence = 1;

        public sealed class CapturedMessage
        {
            public long Sequence { get; set; }
            public string Text { get; set; }
            public DateTime RealTimeUtc { get; set; }
            public string CampaignTime { get; set; }
            public double CampaignDays { get; set; }
        }

        internal static void RecordMessage(string text)
        {
            if (string.IsNullOrEmpty(text)) return;

            var message = new CapturedMessage
            {
                Text = text,
                RealTimeUtc = DateTime.UtcNow,
                CampaignTime = Campaign.Current != null ? TaleWorlds.CampaignSystem.CampaignTime.Now.ToString() : null,
                CampaignDays = Campaign.Current != null ? TaleWorlds.CampaignSystem.CampaignTime.Now.ToDays : 0d,
            };

            lock (Gate)
            {
                message.Sequence = nextMessageSequence++;
                Messages.Add(message);
                if (Messages.Count > MessageCapacity) Messages.RemoveAt(0);
            }
        }

        public static IReadOnlyList<CapturedMessage> MessageSnapshot()
        {
            lock (Gate) { return Messages.ToList(); }
        }

        public static int ClearMessages()
        {
            lock (Gate)
            {
                int count = Messages.Count;
                Messages.Clear();
                return count;
            }
        }

        internal static void MarkAnswered(CapturedPopup popup, string answer)
        {
            if (popup == null) return;
            lock (Gate)
            {
                popup.Answered = true;
                popup.Answer = answer;
            }
        }

        internal static string TextOf(TextObject text)
        {
            if (text == null) return null;
            try { return text.ToString(); }
            catch { return "<unreadable TextObject>"; }
        }
    }

    /// <summary>
    /// Diverts modal inquiries into <see cref="PopupCapture"/> on a render-free process.
    /// </summary>
    /// <remarks>
    /// Prefix returning false, so the engine never tries to build a UI it has no context for. On a rendered
    /// process this does nothing at all - the popup is shown exactly as before.
    /// </remarks>
    [HarmonyPatch(typeof(InformationManager))]
    internal static class PopupCaptureInquiryPatches
    {
        // Bound positionally with __0 rather than by parameter name. Harmony matches prefix parameters by
        // NAME, and these two disagree - ShowInquiry takes "data" while ShowTextInquiry takes "textData".
        // Getting it wrong is not a quiet no-op: the patch class throws during PatchAll, which aborts the
        // whole patch set, and the process then fails to start a session at all. That is C2's "a patch
        // failure is loud" property doing its job, and it cost a rig outage to learn.
        [HarmonyPatch(nameof(InformationManager.ShowInquiry))]
        [HarmonyPrefix]
        private static bool ShowInquiryPrefix(InquiryData __0)
        {
            if (!ModInformation.IsHeadless) return true;

            var popup = PopupCapture.Record(
                "inquiry",
                __0?.TitleText,
                __0?.Text,
                __0?.AffirmativeText,
                __0?.NegativeText);

            AnswerInquiry(popup, __0?.TitleText, __0?.Text, __0?.AffirmativeAction, __0?.NegativeAction);
            return false;
        }

        /// <summary>
        /// C23 - answers the modal so the flow behind it continues, under the default-deny rule.
        /// </summary>
        /// <remarks>
        /// The answer's action is invoked here rather than merely recorded, because recording alone leaves
        /// the caller waiting forever - a captured popup and a hung client look identical from outside, and
        /// that was the state C22 left things in.
        ///
        /// A throwing action must not escape into the engine's inquiry call, so it is contained: the run is
        /// already failing in that case and the useful outcome is a log line naming the popup, not a second
        /// crash on top of the first.
        /// </remarks>
        internal static void AnswerInquiry(
            PopupCapture.CapturedPopup popup,
            string title,
            string text,
            Action affirmative,
            Action negative)
        {
            var answer = PopupPolicy.Decide(title, text, out bool wasDeclared);
            if (!wasDeclared) PopupPolicy.FailRun(title, text);

            PopupCapture.MarkAnswered(popup, wasDeclared ? answer.ToString() : answer + " (undeclared)");

            try
            {
                if (answer == PopupPolicy.Answer.Affirmative) affirmative?.Invoke();
                else negative?.Invoke();
            }
            catch (Exception exception)
            {
                LogManager.GetLogger(typeof(PopupCaptureInquiryPatches))
                    .Error(exception, "[Popup] answering '{Title}' threw", title);
            }
        }

        [HarmonyPatch(nameof(InformationManager.ShowTextInquiry))]
        [HarmonyPrefix]
        private static bool ShowTextInquiryPrefix(TextInquiryData __0)
        {
            if (!ModInformation.IsHeadless) return true;

            var popup = PopupCapture.Record(
                "text-inquiry",
                __0?.TitleText,
                __0?.Text,
                __0?.AffirmativeText,
                __0?.NegativeText);

            // A text inquiry's affirmative action takes the typed string. Answering one affirmatively would
            // mean inventing input on the player's behalf, so the affirmative path is not wired at all here -
            // it can only ever be declined, and a scenario that needs typed input needs a real verb for it.
            AnswerInquiry(popup, __0?.TitleText, __0?.Text, null, __0?.NegativeAction);
            return false;
        }
    }

    /// <summary>
    /// Diverts the corner "quick information" messages into <see cref="PopupCapture"/>.
    /// </summary>
    /// <remarks>
    /// Not modal, so these cannot hang anything - but they carry most of what the campaign tells a player
    /// about itself, which is what C27 needs and what a behavioural comparison reads.
    /// </remarks>
    [HarmonyPatch(typeof(MBInformationManager))]
    internal static class PopupCaptureQuickInformationPatches
    {
        [HarmonyPatch(nameof(MBInformationManager.AddQuickInformation))]
        [HarmonyPrefix]
        private static bool AddQuickInformationPrefix(TextObject __0)
        {
            if (!ModInformation.IsHeadless) return true;

            PopupCapture.Record("quick", null, PopupCapture.TextOf(__0));
            return false;
        }
    }

    /// <summary>
    /// C27 - diverts the campaign's message feed into <see cref="PopupCapture"/>.
    /// </summary>
    /// <remarks>
    /// <c>DisplayMessage</c> is where the campaign narrates itself: raids, recruitment, wages, war
    /// declarations, everything the message log shows a player. On a render-free process those go nowhere,
    /// which is a real loss for behavioural observation - a client that is being told something alarming
    /// every hour looks identical to a quiet one.
    ///
    /// High volume, so its own buffer with its own capacity. Not logged per message either: writing hundreds
    /// of lines a minute into the log is how the signal in it gets destroyed, and the buffer is queryable on
    /// demand.
    /// </remarks>
    [HarmonyPatch(typeof(InformationManager))]
    internal static class MessageCapturePatches
    {
        [HarmonyPatch(nameof(InformationManager.DisplayMessage))]
        [HarmonyPrefix]
        private static bool DisplayMessagePrefix(InformationMessage __0)
        {
            if (!ModInformation.IsHeadless) return true;

            PopupCapture.RecordMessage(__0.Information);
            return false;
        }
    }
}
