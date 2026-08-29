using Common;
using Common.Logging;
using Serilog;
using System;
using TaleWorlds.Engine.GauntletUI;
using TaleWorlds.Library;
using TaleWorlds.ScreenSystem;

namespace GameInterface.Services.MapEvents.UI;

/// <summary>
/// "Waiting for Omar to choose" - shown after you have answered, while the battle waits on everyone else.
/// </summary>
/// <remarks>
/// Purely informational, and deliberately claims no input: no focus layer, no input restrictions, and the movie
/// sets DoNotAcceptEvents. An earlier overlay in this feature took the mouse in order to be clickable and made
/// the rest of the screen unusable; this one has nothing to click, so it can afford to take nothing.
///
/// A GlobalLayer rather than a screen view, so it survives whatever the player is looking at while they wait -
/// the same approach the join-attempt overlay uses.
/// </remarks>
internal sealed class TroopPreferenceWaitOverlay : GlobalLayer
{
    private static readonly ILogger Logger = LogManager.GetLogger(typeof(TroopPreferenceWaitOverlay));

    /// <summary>Above the map, below the join-cancel button, which is a control and outranks a notice.</summary>
    private const int WaitLayerOrder = 115000;

    private const string MovieName = "CoopTroopPreferenceWait";

    private static readonly TroopPreferenceWaitOverlay Instance = new TroopPreferenceWaitOverlay();

    private GauntletLayer gauntletLayer;
    private GauntletMovieIdentifier movie;
    private TroopPreferenceWaitVM dataSource;
    private bool isShown;

    public static void ShowWaitingFor(string names) => Instance.Show(names);

    public static void HideIfShown() => Instance.Hide();

    private void Show(string names)
    {
        // A render-free process has no Gauntlet to put this on and building the layer there throws. A driven
        // client answers instantly through the control channel and never waits, so there is nothing to lose.
        if (ModInformation.IsHeadless) return;

        if (isShown)
        {
            dataSource?.Update(names);
            return;
        }

        try
        {
            isShown = true;
            dataSource = new TroopPreferenceWaitVM(names);
            gauntletLayer = new GauntletLayer(MovieName, WaitLayerOrder);
            movie = gauntletLayer.LoadMovie(MovieName, dataSource);
            Layer = gauntletLayer;
            ScreenManager.AddGlobalLayer(this, false);
        }
        catch (Exception e)
        {
            // Never let a missing overlay stop a battle - the mission start is already on its way.
            Logger.Error(e, "[TroopPreference] could not show the waiting overlay");
            Hide();
        }
    }

    private void Hide()
    {
        if (!isShown) return;
        isShown = false;

        try
        {
            if (gauntletLayer != null)
            {
                if (movie != null) gauntletLayer.ReleaseMovie(movie);
                ScreenManager.RemoveGlobalLayer(this);
            }
        }
        catch (Exception e)
        {
            Logger.Error(e, "[TroopPreference] could not take the waiting overlay down");
        }
        finally
        {
            dataSource?.OnFinalize();
            dataSource = null;
            movie = null;
            gauntletLayer = null;
            Layer = null;
        }
    }
}

/// <summary>The names the battle is still waiting on.</summary>
internal sealed class TroopPreferenceWaitVM : ViewModel
{
    private string waitingText;

    public TroopPreferenceWaitVM(string names)
    {
        waitingText = names;
    }

    [DataSourceProperty]
    public string TitleText => "Waiting for other players";

    [DataSourceProperty]
    public string WaitingText
    {
        get => waitingText;
        private set
        {
            if (waitingText == value) return;
            waitingText = value;
            OnPropertyChanged(nameof(WaitingText));
        }
    }

    public void Update(string names) => WaitingText = names;
}
