namespace Pfr.Core.Query;

/// <summary>
/// A saved query that can be run as a report on any grid: a name, a description and read-only SQL.
/// Stored as a plain JSON file (see <see cref="ReportTemplateStore"/>), so templates are easy to share and edit.
/// </summary>
public sealed class ReportTemplate
{
    /// <summary>The file name without ".json"; empty until the template has been saved.</summary>
    public string Id { get; set; } = "";

    public string Name { get; set; } = "";

    public string Description { get; set; } = "";

    public string Sql { get; set; } = "";

    /// <summary>
    /// The values the report asks for when it runs. Every <c>:name</c> in the SQL is declared here, and every
    /// declared name is used in the SQL.
    /// </summary>
    public List<ReportParameter> Parameters { get; set; } = [];

    public DateTimeOffset CreatedUtc { get; set; } = DateTimeOffset.UtcNow;

    public DateTimeOffset UpdatedUtc { get; set; } = DateTimeOffset.UtcNow;

    public override string ToString() => Name;
}
