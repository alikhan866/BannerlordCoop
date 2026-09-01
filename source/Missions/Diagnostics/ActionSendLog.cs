using System;
using System.Globalization;
using System.Text;

namespace Missions.Diagnostics;

/// <summary>
/// The sending half: what the owner's client detects about its own agents, and what it actually publishes.
/// </summary>
/// <remarks>
/// <para>
/// A side-by-side timeline of the same agent showed the owner performing a full ReadyMelee to ReleaseMelee while
/// the client displayed a shield and then nothing - 71% of the owner's swing time never rendered. On the
/// receiving side every outcome accounts for itself: nothing suppressed a swing, nothing failed to resolve,
/// nothing was preserved, nothing was dropped in delivery. If a packet carrying that attack had arrived, the
/// index comparison would have applied it, and there is no remaining outcome for it to vanish into.
/// </para>
/// <para>
/// So the attack was never sent. This counts the send path, which has been reasoned about twice in this
/// investigation and never measured - the same mistake that produced every other wrong answer here.
/// </para>
/// <para>
/// The decisive pair is <c>swingChangesDetected</c> against <c>swingChangesPublished</c>. A gap between them is
/// the owner noticing its own agent start a swing and then declining to tell anyone.
/// </para>
/// </remarks>
internal static class ActionSendLog
{
    private const int ReadyMelee = 19;
    private const int ReleaseMelee = 20;

    private static readonly object Gate = new object();

    private static bool enabled;
    private static long polls;
    private static long visited;
    private static long notSyncedNullOrMission;
    private static long notSyncedInactive;
    private static long notSyncedNoHealth;
    private static long notSyncedIsMount;
    private static long nothingChanged;
    private static long broadcasts;
    private static long windupDetected;
    private static long windupPublished;
    private static long releaseDetected;
    private static long releasePublished;
    private static long swingChangesDetected;
    private static long swingChangesPublished;
    private static long swingChangesDroppedByGate;
    private static long swingChangesDroppedEarly;

    public static bool Enabled => enabled;

    public static void Start()
    {
        lock (Gate)
        {
            polls = 0;
            visited = 0;
            notSyncedNullOrMission = 0;
            notSyncedInactive = 0;
            notSyncedNoHealth = 0;
            notSyncedIsMount = 0;
            nothingChanged = 0;
            broadcasts = 0;
            windupDetected = 0;
            windupPublished = 0;
            releaseDetected = 0;
            releasePublished = 0;
            swingChangesDetected = 0;
            swingChangesPublished = 0;
            swingChangesDroppedByGate = 0;
            swingChangesDroppedEarly = 0;
            enabled = true;
        }
    }

    public static void Poll()
    {
        if (!enabled) return;
        lock (Gate) polls++;
    }

    public static void Visited()
    {
        if (!enabled) return;
        lock (Gate) visited++;
    }

    /// <summary>An agent the owner skipped before any change detection ran.</summary>
    public static void NotSynced(bool nullOrWrongMission, bool inactive, bool noHealth, bool isMount)
    {
        if (!enabled) return;
        lock (Gate)
        {
            if (nullOrWrongMission) notSyncedNullOrMission++;
            else if (inactive) notSyncedInactive++;
            else if (noHealth) notSyncedNoHealth++;
            else if (isMount) notSyncedIsMount++;
        }
    }

    /// <summary>A swing action change the owner noticed on one of its own agents.</summary>
    public static void SwingChangeDetected(bool isWindup)
    {
        if (!enabled) return;
        lock (Gate)
        {
            swingChangesDetected++;
            if (isWindup) windupDetected++;
            else releaseDetected++;
        }
    }

    /// <summary>The early return taken when the owner decides nothing changed at all.</summary>
    public static void NothingChanged(bool hadSwingChange)
    {
        if (!enabled) return;
        lock (Gate)
        {
            nothingChanged++;
            if (hadSwingChange) swingChangesDroppedEarly++;
        }
    }

    /// <summary>The publish decision, and whether a detected swing change survived it.</summary>
    public static void Decision(bool broadcast, bool hadSwingChange, bool wasWindup)
    {
        if (!enabled) return;
        lock (Gate)
        {
            if (broadcast) broadcasts++;
            if (!hadSwingChange) return;
            if (broadcast)
            {
                swingChangesPublished++;
                if (wasWindup) windupPublished++;
                else releasePublished++;
            }
            else swingChangesDroppedByGate++;
        }
    }

    /// <summary>ReadyMelee: the wind-up. Missed in 100% of client samples across four recordings.</summary>
    public static bool IsWindup(int actionType) => actionType == ReadyMelee;

    public static bool IsSwing(int actionType) =>
        actionType == ReadyMelee || actionType == ReleaseMelee;

    public static string Snapshot(bool stop)
    {
        lock (Gate)
        {
            if (stop) enabled = false;
            if (polls == 0 && visited == 0) return "actionSend: nothing recorded";

            var text = new StringBuilder();
            text.Append("actionSend: polls=").Append(polls.ToString(CultureInfo.InvariantCulture));
            text.Append(" visited=").Append(visited.ToString(CultureInfo.InvariantCulture));
            text.Append(" notSynced=[nullOrMission:").Append(notSyncedNullOrMission.ToString(CultureInfo.InvariantCulture));
            text.Append(" inactive:").Append(notSyncedInactive.ToString(CultureInfo.InvariantCulture));
            text.Append(" noHealth:").Append(notSyncedNoHealth.ToString(CultureInfo.InvariantCulture));
            text.Append(" isMount:").Append(notSyncedIsMount.ToString(CultureInfo.InvariantCulture)).Append(']');
            text.Append(" nothingChanged=").Append(nothingChanged.ToString(CultureInfo.InvariantCulture));
            text.Append(" broadcasts=").Append(broadcasts.ToString(CultureInfo.InvariantCulture));

            text.Append(" | SWING_CHANGES detected=").Append(swingChangesDetected.ToString(CultureInfo.InvariantCulture));
            text.Append(" published=").Append(swingChangesPublished.ToString(CultureInfo.InvariantCulture));
            text.Append(" droppedByGate=").Append(swingChangesDroppedByGate.ToString(CultureInfo.InvariantCulture));
            text.Append(" droppedEarly=").Append(swingChangesDroppedEarly.ToString(CultureInfo.InvariantCulture));
            if (swingChangesDetected > 0)
            {
                text.Append(" publishedShare=")
                    .Append((100d * swingChangesPublished / swingChangesDetected)
                        .ToString("0.0", CultureInfo.InvariantCulture)).Append('%');
            }

            text.Append(" | WINDUP detected=").Append(windupDetected.ToString(CultureInfo.InvariantCulture));
            text.Append(" published=").Append(windupPublished.ToString(CultureInfo.InvariantCulture));
            text.Append(" | RELEASE detected=").Append(releaseDetected.ToString(CultureInfo.InvariantCulture));
            text.Append(" published=").Append(releasePublished.ToString(CultureInfo.InvariantCulture));

            return text.ToString();
        }
    }
}
