using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Pfr.Core.Query;
using Pfr.Gui.Controls;

namespace Pfr.Gui.Views;

/// <summary>
/// Describes one parameter when a query is saved as a report: the label people see, whether it is text, a number or a
/// choice (and the options), whether a value is required, an optional description, and the default the form starts with.
/// </summary>
public sealed class ParameterDefinitionEditor : UserControl
{
    private readonly string _name;
    private readonly TextBox _label = new() { Width = 220, Watermark = "Label" };
    private readonly ComboBox _type = new() { ItemsSource = Enum.GetNames<ParameterType>(), Width = 110 };
    private readonly CheckBox _required = new() { Content = "Required", VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBox _description = new() { Width = 440, Watermark = "Description (optional)", HorizontalAlignment = HorizontalAlignment.Left };
    private readonly TextBox _default = new() { Width = 220, Watermark = "Default value" };
    private readonly TextBox _options = new() { Width = 440, Watermark = "Options, separated by commas", HorizontalAlignment = HorizontalAlignment.Left };

    public ParameterDefinitionEditor(ReportParameter definition)
    {
        _name = definition.Name;
        _label.Text = definition.Label.Length > 0 ? definition.Label : definition.Name;
        _type.SelectedItem = definition.Type.ToString();
        _required.IsChecked = definition.Required;
        _description.Text = definition.Description;
        _default.Text = definition.Default;
        _options.Text = string.Join(", ", definition.Options);
        _options.IsVisible = definition.Type == ParameterType.Choice;
        _type.SelectionChanged += (_, _) => _options.IsVisible = SelectedType == ParameterType.Choice;

        Content = new Border
        {
            BorderBrush = Ui.Divider,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(10, 6),
            Child = new StackPanel
            {
                Spacing = 4,
                Children =
                {
                    new TextBlock { Text = ":" + _name, FontWeight = FontWeight.SemiBold },
                    Ui.Row(_label, _type, _required),
                    _description,
                    Ui.Row(new TextBlock { Text = "Starts as", VerticalAlignment = VerticalAlignment.Center, Opacity = 0.7 }, _default),
                    _options,
                },
            },
        };
    }

    /// <summary>The parameter's name as used in the SQL (:name).</summary>
    public string ParameterName => _name;

    public ParameterType SelectedType =>
        Enum.TryParse<ParameterType>(_type.SelectedItem as string, out var type) ? type : ParameterType.Text;

    /// <summary>The parameter as described here. It is checked when the report is saved.</summary>
    public ReportParameter ToDefinition() => new()
    {
        Name = _name,
        Label = (_label.Text ?? "").Trim(),
        Description = (_description.Text ?? "").Trim(),
        Type = SelectedType,
        Default = (_default.Text ?? "").Trim(),
        Required = _required.IsChecked == true,
        Options = SelectedType == ParameterType.Choice
            ? (_options.Text ?? "").Split([',', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList()
            : [],
    };
}
