namespace Pfr.Core.Query;

/// <summary>What kind of value a report parameter takes.</summary>
public enum ParameterType
{
    /// <summary>Free text (a network, a zone name...).</summary>
    Text,

    /// <summary>A whole or decimal number; passed to SQL as a number, so it can be compared and used in LIMIT.</summary>
    Number,

    /// <summary>One of a fixed list of options.</summary>
    Choice,
}

/// <summary>
/// A value a report asks for each time it runs. The SQL refers to it as <c>:name</c>; the value is always bound as
/// data, never pasted into the SQL text.
/// </summary>
public sealed class ReportParameter
{
    /// <summary>The name used in the SQL (<c>:name</c>): letters, digits and underscores, starting with a letter.</summary>
    public string Name { get; set; } = "";

    /// <summary>What the person sees; the name is used when this is empty.</summary>
    public string Label { get; set; } = "";

    public string Description { get; set; } = "";

    public ParameterType Type { get; set; } = ParameterType.Text;

    /// <summary>The value the form starts with (and the CLI uses when none is given).</summary>
    public string Default { get; set; } = "";

    /// <summary>If true a value must be given; if false an empty value is passed to the SQL as NULL.</summary>
    public bool Required { get; set; } = true;

    /// <summary>The allowed values of a <see cref="ParameterType.Choice"/>.</summary>
    public List<string> Options { get; set; } = [];

    public string DisplayLabel => Label.Length > 0 ? Label : Name;

    public ReportParameter Clone() => new()
    {
        Name = Name,
        Label = Label,
        Description = Description,
        Type = Type,
        Default = Default,
        Required = Required,
        Options = [.. Options],
    };
}
