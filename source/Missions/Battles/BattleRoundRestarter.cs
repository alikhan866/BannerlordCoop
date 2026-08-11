using Common.Logging;
using GameInterface.Services.MapEvents.TroopSupply;
using Serilog;
using System;
using TaleWorlds.Core;
using TaleWorlds.Library;
using TaleWorlds.MountAndBlade;

namespace Missions.Battles;

/// <summary>
/// Restarts a battle's round after reinforcements arrive, so the fight is re-sized around who is actually
/// present instead of around who was present when it began.
/// </summary>
/// <remarks>
/// The engine fixes a battle's shape at its first <c>Init</c>: the battle size is split between the two sides in
/// proportion to the totals handed over then, and every reinforcement wave afterwards is drawn against that
/// split. So a side that started 100 against 300 keeps feeding in at its opening rate no matter how many lords
/// ride in behind it - the relief exists on the map and in the scoreboard, but never reaches the field at a rate
/// that reflects it.
///
/// Restarting is what re-derives the split. Everyone returns to their spawn, the sizing is recomputed from the
/// totals as they now stand, and each owner contributes its proportional share through the same
/// <see cref="CoopTroopSupplier.OwnedShareOf"/> path used at battle start - which is also what keeps every
/// player's own hero guaranteed a place, since the per-player reservation is taken off the top there.
///
/// The countdown is what makes it legible rather than jarring: players are told the round is restarting and why,
/// and every client runs the same clock from the same instruction so the re-spawn lands together.
/// </remarks>
public interface IBattleRoundRestarter
{
    /// <summary>Accepts a scheduled restart. Ignores one already seen, so a duplicate delivery is harmless.</summary>
    void Schedule(float countdownSeconds, int restartSequence);

    /// <summary>Drives the countdown. Performs the restart when it reaches zero.</summary>
    void Tick(float dt);

    /// <summary>True while a restart is counting down.</summary>
    bool IsPending { get; }
}

public class BattleRoundRestarter : IBattleRoundRestarter
{
    private static readonly ILogger Logger = LogManager.GetLogger<BattleRoundRestarter>();

    internal const string BannerText = "Reinforcements have arrived in the battle - restarting...";

    /// <summary>
    /// How long past the countdown a client will keep waiting for its rebuilt reserve before re-forming with
    /// whatever it has.
    /// </summary>
    /// <remarks>
    /// Bounded on purpose. Waiting forever would strand this client in a battle everyone else has already
    /// re-formed, which is a worse desync than re-forming from a slightly stale reserve - and a lost or refused
    /// reserve reply must never be able to wedge the mission.
    /// </remarks>
    internal const float ReserveGraceSeconds = 5f;

    private readonly Func<Mission> missionAccessor;
    private readonly Action performRestart;
    private readonly Func<bool> reservesRebuilt;

    private float remaining;
    private float graceRemaining;
    private bool inGrace;
    private bool pending;
    private int lastSequence = -1;

    public BattleRoundRestarter(Func<Mission> missionAccessor, Action performRestart,
        Func<bool> reservesRebuilt = null)
    {
        this.missionAccessor = missionAccessor;
        this.performRestart = performRestart;
        this.reservesRebuilt = reservesRebuilt;
    }

    public bool IsPending => pending;

    public void Schedule(float countdownSeconds, int restartSequence)
    {
        // Monotonic sequence: a resend of the same restart must not extend the countdown or run it twice.
        if (!ShouldAccept(restartSequence, lastSequence)) return;

        lastSequence = restartSequence;
        remaining = countdownSeconds;
        graceRemaining = ReserveGraceSeconds;
        inGrace = false;
        pending = true;

        Announce();
        Logger.Information("[BattleSync] Round restart #{Sequence} scheduled in {Seconds}s", restartSequence, countdownSeconds);
    }

    public void Tick(float dt)
    {
        if (!pending) return;

        remaining -= dt;
        if (remaining > 0f) return;

        // The countdown has run out, but re-forming from a reserve the server has already replaced would put
        // this client's troops out of step with everyone else's. Hold briefly for the rebuilt reserve, then go
        // regardless - a bounded stale round beats one client stranded in the previous one.
        if (!ReservesReady())
        {
            // The grace is measured FROM the countdown expiring, so the tick that crossed the countdown does
            // not also spend the wait. Otherwise a single long frame - a lag spike, or a mission that stalled -
            // would burn the whole window at once and re-form from the stale reserve it was meant to avoid.
            if (!inGrace)
            {
                inGrace = true;
                return;
            }

            graceRemaining -= dt;
            if (graceRemaining > 0f) return;

            Logger.Warning(
                "[BattleSync] Round restart #{Sequence} proceeding without the rebuilt reserve after {Grace}s",
                lastSequence, ReserveGraceSeconds);
        }

        pending = false;
        Logger.Information("[BattleSync] Round restart #{Sequence} firing", lastSequence);

        try
        {
            performRestart();
        }
        catch (Exception e)
        {
            // A failed restart must not take the battle down with it - the fight is still playable at its old
            // sizing, which is strictly better than dropping everyone out of the mission.
            Logger.Error(e, "[BattleSync] Round restart #{Sequence} failed; the battle continues at its previous sizing", lastSequence);
        }
    }

    /// <summary>No readiness check supplied means nothing to wait for, so the countdown alone decides.</summary>
    private bool ReservesReady() => reservesRebuilt == null || reservesRebuilt();

    /// <summary>Only a strictly newer restart is accepted, so duplicates and reordering are both absorbed.</summary>
    internal static bool ShouldAccept(int incomingSequence, int lastAcceptedSequence)
        => incomingSequence > lastAcceptedSequence;

    private void Announce()
    {
        var mission = missionAccessor?.Invoke();
        if (mission == null) return;

        InformationManager.DisplayMessage(new InformationMessage(BannerText, Colors.Yellow));
    }
}
