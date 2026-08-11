using Common;
using Common.Messaging;
using Common.Network;
using Common.Util;
using GameInterface.Services.Heroes.Messages;
using GameInterface.Services.ObjectManager;
using System;
using TaleWorlds.CampaignSystem;
using TaleWorlds.Core;
using TaleWorlds.Library;
using TaleWorlds.Localization;

namespace GameInterface.Services.Heroes.Handlers
{
    /// <summary>
    /// Replicates server-authoritative hero relation changes to clients.
    /// </summary>
    public class CharacterRelationManagerHandler : IHandler
    {
        private readonly IMessageBroker messageBroker;
        private readonly IObjectManager objectManager;
        private readonly INetwork network;

        public CharacterRelationManagerHandler(IMessageBroker messageBroker, IObjectManager objectManager, INetwork network)
        {
            this.messageBroker = messageBroker;
            this.objectManager = objectManager;
            this.network = network;
            messageBroker.Subscribe<HeroRelationChanged>(Handle);
            messageBroker.Subscribe<NetworkHeroRelationChanged>(Handle);
        }

        public void Dispose()
        {
            messageBroker.Unsubscribe<HeroRelationChanged>(Handle);
            messageBroker.Unsubscribe<NetworkHeroRelationChanged>(Handle);
        }

        private void Handle(MessagePayload<HeroRelationChanged> obj)
        {
            if (ModInformation.IsClient) return;

            var payload = obj.What;
            network.SendAll(new NetworkHeroRelationChanged(payload.Hero1Id, payload.Hero2Id, payload.Value));
        }

        private void Handle(MessagePayload<NetworkHeroRelationChanged> obj)
        {
            var payload = obj.What;

            GameThread.RunSafe(() =>
            {
                if (!objectManager.TryGetObjectWithLogging<Hero>(payload.Hero1Id, out var hero1)) return;
                if (!objectManager.TryGetObjectWithLogging<Hero>(payload.Hero2Id, out var hero2)) return;

                // The change is applied as an absolute value (SetHeroRelation) to match the server exactly
                // and avoid the RNG/model factor ChangeRelationAction applies to positive gains; capture the
                // delta first so we can still show the bottom-left popup vanilla would.
                int delta = payload.Value - CharacterRelationManager.GetHeroRelation(hero1, hero2);

                using (new AllowedThread())
                {
                    CharacterRelationManager.SetHeroRelation(hero1, hero2, payload.Value);
                }

                ShowLocalRelationChange(hero1, hero2, delta, payload.Value);
            }, context: $"apply hero relation {payload.Hero1Id}<->{payload.Hero2Id}");
        }

        /// <summary>
        /// Shows the relation-change line vanilla would have shown, for the local player only.
        /// </summary>
        /// <remarks>
        /// The client never runs <c>ChangeRelationAction.ApplyInternal</c> - its prefix returns
        /// <c>ModInformation.IsServer</c> - and that method is where vanilla both moves the number AND raises
        /// the notification. The number comes back through this handler; the notification was lost with the
        /// method, which is why a player sees relations change silently, or believes they did not change at
        /// all. The delta above was already being captured for exactly this and then never used.
        ///
        /// Gated to the local player's own relations on purpose: this handler receives EVERY relation change
        /// in the world, so notifying unconditionally would bury the log under AI politics.
        /// </remarks>
        private static void ShowLocalRelationChange(Hero hero1, Hero hero2, int delta, int newValue)
        {
            if (delta == 0) return;

            var mainHero = Hero.MainHero;
            if (mainHero == null) return;

            Hero other;
            if (hero1 == mainHero) other = hero2;
            else if (hero2 == mainHero) other = hero1;
            else return;

            if (other == null) return;

            var text = new TextObject("{HERO}: relation {SIGN}{CHANGE} (now {VALUE})");
            text.SetTextVariable("HERO", other.Name);
            text.SetTextVariable("SIGN", delta > 0 ? "+" : "-");
            text.SetTextVariable("CHANGE", Math.Abs(delta));
            text.SetTextVariable("VALUE", newValue);

            InformationManager.DisplayMessage(new InformationMessage(
                text.ToString(),
                delta > 0 ? new Color(0f, 1f, 0f, 1f) : new Color(1f, 0f, 0f, 1f)));
        }
    }
}
