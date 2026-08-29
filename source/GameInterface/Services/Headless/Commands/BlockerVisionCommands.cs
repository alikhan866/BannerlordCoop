using Common;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.BarterSystem;
using TaleWorlds.CampaignSystem.Conversation;
using TaleWorlds.CampaignSystem.Encounters;
using TaleWorlds.CampaignSystem.GameMenus;
using TaleWorlds.Core;
using TaleWorlds.MountAndBlade;
using static TaleWorlds.Library.CommandLineFunctionality;

namespace GameInterface.Services.Headless.Commands;

/// <summary>
/// One answer to "what is this process waiting on, and what can I do about it?".
/// </summary>
/// <remarks>
/// WHY THIS EXISTS
/// Driving a headless client into a battle kept failing in a different place each time - a lord's parley, a
/// bandit demand, a barter screen that never opens, an encounter menu that needs activating first. Each was
/// diagnosed by reading a log after the fact and then guessing which of ~590 commands applied. That is the
/// wrong shape: the process knows exactly what it is blocked on, and it should say so.
///
/// So this reports the blocker rather than requiring the caller to deduce it, and names the actions that would
/// clear it. A scenario can then loop "where am I stuck / clear it" without knowing in advance whether this
/// particular battle opens with a conversation, a menu, or nothing at all.
///
/// PRIORITY ORDER IS THE WHOLE DESIGN
/// These states nest: a conversation runs on top of an encounter, a barter runs on top of a conversation, and
/// a menu can be current while a conversation covers it. Reporting the OUTERMOST thing would tell a caller to
/// act on something it cannot reach. So the checks run innermost-first and the first hit wins.
///
/// LOOP DETECTION, BECAUSE THE STALL IS SILENT
/// A conversation node with no options is advanced by continuing. Some nodes never advance - a bandit barter
/// whose screen cannot open leaves the flow on "Mm. Pity." forever, and six continues in a row look exactly
/// like progress from the outside. advance therefore remembers the sentence it last saw and refuses to
/// continue past a repeat, reporting a stall instead of spinning. That failure cost an entire test cycle
/// before it was recognised.
///
/// ADVANCE DOES NOT INVENT CHOICES
/// Where a real player has a decision, advance takes the first CLICKABLE option, or one matching the caller's
/// goal, and says which it took. It never runs a consequence whose condition fails - that is the same rule
/// MenuOptionDebugCommand holds to, for the same reason: a rig that reaches states no player can reach
/// manufactures its own findings.
/// </remarks>
public static class BlockerVisionCommands
{
    // Remembered per process, so a repeated sentence can be recognised across separate advance calls - the
    // stall spans calls, which is exactly why a single call cannot see it.
    private static string lastSentence;
    private static int sameSentenceCount;

    [CommandLineArgumentFunction("state", "coop.debug.blocker")]
    public static string State(List<string> args)
    {
        if (args.Count != 0) return "Usage: coop.debug.blocker.state";
        var blocker = Detect();
        return blocker.Describe();
    }

    [CommandLineArgumentFunction("advance", "coop.debug.blocker")]
    public static string Advance(List<string> args)
    {
        if (args.Count > 1) return "Usage: coop.debug.blocker.advance [goal]   (goal: fight | leave | release | any)";
        string goal = args.Count == 1 ? args[0].ToLowerInvariant() : "any";

        var blocker = Detect();
        switch (blocker.Kind)
        {
            case "CONVERSATION_CHOICE": return AdvanceConversationChoice(blocker, goal);
            case "CONVERSATION_CONTINUE": return AdvanceConversationContinue();
            case "BARTER": return EscapeBarter();
            case "MENU": return AdvanceMenu(goal);
            case "DEPLOYMENT": return "BLOCKER_ADVANCE kind=DEPLOYMENT action=none " +
                                      "hint=use coop.debug.mapevent.click_deployment_ready";
            case "MISSION":
                // A mission is only a blocker if the caller wanted to be somewhere else, so leaving needs an
                // explicit goal. Without one this stays a report: ending a battle a scenario is midway through
                // observing would destroy the thing it was measuring.
                return goal == "leave"
                    ? "BLOCKER_ADVANCE kind=MISSION action=leave " + BattleLeaveCommand.Execute(retreat: false)
                    : "BLOCKER_ADVANCE kind=MISSION action=none note=already in a mission; " +
                      "pass 'leave' to exit, or use coop.debug.battle.leave";
            case "NONE": return "BLOCKER_ADVANCE kind=NONE action=none note=nothing is blocking";
            default: return $"BLOCKER_ADVANCE kind={blocker.Kind} action=none note=no automatic action";
        }
    }

    /// <summary>Gets out of a conversation this process cannot finish, by force if the clean exit fails.</summary>
    [CommandLineArgumentFunction("escape", "coop.debug.blocker")]
    public static string Escape(List<string> args)
    {
        if (args.Count != 0) return "Usage: coop.debug.blocker.escape";

        var manager = Campaign.Current?.ConversationManager;
        if (manager == null) return "BLOCKER_ESCAPE action=none reason=no-campaign";
        if (!Safe(() => manager.IsConversationInProgress, false))
            return "BLOCKER_ESCAPE action=none wasInConversation=false";

        string sentence = Trim(Safe(() => manager.CurrentSentenceText) ?? "");
        string result = ForceEndConversation(manager);
        ResetStall();
        return $"BLOCKER_ESCAPE wasInConversation=true sentence=\"{sentence}\" {result}";
    }

    [CommandLineArgumentFunction("reset", "coop.debug.blocker")]
    public static string Reset(List<string> args)
    {
        if (args.Count != 0) return "Usage: coop.debug.blocker.reset";
        lastSentence = null;
        sameSentenceCount = 0;
        return "BLOCKER_RESET stallTracking=cleared";
    }

    private sealed class Blocker
    {
        public string Kind = "NONE";
        public string Detail = "";
        public List<string> Options = new List<string>();
        public string Actions = "";

        public string Describe()
        {
            var report = new StringBuilder();
            report.AppendLine($"BLOCKER kind={Kind} {Detail} readiness={PartyBattleReadinessCommand.LocalVerdict()}");
            foreach (var option in Options) report.AppendLine("  " + option);
            if (!string.IsNullOrEmpty(Actions)) report.Append($"  actions: {Actions}");
            return report.ToString().TrimEnd();
        }
    }

    // Innermost first - see the remarks. Each check is defensive because this command is most useful exactly
    // when the process is in a state its author did not anticipate.
    private static Blocker Detect()
    {
        if (Campaign.Current == null)
            return new Blocker { Kind = "NO_CAMPAIGN", Detail = "campaign=false" };

        var barter = DescribeBarterIfOpen();
        if (barter != null) return barter;

        var manager = Campaign.Current.ConversationManager;
        if (manager != null && manager.IsConversationInProgress)
        {
            var options = manager.CurOptions ?? new List<ConversationSentenceOption>();
            string sentence = Safe(() => manager.CurrentSentenceText) ?? "";
            if (options.Count > 0)
            {
                var blocker = new Blocker
                {
                    Kind = "CONVERSATION_CHOICE",
                    Detail = $"options={options.Count} sentence=\"{Trim(sentence)}\" " +
                             $"needsActivation={Lower(Safe(() => manager.NeedsToActivateForMapConversation, false))} " +
                             $"with={Safe(() => manager.OneToOneConversationHero?.StringId) ?? "none"}",
                    Actions = "coop.debug.blocker.advance [fight|leave] | coop.debug.blocker.escape",
                };
                for (int index = 0; index < options.Count; index++)
                {
                    var option = options[index];
                    blocker.Options.Add(
                        $"[{index}] id={Safe(() => option.Id) ?? "?"} " +
                        $"clickable={Lower(Safe(() => option.IsClickable, false))} " +
                        $"text=\"{Trim(Safe(() => option.Text?.ToString()) ?? "")}\"");
                }
                return blocker;
            }

            return new Blocker
            {
                Kind = "CONVERSATION_CONTINUE",
                Detail = $"options=0 sentence=\"{Trim(sentence)}\" " +
                         $"needsActivation={Lower(Safe(() => manager.NeedsToActivateForMapConversation, false))} " +
                         $"repeatedSoFar={(sentence == lastSentence ? sameSentenceCount : 0)}",
                Actions = "coop.debug.blocker.advance | coop.debug.blocker.escape",
            };
        }

        var mission = Mission.Current;
        if (mission != null)
        {
            bool deploying = Safe(() =>
                mission.GetMissionBehavior<DeploymentMissionController>()?.TeamSetupOver == false, false);
            if (deploying)
                return new Blocker
                {
                    Kind = "DEPLOYMENT",
                    Detail = "teamSetupOver=false",
                    Actions = "coop.debug.mapevent.click_deployment_ready",
                };

            return new Blocker
            {
                Kind = "MISSION",
                Detail = $"scene={Safe(() => mission.SceneName) ?? "?"} " +
                         $"agents={Safe(() => mission.Agents?.Count, 0)}",
                Actions = "coop.debug.battle.snapshot | coop.debug.battle.leave",
            };
        }

        var menuContext = Campaign.Current.CurrentMenuContext;
        if (menuContext?.GameMenu != null)
        {
            string menuId = Safe(() => menuContext.GameMenu.StringId) ?? "unknown";
            int count = Safe(() => menuContext.GameMenu.MenuItemAmount, 0);
            return new Blocker
            {
                Kind = "MENU",
                Detail = $"menu={menuId} options={count} " +
                         $"{(count == 0 ? "activated=false" : "activated=true")}",
                Actions = count == 0
                    ? $"coop.debug.menu.activate {menuId}  (options appear only after activation)"
                    : "coop.debug.menu.invoke_id <id> | coop.debug.blocker.advance fight",
            };
        }

        if (PlayerEncounter.Current != null)
            return new Blocker
            {
                Kind = "ENCOUNTER",
                Detail = "encounter=present menu=none",
                Actions = "coop.debug.menu.activate encounter_meeting",
            };

        return new Blocker { Kind = "NONE", Detail = "state=map" };
    }

    private static Blocker DescribeBarterIfOpen()
    {
        // Read through reflection deliberately. Whether a barter is OPEN is not on BarterManager's public
        // surface under a stable name, and this command must not fail to report the other blockers just
        // because one probe did not resolve - a partial answer here is still the difference between a caller
        // that knows where it is and one that does not.
        var instance = Safe(() => BarterManager.Instance);
        if (instance == null) return null;

        foreach (string name in new[] { "LastBarterData", "CurrentBarterData", "_currentBarterData", "_lastBarterData" })
        {
            object value = ReadMember(instance, name);
            if (value == null) continue;

            bool accepted = Safe(() => instance.LastBarterIsAccepted, false);
            return new Blocker
            {
                Kind = "BARTER",
                Detail = $"field={name} accepted={Lower(accepted)}",
                Actions = "coop.debug.blocker.advance  (cancels and closes the barter)",
            };
        }

        return null;
    }

    private static string AdvanceConversationChoice(Blocker blocker, string goal)
    {
        var manager = Campaign.Current?.ConversationManager;
        var options = manager?.CurOptions ?? new List<ConversationSentenceOption>();
        if (options.Count == 0) return "BLOCKER_ADVANCE kind=CONVERSATION_CHOICE action=none note=options vanished";

        // Goal words are matched against the option TEXT because conversation ids are per-quest and not a
        // vocabulary a caller can be expected to know. Falling back to the first clickable option keeps the
        // command useful when nothing matches, and the chosen option is always reported so a scenario can
        // assert on what it actually picked rather than trusting the goal.
        string[] wanted = goal == "fight"
            ? new[] { "fight", "attack", "no ", "never", "refuse", "die", "blood", "sword" }
            : goal == "leave"
                ? new[] { "leave", "go", "farewell", "later", "nothing" }
                : goal == "release"
                    // The free-or-capture conversation after a battle. "go free" and "let ... go" are the
                    // usual phrasings; "ransom" and "prisoner" are deliberately absent because those are the
                    // opposite choice and matching them would capture the lord this goal exists to free.
                    ? new[] { "release", "free", "let him go", "let her go", "let them go", "set free", "spare" }
                    : Array.Empty<string>();

        int chosen = -1;
        if (wanted.Length > 0)
        {
            for (int index = 0; index < options.Count && chosen < 0; index++)
            {
                if (!(Safe(() => options[index].IsClickable, false))) continue;
                string text = (Safe(() => options[index].Text?.ToString()) ?? "").ToLowerInvariant();
                if (wanted.Any(word => text.Contains(word))) chosen = index;
            }
        }
        if (chosen < 0)
            chosen = options.FindIndex(option => Safe(() => option.IsClickable, false));
        if (chosen < 0) return "BLOCKER_ADVANCE kind=CONVERSATION_CHOICE action=none note=no clickable option";

        string id = Safe(() => options[chosen].Id) ?? "";
        string chosenText = Trim(Safe(() => options[chosen].Text?.ToString()) ?? "");

        // Selecting an option in a MAP conversation throws: DoOption walks a path that casts the speaker to
        // TaleWorlds.MountAndBlade.Agent, and a map conversation's speaker is a MapConversationAgent. It is
        // the same window NeedsToActivateForMapConversation describes - a conversation that exists but has no
        // mission behind it - and it is not specific to this command: coop.debug.conversation.select_index
        // fails identically on the same node.
        //
        // A driven client meeting a lord on the map would otherwise stop dead there, which is precisely the
        // dead end this whole command exists to remove. So the throw is caught, reported by name so the
        // underlying defect stays visible, and the conversation is ENDED to hand control back. Ending is the
        // same escape a person has (walk away), and leaves the encounter behind it intact.
        try
        {
            manager.DoOption(id);
        }
        catch (Exception selectFailure)
        {
            string recovered = ForceEndConversation(manager);
            ResetStall();
            return $"BLOCKER_ADVANCE kind=CONVERSATION_CHOICE action=SELECT_FAILED goal={goal} " +
                   $"index={chosen} id={id} error=\"{Describe(selectFailure)}\" at={Where(selectFailure)} " +
                   $"recovered={recovered}";
        }

        ResetStall();

        return $"BLOCKER_ADVANCE kind=CONVERSATION_CHOICE action=select goal={goal} " +
               $"index={chosen} id={id} text=\"{chosenText}\" " +
               $"nowInProgress={Lower(Safe(() => manager.IsConversationInProgress, false))} " +
               $"nowSentence=\"{Trim(Safe(() => manager.CurrentSentenceText) ?? "")}\"";
    }

    private static string AdvanceConversationContinue()
    {
        var manager = Campaign.Current?.ConversationManager;
        if (manager?.IsConversationInProgress != true)
            return "BLOCKER_ADVANCE kind=CONVERSATION_CONTINUE action=none note=conversation ended";

        string before = Safe(() => manager.CurrentSentenceText) ?? "";
        if (before == lastSentence)
        {
            sameSentenceCount++;
            if (sameSentenceCount >= 2)
                return $"BLOCKER_ADVANCE kind=CONVERSATION_CONTINUE action=REFUSED reason=stalled " +
                       $"repeats={sameSentenceCount} sentence=\"{Trim(before)}\" " +
                       $"hint=this node does not advance; use coop.debug.blocker.escape";
        }
        else
        {
            lastSentence = before;
            sameSentenceCount = 0;
        }

        manager.DoOptionContinue();
        string after = Safe(() => manager.CurrentSentenceText) ?? "";
        lastSentence = after;

        return $"BLOCKER_ADVANCE kind=CONVERSATION_CONTINUE action=continue " +
               $"inProgress={Lower(Safe(() => manager.IsConversationInProgress, false))} " +
               $"sentence=\"{Trim(after)}\" changed={Lower(after != before)}";
    }

    private static string EscapeBarter()
    {
        var instance = Safe(() => BarterManager.Instance);
        if (instance == null) return "BLOCKER_ADVANCE kind=BARTER action=none note=no barter manager";

        // Closing rather than accepting. An accepted barter moves gold and troops, so a rig that cleared a
        // blocker by accepting would silently change the world it is supposed to be observing.
        bool closed = Safe(() => { instance.Close(); return true; }, false);
        ResetStall();
        return $"BLOCKER_ADVANCE kind=BARTER action=close closed={Lower(closed)}";
    }

    private static string AdvanceMenu(string goal)
    {
        var menuContext = Campaign.Current?.CurrentMenuContext;
        var menu = menuContext?.GameMenu;
        if (menu == null)
            return "BLOCKER_ADVANCE kind=MENU action=none note=menu vanished";

        string menuId = Safe(() => menu.StringId) ?? "";
        int count = Safe(() => menu.MenuItemAmount, 0);

        // An unactivated menu has no options to choose between, so activation IS the advance. Reported
        // separately rather than chained into a pick, because a caller that asked to see the menu should not
        // silently have an option taken for it on the same call.
        if (count == 0)
        {
            GameMenu.ActivateGameMenu(menuId);
            int now = Safe(() => Campaign.Current.CurrentMenuContext?.GameMenu?.MenuItemAmount ?? 0, 0);
            return $"BLOCKER_ADVANCE kind=MENU action=activate menu={menuId} options={now}";
        }

        // No goal means no pick. Choosing "the first enabled option" on an encounter menu could surrender a
        // battle or abandon an army, and a caller that did not say what it wanted has not consented to that.
        if (goal != "fight" && goal != "leave")
            return $"BLOCKER_ADVANCE kind=MENU action=none menu={menuId} options={count} " +
                   $"note=pass a goal (fight|leave) or pick with coop.debug.menu.invoke_id";

        // Preference order, then any enabled option of the right shape. Ids are matched before text because a
        // menu id is stable while its text is localised.
        string[] preferred = goal == "fight"
            ? new[] { "attack", "str_order_attack", "continue_preparations" }
            : new[] { "leave", "go_back_to_settlement", "abandon_army" };

        int chosen = -1;
        string chosenId = null;
        foreach (string want in preferred)
        {
            for (int index = 0; index < count && chosen < 0; index++)
            {
                string id = Safe(() => menu.GetMenuOptionIdString(index)) ?? "";
                if (!string.Equals(id, want, StringComparison.OrdinalIgnoreCase)) continue;
                if (!Safe(() => menu.GetMenuOptionConditionsHold(Game.Current, menuContext, index), false)) continue;
                chosen = index;
                chosenId = id;
            }
            if (chosen >= 0) break;
        }

        if (chosen < 0)
        {
            for (int index = 0; index < count && chosen < 0; index++)
            {
                if (!Safe(() => menu.GetMenuOptionConditionsHold(Game.Current, menuContext, index), false)) continue;
                bool isLeave = Safe(() => menu.GetMenuOptionIsLeave(index), false);
                if (goal == "fight" && isLeave) continue;
                if (goal == "leave" && !isLeave) continue;
                chosen = index;
                chosenId = Safe(() => menu.GetMenuOptionIdString(index)) ?? "";
            }
        }

        if (chosen < 0)
            return $"BLOCKER_ADVANCE kind=MENU action=none menu={menuId} options={count} goal={goal} " +
                   $"note=no enabled option matches this goal";

        string before = Safe(() => menuContext.StringId) ?? "";
        menuContext.InvokeConsequence(chosen);
        var after = Campaign.Current?.CurrentMenuContext;

        return $"BLOCKER_ADVANCE kind=MENU action=invoke goal={goal} index={chosen} id={chosenId} " +
               $"menuBefore={before} menuAfter={Safe(() => after?.StringId) ?? "none"} " +
               $"options={Safe(() => after?.GameMenu?.MenuItemAmount ?? 0, 0)}";
    }

    // The FRAMES are reported, not only the message. An InvalidCastException names two types and no location,
    // and locating the cast by reasoning about which vanilla method might hold it cost two full test cycles
    // before this was added. A driven client that cannot say WHERE it broke is the exact blindness this command
    // set exists to remove, so the failure carries its own stack.
    private static string Where(Exception failure)
    {
        try
        {
            var frames = new System.Diagnostics.StackTrace(failure, false).GetFrames();
            if (frames == null || frames.Length == 0) return "<no-frames>";
            return string.Join("<-", frames.Take(6).Select(frame =>
            {
                var method = frame.GetMethod();
                return method == null ? "?" : $"{method.DeclaringType?.Name ?? "?"}.{method.Name}";
            }).ToArray());
        }
        catch { return "<unreadable>"; }
    }

    private static string Describe(Exception failure)
    {
        string text = $"{failure.GetType().Name}: {Trim(failure.Message)}";
        var inner = failure.InnerException;
        return inner == null ? text : $"{text} <- {inner.GetType().Name}: {Trim(inner.Message)}";
    }

    /// <summary>
    /// Leaves the current conversation, and says which rung it had to use to do it.
    /// </summary>
    /// <remarks>
    /// EndConversation is tried first and is the only CLEAN exit: it raises ConversationEnd, so whatever opened
    /// the conversation - an encounter, a quest - gets to tidy up after itself.
    ///
    /// It can throw on a headless client for the same reason selecting an option can, and that leaves the
    /// process permanently STUCK rather than merely blocked: IsConversationInProgress stays true, no mission can
    /// start behind it, and every later blocker report reads CONVERSATION forever. So there is a second rung
    /// that resets the manager's per-conversation state directly.
    ///
    /// The force path deliberately does NOT call ConversationManager.Clear(). That drops every sentence
    /// registered in the campaign, so the process would escape this conversation at the price of being unable to
    /// hold any other - trading a stuck client for a quietly broken one. Only per-conversation state is touched,
    /// and every field it could not reach is named in the result, so a caller is never told the escape was
    /// complete when it was partial.
    /// </remarks>
    internal static string ForceEndConversation(ConversationManager manager)
    {
        if (manager == null) return "none reason=no-manager";

        string clean = Safe(() => { manager.EndConversation(); return "ok"; }, (string)null) ?? "threw";
        if (clean == "ok" && !Safe(() => manager.IsConversationInProgress, false))
            return "endConversation stillInConversation=false";

        var cleared = new List<string>
        {
            "options=" + Lower(Safe(() => { manager.ClearCurrentOptions(); return true; }, false)),
            "inProgress=" + Lower(SetMember(manager, "<IsConversationInProgress>k__BackingField", false)),
            "isActive=" + Lower(SetMember(manager, "_isActive", false)),
            "needsActivation=" + Lower(SetMember(manager, "<NeedsToActivateForMapConversation>k__BackingField", false)),
            "speaker=" + Lower(SetMember(manager, "_speakerAgent", null)),
            "listener=" + Lower(SetMember(manager, "_listenerAgent", null)),
            "main=" + Lower(SetMember(manager, "_mainAgent", null)),
            "party=" + Lower(SetMember(manager, "_conversationParty", null)),
            "agents=" + Lower(Safe(() =>
            {
                (ReadMember(manager, "_conversationAgents") as System.Collections.IList)?.Clear();
                return true;
            }, false)),
            "context=" + Lower(Safe(() =>
            {
                Campaign.Current.CurrentConversationContext = ConversationContext.Default;
                return true;
            }, false)),
        };

        return $"forceClear endConversation={clean} " +
               $"stillInConversation={Lower(Safe(() => manager.IsConversationInProgress, false))} " +
               $"[{string.Join(" ", cleared.ToArray())}]";
    }

    private static bool SetMember(object target, string name, object value)
    {
        if (target == null) return false;
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        try
        {
            var field = target.GetType().GetField(name, flags);
            if (field != null) { field.SetValue(target, value); return true; }
            var property = target.GetType().GetProperty(name, flags);
            if (property?.CanWrite != true) return false;
            property.SetValue(target, value);
            return true;
        }
        catch { return false; }
    }

    private static void ResetStall()
    {
        lastSentence = null;
        sameSentenceCount = 0;
    }

    private static object ReadMember(object target, string name)
    {
        if (target == null) return null;
        var type = target.GetType();
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        try
        {
            var property = type.GetProperty(name, flags);
            if (property != null) return property.GetValue(target);
            var field = type.GetField(name, flags);
            return field?.GetValue(target);
        }
        catch { return null; }
    }

    private static T Safe<T>(Func<T> read, T fallback = default)
    {
        try { return read(); }
        catch { return fallback; }
    }

    private static string Trim(string text)
    {
        if (string.IsNullOrEmpty(text)) return "";
        text = text.Replace("\r", " ").Replace("\n", " ").Trim();
        return text.Length <= 90 ? text : text.Substring(0, 87) + "...";
    }

    private static string Lower(bool value) => value.ToString().ToLowerInvariant();
}
