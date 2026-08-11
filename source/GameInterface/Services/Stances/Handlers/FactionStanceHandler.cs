using Common;
using Common.Logging;
using Common.Messaging;
using Common.Util;
using GameInterface.Services.ObjectManager;
using GameInterface.Services.Stances.Messages;
using Serilog;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.Actions;

namespace GameInterface.Services.Stances.Handlers
{
    /// <summary>
    /// Applies replicated faction stance changes (war / peace) on the receiving machine by
    /// re-running the vanilla action under AllowedThread, which re-fires the campaign events
    /// client-side without re-announcing.
    /// </summary>
    public class FactionStanceHandler : IHandler
    {
        private static readonly ILogger Logger = LogManager.GetLogger<FactionStanceHandler>();
        private readonly IMessageBroker messageBroker;
        private readonly IObjectManager objectManager;

        public FactionStanceHandler(IMessageBroker messageBroker, IObjectManager objectManager)
        {
            this.messageBroker = messageBroker;
            this.objectManager = objectManager;
            messageBroker.Subscribe<DeclareWarChanged>(HandleDeclareWar);
            messageBroker.Subscribe<MakePeaceChanged>(HandleMakePeace);
            messageBroker.Subscribe<WarStatsChanged>(HandleWarStats);
        }

        /// <summary>
        /// Writes the server's war totals onto this client's stance link, so the diplomacy screen shows the
        /// same casualties, sieges and raids the server counted.
        /// </summary>
        /// <remarks>
        /// Assigned rather than added to. The payload is the authoritative total, so assignment converges on the
        /// right value no matter what this client had before, and cannot drift.
        ///
        /// The stance is looked up from THIS client's factions rather than trusting the message's ordering: the
        /// server sends Faction1/Faction2 as its own stance link holds them, and the pair is resolved back into
        /// the local link before the numbers are written, so 1 and 2 always mean the same two factions on both
        /// machines.
        /// </remarks>
        private void HandleWarStats(MessagePayload<WarStatsChanged> obj)
        {
            var payload = obj.What;
            if (!TryGetFaction(payload.Faction1Id, out var faction1)) return;
            if (!TryGetFaction(payload.Faction2Id, out var faction2)) return;

            GameThread.Run(() =>
            {
                var stance = faction1.GetStanceWith(faction2);
                if (stance == null)
                {
                    Logger.Warning("[WarStats] No local stance between {F1} and {F2}; the diplomacy screen keeps its old numbers",
                        payload.Faction1Id, payload.Faction2Id);
                    return;
                }

                Logger.Information("[WarStats] Applying {F1} vs {F2}: casualties {C1}/{C2}, raids {R1}/{R2}",
                    payload.Faction1Id, payload.Faction2Id,
                    payload.TroopCasualties1, payload.TroopCasualties2,
                    payload.SuccessfulRaids1, payload.SuccessfulRaids2);

                bool sameOrder = stance.Faction1 == faction1;

                stance.TroopCasualties1 = sameOrder ? payload.TroopCasualties1 : payload.TroopCasualties2;
                stance.TroopCasualties2 = sameOrder ? payload.TroopCasualties2 : payload.TroopCasualties1;
                stance.SuccessfulSieges1 = sameOrder ? payload.SuccessfulSieges1 : payload.SuccessfulSieges2;
                stance.SuccessfulSieges2 = sameOrder ? payload.SuccessfulSieges2 : payload.SuccessfulSieges1;
                stance.SuccessfulTownSieges1 = sameOrder ? payload.SuccessfulTownSieges1 : payload.SuccessfulTownSieges2;
                stance.SuccessfulTownSieges2 = sameOrder ? payload.SuccessfulTownSieges2 : payload.SuccessfulTownSieges1;
                stance.SuccessfulRaids1 = sameOrder ? payload.SuccessfulRaids1 : payload.SuccessfulRaids2;
                stance.SuccessfulRaids2 = sameOrder ? payload.SuccessfulRaids2 : payload.SuccessfulRaids1;
            }, true);
        }

        private void HandleDeclareWar(MessagePayload<DeclareWarChanged> obj)
        {
            var payload = obj.What;
            if (!TryGetFaction(payload.Faction1Id, out var faction1)) return;
            if (!TryGetFaction(payload.Faction2Id, out var faction2)) return;

            // ApplyInternal is the funnel for every war cause; calling it directly (publicized)
            // preserves the original DeclareWarDetail so detail-sensitive client listeners match the server.
            GameThread.Run(() =>
            {
                using (new AllowedThread())
                {
                    DeclareWarAction.ApplyInternal(faction1, faction2, (DeclareWarAction.DeclareWarDetail)payload.Detail);
                }
            }, true);
        }

        private void HandleMakePeace(MessagePayload<MakePeaceChanged> obj)
        {
            var payload = obj.What;
            if (!TryGetFaction(payload.Faction1Id, out var faction1)) return;
            if (!TryGetFaction(payload.Faction2Id, out var faction2)) return;

            // ApplyInternal is the funnel for every peace cause; calling it directly (publicized)
            // preserves the original MakePeaceDetail and the daily tribute.
            GameThread.Run(() =>
            {
                using (new AllowedThread())
                {
                    MakePeaceAction.ApplyInternal(faction1, faction2, payload.DailyTribute, payload.DailyTributeDuration, (MakePeaceAction.MakePeaceDetail)payload.Detail);
                }
            }, true);
        }

        private bool TryGetFaction(string id, out IFaction faction)
        {
            if (objectManager.TryGetObject(id, out Kingdom kingdom))
            {
                faction = kingdom;
                return true;
            }
            if (objectManager.TryGetObject(id, out Clan clan))
            {
                faction = clan;
                return true;
            }
            Logger.Debug("Faction not found in FactionStanceHandler with id: {id}", id);
            faction = null;
            return false;
        }

        public void Dispose()
        {
            messageBroker.Unsubscribe<DeclareWarChanged>(HandleDeclareWar);
            messageBroker.Unsubscribe<MakePeaceChanged>(HandleMakePeace);
            messageBroker.Unsubscribe<WarStatsChanged>(HandleWarStats);
        }
    }
}
