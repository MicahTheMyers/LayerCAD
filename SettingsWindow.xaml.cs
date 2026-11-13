using System.Reflection;
using System.Windows;
using System.Windows.Controls;

namespace LayerCAD;

public partial class SettingsWindow : Window
{
    private Bed displayedBed;

    public DisplayUnits Units { get; private set; }
    public Bed BuildVolume { get; private set; }

    public SettingsWindow(Bed bed, DisplayUnits units)
    {
        Units = units;
        BuildVolume = displayedBed = bed;
        InitializeComponent();
        var version = typeof(SettingsWindow).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        BuildVersion.Text = $"LayerCAD {version?.Split('+')[0]}";
        if (units == DisplayUnits.Inches)
            Imperial.IsChecked = true;
        else
            Metric.IsChecked = true;
        DisplayBed();
    }

    private void ChangeUnits(object sender, RoutedEventArgs e)
    {
        var selected = ReferenceEquals(sender, Imperial) ? DisplayUnits.Inches : DisplayUnits.Millimeters;
        if (Units == selected)
            return;

        displayedBed = ReadBed();
        Units = selected;
        DisplayBed();
        Feedback.Visibility = Visibility.Collapsed;
    }

    private Bed ReadBed() => new(
        Units.Read(WidthInput.Text, displayedBed.X),
        Units.Read(DepthInput.Text, displayedBed.Y),
        Units.Read(HeightInput.Text, displayedBed.Z));

    private void DisplayBed()
    {
        VolumeHeading.Text = $"Build volume ({Units.Symbol()})";
        Display(WidthInput, displayedBed.X);
        Display(DepthInput, displayedBed.Y);
        Display(HeightInput, displayedBed.Z);

        void Display(TextBox input, double millimeters)
        {
            if (double.IsFinite(millimeters))
                input.Text = Units.Format(millimeters);
        }
    }

    private void Apply(object sender, RoutedEventArgs e)
    {
        var bed = ReadBed();
        if (!bed.IsValid())
        {
            Feedback.Text = $"Enter dimensions between {Units.Format(0.01)} and {Units.Format(10000)} {Units.Symbol()}.";
            Feedback.Visibility = Visibility.Visible;
            return;
        }

        BuildVolume = bed;
        DialogResult = true;
    }
}
