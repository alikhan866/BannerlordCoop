using System;
using System.Threading;
using Common;
using Common.Logging;
using Common.Messaging;
using GameInterface.Services.Heroes.Messages.LordConversations;
using HarmonyLib;
using Helpers;
using Serilog;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.CampaignBehaviors;
using TaleWorlds.CampaignSystem.Encounters;

namespace GameInterface.Services.Heroes.Patches;

[HarmonyPatch(typeof(LordConversationsCampaignBehavior))]
internal class LordConversationsCampaignBehaviorPatches
{
    private static readonly ILogger Logger = LogManager.GetLogger<LordConversationsCampaignBehaviorPatches>();

    /// <summary>The name of the private condition guarded below. Also what the applied-patch test asserts on.</summary>
    internal const string LordMakesCommentCondition = "conversation_lord_makes_comment_on_condition";

    private static int commentFailuresSuppressed;

    /// <summary>
    /// Stops a broken campaign log entry from freezing every lord conversation.
    /// </summary>
    /// <remarks>
    /// This condition asks the campaign's log history for something the lord could remark on, and the walk calls
    /// <c>GetConversationScoreAndComment</c> on every entry - seventeen different <c>LogEntry</c> subclasses
    /// implement it, each dereferencing whatever it happens to reference. An entry that outlives what it points
    /// at therefore throws from inside a dialogue condition.
    ///
    /// One did. A <c>DeclareWarLogEntry</c> naming a destroyed rebel faction dereferenced Faction1, its Leader
    /// and that Leader's Clan with no null guard, so it threw a NullReferenceException every time the condition
    /// ran. The exception escaped into ConversationManager.ContinueConversation, was swallowed higher up by
    /// ScreenManagerRobustnessPatches, and the conversation simply never advanced: measured at 36 clicks on
    /// "Continue" producing 36 identical exceptions and no progress, with no error shown to the player. The only
    /// way out was to leave the server and rejoin.
    ///
    /// Guarded HERE rather than on the log entry, because this condition is the ONLY caller of
    /// <c>GetRelevantComment</c>. One guard therefore covers all seventeen entry types, and any the game adds
    /// later, instead of chasing whichever one is broken this time.
    ///
    /// A failure means the lord makes no remark - a line of flavour text - and the conversation carries on. That
    /// is the whole cost, and it is paid only when the alternative is a dialogue that cannot be closed.
    /// </remarks>
    [HarmonyPatch(LordMakesCommentCondition)]
    [HarmonyFinalizer]
    private static Exception LordMakesCommentFinalizer(Exception __exception, ref bool __result)
        => SuppressCommentFailure(__exception, ref __result);

    /// <summary>
    /// The decision itself: swallow a throw and answer "no comment", leave a clean run alone.
    /// </summary>
    /// <remarks>
    /// Split out so it can be asserted without an engine, and because a finalizer that returns the exception it
    /// was handed is indistinguishable from one that suppresses it unless you read the return very carefully.
    /// Returning null is what suppresses; returning <paramref name="exception"/> would rethrow and change
    /// nothing.
    /// </remarks>
    internal static Exception SuppressCommentFailure(Exception exception, ref bool result)
    {
        if (exception == null) return null;

        // The condition threw, so its return value is undefined - say the lord has nothing to remark on.
        result = false;

        // Once per session at Warning, then silence. This fires per dialogue line evaluated, so a player who
        // keeps talking would otherwise write thousands of identical lines and bury everything else.
        int count = Interlocked.Increment(ref commentFailuresSuppressed);
        if (count == 1)
        {
            Logger.Warning(exception,
                "[Conversation] A campaign log entry threw while looking for a lord's remark; the remark is skipped so the " +
                "conversation can continue. Further occurrences are counted, not logged");
        }

        return null;
    }

    /// <summary>How many condition failures have been suppressed this session; for tests and diagnostics.</summary>
    internal static int CommentFailuresSuppressed => Volatile.Read(ref commentFailuresSuppressed);

    internal static void ResetCommentFailureCount() => Interlocked.Exchange(ref commentFailuresSuppressed, 0);

    [HarmonyPatch(nameof(LordConversationsCampaignBehavior.OnBarterAccepted))]
    [HarmonyPrefix]
    public static bool OnBarterAcceptedPrefix()
    {
        return !ModInformation.IsServer;
    }

    [HarmonyPatch(nameof(LordConversationsCampaignBehavior.conversation_player_liberates_prisoner_on_consequence))]
    [HarmonyPrefix]
    public static bool ConversationPlayerLiberatesPrisonerOnConsequencePrefix()
    {
        var message = new LiberateLordPrisoner(Hero.MainHero, Hero.OneToOneConversationHero);
        MessageBroker.Instance.Publish(null, message);
        MarkFreedLordConversationHandled(Hero.OneToOneConversationHero);

        return false;
    }

    /// <summary>
    /// Letting a prisoner go, which vanilla rewards with +4 relation.
    /// </summary>
    /// <remarks>
    /// Unpatched, this dialogue half-worked on a client: the release itself replicated, because
    /// <c>EndCaptivityActionPatches</c> forwards it to the server, but the relation gain did not. Vanilla
    /// awards it with a SEPARATE <c>ChangeRelationAction.ApplyPlayerRelation</c> call, and that ends up in
    /// <c>ChangeRelationAction.ApplyInternal</c>, whose prefix is <c>ModInformation.IsServer</c>. On a client
    /// that is false, so the relation change was dropped where it stood - nothing applied, nothing sent, and
    /// nothing logged. The lord walked free and the player got no credit for it.
    ///
    /// Every sibling release dialogue is already forwarded this way. This one was simply missed.
    /// <c>LordFreedToRelease</c> is reused rather than duplicated: its handler applies exactly what this
    /// consequence needs - <c>ApplyByReleasedByChoice</c> plus the same +4 - against the requesting player's
    /// own hero instead of the server's.
    /// </remarks>
    [HarmonyPatch(nameof(LordConversationsCampaignBehavior.conversation_player_let_prisoner_go_on_consequence))]
    [HarmonyPrefix]
    public static bool ConversationPlayerLetPrisonerGoOnConsequencePrefix()
    {
        var message = new LordFreedToRelease(Hero.MainHero, Hero.OneToOneConversationHero);
        MessageBroker.Instance.Publish(null, message);
        MarkFreedLordConversationHandled(Hero.OneToOneConversationHero);

        // Vanilla ends the encounter here. That is local screen state, not something the server owns, so it
        // stays on this machine - without it the player is left standing in the encounter after the
        // prisoner has walked away.
        if (PlayerEncounter.Current != null)
        {
            PlayerEncounter.LeaveEncounter = true;
        }

        return false;
    }

    [HarmonyPatch(nameof(LordConversationsCampaignBehavior.conversation_player_fails_to_release_prisoner_on_consequence))]
    [HarmonyPrefix]
    public static bool ConversationPlayerFailsToReleasePrisonerOnConsequencePrefix()
    {
        if (!Hero.OneToOneConversationHero.IsPrisoner) return false;

        var message = new TakeLordPrisoner(Campaign.Current.MainParty.Party, Hero.OneToOneConversationHero);
        MessageBroker.Instance.Publish(null, message);
        MarkFreedLordConversationHandled(Hero.OneToOneConversationHero);

        return false;
    }

    [HarmonyPatch(nameof(LordConversationsCampaignBehavior.conversation_ally_thanks_meet_after_helping_in_battle_2_on_consequence))]
    [HarmonyPrefix]
    public static bool ConversationAllyThanksMeetAfterHelpingInBattle2OnConsequencePrefix()
    {
        // TODO: PlayerMapEvent will be null here. Need to get the relation change without it
        //int playerGainedRelationAmount = Campaign.Current.Models.BattleRewardModel.GetPlayerGainedRelationAmount(MapEvent.PlayerMapEvent, Hero.OneToOneConversationHero);

        var message = new LordHelpedInBattle(Hero.MainHero, Hero.OneToOneConversationHero); // playerGainedRelationAmount
        MessageBroker.Instance.Publish(null, message);

        return false;
    }

    [HarmonyPatch(nameof(LordConversationsCampaignBehavior.conversation_talk_lord_defeat_to_lord_capture_on_consequence))]
    [HarmonyPrefix]
    public static bool ConversationTalkLordDefeatToLordCaptureOnConsequencePrefix()
    {
        Campaign.Current.CurrentConversationContext = ConversationContext.Default;

        var message = new TakeLordPrisoner(Campaign.Current.MainParty.Party, CharacterObject.OneToOneConversationCharacter.HeroObject);
        MessageBroker.Instance.Publish(null, message);

        return false;
    }

    [HarmonyPatch(nameof(LordConversationsCampaignBehavior.conversation_talk_lord_defeat_to_lord_release_on_consequence))]
    [HarmonyPrefix]
    public static bool ConversationTalkLordDefeatToLordReleaseOnConsequencePrefix()
    {
        DialogHelper.SetDialogString("DEFEAT_LORD_ANSWER", "str_prisoner_released");

        var message = new LordDefeatToRelease(Hero.MainHero, Hero.OneToOneConversationHero);
        MessageBroker.Instance.Publish(null, message);

        return false;
    }

    [HarmonyPatch(nameof(LordConversationsCampaignBehavior.conversation_talk_lord_freed_to_lord_capture_on_consequence))]
    [HarmonyPrefix]
    public static bool ConversationTalkLordFreedToLordCaptureOnConsequencePrefix()
    {
        Campaign.Current.CurrentConversationContext = ConversationContext.Default;

        var message = new TakeLordPrisoner(Campaign.Current.MainParty.Party, Hero.OneToOneConversationHero);
        MessageBroker.Instance.Publish(null, message);
        MarkFreedLordConversationHandled(Hero.OneToOneConversationHero);

        return false;
    }

    [HarmonyPatch(nameof(LordConversationsCampaignBehavior.conversation_talk_lord_freed_to_lord_release_on_consequence))]
    [HarmonyPrefix]
    public static bool ConversationTalkLordFreedToLordReleaseOnConsequencePrefix()
    {
        var message = new LordFreedToRelease(Hero.MainHero, Hero.OneToOneConversationHero);
        MessageBroker.Instance.Publish(null, message);
        MarkFreedLordConversationHandled(Hero.OneToOneConversationHero);

        return false;
    }

    private static void MarkFreedLordConversationHandled(Hero conversationHero)
    {
        var pendingHeroes = PlayerEncounter.Current?._capturedAlreadyPrisonerHeroes;
        if (pendingHeroes == null) return;

        // Vanilla advances this list through its synchronous captivity action before the next encounter update.
        pendingHeroes.RemoveAll(element => element.Character?.HeroObject == conversationHero);
    }
}
