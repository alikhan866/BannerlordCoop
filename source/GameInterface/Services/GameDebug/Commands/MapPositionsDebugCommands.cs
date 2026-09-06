#if DEBUG
using System;
using System.Globalization;
using System.Text;
using Common.Commands;
using GameInterface.Services.ObjectManager;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.Party;

namespace GameInterface.Services.GameDebug.Commands;

/// <summary>
/// [Debug] One-shot census of every mobile party out on the map, on whichever machine runs it, with the campaign
/// clock it was read at. The map-drift rig samples it on the server and both clients on a shared wall clock and
/// joins the rows by party id: the distance between a client's copy and the server's is the drift, a copy that
/// moves more than its speed allows between two samples is a jump ("lords teleport").
/// </summary>
internal static class MapPositionsDebugCommands
{
    private static CoopCommandResult Succeeded(string output) => new CoopCommandResult(true, output);

    private static CoopCommandResult Failed(string output) => new CoopCommandResult(false, output, "command_failed");

    private static string F(float value) => value.ToString("0.###", CultureInfo.InvariantCulture);

    // coop.debug.mobile_party.positions_dump
    public sealed class PositionsDumpCoopCommand : ICoopCommand
    {
        public string Prefix => "coop.debug.mobile_party";

        public string Name => "positions_dump";

        public string Description => "Every active party outside a settlement: id, x, y, speed, whether it has a next waypoint away from itself, behaviour; plus the campaign clock.";

        public IExpectedArgs[] ExpectedArgs { get; } = new IExpectedArgs[]
        {
            new ExpectedArgs("filter", "all (default), lords (lord parties, armies and caravans only) or player", isRequired: false),
        };

        public CoopCommandResult ProcessCommand(ICoopCommandArgs args)
        {
            var campaign = Campaign.Current;
            if (campaign == null) return Failed("No campaign.");
            if (!ContainerProvider.TryResolve<IObjectManager>(out var objectManager))
                return Failed("No object manager.");
            string filter = args.Count > 0 ? args[0].ToLowerInvariant() : "all";
            long ticks = 0;
            double hours = 0;
            try { ticks = campaign.MapTimeTracker._numTicks; } catch (Exception) { }
            try { hours = CampaignTime.Now.ToHours; } catch (Exception) { }
            var text = new StringBuilder();
            text.Append("POSITIONS utcMs=").Append((DateTime.UtcNow.Ticks / TimeSpan.TicksPerMillisecond).ToString(CultureInfo.InvariantCulture))
                .Append(" campaignTicks=").Append(ticks.ToString(CultureInfo.InvariantCulture))
                .Append(" campaignHours=").Append(hours.ToString("0.####", CultureInfo.InvariantCulture))
                .Append(" timeMode=").Append(campaign.TimeControlMode)
                .Append(" side=").Append(Common.ModInformation.IsServer ? "server" : "client");
            int n = 0;
            foreach (var party in campaign.MobileParties)
            {
                if (party == null || !party.IsActive || party.CurrentSettlement != null) continue;
                bool isLordLike = party.IsLordParty || party.IsCaravan || party.Army != null;
                if (filter == "lords" && !isLordLike) continue;
                if (filter == "player" && !party.IsMainParty && party.LeaderHero?.IsHumanPlayerCharacter != true) continue;
                if (!objectManager.TryGetId(party, out var id)) id = party.StringId;
                var pos = party.Position;
                float speed = 0f;
                try { speed = party.Speed; } catch (Exception) { }
                bool hasNext = false;
                try
                {
                    // A party with somewhere to go but whose next waypoint is its own position is the "frozen" signature.
                    var next = party.NextTargetPosition;
                    hasNext = next.DistanceSquared(pos) > 0.0001f;
                }
                catch (Exception) { }
                var target = pos;
                try { target = party.TargetPosition; } catch (Exception) { }
                text.Append('\n').Append(id)
                    .Append(' ').Append(F(pos.X)).Append(' ').Append(F(pos.Y))
                    .Append(' ').Append(F(speed))
                    .Append(' ').Append(hasNext ? '1' : '0')
                    .Append(' ').Append(party.ShortTermBehavior)
                    .Append(' ').Append(party.PartyMoveMode)
                    .Append(' ').Append(party.IsLordParty ? 'L' : party.IsCaravan ? 'C' : party.IsVillager ? 'V' : party.IsBandit ? 'B' : party.IsMainParty ? 'P' : 'O')
                    .Append(' ').Append(F(target.X)).Append(' ').Append(F(target.Y))
                    .Append(' ').Append(party.Army != null ? (party.Army.LeaderParty == party ? 'a' : 'A') : '-');
                n++;
            }
            text.Insert(0, "");
            return Succeeded(text.ToString().Replace("POSITIONS utcMs", "POSITIONS n=" + n.ToString(CultureInfo.InvariantCulture) + " utcMs"));
        }
    }

    // coop.debug.mobile_party.position_smoothing
    /// <summary>
    /// Reads or switches the client-side delivery of authoritative position corrections. The number that matters
    /// is MOVE_PER_FRAME_MAX: the largest distance a party was moved in one frame, which is the jump a player
    /// sees. With smoothing off it equals the correction itself; with it on, a fraction of it.
    /// </summary>
    public sealed class PositionSmoothingCoopCommand : ICoopCommand
    {
        public string Prefix => "coop.debug.mobile_party";

        public string Name => "position_smoothing";

        public string Description => "Reports the client's position-correction counters; 'on', 'off' or 'reset' change or clear them.";

        public IExpectedArgs[] ExpectedArgs { get; } = new IExpectedArgs[]
        {
            new ExpectedArgs("mode", "omit to report; on | off | reset", isRequired: false),
        };

        public CoopCommandResult ProcessCommand(ICoopCommandArgs args)
        {
            string mode = args.Count > 0 ? args[0].ToLowerInvariant() : null;
            switch (mode)
            {
                case null:
                    break;
                case "on":
                    MobileParties.PartyPositionSmoothing.Enabled = true;
                    MobileParties.PartyPositionSmoothing.Clear();
                    break;
                case "off":
                    MobileParties.PartyPositionSmoothing.Enabled = false;
                    MobileParties.PartyPositionSmoothing.Clear();
                    break;
                case "reset":
                    MobileParties.PartyPositionSmoothing.Clear();
                    break;
                default:
                    return Failed("usage: position_smoothing [on|off|reset]");
            }
            return Succeeded(MobileParties.PartyPositionSmoothing.Snapshot());
        }
    }
}
#endif
