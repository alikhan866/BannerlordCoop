using Common;
using Common.Logging;
using Common.Messaging;
using Common.Network;
using Common.Network.Coalescing;
using GameInterface.Services.Heroes.Messages.LordConversations;
using GameInterface.Services.ObjectManager;
using Serilog;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.Actions;
using TaleWorlds.CampaignSystem.Party;

namespace GameInterface.Services.Heroes.Handlers;

internal class LordConversationsCampaignBehaviorHandler : IHandler
{
    private static readonly ILogger Logger = LogManager.GetLogger<LordConversationsCampaignBehaviorHandler>();

    private readonly IObjectManager objectManager;
    private readonly INetwork network;
    private readonly IMessageBroker messageBroker;

    private const int PrisonerLiberationRelationReward = 10;
    private const int LetLordGoReward = 4;

    /// <summary>
    /// Relation credited for fighting on a lord's side in a battle they went on to win.
    /// </summary>
    /// <remarks>
    /// The two constants above mirror vanilla exactly - LordConversationsCampaignBehavior exposes
    /// <c>PlayerReleasesPrisonerRelationChange = 4</c> and <c>PlayerLiberatesPrisonerRelationChange = 10</c>.
    /// This case has no vanilla constant to mirror: vanilla scales it inside the dialogue consequence from
    /// the player's own map event, and on a dedicated server that map event belongs to a client and reads as
    /// null - which is why the call here was commented out with a TODO and the favour went unrewarded
    /// entirely.
    ///
    /// ponytail: a flat reward, matched to vanilla's other "you did me a favour" value, rather than a scaling
    /// factor invented to look like vanilla's. Crediting a defensible fixed amount is closer to correct than
    /// crediting nothing, and it needs no protocol change. Upgrade path if the scaling ever matters: compute
    /// the value on the client, where the map event still exists, and carry it in LordHelpedInBattle.
    /// </remarks>
    private const int HelpedInBattleReward = 4;

    public LordConversationsCampaignBehaviorHandler(
        IObjectManager objectManager,
        INetwork network,
        IMessageBroker messageBroker)
    {
        this.objectManager = objectManager;
        this.network = network;
        this.messageBroker = messageBroker;

        messageBroker.Subscribe<LiberateLordPrisoner>(Handle_LiberateLordPrisoner);
        messageBroker.Subscribe<NetworkLiberateLordPrisoner>(Handle_NetworkLiberateLordPrisoner);

        messageBroker.Subscribe<TakeLordPrisoner>(Handle_TakeLordPrisoner);
        messageBroker.Subscribe<NetworkTakeLordPrisoner>(Handle_NetworkTakeLordPrisoner);

        messageBroker.Subscribe<LordHelpedInBattle>(Handle_LordHelpedInBattle);
        messageBroker.Subscribe<NetworkLordHelpedInBattle>(Handle_NetworkLordHelpedInBattle);

        messageBroker.Subscribe<LordDefeatToRelease>(Handle_LordDefeatToRelease);
        messageBroker.Subscribe<NetworkLordDefeatToRelease>(Handle_NetworkLordDefeatToRelease);

        messageBroker.Subscribe<LordFreedToRelease>(Handle_LordFreedToRelease);
        messageBroker.Subscribe<NetworkLordFreedToRelease>(Handle_NetworkLordFreedToRelease);
    }

    public void Dispose()
    {
        messageBroker.Unsubscribe<LiberateLordPrisoner>(Handle_LiberateLordPrisoner);
        messageBroker.Unsubscribe<NetworkLiberateLordPrisoner>(Handle_NetworkLiberateLordPrisoner);

        messageBroker.Unsubscribe<TakeLordPrisoner>(Handle_TakeLordPrisoner);
        messageBroker.Unsubscribe<NetworkTakeLordPrisoner>(Handle_NetworkTakeLordPrisoner);

        messageBroker.Unsubscribe<LordHelpedInBattle>(Handle_LordHelpedInBattle);
        messageBroker.Unsubscribe<NetworkLordHelpedInBattle>(Handle_NetworkLordHelpedInBattle);

        messageBroker.Unsubscribe<LordDefeatToRelease>(Handle_LordDefeatToRelease);
        messageBroker.Unsubscribe<NetworkLordDefeatToRelease>(Handle_NetworkLordDefeatToRelease);

        messageBroker.Unsubscribe<LordFreedToRelease>(Handle_LordFreedToRelease);
        messageBroker.Unsubscribe<NetworkLordFreedToRelease>(Handle_NetworkLordFreedToRelease);
    }

    private void Handle_LiberateLordPrisoner(MessagePayload<LiberateLordPrisoner> obj)
    {
        if (!objectManager.TryGetIdWithLogging(obj.What.MainHero, out var mainHeroId)) return;
        if (!objectManager.TryGetIdWithLogging(obj.What.ConversationHero, out var conversationHeroId)) return;

        network.SendAll(new NetworkLiberateLordPrisoner(mainHeroId, conversationHeroId));
    }

    private void Handle_NetworkLiberateLordPrisoner(MessagePayload<NetworkLiberateLordPrisoner> obj)
    {
        var data = obj.What;

        GameThread.RunSafe(() =>
        {
            if (!objectManager.TryGetObjectWithLogging<Hero>(data.MainHeroId, out var playerHero)) return;
            if (!objectManager.TryGetObjectWithLogging<Hero>(data.ConversationHeroId, out var conversationHero)) return;
            if (!conversationHero.IsPrisoner) return;

            ChangeRelationAction.ApplyRelationChangeBetweenHeroes(playerHero, conversationHero, PrisonerLiberationRelationReward);
            EndCaptivityAction.ApplyByReleasedAfterBattle(conversationHero);
        },
        context: nameof(Handle_NetworkLiberateLordPrisoner));
    }

    private void Handle_TakeLordPrisoner(MessagePayload<TakeLordPrisoner> obj)
    {
        if (!objectManager.TryGetIdWithLogging(obj.What.MainParty, out var mainPartyId)) return;
        if (!objectManager.TryGetIdWithLogging(obj.What.ConversationHero, out var conversationHeroId)) return;

        network.SendAll(new NetworkTakeLordPrisoner(mainPartyId, conversationHeroId));
    }

    private void Handle_NetworkTakeLordPrisoner(MessagePayload<NetworkTakeLordPrisoner> obj)
    {
        var data = obj.What;

        GameThread.RunSafe(() =>
        {
            if (!objectManager.TryGetObjectWithLogging<PartyBase>(data.MainPartyId, out var mainParty)) return;
            if (!objectManager.TryGetObjectWithLogging<Hero>(data.ConversationHeroId, out var conversationHero)) return;

            TakePrisonerAction.Apply(mainParty, conversationHero);
        },
        context: nameof(Handle_NetworkTakeLordPrisoner));
    }

    private void Handle_LordHelpedInBattle(MessagePayload<LordHelpedInBattle> obj)
    {
        if (!objectManager.TryGetIdWithLogging(obj.What.MainHero, out var mainHeroId)) return;
        if (!objectManager.TryGetIdWithLogging(obj.What.ConversationHero, out var conversationHeroId)) return;

        network.SendAll(new NetworkLordHelpedInBattle(mainHeroId, conversationHeroId));
    }

    private void Handle_NetworkLordHelpedInBattle(MessagePayload<NetworkLordHelpedInBattle> obj)
    {
        var data = obj.What;

        GameThread.RunSafe(() =>
        {
            if (!objectManager.TryGetObjectWithLogging<Hero>(data.MainHeroId, out var mainHero)) return;
            if (!objectManager.TryGetObjectWithLogging<Hero>(data.ConversationHeroId, out var conversationHero)) return;

            // Credited here rather than left to vanilla: the client never reaches
            // ChangeRelationAction.ApplyInternal (its prefix is ModInformation.IsServer), and the server has
            // no PlayerMapEvent to scale the reward from, so without this the player fought the battle and
            // got nothing for it. See HelpedInBattleReward for why the amount is flat.
            ChangeRelationAction.ApplyRelationChangeBetweenHeroes(mainHero, conversationHero, HelpedInBattleReward);

            if (conversationHero.IsPrisoner)
            {
               EndCaptivityAction.ApplyByReleasedAfterBattle(conversationHero);
            }
        },
        context: nameof(Handle_NetworkLordHelpedInBattle));
    }

    private void Handle_LordDefeatToRelease(MessagePayload<LordDefeatToRelease> obj)
    {
        if (!objectManager.TryGetIdWithLogging(obj.What.MainHero, out var mainHeroId)) return;
        if (!objectManager.TryGetIdWithLogging(obj.What.ConversationHero, out var conversationHeroId)) return;

        network.SendAll(new NetworkLordDefeatToRelease(mainHeroId, conversationHeroId));
    }

    private void Handle_NetworkLordDefeatToRelease(MessagePayload<NetworkLordDefeatToRelease> obj)
    {
        var data = obj.What;

        GameThread.RunSafe(() =>
        {
            if (!objectManager.TryGetObjectWithLogging<Hero>(data.MainHeroId, out var mainHero)) return;
            if (!objectManager.TryGetObjectWithLogging<Hero>(data.ConversationHeroId, out var conversationHero)) return;

            if (conversationHero.IsPrisoner)
            {
                EndCaptivityAction.ApplyByReleasedAfterBattle(conversationHero);
            }
            else
            {
                MakeHeroFugitiveAction.Apply(conversationHero, false);
            }

            ChangeRelationAction.ApplyRelationChangeBetweenHeroes(mainHero, conversationHero, LetLordGoReward);
        },
        context: nameof(Handle_NetworkLordDefeatToRelease));
    }

    private void Handle_LordFreedToRelease(MessagePayload<LordFreedToRelease> obj)
    {
        if (!objectManager.TryGetIdWithLogging(obj.What.MainHero, out var mainHeroId)) return;
        if (!objectManager.TryGetIdWithLogging(obj.What.ConversationHero, out var conversationHeroId)) return;

        network.SendAll(new NetworkLordFreedToRelease(mainHeroId, conversationHeroId));
    }

    private void Handle_NetworkLordFreedToRelease(MessagePayload<NetworkLordFreedToRelease> obj)
    {
        var data = obj.What;

        GameThread.RunSafe(() =>
        {
            if (!objectManager.TryGetObjectWithLogging<Hero>(data.MainHeroId, out var mainHero)) return;
            if (!objectManager.TryGetObjectWithLogging<Hero>(data.ConversationHeroId, out var conversationHero)) return;
            if (!conversationHero.IsPrisoner) return;

            EndCaptivityAction.ApplyByReleasedByChoice(conversationHero, mainHero);
            ChangeRelationAction.ApplyRelationChangeBetweenHeroes(mainHero, conversationHero, LetLordGoReward);
            
            // TODO
            //TraitLevelingHelper.OnLordFreed(conversationHero);
        },
        context: nameof(Handle_NetworkLordFreedToRelease));
    }
}
