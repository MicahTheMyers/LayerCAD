using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace LayerCAD;

internal sealed class ModelingDialog : Window
{
    private readonly StackPanel fields = new();
    private readonly TextBlock feedback = new() { TextWrapping = TextWrapping.Wrap, Foreground = Brushes.Salmon };
    private readonly DisplayUnits units;

    public ModelingDialog(Window owner, string title, DisplayUnits units)
    {
        Owner = owner;
        Title = title;
        this.units = units;
        Width = 380;
        SizeToContent = SizeToContent.Height;
        MaxHeight = SystemParameters.WorkArea.Height;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = new SolidColorBrush(Color.FromRgb(32, 40, 56));
        Foreground = Brushes.WhiteSmoke;
        Content = new ScrollViewer { Content = fields, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Padding = new Thickness(16) };
    }

    public void Hint(string text) => fields.Children.Add(new TextBlock
    {
        Text = text, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 12)
    });

    public Func<double> Length(string label, double initial, bool signed = false) =>
        Number($"{label} ({units.Symbol()})", units.Format(initial), value => units.Read(value, initial),
            signed ? Part.ValidOffset : Part.ValidDimension);

    public Func<double> Number(string label, double initial, double min, double max, bool whole = false) =>
        Number(label, initial.ToString(System.Globalization.CultureInfo.CurrentCulture),
            text => double.TryParse(text, out var value) ? value : double.NaN,
            value => value >= min && value <= max && (!whole || value == Math.Truncate(value)));

    private Func<double> Number(string label, string initial, Func<string, double> parse, Func<double, bool> valid)
    {
        var box = new TextBox { Text = initial };
        AddField(label, box);
        return () =>
        {
            double value = parse(box.Text);
            return valid(value) ? value : throw new ArgumentException($"Enter a valid value for {label}.");
        };
    }

    public Func<T> Choice<T>(string label, T[] choices, int index = 0)
    {
        var box = new ComboBox { ItemsSource = choices, SelectedIndex = choices.Length > 0 ? Math.Clamp(index, 0, choices.Length - 1) : -1 };
        AddField(label, box);
        return () => box.SelectedItem is T selected ? selected : throw new ArgumentException($"Choose {label.ToLowerInvariant()}.");
    }

    public Func<T[]> MultiChoice<T>(string label, T[] choices)
    {
        var list = new ListBox { ItemsSource = choices, SelectionMode = SelectionMode.Extended, Height = 130 };
        AddField(label, list);
        return () => list.SelectedItems.Cast<T>().ToArray();
    }

    private void AddField(string label, Control control)
    {
        fields.Children.Add(new Label { Content = label, Target = control, Foreground = Foreground, Padding = new Thickness(0) });
        control.Margin = new Thickness(0, 3, 0, 10);
        fields.Children.Add(control);
    }

    public Part[]? Run(Func<Part[]> create) => RunValue(() =>
    {
        var items = create();
        if (items.Length == 0 || items.Any(part => !part.IsValid()))
            throw new ArgumentException("The inputs do not produce a valid solid. Check the geometry and dimensions.");
        return items;
    }, "Create");

    public T? RunValue<T>(Func<T> create, string action) where T : class
    {
        T? result = null;
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        var apply = new Button { Content = action, IsDefault = true };
        apply.Click += (_, _) =>
        {
            try
            {
                result = create();
                DialogResult = true;
            }
            catch (ArgumentException error)
            {
                feedback.Text = error.Message;
            }
            catch (Exception error) when (error is DllNotFoundException or BadImageFormatException or EntryPointNotFoundException)
            {
                feedback.Text = "The solid geometry engine is unavailable in this build.";
            }
        };
        buttons.Children.Add(new Button { Content = "Cancel", IsCancel = true });
        buttons.Children.Add(apply);
        fields.Children.Add(feedback);
        fields.Children.Add(buttons);
        return ShowDialog() == true ? result : null;
    }
}
