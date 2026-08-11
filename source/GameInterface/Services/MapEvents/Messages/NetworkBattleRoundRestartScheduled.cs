using Common.Messaging;
using ProtoBuf;

namespace GameInterface.Services.MapEvents.Messages;

/// <summary>
/// Server -&gt; Clients: reinforcements have changed a battle's strength enough to be worth re-sizing it, so every
/// client in the mission should show the notice and then restart the round together.
/// </summary>
/// <remarks>
/// Sent once per restart, ahead of the restart itself by <see cref="CountdownSeconds"/>, so players are told what
/// is about to happen rather than being teleported without warning. The countdown is carried in the message
/// rather than assumed locally so every client runs the same clock from the same instruction, and so a late
/// joiner cannot start a shorter one.
///
/// The restart itself is driven by each client off this countdown, not by a second message: a "restart now"
/// broadcast would land at different times on different connections and desynchronise the very spawn it exists
/// to align.
/// </remarks>
[ProtoContract(SkipConstructor = true)]
internal readonly struct NetworkBattleRoundRestartScheduled : ICommand
{
    [ProtoMember(1)]
    public readonly string MapEventId;

    [ProtoMember(2)]
    public readonly float CountdownSeconds;

    /// <summary>Monotonic per battle, so a duplicate or out-of-order delivery cannot restart the round twice.</summary>
    [ProtoMember(3)]
    public readonly int RestartSequence;

    public NetworkBattleRoundRestartScheduled(string mapEventId, float countdownSeconds, int restartSequence)
    {
        MapEventId = mapEventId;
        CountdownSeconds = countdownSeconds;
        RestartSequence = restartSequence;
    }
}
