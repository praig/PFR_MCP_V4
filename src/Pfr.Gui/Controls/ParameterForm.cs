using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Pfr.Core.Query;

namespace Pfr.Gui.Controls;

/// <summary>
/// The input boxes for a query's or report's parameters: a text box for text and numbers, a drop-down for a choice,
/// with a label (starred when a value is required) and the description underneath. Nothing is validated here; the
/// values go through <see cref="QueryParameters.Bind"/> when the query runs.
/// </summary>
public sealed class ParameterForm : UserControl
{
    private readonly StackPanel _rows = new() { Spacing = 8 };
    private readonly Dictionary<string, Control> _inputs = new(StringComparer.Ordinal);
    private IReadOnlyList<ReportParameter> _definitions = [];
    private string _signature = "";

    public ParameterForm()
    {
        Content = _rows;
        IsVisible = false;
    }

    /// <summary>Raised when a value is typed or chosen.</summary>
    public event EventHandler? ValuesChanged;

    public IReadOnlyList<ReportParameter> Definitions => _definitions;

    public bool HasParameters => _definitions.Count > 0;

    /// <summary>The value of every parameter as typed or chosen (empty text when blank).</summary>
    public IReadOnlyDictionary<string, string> Values =>
        _definitions.ToDictionary(d => d.Name, d => GetValue(d.Name), StringComparer.Ordinal);

    /// <summary>
    /// Shows these parameters. If they are the same fields as already shown, nothing is rebuilt, so what was typed
    /// (and the cursor) stays. Otherwise the form is rebuilt, keeping what was typed for names that are still there
    /// and starting new ones from their default.
    /// </summary>
    public void SetParameters(IReadOnlyList<ReportParameter> definitions)
    {
        var signature = string.Join(";", definitions.Select(d =>
            $"{d.Name}|{d.Label}|{d.Description}|{d.Type}|{d.Required}|{string.Join(",", d.Options)}"));
        if (signature == _signature) return;

        var typed = Values;
        _definitions = definitions.Select(d => d.Clone()).ToList();
        _signature = signature;
        _inputs.Clear();
        _rows.Children.Clear();

        foreach (var definition in _definitions)
        {
            var row = new StackPanel { Spacing = 2 };
            row.Children.Add(new TextBlock
            {
                Text = definition.DisplayLabel + (definition.Required ? " *" : ""),
                FontWeight = FontWeight.SemiBold,
            });

            Control input;
            if (definition.Type == ParameterType.Choice)
            {
                var items = definition.Required ? definition.Options.ToList() : new[] { "" }.Concat(definition.Options).ToList();
                var box = new ComboBox { ItemsSource = items, Width = 320, HorizontalAlignment = HorizontalAlignment.Left };
                box.SelectionChanged += (_, _) => ValuesChanged?.Invoke(this, EventArgs.Empty);
                input = box;
            }
            else
            {
                var box = new TextBox
                {
                    Width = 320,
                    HorizontalAlignment = HorizontalAlignment.Left,
                    Watermark = definition.Type == ParameterType.Number ? "A number" : "",
                };
                box.TextChanged += (_, _) => ValuesChanged?.Invoke(this, EventArgs.Empty);
                input = box;
            }
            _inputs[definition.Name] = input;
            row.Children.Add(input);
            if (definition.Description.Length > 0) row.Children.Add(Ui.Muted(definition.Description));
            _rows.Children.Add(row);

            var start = typed.TryGetValue(definition.Name, out var previous) && previous.Length > 0 ? previous : definition.Default;
            if (start.Length == 0 && definition.Type == ParameterType.Choice && definition.Required && definition.Options.Count > 0)
                start = definition.Options[0];
            SetValue(definition.Name, start);
        }
        IsVisible = _definitions.Count > 0;
    }

    public string GetValue(string name) =>
        !_inputs.TryGetValue(name, out var input)
            ? ""
            : input switch
            {
                TextBox text => text.Text ?? "",
                ComboBox box => box.SelectedItem as string ?? "",
                _ => "",
            };

    /// <summary>Puts a value in a parameter's box (a choice takes the matching option, ignoring case).</summary>
    public void SetValue(string name, string value)
    {
        if (!_inputs.TryGetValue(name, out var input)) return;
        switch (input)
        {
            case TextBox text:
                text.Text = value;
                break;
            case ComboBox box:
                box.SelectedItem = (box.ItemsSource as IEnumerable<string>)?
                    .FirstOrDefault(option => string.Equals(option, value, StringComparison.OrdinalIgnoreCase));
                break;
        }
    }

    public void SetValues(IReadOnlyDictionary<string, string> values)
    {
        foreach (var (name, value) in values) SetValue(name, value);
    }
}
