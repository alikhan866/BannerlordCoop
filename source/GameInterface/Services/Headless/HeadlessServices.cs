using Common;
using Common.Logging;
using HarmonyLib;
using Serilog;
using System;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.MapEvents;
using TaleWorlds.Core;
using TaleWorlds.Library;

namespace GameInterface.Services.Headless
{
    /// <summary>
    /// The engine services a windowless server has to supply for itself.
    /// </summary>
    /// <remarks>
    /// Each of these is normally provided by a view module that a headless engine never loads, leaving the
    /// slot null or pointing at something that needs a renderer. Installed once, as early as the mod runs.
    /// </remarks>
    public static class HeadlessServices
    {
        private static readonly ILogger Logger = LogManager.GetLogger(typeof(HeadlessServices));

        private static bool installed;

        /// <summary>
        /// The save this server hosts, published by the mod at start-up. Console commands default to it, so
        /// "save" with no argument overwrites the world in place rather than inventing a new file.
        /// </summary>
        public static string HostedSaveName { get; set; }

        public static void Install()
        {
            if (!ModInformation.IsHeadless || installed) return;
            installed = true;

            // So that anything failing afterwards is logged rather than shown in a dialog nobody can see -
            // which on this process means dying without saying why.
            Debug.DebugManager = new HeadlessDebugManager();

            // Before any save is touched: campaign loading branches on the game version, and an unresolved
            // one sends a current save down migration paths written for far older ones.
            Patches.HeadlessGameVersionGuard.Apply(new Harmony("Coop.Headless"));

            Logger.Information("[Headless] engine services installed (debug manager, version guard)");
        }

        /// <summary>
        /// The services that hang off a campaign, so they can only be installed once one exists. Called
        /// from the map scene load, which is the first point where Campaign.Current is valid and still
        /// early enough to precede the save's map events being restored.
        /// </summary>
        public static void InstallCampaignServices()
        {
            if (!ModInformation.IsHeadless) return;

            var visualCreator = Campaign.Current?.VisualCreator;
            if (visualCreator == null)
            {
                Logger.Error("[Headless] no VisualCreator on the campaign; map events will have no visual");
                return;
            }

            visualCreator.MapEventVisualCreator = new HeadlessMapEventVisualCreator();

            InstallBannerManager();

            Logger.Information("[Headless] campaign services installed (map event visuals, banners)");
        }
        /// <summary>
        /// Loads the banner colour palette, which nothing else here does.
        /// </summary>
        /// <remarks>
        /// Vanilla initialises BannerManager from Module.LoadSingleModule, a path a headless boot never
        /// takes, leaving BannerManager.Instance null. That is not a cosmetic loss: Clan.PreAfterLoad calls
        /// UpdateBannerColorsAccordingToKingdom for every clan, which reaches BannerManager.GetColorId, and
        /// the resulting NullReferenceException kills the load on the very first clan - player_faction.
        ///
        /// Only banner_icons.xml is read, which is data, so there is nothing here that needs a renderer.
        /// </remarks>
        private static void InstallBannerManager()
        {
            try
            {
                // A no-op if something already created it.
                BannerManager.Initialize();

                // LoadBannerIcons, not ResetAndLoad: ResetAndLoad replaces the backing dictionary with a
                // fresh one, while the read-only view returned by ColorPalette still wraps the original
                // created in the constructor - so everything it loads lands somewhere nothing reads, and
                // the palette stays empty. Loading directly fills the dictionary the view actually sees.
                if (BannerManager.ColorPalette == null || BannerManager.ColorPalette.Count == 0)
                {
                    BannerManager.Instance.LoadBannerIcons();
                }

                Logger.Information("[Headless] banner colours loaded: {Count}",
                    BannerManager.ColorPalette?.Count ?? 0);
            }
            catch (Exception e)
            {
                Logger.Error(e, "[Headless] could not load banner colours; clan loading will fail");
            }
        }
    }

    /// <summary>
    /// Supplies map events with a visual that does not exist.
    /// </summary>
    /// <remarks>
    /// Every map event asks for one when it starts, and the campaign holds the result without null-checking
    /// it. On a client this is a marker drawn on the campaign map; here there is no map and no one looking
    /// at it, so the object exists only to be a valid thing to call.
    /// </remarks>
    internal sealed class HeadlessMapEventVisualCreator : IMapEventVisualCreator
    {
        public IMapEventVisual CreateMapEventVisual(MapEvent mapEvent) => new HeadlessMapEventVisual();
    }

    internal sealed class HeadlessMapEventVisual : IMapEventVisual
    {
        public void Initialize(CampaignVec2 position, bool isVisible) { }

        public void OnMapEventEnd() { }

        public void SetVisibility(bool isVisible) { }
    }
}
