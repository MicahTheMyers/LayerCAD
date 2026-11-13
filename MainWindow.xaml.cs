using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Media3D;
using Microsoft.Win32;

namespace LayerCAD;

public partial class MainWindow : Window
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private readonly List<Part> parts = [];
    private readonly HashSet<string> collapsedPartGroups = [];
    private Bed bed = new(220, 220, 250);
    private (double Width, double Depth, Model3DGroup Model)? buildPlate;
    private readonly string preferencesPath;
    private DisplayUnits units;
    private string? projectPath;
    private bool dirty;
    private bool updatingSelection;
    private bool partsPaneCollapsed;
    private bool partPopupDismissed;
    private Point? dragOrigin;
    private bool panDrag;
    private Vector3D cameraPan;
    private Point? selectionPress;
    private bool selectionDragged;
    private readonly ViewportOrbitQueue orbitQueue;
    private double yaw, pitch, distance;
    private bool choosingSketchSurface;
    private readonly Dictionary<GeometryModel3D, (Part Part, List<Triangle> Triangles)> pickableParts = [];
    private readonly Dictionary<GeometryModel3D, Vector3D> previewTranslations = [];
    private readonly ConditionalWeakTable<Part, RenderedPart> renderedParts = new();
    private readonly ConditionalWeakTable<Part, SketchRenderCache> sketchRenderCache = new();
    private readonly ConditionalWeakTable<RenderedPart, PartModelCache> partModelCache = new();
    private readonly ConditionalWeakTable<List<Triangle>, TrianglePicker> pickers = new();
    private ScenePickNode? scenePickRoot;
    private Rect3D sceneBounds = Rect3D.Empty;
    private sealed record RenderedPart(List<Triangle> Triangles, MeshGeometry3D Mesh);
    private sealed class SketchRenderCache
    {
        public double Lift;
        public RenderedPart? Rendered;
    }

    private sealed class PartModelCache
    {
        public Color Color;
        public GeometryModel3D? Model;
    }
    private sealed record SceneHit(GeometryModel3D ModelHit, Point3D PointHit, int TriangleIndex);
    private GeometryModel3D? hoveredModel;
    private SketchSurface? hoveredFace;
    private int hoveredTriangle = -1;
    private SketchPlane? hoveredPlane;

    private Part? Selected => PartList.SelectedItem as Part;
    private int SelectedPartIndex => Selected is { } part ? parts.IndexOf(part) : -1;

    public MainWindow() : this(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "LayerCAD", "preferences.txt"))
    {
    }

    internal MainWindow(string preferencesPath)
    {
        this.preferencesPath = preferencesPath;
        orbitQueue = new ViewportOrbitQueue(DragCameraBy);
        Closed += (_, _) => orbitQueue.Cancel();
        units = LoadUnits(preferencesPath);
        InitializeComponent();
        ResetProjectHistory();
        InitializeThreadSizes();
        OrientationCube.ViewSelected += SetViewDirection;
        OrientationCube.OrbitDragged += orbitQueue.Add;
        OrientationCube.MouseEnter += ClearSurfaceHover;
        SetTitle();
        UpdateBedLabel();
        ResetCamera();
        RefreshParts(-1);
        SetStatus("Create a sketch or add a primitive to start modeling.");
        Loaded += (_, _) => _ = CheckForUpdates(manual: false);
    }

    private string NextPartName(PartKind kind)
    {
        int number = 1;
        while (parts.Any(part => part.Name == $"{kind} {number}"))
            number++;
        return $"{kind} {number}";
    }

    private void Add(Part part)
    {
        EndBuildFeature();
        parts.Add(part);
        MarkDirty();
        RefreshParts(parts.Count - 1);
        SetStatus($"Added {part.Name}.");
    }

    private void CreateSketch(object sender, RoutedEventArgs e)
    {
        EndBuildFeature();
        EndSurfaceSelection();
        if (selectedFace is { } face && ReferenceEquals(Selected, face.Surface.Part))
        {
            StartFaceSketch(face.Surface, face.Origin);
            return;
        }
        choosingSketchSurface = true;
        ShowSurfaceSelection();
        SetStatus("Select an origin plane or a flat face. Hold Alt to pick a face behind an origin plane.");
    }

    private void ShowSurfaceSelection()
    {
        SketchSelectionPanel.Visibility = Visibility.Visible;
        OriginPlaneTools.Visibility = choosingSketchSurface ? Visibility.Visible : Visibility.Collapsed;
        CancelSurfaceButton.Content = choosingSketchSurface ? "Cancel Sketch" : choosingHoleFace ? "Cancel Hole" :
            choosingThreadFace ? "Cancel Thread" : choosingMovePart ? "Cancel Move" : choosingBevelPart ? "Cancel Bevel" : "Cancel Extrude";
        BedBadge.Visibility = Visibility.Collapsed;
        UpdatePartsPane();
        UpdatePartPopup();
        DrawScene();
    }

    private void ChooseStandardPlane(object sender, RoutedEventArgs e) =>
        StartPlaneSketch(Enum.Parse<SketchPlane>((string)((Button)sender).Tag));

    private void StartPlaneSketch(SketchPlane plane)
    {
        OpenSketch(plane, null, (profiles, paths, _, dimensions, constraints) =>
        {
            var sketch = new Part(PartKind.Sketch, NextPartName(PartKind.Sketch), 0, 0, 0, 0, 0, 0, profiles.FirstOrDefault(), plane,
                AdditionalProfiles: profiles.Length > 1 ? profiles[1..] : null, OpenPaths: paths.Length > 0 ? paths : null, Dimensions: dimensions, Constraints: constraints);
            AddFinishedSketch(sketch with { Z = -sketch.LocalBounds().Z });
        });
    }

    private void StartFaceSketch(SketchSurface surface, Point3D origin)
    {
        if (!Part.ValidOffset(origin.X) || !Part.ValidOffset(origin.Y) || !Part.ValidOffset(origin.Z))
        {
            SetStatus("Choose a face location within ±10,000 mm of the build-plate origin.");
            return;
        }
        OpenSketch(SketchPlane.XY, null, (profiles, paths, _, dimensions, constraints) =>
        {
            AddFinishedSketch(new Part(PartKind.Sketch, NextPartName(PartKind.Sketch), 0, 0,
                0, origin.X, origin.Y, origin.Z, profiles.FirstOrDefault(), FaceFrame: surface.Frame,
                AdditionalProfiles: profiles.Length > 1 ? profiles[1..] : null, OpenPaths: paths.Length > 0 ? paths : null, Dimensions: dimensions, Constraints: constraints));
        }, surface.Frame, surface.Guide(origin), origin);
    }

    private void AddFinishedSketch(Part sketch)
    {
        Add(sketch with { SketchId = Guid.NewGuid() });
        SetStatus($"{sketch.Name} is ready. Select Extrude to create a solid.");
    }

    private void CancelSurfaceSelection(object sender, RoutedEventArgs e)
    {
        string message = choosingBevelPart ? "Cancelled bevel." :
            choosingThreadFace ? "Cancelled threading." : choosingMovePart ? "Cancelled move." :
            choosingHoleFace ? "Cancelled hole creation." :
            choosingExtrudeFace ? "Cancelled extrusion." : "Cancelled sketch creation.";
        EndSurfaceSelection();
        SetStatus(message);
    }

    private void EndSurfaceSelection()
    {
        EndMeasure();
        if (!SelectingSurface)
            return;
        choosingSketchSurface = false;
        choosingExtrudeFace = false;
        choosingHoleFace = false;
        choosingThreadFace = false;
        choosingMovePart = false;
        choosingBevelPart = false;
        UpdateHistoryButtons();
        SketchSelectionPanel.Visibility = Visibility.Collapsed;
        BedBadge.Visibility = Visibility.Visible;
        UpdatePartsPane();
        UpdatePartPopup();
        DrawScene();
    }

    private void HandleWorkspaceKey(object sender, KeyEventArgs e)
    {
        if (SketchHost.Content is SketchEditor sketchEditor)
        {
            sketchEditor.TryHandleHistoryKey(e);
            return;
        }
        if (SketchHost.Content is not null)
            return;
        if (Keyboard.Modifiers == ModifierKeys.None && e.OriginalSource is not TextBoxBase &&
            e.Key is Key.F6 or Key.D1 or Key.D2 or Key.I)
        {
            switch (e.Key)
            {
                case Key.F6: FitBuildView(); break;
                case Key.D1: WindowModeButton.IsChecked = true; ChangeViewportMouseMode(sender, e); break;
                case Key.D2: LassoModeButton.IsChecked = true; ChangeViewportMouseMode(sender, e); break;
                case Key.I: if (!e.IsRepeat) BeginMeasure(sender, e); break;
            }
            e.Handled = true;
            return;
        }
        if (Keyboard.Modifiers == ModifierKeys.Control && e.Key is Key.E or Key.B)
        {
            if (!e.IsRepeat)
            {
                if (e.Key == Key.E)
                    BeginFaceExtrude(sender, e);
                else
                    BeginBevel(sender, e);
            }
            e.Handled = true;
            return;
        }
        if (LayerClipboard.IsShortcut(e))
        {
            HandleBuildClipboard(e.Key);
            e.Handled = true;
            return;
        }
        if (HistoryShortcut(e, out bool forward, allowText: featureMove || SelectingBevel))
        {
            if (forward)
                RedoProject(sender, e);
            else
                UndoProject(sender, e);
            e.Handled = true;
            return;
        }
        bool editingText = e.OriginalSource is System.Windows.Controls.Primitives.TextBoxBase;
        if (e.Key == Key.Enter && e.OriginalSource is not ButtonBase)
        {
            var pointer = Mouse.GetPosition(ModelViewport);
            if (lassoPoints is not null)
                EndLasso(pointer, ExtendViewportSelection);
            else if (boxSelectionPress)
            {
                if (!CompleteBoxSelection(pointer, ExtendViewportSelection))
                    return;
            }
            else if (SelectingSurface && OrbitSurface.IsMouseOver)
                PickSketchSurface(pointer, commit: true);
            else if (SelectingBevel && featureChange is null && OrbitSurface.IsMouseOver)
                PickBevelTarget(pointer, commit: true);
            else if (featureChange is not null)
                ApplyBuildFeature(sender, e);
            else
                return;
            e.Handled = true;
            return;
        }
        if (e.Key != Key.Escape && (editingText || e.Key is not (Key.Delete or Key.Back)))
            return;
        if (measuring)
        {
            EndMeasure();
            SetStatus("Measure closed.");
            e.Handled = true;
        }
        else if (boxSelectionPress)
        {
            OrbitSurface.ReleaseMouseCapture();
            e.Handled = true;
        }
        else if (lassoPoints is not null)
        {
            CancelLasso();
            e.Handled = true;
        }
        else if (SelectingSurface)
        {
            CancelSurfaceSelection(sender, e);
            e.Handled = true;
        }
        else if (featureSource is not null)
        {
            CancelBuildFeature(sender, e);
            e.Handled = true;
        }
        else if (e.Key == Key.Escape && (lassoSelection.Count > 0 || selectedFaces.Count > 0 || Selected is not null))
        {
            SetLassoSelection([], extend: false);
            e.Handled = true;
        }
        else if (e.Key is Key.Back or Key.Delete && Keyboard.Modifiers == ModifierKeys.None && Selected is not null)
        {
            if (!e.IsRepeat)
            {
                var selection = lassoSelection.ToHashSet<Part>(ReferenceEqualityComparer.Instance);
                selection.Add(Selected);
                DeleteParts(selection);
            }
            e.Handled = true;
        }
    }

    private void EditSketch(object sender, RoutedEventArgs e)
    {
        if (Selected is not { Kind: PartKind.Sketch or PartKind.Extrusion } part)
            return;

        var sketch = part.Kind == PartKind.Extrusion && part.SketchId is Guid id
            ? parts.FirstOrDefault(item => item.Kind == PartKind.Sketch && item.SketchId == id) ?? part : part;
        int index = SelectedPartIndex;
        OpenSketch(sketch.Plane, sketch, (profiles, paths, originalIndices, dimensions, constraints) =>
        {
            var previous = sketch.SketchProfiles.ToArray();
            if (profiles.Length == previous.Length && originalIndices.SequenceEqual(Enumerable.Range(0, previous.Length)) &&
                profiles.Zip(previous).All(pair => pair.First.SequenceEqual(pair.Second)) &&
                paths.Length == (sketch.OpenPaths?.Length ?? 0) &&
                paths.Zip(sketch.OpenPaths ?? []).All(pair => pair.First.SequenceEqual(pair.Second)) &&
                (dimensions ?? []).SequenceEqual(sketch.Dimensions ?? []) && SameConstraints(constraints, sketch.Constraints))
                return;
            if (profiles.Length == 0 && paths.Length == 0)
            {
                PartList.SelectedItem = sketch;
                DeletePart(this, new RoutedEventArgs());
                return;
            }

            if (sketch.Kind == PartKind.Extrusion && (profiles.Length > 1 || paths.Length > 0))
            {
                var link = Guid.NewGuid();
                var solid = sketch with { Profile = profiles[0], SketchId = link, Dimensions = null, Constraints = null };
                if (!solid.IsValid())
                {
                    SetStatus("The profile change conflicts with an existing bevel, hole, or thread.");
                    return;
                }
                parts[index] = solid;
                parts.Add(new Part(PartKind.Sketch, NextPartName(PartKind.Sketch), 0, 0, 0,
                    sketch.X, sketch.Y, sketch.Z, profiles[0], sketch.Plane, sketch.FaceFrame,
                    SketchId: link, AdditionalProfiles: profiles.Length > 1 ? profiles[1..] : null,
                    OpenPaths: paths.Length > 0 ? paths : null, Dimensions: dimensions, Constraints: constraints));
                MarkDirty();
                RefreshParts(index);
                SetStatus($"Updated the sketch for {part.Name}.");
                return;
            }

            var updates = parts.Select((item, itemIndex) => (Item: item, Index: itemIndex))
                .Where(entry => ReferenceEquals(entry.Item, sketch) ||
                    sketch.SketchId is Guid link && entry.Item.SketchId == link)
                .Select(entry => (Updated: UpdateProfile(entry.Item), entry.Index)).ToArray();
            if (updates.Any(entry => !entry.Updated.IsValid()))
            {
                SetStatus("The profile change conflicts with an existing bevel, hole, or thread.");
                return;
            }
            foreach (var (updated, itemIndex) in updates)
                parts[itemIndex] = updated;
            MarkDirty();
            RefreshParts(index);
            SetStatus($"Updated the sketch for {part.Name}.");

            Part UpdateProfile(Part item)
            {
                if (item.Kind == PartKind.Sketch)
                    return item with { Profile = profiles.FirstOrDefault(), AdditionalProfiles = profiles.Length > 1 ? profiles[1..] : null,
                        OpenPaths = paths.Length > 0 ? paths : null, Dimensions = dimensions, Constraints = constraints };
                int profileIndex = Array.IndexOf(originalIndices, item.SketchProfileIndex);
                return profileIndex < 0
                    ? item with { SketchId = null, SketchProfileIndex = 0 }
                    : item with { Profile = profiles[profileIndex], SketchProfileIndex = profileIndex, Dimensions = item.SketchId is null ? dimensions : null, Constraints = item.SketchId is null ? constraints : null };
            }
        }, sketch.FaceFrame, origin: new Point3D(sketch.X, sketch.Y, sketch.Z),
            initialProfile: part.Kind == PartKind.Extrusion ? part.SketchProfileIndex : SelectedSketchProfile(sketch));
    }

    private static bool SameConstraints(ProfileConstraint[]?[]? first, ProfileConstraint[]?[]? second) =>
        (first?.Length ?? 0) == (second?.Length ?? 0) &&
        (first ?? []).Zip(second ?? []).All(pair => (pair.First ?? []).SequenceEqual(pair.Second ?? []));

    private void OpenSketch(SketchPlane plane, Part? part, Action<SketchPoint[][], SketchPoint[][], int[], ProfileDimensions?[]?, ProfileConstraint[]?[]?> commit,
                            SketchFrame? faceFrame = null, (SketchPoint Start, SketchPoint End)[]? guide = null,
                            Point3D origin = default, int initialProfile = 0)
    {
        EndBuildFeature();
        EndSurfaceSelection();
        var editor = new SketchEditor(plane, units, part, faceFrame is not null, guide);
        editor.ActivateProfile(initialProfile);
        var references = new Model3DGroup();
        references.Children.Add(new AmbientLight(Color.FromRgb(118, 126, 140)));
        references.Children.Add(new DirectionalLight(Colors.White, new Vector3D(-0.6, -0.8, -1)));
        references.Children.Add(SketchBuildPlate(out var plateCorners));
        foreach (var other in parts.Where(item => !item.Hidden && item.Kind != PartKind.Sketch))
            references.Children.Add(MakeModel(GetRenderedPart(other).Mesh, Color.FromRgb(92, 105, 125)));
        references.Freeze();
        var frame = faceFrame ?? new SketchFrame(SketchAxis.FromVector((Vector3D)plane.ToModel(1, 0, 0)),
            SketchAxis.FromVector((Vector3D)plane.ToModel(0, 1, 0)));
        editor.SetReferences(references, origin, frame, plateCorners, Math.Max(bed.X, bed.Y), distance);
        editor.Cancelled += CloseSketch;
        editor.Finished += (profile, paths) =>
        {
            CloseSketch();
            commit(profile, paths, editor.OriginalProfileIndices, editor.FinishedDimensions, editor.FinishedConstraints);
        };
        ShowSketchContent(editor);
    }

    private Model3DGroup SketchBuildPlate(out Point3D[] corners)
    {
        double halfX = bed.X / 2, halfY = bed.Y / 2;
        double thickness = Math.Min(bed.X, bed.Y) * 0.006;
        double top = -thickness * 0.2;
        corners = [new(-halfX, -halfY, top), new(halfX, -halfY, top), new(halfX, halfY, top), new(-halfX, halfY, top)];
        var slab = new Part(PartKind.Box, "Build plate", bed.X, bed.Y, thickness, 0, 0, top - thickness);
        var plate = new Model3DGroup();
        plate.Children.Add(MakeModel(Geometry.Build(slab), Color.FromArgb(110, 59, 77, 101), bothSides: true));
        double line = thickness * 0.55;
        double z = top + thickness * 0.025;
        plate.Children.Add(MakeModel([
            ..Rectangle(-halfX, -halfY, halfX, -halfY + line, z),
            ..Rectangle(-halfX, halfY - line, halfX, halfY, z),
            ..Rectangle(-halfX, -halfY, -halfX + line, halfY, z),
            ..Rectangle(halfX - line, -halfY, halfX, halfY, z)],
            Color.FromRgb(103, 125, 150), bothSides: true));
        plate.Children.Add(MakeModel(Rectangle(-halfX, -line / 3, halfX, line / 3, z),
            Color.FromRgb(133, 71, 79), bothSides: true));
        plate.Children.Add(MakeModel(Rectangle(-line / 3, -halfY, line / 3, halfY, z),
            Color.FromRgb(64, 126, 96), bothSides: true));
        return plate;
    }

    private void ShowSketchContent(UserControl content)
    {
        Workspace.Visibility = Visibility.Collapsed;
        SketchHost.Content = content;
        SketchHost.Visibility = Visibility.Visible;
        content.MoveFocus(new TraversalRequest(FocusNavigationDirection.First));
    }

    private void CloseSketch()
    {
        SketchHost.Visibility = Visibility.Collapsed;
        SketchHost.Content = null;
        Workspace.Visibility = Visibility.Visible;
    }

    private void DeletePart(object sender, RoutedEventArgs e)
    {
        EndBuildFeature();
        if (Selected is { } part)
            DeleteParts(new HashSet<Part>(ReferenceEqualityComparer.Instance) { part });
    }

    private void DeleteParts(HashSet<Part> selection)
    {
        int index = SelectedPartIndex;
        var deleted = parts.Where(selection.Contains).ToArray();
        if (deleted.Length == 0)
            return;
        var deletedSketches = deleted.Where(part => part.Kind == PartKind.Sketch)
            .Select(part => part.SketchId).OfType<Guid>().ToHashSet();
        parts.RemoveAll(selection.Contains);
        if (deletedSketches.Count > 0)
        {
            for (int i = 0; i < parts.Count; i++)
                if (parts[i].SketchId is Guid id && deletedSketches.Contains(id))
                    parts[i] = parts[i] with { SketchId = null, SketchProfileIndex = 0 };
        }
        MarkDirty();
        RefreshParts(Math.Min(index, parts.Count - 1));
        SetStatus(deleted.Length == 1 ? $"Deleted {deleted[0].Name}." : $"Deleted {deleted.Length} items.");
    }

    private void OpenPartMenu(object sender, RoutedEventArgs e)
    {
        if (sender is ContextMenu { PlacementTarget: FrameworkElement { DataContext: Part part } })
            PartList.SelectedItem = part;
    }

    private void MovePartFromMenu(object sender, RoutedEventArgs e)
    {
        if (Selected is not { } part)
            return;
        if (part.Kind == PartKind.Sketch)
        {
            EditSketch(sender, e);
            if (SketchHost.Content is SketchEditor editor)
                editor.BeginSketchMove(sender, e);
        }
        else
        {
            EndBuildFeature();
            OpenBuildFeature(part, null, 0, moving: true);
        }
    }

    private void TogglePartVisibility(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: Part part })
            return;
        int index = parts.IndexOf(part);
        if (index < 0)
            return;

        if (ReferenceEquals(part, featureSource))
            EndBuildFeature();
        int selection = SelectedPartIndex;
        var updated = part with { Hidden = !part.Hidden };
        parts[index] = updated;
        MarkDirty();
        RefreshParts(selection);
        SetStatus(updated.Hidden ? $"Hid {updated.Name}." : $"Showed {updated.Name}.");
    }

    private void TogglePartsSection(object sender, RoutedEventArgs e)
    {
        partsPaneCollapsed = !partsPaneCollapsed;
        UpdatePartsPane();
        (partsPaneCollapsed ? ShowPartsButton : PartsSectionButton).Focus();
    }

    private void UpdatePartsPane()
    {
        PartsPane.Visibility = partsPaneCollapsed ? Visibility.Collapsed : Visibility.Visible;
        ShowPartsButton.Visibility = partsPaneCollapsed ? Visibility.Visible : Visibility.Collapsed;
        PartsColumn.Width = new GridLength(partsPaneCollapsed ? 0 : SelectingSurface ? 176 : 232);
    }

    private void RefreshParts(int index)
    {
        sketchProfileSelection = null;
        var visible = parts.Where(part => !part.Hidden).ToHashSet<Part>(ReferenceEqualityComparer.Instance);
        lassoSelection.IntersectWith(visible);
        foreach (var sketch in regionSketchSelections.Keys.Where(sketch => !visible.Contains(sketch)).ToArray())
            regionSketchSelections.Remove(sketch);
        selectedEdges.RemoveWhere(item => !visible.Contains(item.Part));
        selectedCorners.RemoveWhere(item => !visible.Contains(item.Part));
        if (selectedFaces.RemoveAll(item => !parts.Any(part => ReferenceEquals(part, item.Surface.Part) && !part.Hidden)) > 0)
            RebuildFaceHighlights();
        updatingSelection = true;
        partPopupDismissed = false;
        var view = new ListCollectionView(parts.ToArray());
        var groups = new PropertyGroupDescription(nameof(Part.BrowserGroup));
        groups.GroupNames.Add("Objects");
        groups.GroupNames.Add("Sketches");
        view.GroupDescriptions.Add(groups);
        PartList.ItemsSource = view;
        PartList.SelectedItem = index >= 0 ? parts[index] : null;
        updatingSelection = false;
        SetPartFields();
        DrawScene();
    }

    private void RestorePartGroup(object sender, RoutedEventArgs e)
    {
        if (sender is Expander { DataContext: CollectionViewGroup { Name: string group } } expander)
            expander.IsExpanded = !collapsedPartGroups.Contains(group);
    }

    private void RememberPartGroup(object sender, RoutedEventArgs e)
    {
        if (sender is not Expander { IsLoaded: true, DataContext: CollectionViewGroup { Name: string group } } expander)
            return;
        if (expander.IsExpanded)
            collapsedPartGroups.Remove(group);
        else
            collapsedPartGroups.Add(group);
    }

    private void OpenPartSketch(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is DependencyObject source &&
            ItemsControl.ContainerFromElement(PartList, source) is ListBoxItem { DataContext: Part { Kind: PartKind.Sketch } sketch } row &&
            e.GetPosition(row).X < row.ActualWidth - 30)
        {
            PartList.SelectedItem = sketch;
            EditSketch(sender, e);
            e.Handled = true;
        }
    }

    private void OpenPartSketchFromKeyboard(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && e.OriginalSource is not Button && Selected is { Kind: PartKind.Sketch })
        {
            EditSketch(sender, e);
            e.Handled = true;
        }
    }

    private void SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (updatingSelection)
            return;
        if (sketchProfileSelection is { } profile && !ReferenceEquals(Selected, profile.Sketch))
            sketchProfileSelection = null;
        if (!changingViewportSelection && (Selected is not { } selected || !lassoSelection.Contains(selected)))
            lassoSelection.Clear();
        if (!changingViewportSelection)
        {
            ClearSolidDetail();
            regionSketchSelections.Clear();
        }
        if (featureSource is not null && !ReferenceEquals(featureSource, Selected))
            EndBuildFeature();
        partPopupDismissed = false;
        SetPartFields();
        DrawScene();
        SetStatus(Selected is { } part ? $"Selected {part.Name}." : "Select a part to edit or export it.");
    }

    private void SetPartFields()
    {
        var part = Selected;
        if (part is null)
        {
            UpdatePartPopup();
            return;
        }

        WidthLabel.Content = part.Kind switch
        {
            PartKind.Box => "_Width",
            PartKind.Cylinder => "_Diameter",
            _ => "_Outer Ø"
        };
        DepthLabel.Content = part.Kind == PartKind.Tube ? "_Inner Ø" : "_Depth";
        bool imported = part.Kind == PartKind.Mesh;
        bool sketch = part.Kind == PartKind.Sketch;
        DimensionsLabel.Visibility = imported || sketch ? Visibility.Collapsed : Visibility.Visible;
        DimensionsLabel.Text = part.Kind == PartKind.Extrusion
            ? $"Extrusion ({units.Symbol()})" : $"Dimensions ({units.Symbol()})";
        PartDimensions.Visibility = imported || sketch ? Visibility.Collapsed : Visibility.Visible;
        PrimaryDimension.Visibility = part.Kind is PartKind.Extrusion or PartKind.Mesh or PartKind.Sketch ? Visibility.Collapsed : Visibility.Visible;
        HeightField.Visibility = imported || sketch ? Visibility.Collapsed : Visibility.Visible;
        MeshSummary.Visibility = imported ? Visibility.Visible : Visibility.Collapsed;
        if (imported)
        {
            var size = part.LocalBounds().Size;
            MeshSummary.Text = $"STL mesh · {part.Mesh!.Triangles.Length:N0} triangles\n{FormatSize(size.X, size.Y, size.Z)}";
        }
        SecondaryDimension.Visibility = part.Kind is PartKind.Box or PartKind.Tube ? Visibility.Visible : Visibility.Collapsed;
        EditSketchButton.Visibility = part.Kind is PartKind.Sketch or PartKind.Extrusion ? Visibility.Visible : Visibility.Collapsed;
        EditSketchButton.Content = part.FaceFrame is null ? $"Edit {part.Plane} sketch…" : "Edit face sketch…";
        HeightLabel.Content = part.Kind == PartKind.Extrusion ? "_Depth" : "_Height";
        PositionLabel.Text = part.Kind is PartKind.Sketch or PartKind.Extrusion
            ? $"Sketch origin from bed center ({units.Symbol()})"
            : $"Position from bed center ({units.Symbol()})";
        PartName.Text = part.Name;
        PartWidth.Text = units.Format(part.Width);
        PartDepth.Text = units.Format(part.Depth);
        PartHeight.Text = units.Format(part.Height);
        PartX.Text = units.Format(part.X);
        PartY.Text = units.Format(part.Y);
        PartZ.Text = units.Format(part.Z);
        UpdatePartPopup();
    }

    private void DismissPartPopup(object sender, RoutedEventArgs e)
    {
        partPopupDismissed = true;
        UpdatePartPopup();
    }

    private void TogglePartRow(object sender, MouseButtonEventArgs e)
    {
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control) && e.OriginalSource is DependencyObject source &&
            ItemsControl.ContainerFromElement(PartList, source) is ListBoxItem { DataContext: Part { Hidden: false } part } row &&
            e.GetPosition(row).X < row.ActualWidth - 30)
        {
            TogglePartSelection(part);
            e.Handled = true;
        }
    }

    private void ReopenPartPopup(object sender, MouseButtonEventArgs e)
    {
        if (!Keyboard.Modifiers.HasFlag(ModifierKeys.Control) && e.OriginalSource is DependencyObject source &&
            ItemsControl.ContainerFromElement(PartList, source) is ListBoxItem { IsSelected: true } row &&
            e.GetPosition(row).X < row.ActualWidth - 30)
        {
            bool redraw = lassoSelection.Count > 0;
            sketchProfileSelection = null;
            lassoSelection.Clear();
            ClearSolidDetail();
            partPopupDismissed = false;
            UpdatePartPopup();
            UpdateSketchProfileHighlight();
            if (redraw)
                DrawScene();
        }
    }

    private void UpdatePartPopup()
    {
        PartPopup.Visibility = Selected is { Hidden: false } && !CanLasso && lassoSelection.Count <= 1 &&
            !partPopupDismissed && !SelectingSurface && featureSource is null
            ? Visibility.Visible : Visibility.Collapsed;
        PositionPartPopup();
    }

    private void ViewportResized(object sender, SizeChangedEventArgs e)
    {
        PositionPartPopup();
        UpdateOriginPlaneOverlay();
        UpdateFeatureArrow();
        UpdateBevelMarkers();
        UpdateSolidDetailMarkers();
        UpdateMeasureOverlay();
        UpdateMoveGizmo();
    }

    private void PositionPartPopup()
    {
        if (PartPopup.Visibility != Visibility.Visible || Selected is not { } part)
            return;
        double width = ModelViewport.ActualWidth, height = ModelViewport.ActualHeight;
        const double margin = 10;
        if (width <= 2 * margin || height <= 2 * margin)
            return;

        PartPopup.MaxHeight = height - 2 * margin;
        PartPopup.Measure(new Size(width - 2 * margin, height - 2 * margin));
        var size = PartPopup.DesiredSize;
        var projected = ProjectPartBounds(part, width, height);
        double left = projected.Left - size.Width - margin;
        double right = projected.Right + margin;
        double y = Math.Clamp(projected.Top + (projected.Height - size.Height) / 2,
            margin, Math.Max(margin, height - size.Height - margin));
        double x = right + size.Width <= width - margin ||
            left < margin && width - projected.Right >= projected.Left
            ? right : left;
        x = Math.Clamp(x, margin, Math.Max(margin, width - size.Width - margin));
        if (x < width && x + size.Width > width - 108 && y < 136)
            y = 136;
        PartPopupPosition.X = PartsColumn.ActualWidth + x;
        PartPopupPosition.Y = Math.Clamp(y, margin, Math.Max(margin, height - size.Height - margin));
    }

    private Rect ProjectPartBounds(Part part, double width, double height)
    {
        var projected = Rect.Empty;
        foreach (var corner in BoundsCorners(PartWorldBounds(part)))
        {
            if (ProjectViewportPoint(corner) is Point screen)
                projected.Union(new Point(Math.Clamp(screen.X, 0, width), Math.Clamp(screen.Y, 0, height)));
        }
        return projected.IsEmpty ? new Rect(new Point(width / 2, height / 2), new Size()) : projected;
    }

    private static Rect3D PartWorldBounds(Part part)
    {
        var bounds = part.LocalBounds();
        return new Rect3D(bounds.X + part.X, bounds.Y + part.Y, bounds.Z + part.Z,
            bounds.SizeX, bounds.SizeY, bounds.SizeZ);
    }

    private static IEnumerable<Point3D> BoundsCorners(Rect3D bounds)
    {
        for (int corner = 0; corner < 8; corner++)
        {
            yield return new Point3D(bounds.X + ((corner & 1) == 0 ? 0 : bounds.SizeX),
                                     bounds.Y + ((corner & 2) == 0 ? 0 : bounds.SizeY),
                                     bounds.Z + ((corner & 4) == 0 ? 0 : bounds.SizeZ));
        }
    }

    private void ApplyPart(object sender, RoutedEventArgs e) => UpdatePart(drop: false);

    private void DropPart(object sender, RoutedEventArgs e) => UpdatePart(drop: true);

    private void UpdatePart(bool drop)
    {
        int index = SelectedPartIndex;
        if (Selected is not { } part)
            return;

        var updated = part with
        {
            Name = PartName.Text.Trim(),
            Width = part.Kind is PartKind.Extrusion or PartKind.Mesh or PartKind.Sketch ? part.Width : units.Read(PartWidth.Text, part.Width),
            Depth = part.Kind is PartKind.Box or PartKind.Tube ? units.Read(PartDepth.Text, part.Depth) : part.Depth,
            Height = part.Kind is PartKind.Mesh or PartKind.Sketch ? part.Height : units.Read(PartHeight.Text, part.Height),
            X = units.Read(PartX.Text, part.X),
            Y = units.Read(PartY.Text, part.Y),
            Z = drop ? -part.LocalBounds().Z : units.Read(PartZ.Text, part.Z)
        };

        if (!updated.IsValid())
        {
            string symbol = units.Symbol();
            string range = $"±{units.FormatLabel(10000)} {symbol}";
            SetStatus(part.Kind == PartKind.Mesh
                ? $"Enter a name and positions within {range}."
                : $"Enter a name, dimensions from {units.Format(0.01)} to {units.FormatLabel(10000)} {symbol}, and positions within {range}. Tubes require a wall at least {units.Format(0.01)} {symbol} thick.");
            return;
        }

        if (updated != part)
        {
            if (part is { Kind: PartKind.Sketch, SketchId: Guid link })
            {
                var delta = new Vector3D(updated.X - part.X, updated.Y - part.Y, updated.Z - part.Z);
                if (delta.LengthSquared > 0)
                {
                    var followers = parts.Select((item, itemIndex) => (Item: item, Index: itemIndex))
                        .Where(entry => entry.Item.Kind == PartKind.Extrusion && entry.Item.SketchId == link)
                        .Select(entry => (Moved: entry.Item with
                        {
                            X = entry.Item.X + delta.X, Y = entry.Item.Y + delta.Y, Z = entry.Item.Z + delta.Z
                        }, entry.Index)).ToArray();
                    if (followers.Any(entry => !entry.Moved.IsValid()))
                    {
                        SetStatus("The move would put a linked object beyond the supported position range.");
                        return;
                    }
                    foreach (var (moved, followerIndex) in followers)
                        parts[followerIndex] = moved;
                }
            }
            parts[index] = updated;
            MarkDirty();
        }
        RefreshParts(index);
        SetStatus(drop ? $"Placed {updated.Name} on the bed." : $"Updated {updated.Name}.");
    }

    private string FormatSize(double x, double y, double z) =>
        $"{units.FormatLabel(x)} × {units.FormatLabel(y)} × {units.FormatLabel(z)} {units.Symbol()}";

    private void UpdateBedLabel() => BedLabel.Text = $"{FormatSize(bed.X, bed.Y, bed.Z)} build volume";

    private void OpenSettings(object sender, RoutedEventArgs e)
    {
        EndBuildFeature();
        var dialog = new SettingsWindow(bed, units) { Owner = this };
        if (dialog.ShowDialog() == true)
            ApplySettings(dialog.BuildVolume, dialog.Units);
    }

    private void ApplySettings(Bed volume, DisplayUnits displayUnits)
    {
        bool unitsChanged = units != displayUnits;
        units = displayUnits;
        if (bed != volume)
        {
            bed = volume;
            MarkDirty();
            ResetCamera();
            DrawScene();
        }
        UpdateBedLabel();
        if (unitsChanged)
        {
            SetPartFields();
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(preferencesPath)!);
                WriteFile(preferencesPath, stream =>
                {
                    using var writer = new StreamWriter(stream, leaveOpen: true);
                    writer.Write(units);
                });
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                SetStatus("Settings applied for this session. The display preference could not be saved.");
                return;
            }
        }
        SetStatus("Updated settings.");
    }

    private static DisplayUnits LoadUnits(string path)
    {
        try
        {
            if (File.Exists(path) && Enum.TryParse<DisplayUnits>(File.ReadAllText(path), out var stored) &&
                Enum.IsDefined(stored))
                return stored;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return DisplayUnits.Millimeters;
        }
        return DisplayUnits.Millimeters;
    }

    private void DrawScene()
    {
        pickableParts.Clear();
        previewTranslations.Clear();
        ResetSurfaceHover();
        var models = new Model3DGroup();
        models.Children.Add(new AmbientLight(Color.FromRgb(90, 90, 90)));
        models.Children.Add(new DirectionalLight(Color.FromRgb(163, 163, 163), new Vector3D(-1, 1, -2)));
        models.Children.Add(new DirectionalLight(Color.FromRgb(65, 75, 90), new Vector3D(1, -1, -0.5)));

        double bedSize = Math.Max(bed.X, bed.Y);
        double scale = Math.Min(bed.X, bed.Y);
        models.Children.Add(BuildPlate());
        double axis = scale * 0.0013;

        foreach (Part part in parts)
        {
            if (part.Hidden)
                continue;
            bool selected = ReferenceEquals(part, Selected) || lassoSelection.Contains(part);
            bool showingChange = featureChange is not null && ReferenceEquals(part, featureSource);
            Part displayed = showingChange && (SelectingBevel || featureChange!.Removes && featureChange.CutParts is null)
                ? featureChange!.Result : part;
            if (featureChange?.CutParts?.TryGetValue(part, out var cut) == true)
            {
                if (cut is null)
                    continue;
                displayed = cut;
            }
            if (featureChange?.MergedParts is { } merged && merged.Any(item => ReferenceEquals(item, part)))
            {
                if (!ReferenceEquals(merged[0], part))
                    continue;
                displayed = featureChange.Result;
            }
            if (featureChange?.MovedParts?.TryGetValue(part, out var moved) == true)
                displayed = moved;
            bool sketch = displayed.Kind == PartKind.Sketch;
            bool wholeSketchSelected = selected && !regionSketchSelections.ContainsKey(part) &&
                (lassoSelection.Contains(part) || sketchProfileSelection is null || part.AdditionalProfiles is null);
            Color color = sketch ? wholeSketchSelected
                ? Color.FromArgb(168, 255, 173, 92) : Color.FromArgb(110, 71, 218, 184) : displayed.Fits(bed)
                ? selected ? Color.FromRgb(250, 251, 253) : Color.FromRgb(228, 231, 237)
                : Color.FromRgb(252, 212, 187);
            if (selected && !sketch && !SelectingSurface)
            {
                // Blend the highlight into the surface color so the solid keeps its shading and depth.
                color = Color.FromRgb(Tint(color.R, 255), Tint(color.G, 144), Tint(color.B, 42));
            }
            var rendered = GetRenderedPart(featureMove ? part : displayed);
            if (sketch)
            {
                double lift = Math.Clamp(bedSize * 0.00004, 0.00001, 0.02);
                var cache = sketchRenderCache.GetOrCreateValue(featureMove ? part : displayed);
                if (cache.Rendered is null || cache.Lift != lift)
                {
                    var normal = displayed.SketchVertex(default, 1) - displayed.SketchVertex(default, 0);
                    var lifted = rendered.Triangles.Select(triangle => triangle.Translate(normal * lift)).ToList();
                    cache.Rendered = new RenderedPart(lifted, MakeMesh(lifted));
                    cache.Lift = lift;
                }
                rendered = cache.Rendered;
            }
            var translation = featureMove ? new Vector3D(displayed.X - part.X, displayed.Y - part.Y, displayed.Z - part.Z) : default;
            GeometryModel3D model;
            if (translation.LengthSquared > 0)
            {
                model = MakeModel(rendered.Mesh, color, bothSides: sketch, translation);
                previewTranslations.Add(model, translation);
            }
            else
            {
                var cache = partModelCache.GetOrCreateValue(rendered);
                if (cache.Model is null || cache.Color != color)
                {
                    cache.Model = MakeModel(rendered.Mesh, color, bothSides: sketch);
                    cache.Color = color;
                }
                model = cache.Model;
            }
            pickableParts.Add(model, (part, rendered.Triangles));
            models.Children.Add(model);
        }
        if (choosingSketchSurface)
        {
            double axisTop = Math.Min(scale * 0.16, bed.Z);
            var a = new Point3D(-axis, 0, 0);
            var b = new Point3D(axis, 0, 0);
            var c = new Point3D(axis, 0, axisTop);
            var d = new Point3D(-axis, 0, axisTop);
            var e = new Point3D(0, -axis, 0);
            var f = new Point3D(0, axis, 0);
            var g = new Point3D(0, axis, axisTop);
            var h = new Point3D(0, -axis, axisTop);
            models.Children.Add(MakeModel([new(a, b, c), new(a, c, d), new(e, f, g), new(e, g, h)],
                Color.FromRgb(102, 152, 230), bothSides: true));
        }
        double marker = scale * 0.0065;
        double markerZ = Math.Min(bed.X, bed.Y) * 0.001;
        var center = new Point3D(0, 0, markerZ);
        var rim = Enumerable.Range(0, 16).Select(i =>
        {
            double angle = i * Math.Tau / 16;
            return new Point3D(Math.Cos(angle) * marker, Math.Sin(angle) * marker, markerZ);
        }).ToArray();
        models.Children.Add(MakeModel(rim.Select((point, i) => new Triangle(center, point, rim[(i + 1) % rim.Length])),
            Color.FromRgb(160, 214, 255)));
        Scene.Content = models;
        sceneBounds = models.Bounds;
        RebuildScenePickIndex();
        DrawFeaturePreview();
        FitBuildCameraDepth();
        UpdateOriginPlaneOverlay();
        UpdateSolidDetailMarkers();
        UpdateSketchProfileHighlight();

        static byte Tint(byte surface, byte highlight) => (byte)((surface * 81 + highlight * 19) / 100);
    }

    private Model3DGroup BuildPlate()
    {
        double width = bed.X, depth = bed.Y;
        if (buildPlate is { } cached && cached.Width == width && cached.Depth == depth)
            return cached.Model;
        double scale = Math.Min(width, depth);
        double top = -scale * 0.001;
        double thickness = scale * 0.006;
        var slab = new Part(PartKind.Box, "Build plate", width, depth, thickness, 0, 0, top - thickness);
        var plate = new Model3DGroup();
        // Omit the slab's top triangles so the textured face has exclusive depth.
        var shell = MakeMesh(Geometry.Build(slab).Where(triangle => BevelFacetNormal(triangle).Z < 0.5));
        var shellMaterial = new MaterialGroup
        {
            Children =
            {
                new DiffuseMaterial(new SolidColorBrush(Color.FromRgb(30, 39, 53))),
                new EmissiveMaterial(new SolidColorBrush(Color.FromRgb(22, 30, 41)))
            }
        };
        plate.Children.Add(new GeometryModel3D(shell, shellMaterial));

        var texture = new DrawingGroup();
        var bounds = new Rect(0, 0, width, depth);
        using (var drawing = texture.Open())
        {
            drawing.PushClip(new RectangleGeometry(bounds));
            drawing.DrawRectangle(new SolidColorBrush(Color.FromRgb(25, 32, 44)), null, bounds);
            var minor = new Pen(new SolidColorBrush(Color.FromRgb(35, 44, 57)), scale * 0.0017);
            var major = new Pen(new SolidColorBrush(Color.FromRgb(49, 60, 76)), scale * 0.0024);
            const int divisions = 50;
            for (int i = 0; i <= divisions; i++)
            {
                if (i == divisions / 2)
                    continue;
                var pen = i % 5 == 0 ? major : minor;
                double x = width * i / divisions, y = depth * i / divisions;
                drawing.DrawLine(pen, new Point(x, 0), new Point(x, depth));
                drawing.DrawLine(pen, new Point(0, y), new Point(width, y));
            }
            double axisWidth = scale * 0.0026;
            drawing.DrawLine(new Pen(new SolidColorBrush(Color.FromRgb(161, 72, 76)), axisWidth),
                new Point(0, depth / 2), new Point(width, depth / 2));
            drawing.DrawLine(new Pen(new SolidColorBrush(Color.FromRgb(83, 153, 105)), axisWidth),
                new Point(width / 2, 0), new Point(width / 2, depth));
            drawing.Pop();
        }
        var brush = new DrawingBrush(texture);
        RenderOptions.SetCachingHint(brush, CachingHint.Cache);
        var mesh = new MeshGeometry3D
        {
            Positions = new Point3DCollection
            {
                new(-width / 2, -depth / 2, top), new(width / 2, -depth / 2, top),
                new(width / 2, depth / 2, top), new(-width / 2, depth / 2, top)
            },
            TextureCoordinates = new PointCollection { new(0, 1), new(1, 1), new(1, 0), new(0, 0) },
            TriangleIndices = new Int32Collection { 0, 1, 2, 0, 2, 3 }
        };
        plate.Children.Add(new GeometryModel3D(mesh, new DiffuseMaterial(brush)));
        plate.Freeze();
        buildPlate = (width, depth, plate);
        return plate;
    }

    private Rect OriginPlanePickerBounds(SketchPlane plane)
    {
        double span = Math.Min(bed.X, bed.Y) * 0.115;
        double height = plane == SketchPlane.XY ? span : Math.Min(span, bed.Z);
        double width = span * 0.84;
        double depth = height * 0.84;
        // Face the three pick regions into the default isometric view.
        double u = plane == SketchPlane.YZ ? -span : span - width;
        double v = plane == SketchPlane.XY ? -height : height - depth;
        return new Rect(u, v, width, depth);
    }

    private void UpdateOriginPlaneOverlay()
    {
        OriginPlaneOverlay.Data = null;
        HoveredPlaneOverlay.Data = null;
        if (!choosingSketchSurface)
            return;

        var planes = new GeometryGroup { FillRule = FillRule.Nonzero };
        foreach (var plane in Enum.GetValues<SketchPlane>())
        {
            var bounds = OriginPlanePickerBounds(plane);
            if (ProjectViewportPoint(plane.ToModel(bounds.Left, bounds.Top, 0)) is not Point a ||
                ProjectViewportPoint(plane.ToModel(bounds.Right, bounds.Top, 0)) is not Point b ||
                ProjectViewportPoint(plane.ToModel(bounds.Right, bounds.Bottom, 0)) is not Point c ||
                ProjectViewportPoint(plane.ToModel(bounds.Left, bounds.Bottom, 0)) is not Point d)
                continue;
            var outline = new StreamGeometry();
            using (var path = outline.Open())
            {
                path.BeginFigure(a, isFilled: true, isClosed: true);
                path.PolyLineTo([b, c, d], isStroked: true, isSmoothJoin: false);
            }
            outline.Freeze();
            if (plane == hoveredPlane)
                HoveredPlaneOverlay.Data = outline;
            else
                planes.Children.Add(outline);
        }
        planes.Freeze();
        OriginPlaneOverlay.Data = planes;
    }

    private Vector3D? ViewportRay(Point point) => BuildProjection.Ray(point);

    private (SceneHit? Part, SketchPlane? Plane) HitScene(Point point) => HitSceneCore(point, includeSketches: true);

    private (SceneHit? Part, SketchPlane? Plane) HitSceneCore(Point point, bool includeSketches, Part? onlySketch = null)
    {
        if (ViewportRay(point) is not Vector3D ray)
            return default;
        double near = Camera.NearPlaneDistance, far = Camera.FarPlaneDistance;
        var eye = Camera.Position;
        SceneHit? nearestPart = null;
        double distance = far;
        int nearestOrder = int.MaxValue;
        bool allowSketches = includeSketches && !(choosingSketchSurface || choosingHoleFace || choosingThreadFace || choosingMovePart || choosingBevelPart);
        Visit(scenePickRoot);
        SketchPlane? nearestPlane = null;
        if (choosingSketchSurface)
        {
            distance = far;
            foreach (var plane in Enum.GetValues<SketchPlane>())
            {
                var normal = (Vector3D)plane.ToModel(0, 0, 1);
                double approach = Vector3D.DotProduct(ray, normal);
                if (Math.Abs(approach) < 1e-12)
                    continue;
                double depth = -Vector3D.DotProduct((Vector3D)eye, normal) / approach;
                if (depth < near || depth >= distance)
                    continue;
                var pointOnPlane = (Vector3D)(eye + ray * depth);
                double u = Vector3D.DotProduct(pointOnPlane, (Vector3D)plane.ToModel(1, 0, 0));
                double v = Vector3D.DotProduct(pointOnPlane, (Vector3D)plane.ToModel(0, 1, 0));
                if (OriginPlanePickerBounds(plane).Contains(u, v))
                {
                    nearestPlane = plane;
                    distance = depth;
                }
            }
        }
        return (nearestPart, nearestPlane);

        void Visit(ScenePickNode? node)
        {
            if (node is null || (!allowSketches && !node.HasSolids) ||
                !TrianglePicker.Intersects(node.Bounds, eye, ray, near, distance))
                return;
            if (node.Entries is not { } entries)
            {
                Visit(node.Left);
                Visit(node.Right);
                return;
            }
            foreach (var entry in entries)
            {
                if ((!allowSketches && entry.IsSketch) ||
                    (onlySketch is not null && !ReferenceEquals(entry.Part, onlySketch)) ||
                    !TrianglePicker.Intersects(entry.Bounds, eye, ray, near, distance))
                    continue;
                var picker = pickers.GetValue(entry.Triangles, static triangles => new TrianglePicker(triangles));
                double hitDepth = nearestPart is null ? distance : Math.BitIncrement(distance);
                int index = picker.Find(eye - entry.Translation, ray, near, entry.IsSketch, ref hitDepth);
                if (index >= 0 && (hitDepth < distance || entry.Order < nearestOrder))
                {
                    distance = hitDepth;
                    nearestOrder = entry.Order;
                    nearestPart = new SceneHit(entry.Model, eye + ray * distance, index);
                }
            }
        }
    }

    private void PickPart(Point point)
    {
        var hit = HitScene(point).Part ?? SketchBoundaryHit(point);
        if (hit?.ModelHit is GeometryModel3D model)
        {
            var part = pickableParts[model].Part;
            sketchProfileSelection = part.Kind == PartKind.Sketch ? (part, part.SketchProfileRange(hit.TriangleIndex).Index) : null;
            partPopupDismissed = false;
            if (ReferenceEquals(Selected, part))
                UpdatePartPopup();
            else
                PartList.SelectedItem = part;
            UpdateSketchProfileHighlight();
            if (part.AdditionalProfiles is not null)
                SetStatus($"Selected Profile {SelectedSketchProfile(part) + 1} of {part.Name}.");
        }
        else
        {
            sketchProfileSelection = null;
            PartList.SelectedIndex = -1;
        }
    }

    private void OpenViewportPartMenu(object sender, MouseButtonEventArgs e)
    {
        if (!ReferenceEquals(e.OriginalSource, OrbitSurface))
            return;
        e.Handled = ShowViewportPartMenu(e.GetPosition(ModelViewport)) is not null;
    }

    private ContextMenu? ShowViewportPartMenu(Point point)
    {
        if (SelectingSurface || (HitSceneCore(point, includeSketches: false).Part ?? HitScene(point).Part) is not { } hit)
            return null;
        ResetSurfaceHover();
        ClearSolidDetail();
        var part = pickableParts[hit.ModelHit].Part;
        PartList.SelectedItem = part;
        var menu = new ContextMenu { PlacementTarget = OrbitSurface, Placement = System.Windows.Controls.Primitives.PlacementMode.MousePoint };
        var move = new MenuItem { Header = "_Move…" };
        var delete = new MenuItem { Header = "_Delete" };
        if (part.AdditionalProfiles is not null && part.SketchProfileRange(hit.TriangleIndex).Index >= 0)
        {
            int index = part.SketchProfileRange(hit.TriangleIndex).Index;
            sketchProfileSelection = (part, index);
            UpdateSketchProfileHighlight();
            move.Header = "_Move profile…";
            delete.Header = "_Delete profile";
            move.Click += (sender, e) => EditProfileFromViewport(part, index, false, sender, e);
            delete.Click += (sender, e) => EditProfileFromViewport(part, index, true, sender, e);
        }
        else
        {
            move.Click += MovePartFromMenu;
            delete.Click += DeletePart;
        }
        menu.Items.Add(move);
        menu.Items.Add(delete);
        menu.IsOpen = true;
        return menu;
    }

    private static SketchSurface? FindSketchableFace(Part part, List<Triangle> triangles, SceneHit hit)
    {
        var normal = BevelFacetNormal(triangles[hit.TriangleIndex]);
        return ThreadPlacement.Find(part, hit.PointHit, normal) is null
            ? SketchSurface.Find(part, triangles, hit.TriangleIndex) : null;
    }

    private void PickSketchSurface(Point point, bool commit)
    {
        var (nearestPart, nearestPlane) = HitScene(point);
        if (choosingExtrudeFace)
            nearestPart ??= SketchBoundaryHit(point);
        if (choosingBevelPart)
        {
            var model = DetailModel(point, nearestPart);
            if (model is not null && pickableParts[model].Part is { } part &&
                BevelLimit(part) > 0 && FindBevelTarget(part, point) is { } target)
            {
                HighlightBevelTarget(part, target);
                ShowSurfaceTooltip(point, $"Bevel {part.Name}");
                OrbitSurface.Cursor = Cursors.Hand;
                if (commit)
                {
                    StartBevel(part);
                    if (SelectingBevel && bevelTargets.Count == 0)
                        PickBevelTarget(point, commit: true);
                }
            }
            else
                ResetSurfaceHover();
            return;
        }

        if (choosingThreadFace)
        {
            PickThreadSurface(point, nearestPart, commit);
            return;
        }
        bool preferPlane = choosingSketchSurface && nearestPlane is not null && !Keyboard.Modifiers.HasFlag(ModifierKeys.Alt);
        if (!preferPlane && nearestPart is { ModelHit: GeometryModel3D partModel } partHit)
        {
            var (part, triangles) = pickableParts[partModel];
            if (choosingMovePart)
            {
                HoverSolidFace(point);
                ShowSurfaceTooltip(point, $"Move {part.Name}");
                OrbitSurface.Cursor = Cursors.Hand;
                if (commit)
                    OpenBuildFeature(part, null, 0, moving: true);
                return;
            }
            if (part.Kind == PartKind.Sketch)
            {
                ResetSurfaceHover();
                var (profileIndex, start, count) = part.SketchProfileRange(partHit.TriangleIndex);
                if (profileIndex < 0)
                {
                    ShowSurfaceTooltip(point, "Select a closed sketch profile to extrude.");
                    return;
                }
                SurfaceHighlight.Content = SketchProfileModel(part, triangles.Skip(start).Take(count));
                ShowSurfaceTooltip(point, $"Extrude Profile {profileIndex + 1} of {part.Name}");
                OrbitSurface.Cursor = Cursors.Hand;
                if (commit)
                    OpenBuildFeature(part, null, 10, profileIndex: profileIndex);
                return;
            }
            int index = partHit.TriangleIndex;
            if (hoveredModel != partModel || hoveredFace?.Contains(index) != true)
            {
                ResetSurfaceHover();
                hoveredModel = partModel;
                hoveredFace = choosingSketchSurface ? FindSketchableFace(part, triangles, partHit) :
                    SketchSurface.Find(part, triangles, index);
                if (hoveredFace is { } face &&
                    (!choosingExtrudeFace || FaceExtrusion.Find(part, partHit.PointHit, face.Frame.Normal) is not null) &&
                    (!choosingHoleFace || HolePlacement.Find(part, partHit.PointHit, face.Frame.Normal) is not null))
                {
                    double lift = Math.Clamp(Math.Max(bed.X, bed.Y) * 0.00004, 0.00001, 0.02);
                    SurfaceHighlight.Content = MakeModel(face.Highlight(lift), Color.FromArgb(100, 231, 244, 255));
                    OrbitSurface.Cursor = Cursors.Hand;
                }
            }
            if (!commit)
                ShowHoverDetail(point, partModel, replaceFace: false);
            if (choosingHoleFace)
            {
                var hole = hoveredFace is { } picked ? HolePlacement.Find(part, partHit.PointHit, picked.Frame.Normal) : null;
                double diameter = hole is null ? 0 : InitialHoleDiameter(hole);
                HoleTargetOutline.Data = hole is not null && !commit ? HoleTarget(hole, diameter) : null;
                ShowSurfaceTooltip(point, hole is null ? "Choose a supported flat face" : $"Place hole on {part.Name}");
                if (commit && hole is not null && hoveredFace is { } surface)
                    OpenBuildFeature(part, null, diameter,
                        hole.WithFaceCenter(surface.Center()));
            }
            else if (choosingExtrudeFace)
            {
                var extrusion = hoveredFace is { } picked ? FaceExtrusion.Find(part, partHit.PointHit, picked.Frame.Normal) : null;
                ShowSurfaceTooltip(point, extrusion is null ? "Choose a supported face" : $"Extrude face on {part.Name}");
                if (commit && extrusion is not null)
                    OpenBuildFeature(part, extrusion, 5);
            }
            else
            {
                ShowSurfaceTooltip(point, hoveredFace is null ? "Choose a flat face" : $"Flat face on {part.Name}");
                if (commit && hoveredFace is { } selected)
                    StartFaceSketch(selected, partHit.PointHit);
            }
            return;
        }

        if (nearestPlane is { } plane)
        {
            if (hoveredPlane != plane)
            {
                ResetSurfaceHover();
                hoveredPlane = plane;
                UpdateOriginPlaneOverlay();
                OrbitSurface.Cursor = Cursors.Hand;
            }
            ShowSurfaceTooltip(point, $"{plane} plane");
            if (commit)
                StartPlaneSketch(plane);
            return;
        }

        ResetSurfaceHover();
    }

    private void ClearSurfaceHover(object sender, MouseEventArgs e) => ResetSurfaceHover();

    private void ShowSurfaceTooltip(Point point, string text)
    {
        SketchSelectionHint.Text = text;
        SurfaceTooltip.Visibility = Visibility.Visible;
        SurfaceTooltip.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        var size = SurfaceTooltip.DesiredSize;
        SurfaceTooltipPosition.X = Math.Clamp(point.X + 14, 0, Math.Max(0, ModelViewport.ActualWidth - size.Width - 4));
        SurfaceTooltipPosition.Y = Math.Clamp(point.Y + 16, 0, Math.Max(0, ModelViewport.ActualHeight - size.Height - 4));
    }

    private void ResetSurfaceHover()
    {
        hoveredModel = null;
        hoveredFace = null;
        hoveredTriangle = -1;
        HoverEdgeMarker.Data = HoverCornerMarker.Data = null;
        HoleTargetOutline.Data = null;
        hoveredPlane = null;
        HoveredPlaneOverlay.Data = null;
        SurfaceHighlight.Content = null;
        SurfaceTooltip.Visibility = Visibility.Collapsed;
        OrbitSurface.Cursor = null;
    }

    private static Triangle[] Rectangle(double x1, double y1, double x2, double y2, double z)
    {
        var a = new Point3D(x1, y1, z);
        var b = new Point3D(x2, y1, z);
        var c = new Point3D(x2, y2, z);
        var d = new Point3D(x1, y2, z);
        return [new(a, b, c), new(a, c, d)];
    }

    private RenderedPart GetRenderedPart(Part part) => renderedParts.GetValue(part, static item =>
    {
        var triangles = Geometry.Build(item);
        return new RenderedPart(triangles, MakeMesh(triangles, RoundedBevelNormals(item)));
    });

    private static GeometryModel3D MakeModel(IEnumerable<Triangle> triangles, Color color, bool bothSides = false) =>
        MakeModel(MakeMesh(triangles), color, bothSides);

    private static MeshGeometry3D MakeMesh(IEnumerable<Triangle> triangles, Func<Point3D, Vector3D, Vector3D>? shading = null)
    {
        var mesh = new MeshGeometry3D();
        var vertices = new Dictionary<(Point3D Point, Vector3D Normal), int>();
        foreach (var (a, b, c) in triangles)
        {
            var normal = Vector3D.CrossProduct(b - a, c - a);
            normal.Normalize();
            Add(a, shading?.Invoke(a, normal) ?? normal);
            Add(b, shading?.Invoke(b, normal) ?? normal);
            Add(c, shading?.Invoke(c, normal) ?? normal);
        }
        mesh.Freeze();
        return mesh;

        void Add(Point3D point, Vector3D normal)
        {
            normal = new Vector3D(Math.Round(normal.X, 8), Math.Round(normal.Y, 8), Math.Round(normal.Z, 8));
            if (!vertices.TryGetValue((point, normal), out int index))
            {
                index = mesh.Positions.Count;
                vertices.Add((point, normal), index);
                mesh.Positions.Add(point);
                mesh.Normals.Add(normal);
            }
            mesh.TriangleIndices.Add(index);
        }
    }

    private static GeometryModel3D MakeModel(MeshGeometry3D mesh, Color color, bool bothSides = false, Vector3D translation = default)
    {
        var material = new DiffuseMaterial(new SolidColorBrush(color));
        var model = new GeometryModel3D(mesh, material);
        if (translation.LengthSquared > 0)
            model.Transform = new TranslateTransform3D(translation);
        if (bothSides)
            model.BackMaterial = material;
        model.Freeze();
        return model;
    }

    private void ResetView(object sender, RoutedEventArgs e) => ResetCamera();

    private void SetViewDirection(Vector3D direction)
    {
        orbitQueue.Cancel();
        (yaw, pitch) = ViewportCamera.Angles(direction);
        ResetSurfaceHover();
        PositionCamera();
    }

    private void ResetCamera()
    {
        orbitQueue.Cancel();
        cameraPan = default;
        (yaw, pitch) = ViewportCamera.DefaultAngles;
        distance = ViewportCamera.DefaultDistance(Math.Max(bed.X, bed.Y));
        PositionCamera();
    }

    private void FitBuildView()
    {
        var bounds = Rect3D.Empty;
        foreach (var part in parts.Where(part => !part.Hidden))
            bounds.Union(PartWorldBounds(part));
        if (bounds.IsEmpty)
        {
            ResetCamera();
            return;
        }
        var center = new Point3D(bounds.X + bounds.SizeX / 2, bounds.Y + bounds.SizeY / 2, bounds.Z + bounds.SizeZ / 2);
        cameraPan = (Vector3D)center;
        distance = ViewportCamera.FitDistance(Camera, center, BoundsCorners(bounds), ModelViewport.ActualWidth, ModelViewport.ActualHeight);
        PositionCamera();
    }

    private void PositionCamera()
    {
        ViewportCamera.Position(Camera, new Point3D() + cameraPan, yaw, pitch, distance);
        FitBuildCameraDepth();
        OrientationCube.SetOrientation(-Camera.LookDirection, Camera.UpDirection);
        PositionPartPopup();
        UpdateOriginPlaneOverlay();
        UpdateFeatureArrow();
        UpdateBevelMarkers();
        UpdateSolidDetailMarkers();
        UpdateMeasureOverlay();
        UpdateMoveGizmo();
    }

    private void FitBuildCameraDepth() => ViewportCamera.FitDepth(Camera, distance,
        sceneBounds, FeaturePreview.Content?.Bounds ?? Rect3D.Empty);

    private void OrbitStart(object sender, MouseButtonEventArgs e)
    {
        if (OrbitSurface.IsMouseCaptured || e.ChangedButton is not (MouseButton.Left or MouseButton.Middle))
            return;
        if (e.ChangedButton == MouseButton.Middle)
        {
            if (OrbitSurface.CaptureMouse())
            {
                ResetSurfaceHover();
                dragOrigin = e.GetPosition(OrbitSurface);
                panDrag = Keyboard.Modifiers.HasFlag(ModifierKeys.Control);
                e.Handled = true;
            }
            return;
        }
        var position = e.GetPosition(ModelViewport);
        if (BeginMoveDrag(position) || BeginFeatureArrowDrag(position) || BeginLasso(position))
        {
            e.Handled = true;
            return;
        }
        if (OrbitSurface.CaptureMouse())
        {
            selectionPress = position;
            selectionDragged = false;
            boxSelectionPress = !measuring && !SelectingSurface && featureSource is null && (ExtendViewportSelection || HitScene(position).Part is null);
        }
    }

    private void OrbitEnd(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Middle && dragOrigin is not null)
        {
            orbitQueue.Flush();
            OrbitSurface.ReleaseMouseCapture();
            e.Handled = true;
            return;
        }
        if (e.ChangedButton != MouseButton.Left || dragOrigin is not null)
            return;
        if (moveDrag is not null)
        {
            EndMoveDrag();
            e.Handled = true;
            return;
        }
        if (lassoPoints is not null)
        {
            EndLasso(e.GetPosition(ModelViewport), ExtendViewportSelection);
            e.Handled = true;
            return;
        }
        if (featureArrowDrag is not null)
        {
            EndFeatureArrowDrag();
            e.Handled = true;
            return;
        }
        var point = e.GetPosition(ModelViewport);
        if (CompleteBoxSelection(point, ExtendViewportSelection))
        {
            e.Handled = true;
            return;
        }
        bool select = selectionPress is Point start && !selectionDragged && (point - start).LengthSquared < 16;
        selectionPress = null;
        OrbitSurface.ReleaseMouseCapture();
        if (select)
        {
            if (measuring)
                PickMeasurePoint(point);
            else if (SelectingSurface)
                PickSketchSurface(point, commit: true);
            else if (SelectingBevel)
                PickBevelTarget(point, commit: true);
            else if (featureSource is { Kind: PartKind.Sketch, AdditionalProfiles: not null } sketch &&
                     HitSceneCore(point, includeSketches: true, onlySketch: sketch).Part is { } profileHit)
            {
                int profile = sketch.SketchProfileRange(profileHit.TriangleIndex).Index;
                if (profile >= 0)
                    ExtrudeProfileChoice.SelectedIndex = profile;
            }
            else if (featureSource is null)
            {
                PickViewportSelection(point, ExtendViewportSelection);
            }
        }
    }

    private void OrbitMove(object sender, MouseEventArgs e)
    {
        var position = e.GetPosition(ModelViewport);
        if (selectionPress is Point press && (position - press).LengthSquared >= 16)
            selectionDragged = true;
        if (boxSelectionPress && selectionDragged && selectionPress is Point boxStart)
        {
            LassoOutline.Stroke = position.X >= boxStart.X ? ViewportSelection.WindowStroke : ViewportSelection.CrossingStroke;
            LassoOutline.Data = ViewportSelection.Geometry(ViewportSelection.Box(boxStart, position));
            return;
        }
        if (moveDrag is not null)
        {
            UpdateMoveDrag(position);
            return;
        }
        if (lassoPoints is not null)
        {
            UpdateLasso(position);
            return;
        }
        if (featureArrowDrag is not null)
        {
            UpdateFeatureArrowDrag(position);
            return;
        }
        if (dragOrigin is not Point previous || !OrbitSurface.IsMouseCaptured)
        {
            if (SelectingSurface)
                PickSketchSurface(e.GetPosition(ModelViewport), commit: false);
            else if (IsDraggableFeatureArrow(position))
            {
                ResetSurfaceHover();
                OrbitSurface.Cursor = Cursors.SizeAll;
            }
            else if (SelectingBevel)
                PickBevelTarget(e.GetPosition(ModelViewport), commit: false);
            else if (featureSource is not null && ReferenceEquals(e.OriginalSource, OrbitSurface))
            {
                HoverSolidFace(position);
                OrbitSurface.Cursor = featureMove && CanStartMoveDrag(position) ? Cursors.SizeAll : null;
            }
            else if (CanLasso)
                OrbitSurface.Cursor = ReferenceEquals(e.OriginalSource, OrbitSurface) ? Cursors.Cross : null;
            else if (ReferenceEquals(e.OriginalSource, OrbitSurface))
                HoverSolidFace(position);
            else
                ResetSurfaceHover();
            return;
        }
        var current = e.GetPosition(OrbitSurface);
        orbitQueue.Add(current - previous);
        dragOrigin = current;
    }

    private void DragCameraBy(Vector delta)
    {
        if (panDrag)
            cameraPan += ViewportCamera.Pan(Camera, ModelViewport.ActualWidth, distance, delta);
        else
            (yaw, pitch) = ViewportCamera.Orbit(yaw, pitch, delta);
        PositionCamera();
    }

    private void LoseBuildCapture(object sender, MouseEventArgs e)
    {
        featureArrowDrag = null;
        orbitQueue.Cancel();
        moveDrag = null;
        ClearLassoGesture();
        selectionPress = dragOrigin = null;
        panDrag = false;
    }

    private void Zoom(object sender, MouseWheelEventArgs e)
    {
        if (moveDrag is not null || lassoPoints is not null || boxSelectionPress)
            return;
        double scale = Math.Max(bed.X, bed.Y);
        distance = ViewportCamera.Zoom(distance, scale, e.Delta);
        PositionCamera();
        if (SelectingSurface)
            PickSketchSurface(e.GetPosition(ModelViewport), commit: false);
    }

    private void SetStatus(string message)
    {
        int outside = parts.Count(part => !part.Fits(bed));
        Status.Text = outside switch
        {
            0 => message,
            1 => $"{message}  •  1 part outside the build volume.",
            _ => $"{message}  •  {outside} parts outside the build volume."
        };
    }

    private void MarkDirty()
    {
        RecordProjectEdit();
        dirty = true;
        SetTitle();
    }

    private void SetTitle()
    {
        string name = projectPath is null ? "Untitled" : Path.GetFileNameWithoutExtension(projectPath);
        Title = $"LayerCAD — {name}{(dirty ? " *" : "")}";
    }

    private bool ConfirmDiscard()
    {
        if (!dirty)
            return true;
        return MessageBox.Show(this, "Save changes to the current project?", "LayerCAD",
            MessageBoxButton.YesNoCancel, MessageBoxImage.Question) switch
        {
            MessageBoxResult.Yes => Save(),
            MessageBoxResult.No => true,
            _ => false
        };
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        base.OnClosing(e);
        if (!e.Cancel)
            e.Cancel = !ConfirmDiscard();
    }

    private void NewProject(object sender, RoutedEventArgs e)
    {
        if (ConfirmDiscard())
            LoadProject(new Project(1, new Bed(220, 220, 250), []), null, "Created a new project.");
    }

    private void OpenProject(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Filter = "LayerCAD project (*.layercad)|*.layercad" };
        if (dialog.ShowDialog(this) != true)
            return;
        try
        {
            if (!ConfirmDiscard())
                return;
            var project = JsonSerializer.Deserialize<Project>(File.ReadAllText(dialog.FileName), JsonOptions);
            if (project is null || !project.IsValid())
            {
                SetStatus("This file is invalid or uses an unsupported LayerCAD project version.");
                return;
            }
            LoadProject(project, dialog.FileName, $"Opened {Path.GetFileName(dialog.FileName)}.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            SetStatus($"Could not open the project. {ex.Message}");
        }
    }

    private void LoadProject(Project project, string? path, string message)
    {
        EndBuildFeature();
        EndSurfaceSelection();
        parts.Clear();
        parts.AddRange(project.Parts);
        if (project.Version < 11)
            RecoverSourceSketches();
        bed = project.Bed;
        ResetProjectHistory();
        projectPath = path;
        dirty = false;
        SetTitle();
        UpdateBedLabel();
        ResetCamera();
        RefreshParts(parts.Count > 0 ? 0 : -1);
        SetStatus(message);
    }

    private void RecoverSourceSketches()
    {
        int count = parts.Count;
        for (int index = 0; index < count; index++)
        {
            var part = parts[index];
            if (part.Kind == PartKind.Sketch)
            {
                parts[index] = part with { SketchId = Guid.NewGuid() };
                continue;
            }
            if (part.Kind != PartKind.Extrusion)
                continue;

            var link = Guid.NewGuid();
            parts[index] = part with { SketchId = link };
            parts.Add(new Part(PartKind.Sketch, NextPartName(PartKind.Sketch), 0, 0, 0,
                part.X, part.Y, part.Z, part.Profile, part.Plane, part.FaceFrame, Hidden: true, SketchId: link));
        }
    }

    private void SaveProject(object sender, RoutedEventArgs e) => Save();

    private bool Save()
    {
        string? path = projectPath;
        if (path is null)
        {
            var dialog = new SaveFileDialog
            {
                Filter = "LayerCAD project (*.layercad)|*.layercad", FileName = "Untitled",
                DefaultExt = ".layercad"
            };
            if (dialog.ShowDialog(this) != true)
                return false;
            path = dialog.FileName;
        }
        try
        {
            int version = parts.Any(part => part.Constraints is not null) ? 21 : parts.Any(part => part.Dimensions is not null) ? 20 :
                parts.Any(part => part.Holes?.Any(hole => hole.Seat is not null) == true) ? 19 :
                parts.Any(part => part.BevelCuts?.Any(cut => cut.Target.ProfileKind != ProfileBevelKind.None) == true) ? 18 :
                parts.Any(part => part.OpenPaths is not null ||
                part.Holes?.Select(hole => hole.Axis).Distinct().Count() is > 1) ? 17 :
                parts.Any(part => part.NeedsExtendedBevelVersion) ? 16 :
                parts.Any(part => part.BevelCuts is { } cuts && (part.BevelBox() is null || cuts.Any(cut => cut.Rounded))) ? 15 :
                parts.Any(part => part.AdditionalProfiles is not null || part.SketchProfileIndex != 0) ? 14 :
                parts.Any(part => part.Threads?.Any(thread => thread.MetricSize is not null) == true) ? 13 : 12;
            WriteFile(path, stream => JsonSerializer.Serialize(stream, new Project(version, bed, parts.ToArray()), JsonOptions));
            projectPath = path;
            savedProjectState = currentProjectState;
            dirty = false;
            SetTitle();
            SetStatus($"Saved {Path.GetFileName(path)}.");
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            SetStatus($"Could not save the project. {ex.Message}");
            return false;
        }
    }

    private void ImportStl(object sender, RoutedEventArgs e)
    {
        EndMeasure();
        var dialog = new OpenFileDialog { Filter = "STL mesh (*.stl)|*.stl", Title = "Import STL mesh" };
        if (dialog.ShowDialog(this) == true)
            ImportMesh(dialog.FileName);
    }

    private void ImportMesh(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            var mesh = StlImport.Read(stream);
            string name = Path.GetFileNameWithoutExtension(path).Trim();
            EndSurfaceSelection();
            Add(new Part(PartKind.Mesh, string.IsNullOrEmpty(name) ? NextPartName(PartKind.Mesh) : name,
                0, 0, 0, 0, 0, 0, Mesh: mesh));
            SetStatus($"Imported {Path.GetFileName(path)} as an STL mesh.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            SetStatus($"Could not import the STL. {ex.Message}");
        }
    }

    private void ExportStl(object sender, RoutedEventArgs e)
    {
        if (Selected is not { } part)
        {
            SetStatus("Select a part to export.");
            return;
        }
        if (part.Kind == PartKind.Sketch)
        {
            SetStatus("Extrude the sketch before exporting it as STL.");
            return;
        }
        string name = string.Concat(part.Name.Select(ch => Path.GetInvalidFileNameChars().Contains(ch) ? '_' : ch));
        var dialog = new SaveFileDialog
        {
            Filter = "Stereolithography mesh (*.stl)|*.stl", FileName = name,
            DefaultExt = ".stl", Title = "Export selected part to STL"
        };
        if (dialog.ShowDialog(this) != true)
            return;
        try
        {
            WriteFile(dialog.FileName, stream => Geometry.WriteStl(stream, part));
            SetStatus($"Exported {part.Name} to {Path.GetFileName(dialog.FileName)}.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            SetStatus($"Could not export the STL. {ex.Message}");
        }
    }

    private static void WriteFile(string path, Action<Stream> write)
    {
        string temporary = Path.Combine(Path.GetDirectoryName(path)!, $".{Guid.NewGuid():N}.tmp");
        using (var stream = new FileStream(temporary, FileMode.CreateNew))
        {
            try
            {
                write(stream);
            }
            catch
            {
                stream.Dispose();
                File.Delete(temporary);
                throw;
            }
        }
        try
        {
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            File.Delete(temporary);
        }
    }
}



