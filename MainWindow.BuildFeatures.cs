using System.Globalization;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Media3D;

namespace LayerCAD;

public partial class MainWindow
{
    private bool choosingExtrudeFace;
    private bool choosingHoleFace;
    private bool choosingThreadFace;
    private bool choosingMovePart;
    private bool choosingBevelPart;
    private bool loadingFeatureInputs;
    private double initialFeatureAmount;
    private Part? featureSource;
    private FaceExtrusion? featureExtrusion;
    private HolePlacement? featureHole;
    private ThreadPlacement? featureThread;
    private ThreadSpec? initialThread;
    private readonly ConditionalWeakTable<Part, ThreadPlacement[]> threadOpenings = new();
    private readonly ConditionalWeakTable<BuildChange, TrianglePicker> extrusionPreviewPickers = new();
    private string customThreadPitch = "";
    private string customThreadDepth = "";
    private bool featureMove;
    private Part[] moveSources = [];
    private (Point Start, Vector PixelsPerMillimeter, double SignedDistance)? featureArrowDrag;
    private double initialHoleDepth;
    private BuildChange? featureChange;
    private readonly HashSet<BoxBevelTarget> bevelTargets = [];

    private bool SelectingSurface => choosingSketchSurface || choosingExtrudeFace || choosingHoleFace ||
                                     choosingThreadFace || choosingMovePart || choosingBevelPart;
    private bool SelectingBevel => featureSource is { Kind: not PartKind.Sketch } && featureExtrusion is null &&
                                   featureHole is null && featureThread is null && !featureMove;

    private void BeginFaceExtrude(object sender, RoutedEventArgs e)
    {
        EndBuildFeature();
        EndSurfaceSelection();
        var targets = SelectedExtrusionTargets();
        if (targets.Length > 1)
        {
            batchExtrusionTargets = targets;
            var first = targets[0];
            OpenBuildFeature(first.Part, first.Face, first.Face is null ? 10 : 5);
            return;
        }
        if (targets.Length == 1)
        {
            var target = targets[0];
            OpenBuildFeature(target.Part, target.Face, target.Face is null ? 10 : 5, profileIndex: target.Profile);
            return;
        }
        if (Selected is { Kind: PartKind.Sketch } sketch)
        {
            if (sketch.Profile is null || regionSketchSelections.ContainsKey(sketch) ||
                sketchProfileSelection is { Index: < 0 } selected && ReferenceEquals(selected.Sketch, sketch))
            {
                SetStatus("Select a closed sketch profile to extrude.");
                return;
            }
            OpenBuildFeature(sketch, null, 10, profileIndex: SelectedSketchProfile(sketch));
            return;
        }
        if (selectedFace is { } face && ReferenceEquals(Selected, face.Surface.Part) &&
            FaceExtrusion.Find(face.Surface.Part, face.Origin, face.Surface.Frame.Normal) is { } extrusion)
        {
            OpenBuildFeature(extrusion.Source, extrusion, 5);
            return;
        }
        choosingExtrudeFace = true;
        ShowSurfaceSelection();
        SetStatus("Select a finished sketch or a supported solid face.");
    }

    private void BeginHole(object sender, RoutedEventArgs e)
    {
        EndBuildFeature();
        EndSurfaceSelection();
        choosingHoleFace = true;
        ShowSurfaceSelection();
        SetStatus("Click a flat face where you want the hole center.");
    }

    private void BeginThread(object sender, RoutedEventArgs e)
    {
        EndBuildFeature();
        EndSurfaceSelection();
        choosingThreadFace = true;
        ShowSurfaceSelection();
        SetStatus("Click the outside of a cylinder, a hole opening, or the curved wall inside a hole.");
    }

    private void PickThreadSurface(Point point, SceneHit? hit, bool commit)
    {
        ThreadPlacement? thread = FindBoreOpening(point, hit);
        if (thread is null && hit is not null)
        {
            var entry = pickableParts[hit.ModelHit];
            thread = ThreadPlacement.Find(entry.Part, hit.PointHit, BevelFacetNormal(entry.Triangles[hit.TriangleIndex]));
        }
        ResetSurfaceHover();
        if (thread is { Surface: var surface } target)
        {
            var outline = new GeometryGroup();
            var offset = new Vector3D(target.Part.X, target.Part.Y, target.Part.Z);
            double depth = target.FromEnd ? surface.High : surface.Low;
            for (int i = 0; i < surface.Ring.Length; i++)
            {
                var a = surface.Prism.Vertex(surface.Ring[i], depth, offset);
                var b = surface.Prism.Vertex(surface.Ring[(i + 1) % surface.Ring.Length], depth, offset);
                AddBevelLine(outline, a, b);
            }
            HoverEdgeMarker.Data = outline;
        }
        ShowSurfaceTooltip(point, thread is null ? "Choose a round wall or a hole opening" :
            thread.Surface.External ? "External thread" : "Internal thread");
        OrbitSurface.Cursor = thread is null ? null : Cursors.Hand;
        if (commit && thread is not null)
            OpenBuildFeature(thread.Part, null, 0, thread: thread);
    }

    private ThreadPlacement? FindBoreOpening(Point point, SceneHit? hit)
    {
        if (ViewportRay(point) is not Vector3D ray)
            return null;
        var eye = Camera.Position;
        double limit = hit is null ? Camera.FarPlaneDistance :
            Vector3D.DotProduct(hit.PointHit - eye, ray) / ray.LengthSquared + 1e-5;
        ThreadPlacement? nearest = null;
        foreach (var part in parts.Where(part => !part.Hidden && (part.Holes is not null || part.Kind == PartKind.Tube)))
        {
            foreach (var opening in threadOpenings.GetValue(part, static item => ThreadPlacement.BoreOpenings(item)))
            {
                var surface = opening.Surface;
                var normal = surface.Prism.Frame.Normal;
                double approach = Vector3D.DotProduct(normal, ray);
                if (opening.FromEnd ? approach >= -1e-10 : approach <= 1e-10)
                    continue;
                var offset = new Vector3D(part.X, part.Y, part.Z);
                var center = surface.Prism.Vertex(surface.Center, opening.FromEnd ? surface.High : surface.Low, offset);
                double depth = Vector3D.DotProduct(normal, center - eye) / approach;
                if (depth < Camera.NearPlaneDistance || depth > limit)
                    continue;
                var projected = eye + ray * depth;
                double tolerance = Math.Min(surface.Radius * 0.15,
                    depth * Math.Tan(Camera.FieldOfView * Math.PI / 360) * 8 / ModelViewport.ActualWidth);
                if ((projected - center).Length > surface.Radius + tolerance)
                    continue;
                nearest = opening;
                limit = depth;
            }
        }
        return nearest;
    }

    private void InitializeThreadSizes()
    {
        ThreadSizeChoice.Items.Add(new ComboBoxItem { Content = "Custom" });
        foreach (var size in MetricThreadSize.Coarse)
            ThreadSizeChoice.Items.Add(new ComboBoxItem
            {
                Content = $"{size.Name} × {size.Pitch.ToString("0.##", CultureInfo.InvariantCulture)}",
                Tag = size
            });
    }

    private MetricThreadSize? SelectedMetricThread => (ThreadSizeChoice.SelectedItem as ComboBoxItem)?.Tag as MetricThreadSize;

    private void ThreadSizeChanged(object sender, SelectionChangedEventArgs e)
    {
        if (loadingFeatureInputs || featureThread is null)
            return;

        if (e.RemovedItems.OfType<ComboBoxItem>().Any(item => item.Tag is null))
        {
            customThreadPitch = FeatureAmount.Text;
            customThreadDepth = ThreadDepthInput.Text;
        }

        loadingFeatureInputs = true;
        UpdateThreadSizeFields();
        loadingFeatureInputs = false;
        UpdateBuildPreview();
    }

    private void UpdateThreadSizeFields()
    {
        var size = SelectedMetricThread;
        var surface = featureThread!.Surface;
        bool external = surface.External;
        FeatureAmount.IsReadOnly = ThreadDepthInput.IsReadOnly = size is not null;
        ThreadSizeDimensions.Visibility = size is null ? Visibility.Collapsed : Visibility.Visible;
        FeatureAmount.Text = size is null ? customThreadPitch : units.Format(size.Pitch);
        ThreadDepthInput.Text = size is null ? customThreadDepth : units.FormatLabel(size.RadialDepth(external));
        if (size is not null)
        {
            double major = ThreadGeometry.MajorDiameter(surface, size);
            double offset = major - size.Diameter;
            ThreadSizeDimensions.Text = $"Major Ø {units.FormatLabel(major)} {units.Symbol()}\n" +
                $"Pitch Ø {units.FormatLabel(size.PitchDiameter + offset)} {units.Symbol()}\n" +
                $"Minor Ø {units.FormatLabel(size.MinorDiameter(external) + offset)} {units.Symbol()}";
        }
    }

    private void BeginMove(object sender, RoutedEventArgs e)
    {
        EndBuildFeature();
        EndSurfaceSelection();
        if (Selected is { Hidden: false, Kind: not PartKind.Sketch } part)
        {
            OpenBuildFeature(part, null, 0, moving: true);
            return;
        }
        choosingMovePart = true;
        ShowSurfaceSelection();
        SetStatus("Select an object to move.");
    }

    private void BeginBevel(object sender, RoutedEventArgs e)
    {
        if (Selected is not { Hidden: false } source || BevelLimit(source) <= 0)
        {
            EndBuildFeature();
            EndSurfaceSelection();
            choosingBevelPart = true;
            UpdateHistoryButtons();
            ShowSurfaceSelection();
            SetStatus("Click an edge, corner, face perimeter, or circular rim.");
            return;
        }
        StartBevel(source);
    }

    private static double BevelLimit(Part part) =>
        part.BevelBox() is { } box
            ? Math.Min(box.Width, Math.Min(box.Depth, box.Height)) / 2
            : RoundBevel.Surface(part) is { } round ? RoundBevel.Limit(round) : PolygonBevel.Create(part)?.Limit ?? 0;

    private void StartBevel(Part source)
    {
        var sourceEdges = selectedEdges.Where(item => ReferenceEquals(item.Part, source)).Select(item => item.Edge);
        var sourceCorners = selectedCorners.Where(item => ReferenceEquals(item.Part, source)).Select(item => item.Point);
        var preselected = RoundBevel.Surface(source) is { } round
            ? sourceEdges.Select(edge => RoundRimAt(source, round, edge.Start)).Distinct().ToArray()
            : sourceEdges.Select(edge => edge.Start + (edge.End - edge.Start) * 0.5)
            .Concat(sourceCorners).Select(ProjectViewportPoint).OfType<Point>()
            .Select(point => FindBevelTarget(source, point)).OfType<BoxBevelTarget>()
            .Where(target => target.AxisCount > 1).ToArray();
        EndBuildFeature();
        EndSurfaceSelection();
        double limit = BevelLimit(source);
        if (limit <= 0.01)
        {
            SetStatus("This object is too small for a bevel.");
            return;
        }
        double amount = source.BevelCuts is { Length: > 0 } cuts ? cuts[0].Amount :
            source.Bevel > 0 ? source.Bevel : Math.Clamp(limit * 0.25, 0.01, 5);
        OpenBuildFeature(source, null, amount);
        bevelTargets.UnionWith(preselected);
        if (preselected.Length > 0)
            UpdateBuildPreview();
    }

    private void OpenBuildFeature(Part part, FaceExtrusion? extrusion, double amount, HolePlacement? hole = null,
                                  ThreadPlacement? thread = null, bool moving = false, int profileIndex = 0)
    {
        EndSurfaceSelection();
        PartList.SelectedItem = part;
        featureSource = part;
        featureExtrusion = extrusion;
        featureHole = hole;
        featureThread = thread;
        featureMove = moving;
        UpdateHistoryButtons();
        moveSources = !moving ? [] : lassoSelection.Contains(part)
            ? parts.Where(lassoSelection.Contains).ToArray() : [part];
        initialThread = thread is null ? null : part.Threads?.FirstOrDefault(item => item.Surface == thread.Index) ??
            new ThreadSpec(thread.Index, Math.Min(1.5, thread.Surface.Length),
                Math.Max(0.01, Math.Min(thread.Surface.Radius / 6, 0.45)),
                Math.Min(12, thread.Surface.Length), false, thread.FromEnd);
        initialFeatureAmount = initialThread?.Pitch ?? amount;

        loadingFeatureInputs = true;
        FeatureTitle.Text = moveSources.Length > 1 ? $"Move {moveSources.Length} objects" : moving ? "Move" : thread is not null ? "Thread" :
            hole is not null ? "Hole" : SelectingBevel ? "Bevel" : "Extrude";
        FeatureAmountLabel.Content = hole is not null ? $"_Diameter ({units.Symbol()})" :
            thread is not null ? $"_Pitch ({units.Symbol()})" :
            SelectingBevel ? $"Bevel _size ({units.Symbol()})" : $"_Distance ({units.Symbol()})";
        FeatureAmountLabel.Visibility = FeatureAmount.Visibility = moving ? Visibility.Collapsed : Visibility.Visible;
        ExtrudeOptions.Visibility = extrusion is not null || part.Kind == PartKind.Sketch ? Visibility.Visible : Visibility.Collapsed;
        ReverseExtrusion.Visibility = part.Kind == PartKind.Sketch ? Visibility.Visible : Visibility.Collapsed;
        ReverseExtrusion.IsChecked = false;
        BevelOptions.Visibility = SelectingBevel ? Visibility.Visible : Visibility.Collapsed;
        var firstBevel = part.BevelCuts?.FirstOrDefault();
        RoundedBevel.IsChecked = firstBevel?.Rounded == true;
        CenteredBevel.IsChecked = firstBevel?.Angle is null;
        BevelAngleInput.Text = (firstBevel?.Angle ?? 45).ToString(CultureInfo.CurrentCulture);
        ContinueBevel.IsChecked = false;
        ContinueBevel.Visibility = SelectingBevel && (part.BevelBox() is not null || PolygonBevel.Create(part) is not null)
            ? Visibility.Visible : Visibility.Collapsed;
        ExtrudeProfileOptions.Visibility = part.AdditionalProfiles is not null && batchExtrusionTargets.Length == 0
            ? Visibility.Visible : Visibility.Collapsed;
        if (part.AdditionalProfiles is { } additional)
        {
            ExtrudeProfileChoice.ItemsSource = Enumerable.Range(1, additional.Length + 1).Select(number => $"Profile {number}").ToArray();
            ExtrudeProfileChoice.SelectedIndex = profileIndex;
        }
        HoleOptions.Visibility = hole is not null ? Visibility.Visible : Visibility.Collapsed;
        ThreadSizeOptions.Visibility = ThreadOptions.Visibility = thread is not null ? Visibility.Visible : Visibility.Collapsed;
        FeatureAmount.IsReadOnly = false;
        MoveOptions.Visibility = moving ? Visibility.Visible : Visibility.Collapsed;
        if (moving)
        {
            MoveUnitsLabel.Text = $"Offsets ({units.Symbol()})";
            MoveX.Text = MoveY.Text = MoveZ.Text = "0";
        }
        if (thread is not null && initialThread is { } spec)
        {
            ThreadDepthLabel.Content = $"Radial _depth ({units.Symbol()})";
            ThreadLengthLabel.Content = $"_Length ({units.Symbol()})";
            customThreadPitch = units.Format(spec.Pitch);
            customThreadDepth = units.Format(spec.Depth);
            ThreadSizeChoice.SelectedItem = ThreadSizeChoice.Items.OfType<ComboBoxItem>()
                .First(item => (item.Tag as MetricThreadSize)?.Name == spec.MetricSize);
            ThreadLengthInput.Text = units.Format(spec.Length);
            ThreadFullLength.IsChecked = spec.Length == thread.Surface.Length;
            ThreadReverseEnd.IsChecked = false;
            ThreadLeftHanded.IsChecked = spec.LeftHanded;
        }
        if (hole is not null)
        {
            initialHoleDepth = Math.Max(0.01, Math.Min(8, hole.Prism.Thickness / 2));
            HoleDepthLabel.Content = $"D_epth ({units.Symbol()})";
            HoleDepthInput.Text = units.Format(initialHoleDepth);
            HoleCenterOnFace.IsChecked = false;
            HoleThrough.IsChecked = hole.Prism.Thickness <= 0.010001;
            HoleTypeChoice.SelectedIndex = 0;
            HoleSeatOptions.Visibility = Visibility.Collapsed;
            HoleSeatDiameterLabel.Content = $"Head _diameter ({units.Symbol()})";
            HoleSeatDiameter.Text = units.Format(Math.Min(hole.Clearance * 1.5, initialFeatureAmount * 1.75));
        }
        AddMaterial.IsChecked = true;
        UpdateBatchExtrusionOptions();
        if (thread is not null)
            UpdateThreadSizeFields();
        else
            FeatureAmount.Text = units.Format(initialFeatureAmount);
        loadingFeatureInputs = false;

        FeaturePanel.Visibility = Visibility.Visible;
        UpdatePartPopup();
        UpdateBuildPreview();
        SetStatus(moveSources.Length > 1 ? $"Moving {moveSources.Length} objects together." :
            moving ? $"Moving {part.Name}." : thread is not null ? $"Previewing a thread on {part.Name}." :
            hole is not null ? $"Previewing a hole on {part.Name}." :
            SelectingBevel ? $"Select corners, edges, or faces on {part.Name}." : $"Previewing extrusion on {part.Name}.");
        var input = moving ? MoveX : FeatureAmount;
        if (thread is not null)
            ThreadSizeChoice.Focus();
        else
        {
            input.Focus();
            input.SelectAll();
        }
    }

    private void FeatureInputChanged(object sender, RoutedEventArgs e)
    {
        if (!loadingFeatureInputs && featureSource is not null)
            UpdateBuildPreview();
    }

    private void UpdateBuildPreview()
    {
        if (featureSource is not { } source)
            return;

        if (featureMove)
        {
            featureChange = PreviewMove(source);
            FeatureFeedback.Text = TryReadMoveOffset(out _) ?
                "Drag the object along X and Y, use an axis arrow, or enter offsets." : "Enter valid offsets along X, Y, or Z.";
            ShowBuildPreview();
            return;
        }

        double amount = units.Read(FeatureAmount.Text, initialFeatureAmount);
        if (SelectingBevel)
        {
            bool rounded = RoundedBevel.IsChecked == true;
            CenteredBevel.Visibility = rounded ? Visibility.Collapsed : Visibility.Visible;
            BevelAngleOptions.Visibility = !rounded && CenteredBevel.IsChecked != true ? Visibility.Visible : Visibility.Collapsed;
            FeatureAmountLabel.Content = rounded ? $"_Radius ({units.Symbol()})" : $"_Setback ({units.Symbol()})";
        }
        if (featureThread is { } thread && initialThread is { } spec)
        {
            bool full = ThreadFullLength.IsChecked == true;
            ThreadLengthOptions.Visibility = full ? Visibility.Collapsed : Visibility.Visible;
            double length = full ? thread.Surface.Length : units.Read(ThreadLengthInput.Text, spec.Length);
            var size = SelectedMetricThread;
            featureChange = thread.Preview(amount, units.Read(ThreadDepthInput.Text, spec.Depth), length,
                ThreadLeftHanded.IsChecked == true, spec.FromEnd != (ThreadReverseEnd.IsChecked == true), size);
            FeatureFeedback.Text = size is not null && !ThreadGeometry.FitsStock(thread.Surface, size)
                ? "This profile cuts too deep for the selected cylinder. Choose a smaller pitch."
                : featureChange is null
                ? $"Check the pitch, depth, and length. Use at most {ThreadGeometry.MaxTurns} turns and leave enough wall thickness."
                : size is not null && Math.Abs(ThreadGeometry.MajorDiameter(thread.Surface, size) - size.Diameter) > 1e-6
                ? $"{size.Name} pitch and profile on the selected diameter."
                : thread.Surface.External ? "External modeled thread." : "Internal modeled thread.";
            ShowBuildPreview();
            return;
        }

        bool remove = RemoveMaterial.IsChecked == true;
        bool extruding = source.Kind == PartKind.Sketch || featureExtrusion is not null;
        bool through = HoleThrough.IsChecked == true;
        bool centered = HoleCenterOnFace.IsChecked == true;
        var placement = centered && featureHole is { FaceCenter: SketchPoint center }
            ? featureHole with { Center = center } : featureHole;
        HoleDepthOptions.Visibility = through ? Visibility.Collapsed : Visibility.Visible;
        featureChange = placement is not null
            ? placement.Preview(amount, units.Read(HoleDepthInput.Text, initialHoleDepth), through, ReadHoleSeat())
            : extruding ? PreviewExtrusion(source, amount, remove, MergeMaterial.IsChecked == true)
            : PreviewBevel(source, amount);
        FeatureFeedback.Text = placement is not null
            ? featureChange is null ? HoleTypeChoice.SelectedIndex > 0
                ? "Use a head diameter larger than the bore. Keep the recess within the face and shallower than the bore."
                : centered && amount > 0 && amount / 2 >= placement.Clearance
                ? "This hole does not fit at the face center. Reduce the diameter or clear Center on face."
                : "Keep the opening within the face. A blind depth must be less than the part thickness."
                : through ? "Cuts through the full thickness." : "Cuts to the specified depth."
            : SelectingBevel
            ? bevelTargets.Count == 0 ? "Click an edge, a corner, or a face perimeter."
                : featureChange is null ? !TryReadBevelAngle(out _) ? "Enter an angle from 1° to 89°."
                    : RoundedBevel.IsChecked == true ? "Reduce the radius so the bevel fits."
                    : "Adjust the setback or angle so the bevel leaves solid material and clears any holes."
                : $"{bevelTargets.Count} selected. Click a target again to deselect it."
            : extrusionFeedback ?? (featureChange is null
                ? "Enter a distance that leaves the part within the supported dimensions."
                : "Adds material in the arrow direction.");
        ShowBuildPreview();
    }

    private void HoleTypeChanged(object sender, SelectionChangedEventArgs e)
    {
        if (loadingFeatureInputs || featureHole is not { } hole)
            return;
        bool counterbore = HoleTypeChoice.SelectedIndex == 1;
        HoleSeatOptions.Visibility = HoleTypeChoice.SelectedIndex > 0 ? Visibility.Visible : Visibility.Collapsed;
        loadingFeatureInputs = true;
        HoleSeatSizeLabel.Content = counterbore ? $"Recess _depth ({units.Symbol()})" : "Included _angle (°)";
        HoleSeatSize.Text = counterbore ? units.Format(Math.Min(2, hole.Prism.Thickness / 4)) : "90";
        loadingFeatureInputs = false;
        UpdateBuildPreview();
    }

    private HoleSeat? ReadHoleSeat() => HoleTypeChoice.SelectedIndex switch
    {
        1 => new(HoleSeatKind.Counterbore, units.Read(HoleSeatDiameter.Text), Depth: units.Read(HoleSeatSize.Text)),
        2 => new(HoleSeatKind.Countersink, units.Read(HoleSeatDiameter.Text),
            Angle: double.TryParse(HoleSeatSize.Text, NumberStyles.Float, CultureInfo.CurrentCulture, out double angle) ? angle : double.NaN),
        _ => null
    };

    private void ShowBuildPreview()
    {
        ApplyFeatureButton.IsEnabled = featureChange is not null;
        DrawScene();
    }

    private BuildChange? PreviewMove(Part source)
    {
        if (!TryReadMoveOffset(out var delta) || delta.LengthSquared == 0)
            return null;
        var moved = new Dictionary<Part, Part>(ReferenceEqualityComparer.Instance);
        foreach (var part in moveSources)
            moved.Add(part, part with { X = part.X + delta.X, Y = part.Y + delta.Y, Z = part.Z + delta.Z });
        return new BuildChange(moved[source], [], false, MovedParts: moved);
    }

    private BuildChange? PreviewSketchExtrusion(Part sketch, double amount, int? selectedProfile = null)
    {
        string name = sketch.Name.StartsWith("Sketch ", StringComparison.Ordinal) &&
                      int.TryParse(sketch.Name.AsSpan("Sketch ".Length), out _)
            ? NextPartName(PartKind.Extrusion) : sketch.Name;
        int profileIndex = selectedProfile ?? (sketch.AdditionalProfiles is null ? 0 : Math.Max(0, ExtrudeProfileChoice.SelectedIndex));
        var profile = sketch.SketchProfiles.ElementAt(profileIndex);
        var solid = sketch with
        {
            Kind = PartKind.Extrusion, Height = amount, Name = name, Hidden = false, Profile = profile,
            AdditionalProfiles = null, OpenPaths = null, Dimensions = null, Constraints = null, ReferenceImage = null, SketchProfileIndex = profileIndex, SketchId = sketch.SketchId ?? Guid.NewGuid()
        };
        if (!solid.IsValid())
            return null;
        var center = new SketchPoint(profile.Average(point => point.X), profile.Average(point => point.Y));
        var origin = sketch.SketchVertex(center, 0) + new Vector3D(sketch.X, sketch.Y, sketch.Z);
        var direction = sketch.SketchVertex(center, 1) - sketch.SketchVertex(center, 0);
        if (ReverseExtrusion.IsChecked == true)
        {
            var offset = direction * amount;
            solid = solid with { X = solid.X - offset.X, Y = solid.Y - offset.Y, Z = solid.Z - offset.Z };
            if (!solid.IsValid())
                return null;
            direction = -direction;
        }
        return new BuildChange(solid, Geometry.Build(solid), false, origin, direction, amount);
    }

    private BuildChange? PreviewBevel(Part source, double amount)
    {
        if (bevelTargets.Count == 0 || !double.IsFinite(amount) || amount < 0.01 || !TryReadBevelAngle(out var angle))
            return null;
        var previous = source.BevelCuts ?? [];
        var cuts = previous.ToDictionary(cut => cut.Target);
        foreach (var target in bevelTargets)
            cuts[target] = new BoxBevelCut(target, amount, RoundedBevel.IsChecked == true, angle);
        var updated = cuts.Values.ToArray();
        var result = previous.SequenceEqual(updated) ? source : source with { BevelCuts = updated };
        if (!result.IsValid())
            return null;

        var handle = bevelTargets.First();
        var handleCut = cuts[handle];
        if (PolygonBevel.Create(source) is { } polygon)
        {
            var arrow = polygon.Arrow(handleCut);
            return new BuildChange(result, [], false, arrow.Origin, arrow.Direction, arrow.Length);
        }
        if (RoundBevel.Surface(source) is { } round)
        {
            var offset = new Vector3D(source.X, source.Y, source.Z);
            var center = round.Prism.Vertex(round.Center, handle.Z > 0 ? round.High : round.Low, offset);
            var normal = round.Prism.Frame.Normal;
            var radial = Camera.Position - center;
            radial -= normal * Vector3D.DotProduct(radial, normal);
            if (radial.LengthSquared < 1e-8)
                radial = round.Prism.Frame.Horizontal.Vector;
            radial.Normalize();
            var direction = radial + normal * (handle.Z * handleCut.Slope);
            double length = direction.Length;
            return new BuildChange(result, [], false, center + radial * round.Radius,
                -direction / length, amount / length);
        }
        var box = source.BevelBox()!;
        var outward = handleCut.FlatNormal;
        var origin = new Point3D(box.X + handle.X * box.Width / 2, box.Y + handle.Y * box.Depth / 2,
            box.Z + box.Height / 2 + handle.Z * box.Height / 2);
        double projection = outward.Length;
        return new BuildChange(result, [], Removes: false, origin, -outward / projection, amount / projection);
    }

    private bool TryReadBevelAngle(out double? angle)
    {
        angle = null;
        if (RoundedBevel.IsChecked == true || CenteredBevel.IsChecked == true)
            return true;
        if (!double.TryParse(BevelAngleInput.Text, NumberStyles.Float, CultureInfo.CurrentCulture, out double degrees) ||
            degrees is < 1 or > 89 || !double.IsFinite(degrees))
            return false;
        angle = degrees;
        return true;
    }

    private double BevelDragMaximum(Part part)
    {
        if (RoundedBevel.IsChecked == true || !TryReadBevelAngle(out var angle))
            return BevelLimit(part);
        if (part.BevelBox() is { } box)
            return bevelTargets.Min(target => new BoxBevelCut(target, 0, Angle: angle).FlatLimit(box));
        if (PolygonBevel.Create(part) is { } polygon)
            return bevelTargets.Min(target => polygon.MaxAmount(new BoxBevelCut(target, 1, Angle: angle)));
        var round = RoundBevel.Surface(part)!;
        var cut = new BoxBevelCut(bevelTargets.First(), 0, Angle: angle);
        double existingDepth = part.BevelCuts?.Where(item => !bevelTargets.Contains(item.Target)).Sum(RoundBevel.Depth) ?? 0;
        return Math.Min(round.Radius, (round.Length - existingDepth) * cut.Slope / bevelTargets.Count);
    }

    private void PickBevelTarget(Point point, bool commit)
    {
        if (featureSource is not { } box || FeaturePanel.IsMouseOver ||
            FindBevelTarget(box, point) is not { } target)
        {
            ResetSurfaceHover();
            return;
        }

        if (!commit)
        {
            HighlightBevelTarget(box, BevelPerimeter(box, target, point));
            string label = RoundBevel.Surface(box) is not null ? "Circular rim" :
                target.AxisCount switch { 1 => "Face perimeter", 2 => "Edge", _ => "Corner" };
            ShowSurfaceTooltip(point, label);
            OrbitSurface.Cursor = Cursors.Hand;
            return;
        }

        var perimeter = BevelPerimeter(box, target, point);
        var targets = PolygonBevel.Create(box) is { } polygon ? polygon.Perimeter(perimeter).ToArray() :
            perimeter.AxisCount == 1 ? BoxBevelTarget.Edges.Where(edge => edge.OnFace(perimeter)).ToArray() : [perimeter];
        bool remove = targets.All(bevelTargets.Contains);
        foreach (var item in targets)
        {
            if (remove)
                bevelTargets.Remove(item);
            else
                bevelTargets.Add(item);
        }
        UpdateBuildPreview();
    }

    private BoxBevelTarget BevelPerimeter(Part part, BoxBevelTarget target, Point pointer)
    {
        if (ContinueBevel.IsChecked != true || target.AxisCount == 1)
            return target;
        if (PolygonBevel.Create(part) is { } polygon)
        {
            if (HitScene(pointer).Part is { } polygonHit && pickableParts[polygonHit.ModelHit] is var polygonEntry &&
                ReferenceEquals(polygonEntry.Part, part))
            {
                var face = polygon.Face(BevelFacetNormal(polygonEntry.Triangles[polygonHit.TriangleIndex]));
                if (polygon.Perimeter(face).Contains(target))
                    return face;
            }
            return target;
        }
        if (part.BevelBox() is null)
            return target;
        var faces = BoxBevelTarget.Faces.Where(target.OnFace).ToArray();
        if (HitScene(pointer).Part is { } hit && pickableParts[hit.ModelHit] is var entry && ReferenceEquals(entry.Part, part))
        {
            var hovered = BoxBevelTarget.FromNormal(BevelFacetNormal(entry.Triangles[hit.TriangleIndex]));
            if (faces.Contains(hovered))
                return hovered;
        }
        var bounds = PartWorldBounds(part);
        var center = new Point3D(bounds.X + bounds.SizeX / 2, bounds.Y + bounds.SizeY / 2, bounds.Z + bounds.SizeZ / 2);
        return faces.MaxBy(face => Vector3D.DotProduct(face.Direction, Camera.Position - center));
    }

    private void HighlightBevelTarget(Part source, BoxBevelTarget target)
    {
        ResetSurfaceHover();
        if (PolygonBevel.Create(source) is { } polygon)
        {
            DrawPolygonTargets(polygon, polygon.Perimeter(target), HoverEdgeMarker, HoverCornerMarker);
            return;
        }
        if (RoundBevel.Surface(source) is { } round)
        {
            HoverEdgeMarker.Data = RoundRimOutline(source, round, target);
            return;
        }
        var box = source.BevelBox()!;
        var center = new Point3D(box.X, box.Y, box.Z + box.Height / 2);
        Point3D World(BoxBevelTarget point) => center +
            new Vector3D(point.X * box.Width / 2, point.Y * box.Depth / 2, point.Z * box.Height / 2);
        if (target.AxisCount == 3)
        {
            if (ProjectViewportPoint(World(target)) is Point corner)
                HoverCornerMarker.Data = new EllipseGeometry(corner, 5, 5);
            return;
        }
        var outline = new GeometryGroup();
        var edges = target.AxisCount == 1 ? BoxBevelTarget.Edges.Where(edge => edge.OnFace(target)) : [target];
        foreach (var edge in edges)
        {
            var (start, end) = edge.EdgeEnds();
            AddBevelLine(outline, World(start), World(end));
        }
        HoverEdgeMarker.Data = outline;
    }

    private BoxBevelTarget? FindBevelTarget(Part box, Point mouse)
    {
        var hit = HitScene(mouse).Part;
        if (PolygonBevel.Create(box) is { } polygon)
            return FindPolygonBevelTarget(box, polygon, mouse, hit);
        if (RoundBevel.Surface(box) is { } round)
            return FindRoundBevelTarget(box, round, mouse, hit);

        var rectangle = box.BevelBox()!;
        var half = new Vector3D(rectangle.Width / 2, rectangle.Depth / 2, rectangle.Height / 2);
        var center = new Point3D(rectangle.X, rectangle.Y, rectangle.Z + half.Z);
        var view = Camera.Position - center;
        BoxBevelTarget? best = null;
        double distance = 8;
        foreach (var corner in BoxBevelTarget.Corners)
        {
            if (Visible(corner) && ProjectViewportPoint(World(corner)) is Point screen &&
                (mouse - screen).Length < distance && IsDetailVisible(World(corner), screen))
            {
                distance = (mouse - screen).Length;
                best = corner;
            }
        }
        if (best is not null)
            return best;

        distance = EdgePickRadius;
        foreach (var edge in BoxBevelTarget.Edges)
        {
            if (!Visible(edge))
                continue;
            var (a, b) = edge.EdgeEnds();
            if (ProjectViewportPoint(World(a)) is not Point start || ProjectViewportPoint(World(b)) is not Point end)
                continue;
            var line = end - start;
            if (line.LengthSquared < 1)
                continue;
            double fraction = Math.Clamp(Vector.Multiply(mouse - start, line) / line.LengthSquared, 0, 1);
            double gap = (mouse - (start + fraction * line)).Length;
            if (gap < distance && IsDetailVisible(World(a) + fraction * (World(b) - World(a)), start + fraction * line))
            {
                distance = gap;
                best = edge;
            }
        }
        if (best is not null)
            return best;
        if (hit?.ModelHit is not GeometryModel3D model || !ReferenceEquals(pickableParts[model].Part, box))
            return null;
        var triangle = pickableParts[model].Triangles[hit.TriangleIndex];
        var normal = Vector3D.CrossProduct(triangle.B - triangle.A, triangle.C - triangle.A);
        normal.Normalize();
        return BoxBevelTarget.FromNormal(normal);

        bool Visible(BoxBevelTarget target) =>
            target.X * view.X > half.X || target.Y * view.Y > half.Y || target.Z * view.Z > half.Z;

        Point3D World(BoxBevelTarget target) => center + new Vector3D(target.X * half.X, target.Y * half.Y, target.Z * half.Z);
    }

    private void ApplyBuildFeature(object sender, RoutedEventArgs e)
    {
        if (featureSource is null || featureChange is not { } change)
            return;
        int index = parts.IndexOf(featureSource);
        if (index < 0)
        {
            EndBuildFeature();
            return;
        }

        string name = featureMove ? "Move" : featureThread is not null ? "Thread" :
            featureHole is not null ? "Hole" : SelectingBevel ? "Bevel" : change.Removes ? "Extrusion cut" : "Extrusion";
        var original = featureSource;
        if (batchExtrusionTargets.Length > 0)
        {
            var results = batchExtrusionResults;
            EndBuildFeature();
            ApplyBatchExtrusion(results);
            return;
        }
        EndBuildFeature();
        if (change.CutParts is not null || change.MergedParts is not null)
        {
            ApplyExtrusionBoolean(original, change);
            return;
        }
        if (change.MovedParts is { } moved)
        {
            var updates = moved.Select(entry => (Index: parts.FindIndex(item => ReferenceEquals(item, entry.Key)), entry.Key, entry.Value)).ToArray();
            if (updates.Any(update => update.Index < 0))
                return;
            foreach (var update in updates)
            {
                parts[update.Index] = update.Value;
                if (lassoSelection.Remove(update.Key))
                    lassoSelection.Add(update.Value);
            }
            MarkDirty();
            RefreshParts(index);
            OrbitSurface.Focus();
            SetStatus(updates.Length == 1 ? $"Moved {change.Result.Name}." : $"Moved {updates.Length} objects together.");
            return;
        }
        if (original.Kind == PartKind.Sketch)
        {
            var link = original.SketchId ?? Guid.NewGuid();
            parts[index] = original with { SketchId = link,
                Hidden = original.Hidden || original.AdditionalProfiles is null && original.OpenPaths is null };
            parts.Add(change.Result with { SketchId = link });
            index = parts.Count - 1;
            MarkDirty();
        }
        else if (change.Result != original)
        {
            parts[index] = change.Result;
            MarkDirty();
        }
        RefreshParts(index);
        OrbitSurface.Focus();
        SetStatus($"{name} applied to {change.Result.Name}.");
    }

    private void CancelBuildFeature(object sender, RoutedEventArgs e)
    {
        EndBuildFeature();
        SetStatus("Cancelled the build feature.");
    }

    private void EndBuildFeature()
    {
        if (featureSource is null)
            return;
        EndFeatureArrowDrag();
        EndMoveDrag();
        featureSource = null;
        featureExtrusion = null;
        featureHole = null;
        featureThread = null;
        initialThread = null;
        featureMove = false;
        moveSources = [];
        featureChange = null;
        batchExtrusionTargets = [];
        batchExtrusionResults = [];
        UpdateBatchExtrusionOptions();
        bevelTargets.Clear();
        UpdateHistoryButtons();
        FeaturePanel.Visibility = Visibility.Collapsed;
        UpdatePartPopup();
        DrawScene();
    }

    private void KeepFeatureInput(object sender, MouseButtonEventArgs e) => e.Handled = true;

    private void KeepFeatureWheel(object sender, MouseWheelEventArgs e) => e.Handled = true;

    private void DrawFeaturePreview()
    {
        FeaturePreview.Content = featureChange is { Region.Count: > 0 } change
            ? MakeModel(change.Region, change.Removes ? Color.FromArgb(125, 249, 62, 73) : Color.FromArgb(125, 49, 231, 113))
            : null;
        DrawBevelHighlight();
        UpdateFeatureArrow();
        UpdateBevelMarkers();
        UpdateMoveGizmo();
        UpdateViewportMouseHint();
    }

    private bool HoverExtrusionPreview(Point pointer, SceneHit? stockHit)
    {
        if (featureChange is not { Removes: false, Region.Count: > 0 } change ||
            (featureExtrusion is null && featureSource?.Kind != PartKind.Sketch) ||
            FeaturePreview.Content is not GeometryModel3D model || ViewportRay(pointer) is not Vector3D ray)
            return false;

        double limit = stockHit is null ? Camera.FarPlaneDistance :
            Vector3D.DotProduct(stockHit.PointHit - Camera.Position, ray) / ray.LengthSquared + 1e-6;
        var picker = extrusionPreviewPickers.GetValue(change, static item => new TrianglePicker(item.Region));
        int index = picker.Find(Camera.Position, ray, Camera.NearPlaneDistance, false, ref limit);
        if (index < 0)
            return false;
        if (ReferenceEquals(hoveredModel, model) && hoveredFace?.Contains(index) == true)
            return true;

        ResetSurfaceHover();
        hoveredModel = model;
        hoveredTriangle = index;
        hoveredFace = SketchSurface.Find(change.Result, change.Region, index);
        if (hoveredFace is { } face)
            SurfaceHighlight.Content = MakeModel(face.Highlight(FaceHighlightLift), Color.FromArgb(105, 255, 190, 110));
        return true;
    }

    private List<Triangle>? BevelPreviewMesh() =>
        pickableParts.Values.FirstOrDefault(entry => ReferenceEquals(entry.Part, featureSource)).Triangles;

    private void DrawBevelHighlight()
    {
        BevelHighlight.Content = null;
        if (!SelectingBevel || featureChange is null || BevelPreviewMesh() is not { } mesh)
            return;

        double lift = Math.Clamp(Math.Max(bed.X, bed.Y) * 0.00004, 0.00001, 0.02);
        var shaded = new List<Triangle>();
        var round = featureSource is { } source ? RoundBevel.Surface(source) : null;
        var polygon = PolygonBevel.Create(featureChange.Result);
        var polygonFacet = polygon?.Highlight(featureChange.Result.BevelCuts!.Where(cut => bevelTargets.Contains(cut.Target)));
        foreach (var triangle in mesh)
        {
            var normal = BevelFacetNormal(triangle);
            if (polygonFacet?.Invoke(triangle) ?? BevelFacetSelected(normal, round))
                shaded.Add(triangle.Translate(normal * lift));
        }
        if (shaded.Count > 0)
            BevelHighlight.Content = MakeModel(MakeMesh(shaded, RoundedBevelNormals(featureChange.Result)),
                Color.FromArgb(82, 249, 62, 73));
    }

    private bool BevelFacetSelected(Vector3D normal, ThreadSurface? round)
    {
        if (round is not null)
        {
            double axial = Vector3D.DotProduct(normal, round.Prism.Frame.Normal);
            return Math.Abs(axial) is > 1e-6 and < 0.999999 && bevelTargets.Contains(RoundBevel.Rim(axial > 0));
        }
        var target = BoxBevelTarget.FromNormal(normal);
        return bevelTargets.Contains(target) || RoundedBevel.IsChecked == true && target.AxisCount == 3 &&
            AdjacentEdges(target).All(bevelTargets.Contains);
    }

    private static IEnumerable<BoxBevelTarget> AdjacentEdges(BoxBevelTarget corner) =>
        [corner with { X = 0 }, corner with { Y = 0 }, corner with { Z = 0 }];

    private static Vector3D BevelFacetNormal(Triangle triangle)
    {
        var normal = Vector3D.CrossProduct(triangle.B - triangle.A, triangle.C - triangle.A);
        normal.Normalize();
        return normal;
    }

    private void UpdateBevelMarkers()
    {
        BevelEdgeMarkers.Data = null;
        BevelCornerMarkers.Data = null;
        if (!SelectingBevel || bevelTargets.Count == 0 || featureSource is not { } source)
            return;
        if (PolygonBevel.Create(source) is { } polygon)
        {
            DrawPolygonTargets(polygon, bevelTargets, BevelEdgeMarkers, BevelCornerMarkers);
            return;
        }
        if (RoundBevel.Surface(source) is { } round)
        {
            var rims = new GeometryGroup();
            foreach (var rim in bevelTargets)
                rims.Children.Add(RoundRimOutline(source, round, rim));
            BevelEdgeMarkers.Data = rims;
            return;
        }

        var mesh = BevelPreviewMesh();
        if (mesh is null)
            return;

        var selected = new HashSet<BoxBevelTarget>(bevelTargets);
        foreach (var face in BoxBevelTarget.Faces)
        {
            if (BoxBevelTarget.Edges.Where(edge => edge.OnFace(face)).All(bevelTargets.Contains))
                selected.Add(face);
        }
        var matches = new Dictionary<BoxBevelTarget, List<Triangle>>();
        foreach (var triangle in mesh)
        {
            var normal = BevelFacetNormal(triangle);
            var target = BoxBevelTarget.FromNormal(normal);
            if (!selected.Contains(target) || Vector3D.DotProduct(Camera.Position - triangle.A, normal) <= 0)
                continue;
            if (!matches.TryGetValue(target, out var facets))
                matches[target] = facets = [];
            facets.Add(triangle);
        }

        var edges = new GeometryGroup();
        var corners = new GeometryGroup();
        var box = source.BevelBox()!;
        var half = new Vector3D(box.Width / 2, box.Depth / 2, box.Height / 2);
        var center = new Point3D(box.X, box.Y, box.Z + half.Z);
        var view = Camera.Position - center;
        foreach (var target in selected)
        {
            if (matches.TryGetValue(target, out var facets))
            {
                if (target.AxisCount == 3)
                {
                    var vertices = facets.SelectMany(facet => new[] { facet.A, facet.B, facet.C }).Distinct().ToArray();
                    var dot = new Point3D(vertices.Average(point => point.X), vertices.Average(point => point.Y),
                        vertices.Average(point => point.Z));
                    AddCorner(dot);
                }
                else
                    AddBevelOutline(facets, edges);
            }
            else if (target.X * view.X > half.X || target.Y * view.Y > half.Y || target.Z * view.Z > half.Z)
            {
                if (target.AxisCount == 3)
                {
                    var corner = World(target);
                    var nearest = mesh.SelectMany(facet => new[] { facet.A, facet.B, facet.C })
                        .MinBy(point => (point - corner).LengthSquared);
                    AddCorner(nearest);
                }
                else if (target.AxisCount == 2)
                {
                    var (start, end) = target.EdgeEnds();
                    AddBevelLine(edges, World(start), World(end));
                }
            }
        }
        BevelEdgeMarkers.Data = edges.Children.Count > 0 ? edges : null;
        BevelCornerMarkers.Data = corners.Children.Count > 0 ? corners : null;

        Point3D World(BoxBevelTarget target) => center + new Vector3D(target.X * half.X, target.Y * half.Y, target.Z * half.Z);

        void AddCorner(Point3D point)
        {
            if (ProjectViewportPoint(point) is Point screen)
                corners.Children.Add(new EllipseGeometry(screen, 5, 5));
        }
    }

    private void AddBevelOutline(IEnumerable<Triangle> facets, GeometryGroup geometry)
    {
        var border = new HashSet<(Point3D Start, Point3D End)>();
        foreach (var (a, b, c) in facets)
        {
            Add(a, b);
            Add(b, c);
            Add(c, a);
        }
        foreach (var (start, end) in border)
            AddBevelLine(geometry, start, end);

        void Add(Point3D start, Point3D end)
        {
            if (!border.Remove((end, start)))
                border.Add((start, end));
        }
    }

    private void AddBevelLine(GeometryGroup geometry, Point3D start, Point3D end)
    {
        if (ProjectViewportPoint(start) is Point a && ProjectViewportPoint(end) is Point b)
            geometry.Children.Add(new LineGeometry(a, b));
    }

    private void UpdateFeatureArrow()
    {
        FeatureArrow.Data = null;
        FeatureArrowOutline.Data = null;
        if (featureChange is not { ArrowStart: Point3D origin } change)
            return;

        double length = Math.Max(change.ArrowLength, Math.Max(bed.X, bed.Y) * 0.07);
        if (ProjectViewportPoint(origin) is not Point start || ProjectViewportPoint(origin + change.ArrowDirection * length) is not Point end)
            return;

        var arrow = new GeometryGroup();
        var direction = end - start;
        if (direction.Length < 5)
        {
            arrow.Children.Add(new EllipseGeometry(start, 8, 8));
            if (Vector3D.DotProduct(Camera.Position - origin, change.ArrowDirection) > 0)
                arrow.Children.Add(new EllipseGeometry(start, 1, 1));
            else
            {
                arrow.Children.Add(new LineGeometry(start + new Vector(-3, -3), start + new Vector(3, 3)));
                arrow.Children.Add(new LineGeometry(start + new Vector(-3, 3), start + new Vector(3, -3)));
            }
        }
        else
        {
            double pixels = Math.Max(24, direction.Length);
            direction.Normalize();
            end = start + direction * pixels;
            var side = new Vector(-direction.Y, direction.X);
            arrow.Children.Add(new LineGeometry(start, end));
            arrow.Children.Add(new LineGeometry(end, end - direction * 11 + side * 5));
            arrow.Children.Add(new LineGeometry(end, end - direction * 11 - side * 5));
        }
        FeatureArrow.Stroke = new SolidColorBrush(featureMove ? Color.FromRgb(89, 210, 243) :
            change.Removes || SelectingBevel ? Color.FromRgb(255, 103, 111) : Color.FromRgb(92, 244, 150));
        FeatureArrowOutline.Data = arrow;
        FeatureArrow.Data = arrow;
    }

    private bool IsDraggableFeatureArrow(Point position) =>
        (SelectingBevel || featureExtrusion is not null || featureSource?.Kind == PartKind.Sketch) &&
        FeatureArrow.Data?.StrokeContains(new Pen(Brushes.White, 20), position) == true;

    private bool BeginFeatureArrowDrag(Point position)
    {
        if (!IsDraggableFeatureArrow(position) || featureChange is not { ArrowStart: Point3D origin } change)
            return false;

        bool inward = featureExtrusion is not null && change.Removes ||
            featureSource?.Kind == PartKind.Sketch && ReverseExtrusion.IsChecked == true;
        var direction = change.ArrowDirection * (inward ? -1 : 1);
        double factor = SelectingBevel
            ? change.ArrowLength / change.Result.BevelCuts!.First(cut => cut.Target == bevelTargets.First()).Amount : 1;
        double travel = change.ArrowLength * (inward ? -1 : 1);
        var endpoint = origin + direction * travel;
        var pixels = ProjectViewportPoint(endpoint) is Point a && ProjectViewportPoint(endpoint + direction * factor) is Point b
            ? b - a : default;
        if (pixels.Length < 0.15)
        {
            double scale = ModelViewport.ActualWidth / (2 * Math.Tan(Camera.FieldOfView * Math.PI / 360) *
                Math.Max(1, (Camera.Position - endpoint).Length));
            pixels = new Vector(0, -scale * factor);
        }
        if (!OrbitSurface.CaptureMouse())
            return false;
        featureArrowDrag = (position, pixels, travel / factor);
        OrbitSurface.Cursor = Cursors.SizeAll;
        return true;
    }

    private void UpdateFeatureArrowDrag(Point position)
    {
        if (featureArrowDrag is not { } drag)
            return;
        double distance = drag.SignedDistance +
            Vector.Multiply(position - drag.Start, drag.PixelsPerMillimeter) / drag.PixelsPerMillimeter.LengthSquared;
        bool merge = MergeMaterial.IsChecked == true;
        bool remove = featureExtrusion is not null && !merge && distance < 0;
        double amount = Math.Clamp(SelectingBevel || merge && featureExtrusion is not null ? distance : Math.Abs(distance), 0.01, 10000);
        double maximum = 10000;
        if (featureExtrusion is { ProfileEdge: < 0 } face)
        {
            double size = face.Axis == 0 ? face.Source.Width : face.Axis == 1 ? face.Source.Depth : face.Source.Height;
            maximum = remove ? 10000 : 10000 - size;
        }
        else if (SelectingBevel)
            maximum = BevelDragMaximum(featureSource!) - 0.0001;
        if (SelectingBevel)
            amount = Math.Clamp(distance, 0.01, Math.Max(0.01, maximum));
        else
        {
            double step = units == DisplayUnits.Inches ? 2.54 : 1;
            double steps = Math.Floor(maximum / step);
            if (steps < 1)
                return;
            amount = step * Math.Clamp(Math.Round(amount / step, MidpointRounding.AwayFromZero), 1, steps);
        }
        loadingFeatureInputs = true;
        if (featureExtrusion is not null && !merge)
        {
            AddMaterial.IsChecked = !remove;
            RemoveMaterial.IsChecked = remove;
        }
        if (featureSource?.Kind == PartKind.Sketch)
            ReverseExtrusion.IsChecked = distance < 0;
        FeatureAmount.Text = units.Format(Math.Round(amount, 4));
        loadingFeatureInputs = false;
        UpdateBuildPreview();
    }

    private void EndFeatureArrowDrag()
    {
        if (featureArrowDrag is null)
            return;
        featureArrowDrag = null;
        OrbitSurface.ReleaseMouseCapture();
        OrbitSurface.Cursor = null;
    }

    private ViewportProjection BuildProjection => new(Camera, ModelViewport.ActualWidth, ModelViewport.ActualHeight);

    private Point? ProjectViewportPoint(Point3D point) => BuildProjection.Project(point);
}


