using System.Windows;
using System.Windows.Input;

namespace LayerCAD;

public partial class MainWindow
{
    private sealed record ProjectState(Bed Bed, Part[] Parts);
    private readonly EditHistory<ProjectState> projectHistory = new();
    private ProjectState currentProjectState = null!;
    private ProjectState savedProjectState = null!;

    private void ResetProjectHistory()
    {
        projectHistory.Reset();
        currentProjectState = savedProjectState = new ProjectState(bed, parts.ToArray());
        UpdateHistoryButtons();
    }

    private void RecordProjectEdit()
    {
        projectHistory.Record(currentProjectState);
        currentProjectState = new ProjectState(bed, parts.ToArray());
        UpdateHistoryButtons();
    }

    private void UndoProject(object sender, RoutedEventArgs e)
    {
        if (featureMove || SelectingBevel)
            CancelBuildFeature(sender, e);
        else if (choosingBevelPart)
            CancelSurfaceSelection(sender, e);
        else
            StepProjectHistory(false);
    }
    private void RedoProject(object sender, RoutedEventArgs e) => StepProjectHistory(true);

    private void StepProjectHistory(bool forward)
    {
        if (projectHistory.Step(currentProjectState, forward) is not { } state)
            return;
        CancelLasso();
        EndBuildFeature();
        EndSurfaceSelection();
        int selection = SelectedPartIndex;
        currentProjectState = state;
        parts.Clear();
        parts.AddRange(state.Parts);
        bed = state.Bed;
        dirty = !ReferenceEquals(state, savedProjectState);
        SetTitle();
        UpdateBedLabel();
        RefreshParts(Math.Min(selection, parts.Count - 1));
        UpdateHistoryButtons();
        SetStatus(forward ? "Redid the last project edit." : "Undid the last project edit.");
    }

    private void UpdateHistoryButtons()
    {
        UndoButton.IsEnabled = projectHistory.CanUndo || featureMove || SelectingBevel || choosingBevelPart;
        RedoButton.IsEnabled = projectHistory.CanRedo;
    }

    internal static bool HistoryShortcut(KeyEventArgs e, out bool forward, bool allowText = false)
    {
        forward = e.Key == Key.Y || (Keyboard.Modifiers & ModifierKeys.Shift) != 0;
        return (Keyboard.Modifiers & (ModifierKeys.Control | ModifierKeys.Alt)) == ModifierKeys.Control &&
            e.Key is Key.Z or Key.Y && (allowText || e.OriginalSource is not System.Windows.Controls.Primitives.TextBoxBase);
    }
}
