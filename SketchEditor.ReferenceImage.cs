using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Media.Media3D;
using Microsoft.Win32;

namespace LayerCAD;

public sealed record SketchReferenceImage(byte[] Png, double Width, double Height, double X, double Y, double Opacity)
{
    internal bool IsValid() => Png is { Length: > 0 and <= 8_000_000 } &&
        Part.ValidDimension(Width) && Part.ValidDimension(Height) &&
        Part.ValidOffset(X) && Part.ValidOffset(Y) && Opacity is >= 0 and <= 1;
}

public partial class SketchEditor
{
    internal SketchReferenceImage? ReferenceImage { get; private set; }
    private bool calibratingImage;
    private SketchPoint? calibrationStart;

    private void OpenReferenceImageMenu(object sender, RoutedEventArgs e)
    {
        var menu = new ContextMenu { PlacementTarget = (Button)sender, Placement = PlacementMode.Bottom };
        MainWindow.AddContextCommand(menu, "Import or replace…", ImportReferenceImage);
        MainWindow.AddContextCommand(menu, "Size, position, and opacity…", (_, _) => ConfigureReferenceImage(), ReferenceImage is not null);
        MainWindow.AddContextCommand(menu, "Calibrate from two points", (_, _) =>
        {
            EndSketchMove(commit: false);
            EnterSketchSelection();
            calibrationStart = null;
            calibratingImage = true;
            Feedback.Text = "Click two points on the image with a known distance.";
        }, ReferenceImage is not null);
        MainWindow.AddContextCommand(menu, "Remove image", (_, _) => SetReferenceImage(null), ReferenceImage is not null);
        menu.IsOpen = true;
    }

    private void ImportReferenceImage(object sender, RoutedEventArgs e)
    {
        var picker = new OpenFileDialog { Filter = "Images (*.png;*.jpg;*.jpeg)|*.png;*.jpg;*.jpeg" };
        if (picker.ShowDialog(Window.GetWindow(this)) != true)
            return;
        try
        {
            using var stream = File.OpenRead(picker.FileName);
            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.DecodePixelWidth = 1600;
            bitmap.StreamSource = stream;
            bitmap.EndInit();
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using var png = new MemoryStream();
            encoder.Save(png);
            double aspect = (double)bitmap.PixelWidth / bitmap.PixelHeight;
            var image = new SketchReferenceImage(png.ToArray(), 100 * Math.Min(1, aspect),
                100 / Math.Max(1, aspect), 0, 0, 0.55);
            if (!image.IsValid())
                throw new InvalidDataException();
            SetReferenceImage(image);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or NotSupportedException or FormatException or ArgumentException)
        {
            Feedback.Text = "Could not load that reference image. Choose a valid PNG or JPEG.";
        }
    }

    private void ConfigureReferenceImage()
    {
        if (ReferenceImage is not { } image)
            return;
        var dialog = new ModelingDialog(Window.GetWindow(this), "Reference Image", units);
        var width = dialog.Length("Width", image.Width);
        var x = dialog.Length("Center X", image.X, signed: true);
        var y = dialog.Length("Center Y", image.Y, signed: true);
        var opacity = dialog.Number("Opacity (%)", image.Opacity * 100, 0, 100);
        var updated = dialog.RunValue(() =>
        {
            var result = image with { Width = width(), Height = image.Height * width() / image.Width,
                X = x(), Y = y(), Opacity = opacity() / 100 };
            return result.IsValid() ? result : throw new ArgumentException("The image size or position is outside the supported range.");
        }, "Apply");
        if (updated is not null)
            SetReferenceImage(updated);
    }

    private bool PickImageCalibrationPoint(Point pointer)
    {
        if (!calibratingImage || ReferenceImage is not { } image)
            return false;
        var point = SketchPointAt(pointer);
        if (Math.Abs(point.X - image.X) > image.Width / 2 || Math.Abs(point.Y - image.Y) > image.Height / 2 ||
            !double.IsFinite(point.X) || !double.IsFinite(point.Y))
        {
            Feedback.Text = "Pick a point inside the reference image.";
            return true;
        }
        if (calibrationStart is not SketchPoint first)
        {
            calibrationStart = point;
            Feedback.Text = "Click the second calibration point.";
            return true;
        }
        double measured = Difference(first, point).Length;
        if (measured < 0.01)
            return true;
        calibratingImage = false;
        calibrationStart = null;
        var dialog = new ModelingDialog(Window.GetWindow(this), "Calibrate Reference Image", units);
        var length = dialog.Length("Actual distance", measured);
        var updated = dialog.RunValue(() =>
        {
            double scale = length() / measured;
            var result = image with { Width = image.Width * scale, Height = image.Height * scale,
                X = first.X + (image.X - first.X) * scale, Y = first.Y + (image.Y - first.Y) * scale };
            return result.IsValid() ? result : throw new ArgumentException("The calibrated image is outside the supported range.");
        }, "Calibrate");
        if (updated is not null)
            SetReferenceImage(updated);
        return true;
    }

    internal void SetReferenceImage(SketchReferenceImage? image)
    {
        calibratingImage = false;
        calibrationStart = null;
        ReferenceImage = null;
        ReferenceImageModel.Content = null;
        if (image is null)
            return;
        try
        {
            using var stream = new MemoryStream(image.Png);
            var bitmap = BitmapFrame.Create(stream, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
            var brush = new ImageBrush(bitmap) { Opacity = image.Opacity };
            double left = image.X - image.Width / 2, right = image.X + image.Width / 2;
            double bottom = image.Y - image.Height / 2, top = image.Y + image.Height / 2;
            var mesh = new MeshGeometry3D
            {
                Positions = new Point3DCollection { WorldPoint(new(left, bottom)), WorldPoint(new(right, bottom)),
                    WorldPoint(new(right, top)), WorldPoint(new(left, top)) },
                TextureCoordinates = new PointCollection { new(0, 1), new(1, 1), new(1, 0), new(0, 0) },
                TriangleIndices = new Int32Collection { 0, 1, 2, 0, 2, 3 }
            };
            var material = new EmissiveMaterial(brush);
            var model = new GeometryModel3D(mesh, material) { BackMaterial = material };
            model.Freeze();
            ReferenceImageModel.Content = model;
            ReferenceImage = image;
        }
        catch (Exception error) when (error is IOException or FormatException or NotSupportedException or ArgumentException)
        {
            Feedback.Text = "The embedded reference image could not be displayed.";
        }
    }
}
