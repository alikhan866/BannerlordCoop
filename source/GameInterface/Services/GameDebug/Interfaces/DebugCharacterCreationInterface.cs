using Common;
using Common.Logging;
using SandBox;
using Serilog;
using SandBox.GauntletUI.CharacterCreation;
using System.Linq;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.CharacterCreationContent;
using TaleWorlds.Core;
using TaleWorlds.Library;
using TaleWorlds.MountAndBlade;
using TaleWorlds.MountAndBlade.GauntletUI.BodyGenerator;
using TaleWorlds.MountAndBlade.ViewModelCollection.FaceGenerator;

namespace GameInterface.Services.GameDebug.Interfaces
{
    internal interface IDebugCharacterCreationInterface : IGameAbstraction
    {
        void SkipCharacterCreation();
    }

    /// <summary>
    /// Walks vanilla character creation to its end without a person clicking through it.
    /// </summary>
    /// <remarks>
    /// Two of its stages assume things a HEADLESS client does not have, and both were fatal before they were
    /// guarded - the process exited with a half-built campaign, which is why a render-free client could never
    /// reach a mission at all.
    ///
    ///   - the culture stage renames the player's clan, and a joining client has no clan yet;
    ///   - the face stage reaches through a Gauntlet view for its randomiser, and there is no Gauntlet.
    ///
    /// Neither is skipped silently, and neither is skipped on a client that does have them: the guards test
    /// for the thing itself rather than for the role, so a rendered client keeps taking the original path
    /// exactly as before. Both are cosmetic for a joiner in any case - its hero, clan, culture and face are
    /// replaced wholesale by the join baseline moments later.
    /// </remarks>
    internal class DebugCharacterCreationInterface : IDebugCharacterCreationInterface
    {
        private static readonly ILogger Logger = LogManager.GetLogger<DebugCharacterCreationInterface>();

        /// <summary>
        /// Name of into video from <see cref="SandboxGameManager.OnLoadFinished"/>
        /// </summary>
        private static readonly string VideoPathName = "campaign_intro";

        /// <summary>
        /// Determines if game is currently running the character creation intro.
        /// The character creation into is the first state of character creation.
        /// </summary>
        /// <returns>True if game is in character creation intro state, false otherwise</returns>
        public static bool InCharacterCreationIntro()
        {
            return GameStateManager.Current?.ActiveState is VideoPlaybackState videoState &&
                   videoState.VideoPath.Contains(VideoPathName);
        }

        public void SkipCharacterCreation()
        {
            // Validation
            if (InCharacterCreationIntro() == false) return;

            // Logic
            GameThread.Run(SkipCharacterCreationInternal);
        }

        /// <summary>
        /// Points <c>Clan.PlayerClan</c> at the main hero's clan when the campaign never did.
        /// </summary>
        /// <remarks>
        /// <c>Clan.PlayerClan</c> is not stored anywhere of its own - it reads
        /// <c>Campaign.PlayerDefaultFaction</c>, which is assigned exactly once, inside
        /// <c>Campaign.InitializeGamePlayReferences</c>, from <c>Hero.MainHero.Clan</c>. On a headless client
        /// the hero exists by the time that runs but its clan does not, so the reference is left null and
        /// never revisited.
        ///
        /// The consequence is nastier than a missing value. Every later step reaches the clan by callvirt -
        /// <c>Clan.PlayerClan.ChangeClanName</c>, <c>Clan.PlayerClan.Renown = ...</c> - and callvirt null-checks
        /// at the CALL SITE, so the throw names the method doing the reaching rather than the one that failed
        /// to set it. That is why this surfaced first as "SetSelectedCulture threw" and then as
        /// "ApplyFinalEffects threw", two frames that were both innocent.
        ///
        /// Restating the reference is the fix rather than guarding each of those sites, because the
        /// definition of PlayerDefaultFaction IS the main hero's clan - this only says it again, later, once
        /// the clan exists. If the hero has no clan either there is nothing honest to point at, so it says so
        /// and leaves it null.
        /// </remarks>
        private static void EnsurePlayerClanReference()
        {
            Campaign campaign = Campaign.Current;
            if (campaign == null || campaign.PlayerDefaultFaction != null) return;

            Clan clan = Hero.MainHero?.Clan;
            if (clan == null)
            {
                Logger.Warning(
                    "[CharacterCreation] no player clan to point Clan.PlayerClan at: mainHero={HasHero}. " +
                    "Anything reading Clan.PlayerClan from here will throw in its own frame",
                    Hero.MainHero != null);
                return;
            }

            campaign.PlayerDefaultFaction = clan;
            Logger.Information(
                "[CharacterCreation] Clan.PlayerClan was null; pointed it at the main hero's clan {Clan}. " +
                "Campaign.InitializeGamePlayReferences ran before the hero had one",
                clan.StringId);
        }

        public void SkipCharacterCreationInternal()
        {
            // Skip intro video
            SandBoxGameManager gameManager = (SandBoxGameManager)Game.Current.GameManager;
            gameManager.LaunchSandboxCharacterCreation();

            EnsurePlayerClanReference();

            CharacterCreationState characterCreationState = GameStateManager.Current.ActiveState as CharacterCreationState;
            CharacterCreationStageBase currentStage = characterCreationState._characterCreationManager.CurrentStage;
            
            if (currentStage is CharacterCreationCultureStage)
            {
                var manager = characterCreationState._characterCreationManager;
                var content = manager.CharacterCreationContent;
                CultureObject culture = content.GetCultures().First(c => c.Name.ToString() == "Empire");

                // SetSelectedCulture ends in Clan.PlayerClan.ChangeClanName, reached by callvirt - so with no
                // player clan yet it throws with SetSelectedCulture as the top frame and nothing deeper to
                // point at. That is what killed the headless client here. The else branch does the same work
                // minus the rename, which a joining client does not need: its clan name arrives with the
                // baseline.
                if (Clan.PlayerClan != null)
                {
                    content.SetSelectedCulture(culture, manager);
                }
                else
                {
                    content.SelectedCulture = culture;
                    content.SelectedTitleType = content.DefaultSelectedTitleType;
                    manager.ResetMenuOptions();
                    Logger.Information(
                        "[CharacterCreation] selected {Culture} without the clan rename - there is no player " +
                        "clan yet, and a joining client is given one by the join baseline",
                        culture.StringId);
                }

                manager.NextStage();
            }

            if (characterCreationState._characterCreationManager.CurrentStage is CharacterCreationFaceGeneratorStage)
            {
                // The randomiser lives on a Gauntlet view model, so every link here is absent without a UI.
                // Each is read separately rather than in one chain, so the log can say which one was missing
                // instead of reporting a null somewhere in an expression.
                ICharacterCreationStageListener listener = characterCreationState._characterCreationManager.CurrentStage.Listener;
                BodyGeneratorView bgv = (listener as CharacterCreationFaceGeneratorView)?._faceGeneratorView;
                FaceGenVM facegen = bgv?.DataSource;

                if (facegen?.FaceProperties != null)
                {
                    facegen.FaceProperties.Randomize();
                }
                else
                {
                    Logger.Information(
                        "[CharacterCreation] leaving the default face - listener={HasListener} " +
                        "bodyGeneratorView={HasView} faceGen={HasFaceGen}. A face needs a Gauntlet view, and " +
                        "a joining client is given its appearance by the join baseline",
                        listener != null,
                        bgv != null,
                        facegen != null);
                }

                characterCreationState._characterCreationManager.NextStage();
            }

            if (characterCreationState._characterCreationManager.CurrentStage is CharacterCreationNarrativeStage)
            {
                for (int i = 0; i < characterCreationState._characterCreationManager.CharacterCreationMenuCount; i++)
                {
                    NarrativeMenuOption characterCreationOption = characterCreationState._characterCreationManager.GetCurrentMenuOptions(i).FirstOrDefault((NarrativeMenuOption o) => o.OnCondition == null || o.OnCondition(characterCreationState._characterCreationManager));
                    bool flag4 = characterCreationOption != null;
                    if (flag4)
                    {
                        //characterCreationState.CharacterCreation.RunConsequence(characterCreationOption, i, false);
                    }
                }
                characterCreationState._characterCreationManager.NextStage();
            }

            if (characterCreationState._characterCreationManager.CurrentStage is CharacterCreationBannerEditorStage)
            {
                characterCreationState._characterCreationManager.NextStage();
            }

            if (characterCreationState._characterCreationManager.CurrentStage is CharacterCreationClanNamingStage)
            {
                characterCreationState._characterCreationManager.CharacterCreationContent.MainCharacterName = "RandomPlayer";
                characterCreationState._characterCreationManager.NextStage();
            }

            if (characterCreationState._characterCreationManager.CurrentStage is CharacterCreationReviewStage)
            {
                characterCreationState._characterCreationManager.NextStage();
            }

            if (characterCreationState._characterCreationManager.CurrentStage is CharacterCreationOptionsStage)
            {
                characterCreationState._characterCreationManager.NextStage();
            }
        }
    }
}