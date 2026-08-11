using Common.Logging;
using SandBox;
using Serilog;
using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.ComponentInterfaces;
using TaleWorlds.CampaignSystem.Map;
using TaleWorlds.CampaignSystem.Map.DistanceCache;
using TaleWorlds.CampaignSystem.Party;
using TaleWorlds.CampaignSystem.Settlements;
using TaleWorlds.Core;
using TaleWorlds.Engine;
using TaleWorlds.Library;
using TaleWorlds.ModuleManager;

namespace GameInterface.Services.Headless
{
    /// <summary>
    /// The campaign map for a server with no renderer.
    /// </summary>
    /// <remarks>
    /// Vanilla <see cref="MapScene.Load"/> is what a headless dedicated server dies in: it builds an agent
    /// renderer scene controller, optimises the scene, loads atmosphere data and reads the full 19 MB
    /// Main_map, all of which need a renderer this process does not have. The engine's own log gets as far
    /// as "Creating map scene" / "Loading xml file: SceneObj/Main_map/scene.xscene" and the process dies
    /// natively, with no managed exception to catch.
    ///
    /// This subclass overrides only <see cref="Load"/>, so everything that matters - navigation mesh
    /// queries, pathfinding, terrain types - keeps the real implementation. The load itself does the same
    /// work minus the parts that need to draw something:
    ///
    ///   - the scene is read with the same render-free SceneInitializationData vanilla already uses, from a
    ///     stripped Main_map that carries the navmesh and terrain but none of the visual entities;
    ///   - map borders and terrain size are set directly, because the entities vanilla reads them from
    ///     ("border_min", "border_max") are visual and not present in the stripped scene;
    ///   - settlement distances come from the precomputed navigation cache rather than being derived from
    ///     a fully loaded scene.
    ///
    /// The shape of this is taken from the coop team's own dedicated server, which solves it the same way.
    ///
    /// IMapScene is re-declared deliberately. MapScene.Load implements it implicitly, which the compiler
    /// emits as virtual FINAL - it cannot be overridden. Re-implementing the interface here re-maps its
    /// members onto this type, so a caller holding an IMapScene gets the Load below, while every member not
    /// redeclared keeps resolving to MapScene's inherited implementation.
    /// </remarks>
    internal sealed class HeadlessMapScene : MapScene, IMapScene
    {
        private static readonly ILogger Logger = LogManager.GetLogger<HeadlessMapScene>();

        /// <summary>
        /// Map bounds and terrain size, which vanilla reads off the "border_min" and "border_max" entities.
        /// A stripped scene has no entities, so they are stated here. These are Main_map's actual bounds -
        /// getting them wrong silently confines or scatters parties rather than failing outright.
        /// </summary>
        private static readonly Vec2 MinimumPosition = new Vec2(62f, 30f);
        private static readonly Vec2 MaximumPosition = new Vec2(790f, 640f);
        private static readonly Vec2 TerrainSize = new Vec2(848f, 848f);
        private const float MaximumHeight = 620f;

        private const string MapSceneName = "Main_map";

        /// <summary>Where the shipped settlement distance cache lives inside a module.</summary>
        private const string DistanceCacheDirectory = "ModuleData";
        private const string DistanceCacheSubDirectory = "DistanceCaches";
        private const string DistanceCacheFileName = "settlements_distance_cache_Default.bin";

        private const BindingFlags Instance = BindingFlags.Instance | BindingFlags.NonPublic;

        public new void Load()
        {
            Logger.Information("[Headless] creating a render-free map scene");

            var scene = Scene.CreateNewScene(false, true, DecalAtlasGroup.Worldmap, "MapScene");
            SetPrivate("_scene", scene);

            // The same flags vanilla sets. They are what makes reading the scene viable without a renderer.
            var initialization = new SceneInitializationData(true)
            {
                UsePhysicsMaterials = false,
                EnableFloraPhysics = false,
                UseTerrainMeshBlending = false,
                CreateOros = false,
            };

            scene.SetFetchCrcInfoOfScene(true);

            // Vanilla's order, kept exactly: unwalkable meshes are disabled BEFORE the region map is built
            // and before the read, because the region map describes which navigation regions a party may
            // cross. Build it against the wrong set and every path query afterwards walks regions that do
            // not exist - which faults natively, inside the engine, with nothing to catch.
            typeof(MapScene)
                .GetMethod("DisableUnwalkableNavigationMeshes", Instance)
                ?.Invoke(this, null);

            scene.SetNavMeshRegionMap(
                SandBoxHelpers.MapSceneHelper.GetRegionMapping(Campaign.Current.Models.PartyNavigationModel));

            scene.Read(MapSceneName, ref initialization, string.Empty);

            SetPrivate("_minimumPositionCache", MinimumPosition);
            SetPrivate("_maximumPositionCache", MaximumPosition);
            SetPrivate("_maximumHeightCache", MaximumHeight);
            SetPrivate("_terrainSize", TerrainSize);

            Logger.Information("[Headless] map scene read: navMeshFaces={Faces} navMeshCrc={Crc}",
                GetNumberOfNavigationMeshFaces(), scene.GetNavigationMeshCRC());

            RegisterSettlementDistanceCache();

            // Before the campaign restores the save's active map events, each of which asks for a visual.
            HeadlessServices.InstallCampaignServices();

            Campaign.Current.DefaultWeatherNodeDimension = 32;
            Campaign.Current.Models.MapWeatherModel.InitializeCaches();

            scene.Tick(0.1f);

            // Vanilla registers Campaign.LateAITick here as an engine async task. This does NOT, and the
            // omission is deliberate: that task runs on an engine worker thread, and the rest of the load -
            // every hero, clan and party being restored - is still to come on this one. Letting the AI walk
            // half-restored campaign objects from another thread kills the process natively, with no
            // managed exception and no stack, which is precisely how this presented. The coop team's own
            // server does not register it from the load either.
            //
            // The consequence is that the campaign's late AI pass does not run on this server. That is
            // visible as AI parties being less reactive, not as anything breaking.

            Logger.Information("[Headless] map scene ready");
        }

        /// <summary>
        /// Nothing to tear down: this scene owns no render resources.
        /// </summary>
        public new void Destroy()
        {
        }

        // ---------------------------------------------------------------------------------------------
        // Everything below stands in for data a stripped, render-free scene never produced. Vanilla reads
        // these from atmosphere data, a snow/rain texture and tagged scene entities - all of which are
        // loaded by the parts of MapScene.Load that need a renderer, so here they are null or absent and
        // calling the inherited implementation would fault. The values match the coop team's own server.
        // ---------------------------------------------------------------------------------------------

        /// <summary>Flat ground, pointing straight up. Height only ever mattered for drawing the map.</summary>
        public new void GetTerrainHeightAndNormal(Vec2 position, out float height, out Vec3 normal)
        {
            height = 0f;
            normal = new Vec3(0f, 0f, 1f, -1f);
        }

        public new Vec3 GetGroundNormal(Vec2 position) => new Vec3(0f, 0f, 1f, -1f);

        public new float GetFaceVertexZ(PathFaceRecord navMeshFace) => 0f;

        /// <summary>Weather visuals. There is no sky here, and no one to see it.</summary>
        public new float GetSnowAmountAtPosition(Vec2 position) => 0f;

        public new float GetRainAmountAtPosition(Vec2 position) => 0f;

        public new float GetWinterTimeFactor() => 0f;

        public new List<AtmosphereState> GetAtmosphereStates() => new List<AtmosphereState>();

        public new void SetAtmosphereColorgrade(TerrainType terrainType)
        {
        }

        /// <summary>
        /// Vanilla places besieger camps on entities tagged in the scene. Without them, both camps sit on
        /// the settlement gate - the siege still runs, it simply has no distinct camp positions to show.
        /// </summary>
        public new void GetSiegeCampFrames(
            Settlement settlement,
            out List<MatrixFrame> siegeCamp1GlobalFrames,
            out List<MatrixFrame> siegeCamp2GlobalFrames)
        {
            var frame = MatrixFrame.Identity;
            var gate = settlement.GatePosition;
            frame.origin = new Vec3(gate.X, gate.Y, 0f, -1f);

            siegeCamp1GlobalFrames = new List<MatrixFrame> { frame };
            siegeCamp2GlobalFrames = new List<MatrixFrame> { frame };
        }

        /// <summary>
        /// Echo back the CRCs the campaign already recorded. The real ones describe a scene this process
        /// deliberately did not load in full, and reporting those would read as a corrupted map.
        /// </summary>
        public new uint GetSceneXmlCrc() => ReadCampaignCrc("_campaignMapSceneXmlCrc");

        public new uint GetSceneNavigationMeshCrc()
            => ReadCampaignCrc("_campaignMapSceneNavigationMeshCrc");

        private static uint ReadCampaignCrc(string fieldName)
        {
            var field = typeof(Campaign).GetField(fieldName, Instance);
            if (field == null) return 0u;

            return (uint)field.GetValue(Campaign.Current);
        }

        // Explicit implementations, because MapScene declares these explicitly too.

        bool IMapScene.GetHeightAtPoint(in CampaignVec2 point, ref float height)
        {
            height = 0f;
            return true;
        }

        void IMapScene.AddNewEntityToMapScene(string entityId, in CampaignVec2 position)
        {
        }

        MapPatchData IMapScene.GetMapPatchAtPosition(in CampaignVec2 position) => default;

        List<TerrainType> IMapScene.GetEnvironmentTerrainTypes(in CampaignVec2 vec2)
            => new List<TerrainType>();

        List<TerrainType> IMapScene.GetEnvironmentTerrainTypesCount(
            in CampaignVec2 vec2, out TerrainType currentPositionTerrainType)
        {
            currentPositionTerrainType = GetTerrainTypeAtPosition(vec2);
            return new List<TerrainType>();
        }

        /// <summary>
        /// Settlement-to-settlement distances. On a rendered client these come off a fully loaded scene;
        /// here the shipped precomputed cache stands in, so travel times and AI routing stay the values the
        /// campaign was balanced around instead of degrading to straight lines.
        /// </summary>
        private static void RegisterSettlementDistanceCache()
        {
            var cache = new SandBoxNavigationCache(MobileParty.NavigationType.Default);
            var cachePath = FindDistanceCache();

            if (cachePath != null)
            {
                Logger.Information("[Headless] loading settlement distance cache from {Path}", cachePath);
                cache.Deserialize(cachePath);
            }
            else
            {
                // Correct but slow: it walks the navigation mesh between every settlement pair.
                Logger.Warning("[Headless] no shipped distance cache found; generating one (this is slow)");
                cache.GenerateCacheData();
            }

            Campaign.Current.Models.MapDistanceModel
                .RegisterDistanceCache(MobileParty.NavigationType.Default, cache);
        }

        private static string FindDistanceCache()
        {
            foreach (var module in ModuleHelper.GetActiveModules())
            {
                var path = System.IO.Path.Combine(
                    ModuleHelper.GetModuleFullPath(module.Id),
                    DistanceCacheDirectory,
                    DistanceCacheSubDirectory,
                    DistanceCacheFileName);

                if (File.Exists(path)) return path;
            }

            return null;
        }

        /// <summary>
        /// Writes one of <see cref="MapScene"/>'s private caches. They are private with no setter, and
        /// vanilla fills them from scene entities this scene does not have.
        /// </summary>
        private void SetPrivate(string fieldName, object value)
        {
            var field = typeof(MapScene).GetField(fieldName, Instance);
            if (field == null)
            {
                Logger.Error("[Headless] MapScene.{Field} not found; the map scene will be wrong", fieldName);
                return;
            }

            field.SetValue(this, value);
        }
    }
}
