using Common;
using GameInterface.Registry.Auto;
using GameInterface.Services.ObjectManager;
using SandBox.View.Map.Managers;
using SandBox.View.Map.Visuals;
using Serilog;
using System;
using System.Collections.Generic;
using System.Reflection;

namespace GameInterface.Services.PartyVisuals;

internal class PartyVisualRegistry : AutoRegistryBase<MobilePartyVisual>
{
    public PartyVisualRegistry(ILogger logger, IAutoRegistryFactory autoRegistryFactory, IObjectManager objectManager)
        : base(logger, autoRegistryFactory, objectManager)
    {
    }

    public override IEnumerable<MethodBase> Constructors => Array.Empty<MethodBase>();

    public override IEnumerable<MethodBase> DestroyMethods => Array.Empty<MethodBase>();

    public override void RegisterAllObjects()
    {
        // A party visual is the marker drawn for a party on the campaign map, so a server with no renderer
        // has none to register. The null check below cannot cover this: reaching for Current on a headless
        // process throws inside SandBoxViewSubModule rather than returning null, and that exception comes
        // out of the CampaignReady handler - which is what starts the server listening, so the port never
        // opens and nothing says why.
        if (ModInformation.IsHeadless)
        {
            Logger.Information("Headless server: skipping party visuals, which need a renderer");
            return;
        }

        var visualManager = MobilePartyVisualManager.Current;

        if (visualManager == null)
        {
            Logger.Error("Unable to register party visuals when PartyVisualManager is null");
            return;
        }

        foreach (MobilePartyVisual visual in visualManager._visualsFlattened)
        {
            var party = visual.MapEntity.MobileParty;
            RegisterExistingObject(party.StringId, visual);
        }
    }

    public override void OnClientCreated(MobilePartyVisual obj, string id)
    {
    }

    public override void OnClientDestroyed(MobilePartyVisual obj, string id)
    {
    }

    public override void OnServerCreated(MobilePartyVisual obj, string id)
    {
    }

    public override void OnServerDestroyed(MobilePartyVisual obj, string id)
    {
    }
}