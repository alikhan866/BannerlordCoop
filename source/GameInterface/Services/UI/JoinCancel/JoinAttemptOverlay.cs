using Common;
using TaleWorlds.Engine.GauntletUI;
using TaleWorlds.GauntletUI.Data;
using TaleWorlds.ScreenSystem;

namespace GameInterface.Services.UI.JoinCancel;

/// <summary>
/// The cancel affordance a coop join attempt shows while it is in flight, over the engine's own
/// loading window. Both methods touch Gauntlet state and must be called on the game thread.
/// </summary>
public interface IJoinAttemptOverlay : IGameAbstraction
{
    void Show(string cancelLabel);

    /// <summary>Safe to call when nothing is shown.</summary>
    void Hide();
}

/// <inheritdoc cref="IJoinAttemptOverlay"/>
public sealed class JoinAttemptOverlay : GlobalLayer, IJoinAttemptOverlay
{
    // Outranks the engine's loading-window global layer, so the button draws over it.
    private const int CancelLayerOrder = 115005;

    private const string CancelMovieName = "CoopJoinCancelOverlay";

    private GauntletLayer gauntletLayer;
    private GauntletMovieIdentifier movie;
    private JoinCancelVM dataSource;
    private bool isShown;

    public void Show(string cancelLabel)
    {
        if (isShown) return;

        // A render-free process has no Gauntlet to put this on, and building the layer there throws - which
        // surfaced as "Failed to run action on the game thread: ShowJoinAttempt" in the middle of a headless
        // client's join. The overlay is a CANCEL BUTTON: a driven client has nobody to press it and the rig
        // cancels through the control channel instead, so there is nothing to replace it with. Hide() is
        // already safe to call when nothing is shown, so leaving isShown false keeps the pair balanced.
        if (ModInformation.IsHeadless) return;

        // Marked before the layer goes up so a partial Show can still be taken down.
        isShown = true;

        dataSource = new JoinCancelVM(cancelLabel);
        gauntletLayer = new GauntletLayer(CancelMovieName, CancelLayerOrder)
        {
            IsFocusLayer = true,
        };
        movie = gauntletLayer.LoadMovie(CancelMovieName, dataSource);
        Layer = gauntletLayer;

        // The layers below hide the cursor; without this the button cannot be clicked.
        gauntletLayer.InputRestrictions.SetInputRestrictions();
        ScreenManager.AddGlobalLayer(this, false);
        ScreenManager.TrySetFocus(gauntletLayer);
    }

    public void Hide()
    {
        if (!isShown) return;

        isShown = false;
        if (gauntletLayer == null) return;

        try
        {
            gauntletLayer.IsFocusLayer = false;
            ScreenManager.TryLoseFocus(gauntletLayer);
            gauntletLayer.InputRestrictions.ResetInputRestrictions();
            gauntletLayer.ReleaseMovie(movie);
        }
        finally
        {
            // Even a failed teardown must unregister the layer and drop the handles.
            ScreenManager.RemoveGlobalLayer(this);
            dataSource.OnFinalize();
            dataSource = null;
            movie = null;
            gauntletLayer = null;
        }
    }
}
