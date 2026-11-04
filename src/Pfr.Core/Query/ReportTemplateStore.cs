using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using Pfr.Core.Reports;

namespace Pfr.Core.Query;

/// <summary>
/// Report templates kept as plain JSON files in a folder (one file per template), so they can be shared between
/// engineers, kept in version control and edited in a text editor:
/// <code>
/// {
///   "format": 1,
///   "name": "A records in a network",
///   "description": "Every A record inside the network you give.",
///   "created": "2026-10-28T10:15:00+00:00",
///   "updated": "2026-10-28T10:15:00+00:00",
///   "parameters": [
///     { "name": "network", "label": "Network", "type": "text", "default": "10.0.0.0/8" }
///   ],
///   "sql": [
///     "SELECT fqdn, value FROM records",
///     "WHERE type = 'A' AND cidr_contains(:network, value)"
///   ]
/// }
/// </code>
/// Only read-only queries are accepted, both when saving and when reading a file someone else wrote: a file whose
/// SQL isn't a single SELECT-type statement is skipped and reported, never run. Files that can't be read are
/// reported too and never stop the rest from loading. Parameters are checked against the SQL: every <c>:name</c>
/// it uses must be declared, and every declared parameter must be used.
/// </summary>
public sealed class ReportTemplateStore(string folder)
{
    public const int CurrentFormat = 1;
    public const int MaxNameLength = 100;
    public const int MaxParameters = 20;
    public const int MaxOptions = 50;

    /// <summary>Names of the app's own reports; a template can't use them, so the Reports list stays unambiguous.</summary>
    public static readonly IReadOnlyList<string> ReservedNames = [LicenseExpiryReport.Title, DanglingCnameReport.Title, "Explore data"];

    // Relaxed escaping keeps quotes, <, > and non-ASCII text readable in the file.
    private static readonly JsonWriterOptions WriteOptions = new()
    {
        Indented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public string Folder { get; } = Path.GetFullPath(folder);

    /// <summary>
    /// All templates by name. A file that can't be used is left out and described in <paramref name="problems"/>.
    /// </summary>
    public IReadOnlyList<ReportTemplate> List(List<string>? problems = null)
    {
        if (!Directory.Exists(Folder)) return [];

        var templates = new List<ReportTemplate>();
        foreach (var path in Directory.EnumerateFiles(Folder, "*.json").Order(StringComparer.OrdinalIgnoreCase))
        {
            var file = Path.GetFileName(path);
            if (file.StartsWith('.')) continue;

            try
            {
                templates.Add(Parse(Path.GetFileNameWithoutExtension(path), File.ReadAllText(path)));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException
                                           or InvalidDataException or QueryException)
            {
                problems?.Add($"{file}: {ex.Message}");
            }
        }
        return templates.OrderBy(t => t.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>The template with this name (ignoring case) or, failing that, this id.</summary>
    public ReportTemplate? Find(string nameOrId)
    {
        var key = nameOrId.Trim();
        var all = List();
        return all.FirstOrDefault(t => string.Equals(t.Name, key, StringComparison.OrdinalIgnoreCase))
               ?? all.FirstOrDefault(t => string.Equals(t.Id, key, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Saves a new template (empty <see cref="ReportTemplate.Id"/>) or changes an existing one. Renaming moves the
    /// file so its name keeps matching. Returns the template as saved.
    /// </summary>
    /// <exception cref="ArgumentException">No name, a name that is too long, in use or reserved, or a problem with the parameters.</exception>
    /// <exception cref="QueryException">The SQL is not a single read-only statement.</exception>
    public ReportTemplate Save(ReportTemplate template)
    {
        var name = template.Name.Trim();
        if (name.Length == 0) throw new ArgumentException("A report template needs a name.");
        if (name.Length > MaxNameLength) throw new ArgumentException($"A report name can be at most {MaxNameLength} characters.");
        if (ReservedNames.Any(r => string.Equals(r, name, StringComparison.OrdinalIgnoreCase)))
            throw new ArgumentException($"\"{name}\" is the name of a built-in report. Choose another name.");
        var sql = SqlGuard.Check(template.Sql);
        var parameters = NormalizeParameters(template.Parameters);
        ValidateParameters(parameters, sql);

        var existing = List();
        var previous = template.Id.Length > 0 ? existing.FirstOrDefault(t => t.Id == template.Id) : null;
        var clash = existing.FirstOrDefault(t => t.Id != previous?.Id && string.Equals(t.Name, name, StringComparison.OrdinalIgnoreCase));
        if (clash is not null) throw new ArgumentException($"A report template named \"{clash.Name}\" already exists.");

        var id = previous is not null && string.Equals(previous.Name, name, StringComparison.Ordinal)
            ? previous.Id
            : UniqueId(Slug(name), existing.Select(t => t.Id).Where(i => i != previous?.Id), previous?.Id);

        var saved = new ReportTemplate
        {
            Id = id,
            Name = name,
            Description = template.Description.Trim(),
            Sql = sql,
            Parameters = parameters,
            CreatedUtc = previous?.CreatedUtc ?? template.CreatedUtc,
            UpdatedUtc = DateTimeOffset.UtcNow,
        };

        Directory.CreateDirectory(Folder);
        Write(Path.Combine(Folder, id + ".json"), saved);
        if (previous is not null && !string.Equals(previous.Id, id, StringComparison.OrdinalIgnoreCase))
            File.Delete(Path.Combine(Folder, previous.Id + ".json"));
        return saved;
    }

    /// <summary>Deletes a template's file. Deleting one that isn't there does nothing.</summary>
    public void Delete(string id)
    {
        if (id.Length == 0 || id is "." or ".." || id.Contains('/') || id.Contains('\\')
            || id.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            throw new ArgumentException("That is not a report template id.");

        var path = Path.Combine(Folder, id + ".json");
        if (File.Exists(path)) File.Delete(path);
    }

    /// <summary>"Networks with no DHCP range" becomes "networks-with-no-dhcp-range" (the file name).</summary>
    public static string Slug(string name)
    {
        var slug = new StringBuilder();
        foreach (var c in name.Trim().ToLowerInvariant())
        {
            if (char.IsAsciiLetterOrDigit(c)) slug.Append(c);
            else if (slug.Length > 0 && slug[^1] != '-') slug.Append('-');
        }

        var text = slug.ToString().Trim('-');
        if (text.Length > 60) text = text[..60].Trim('-');
        return text.Length == 0 ? "report" : text;
    }

    private string UniqueId(string slug, IEnumerable<string> taken, string? own)
    {
        var used = new HashSet<string>(taken, StringComparer.OrdinalIgnoreCase);
        var id = slug;
        for (var n = 2; used.Contains(id) || (!string.Equals(id, own, StringComparison.OrdinalIgnoreCase)
                                              && File.Exists(Path.Combine(Folder, id + ".json"))); n++)
        {
            id = $"{slug}-{n}";
        }
        return id;
    }

    // ---- Parameters ---------------------------------------------------------------------

    private static List<ReportParameter> NormalizeParameters(IEnumerable<ReportParameter> parameters) =>
        parameters.Select(p =>
        {
            var copy = p.Clone();
            copy.Name = copy.Name.Trim();
            copy.Label = copy.Label.Trim();
            copy.Description = copy.Description.Trim();
            copy.Default = copy.Default.Trim();
            copy.Options = copy.Options.Select(o => o.Trim()).Where(o => o.Length > 0)
                .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            return copy;
        }).ToList();

    /// <summary>The first problem with the parameters, or with how the SQL uses them.</summary>
    /// <exception cref="ArgumentException">Something is wrong; the message says what.</exception>
    /// <exception cref="QueryException">The SQL uses a positional ? instead of a name.</exception>
    private static void ValidateParameters(IReadOnlyList<ReportParameter> parameters, string sql)
    {
        if (parameters.Count > MaxParameters) throw new ArgumentException($"A report can have at most {MaxParameters} parameters.");

        var declared = new HashSet<string>(StringComparer.Ordinal);
        foreach (var p in parameters)
        {
            if (!QueryParameters.IsValidName(p.Name))
                throw new ArgumentException($"“{p.Name}” is not a valid parameter name: use letters, digits and underscores, starting with a letter.");
            if (!declared.Add(p.Name)) throw new ArgumentException($"The parameter “{p.Name}” is declared twice.");

            switch (p.Type)
            {
                case ParameterType.Choice:
                    if (p.Options.Count == 0)
                        throw new ArgumentException($"“{p.DisplayLabel}” is a choice, so it needs at least one option.");
                    if (p.Options.Count > MaxOptions)
                        throw new ArgumentException($"“{p.DisplayLabel}” can have at most {MaxOptions} options.");
                    if (p.Default.Length > 0 && !p.Options.Contains(p.Default, StringComparer.OrdinalIgnoreCase))
                        throw new ArgumentException($"The default of “{p.DisplayLabel}” must be one of its options.");
                    break;
                case ParameterType.Number:
                    if (p.Default.Length > 0 && !double.TryParse(p.Default, NumberStyles.Float, CultureInfo.InvariantCulture, out _))
                        throw new ArgumentException($"The default of “{p.DisplayLabel}” must be a number.");
                    break;
            }
        }

        var used = QueryParameters.FindNames(sql);
        foreach (var name in used)
        {
            if (!declared.Contains(name))
                throw new ArgumentException($"The SQL uses :{name} but the report has no parameter named “{name}”.");
        }
        foreach (var p in parameters)
        {
            if (!used.Contains(p.Name)) throw new ArgumentException($"The parameter “{p.Name}” is not used in the SQL.");
        }
    }

    private static List<ReportParameter> ParseParameters(JsonElement root)
    {
        var parameters = new List<ReportParameter>();
        if (!root.TryGetProperty("parameters", out var list)) return parameters;
        if (list.ValueKind != JsonValueKind.Array) throw new InvalidDataException("\"parameters\" must be a list");

        foreach (var item in list.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object) throw new InvalidDataException("each parameter must be an object");

            var options = new List<string>();
            if (item.TryGetProperty("options", out var array) && array.ValueKind == JsonValueKind.Array)
                options.AddRange(array.EnumerateArray().Where(o => o.ValueKind == JsonValueKind.String).Select(o => o.GetString()!));

            parameters.Add(new ReportParameter
            {
                Name = Text(item, "name"),
                Label = Text(item, "label"),
                Description = Text(item, "description"),
                Type = ParseType(Text(item, "type")),
                Default = DefaultText(item),
                Required = !item.TryGetProperty("required", out var required) || required.ValueKind != JsonValueKind.False,
                Options = options,
            });
        }
        return parameters;
    }

    private static ParameterType ParseType(string type) => type.Trim().ToLowerInvariant() switch
    {
        "" or "text" => ParameterType.Text,
        "number" => ParameterType.Number,
        "choice" => ParameterType.Choice,
        _ => throw new InvalidDataException($"unknown parameter type \"{type}\" (use text, number or choice)"),
    };

    /// <summary>A default may be written as text or, by hand, as a plain number.</summary>
    private static string DefaultText(JsonElement parameter)
    {
        if (!parameter.TryGetProperty("default", out var value)) return "";
        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString() ?? "",
            JsonValueKind.Number => value.GetRawText(),
            _ => "",
        };
    }

    // ---- Files --------------------------------------------------------------------------

    private static ReportTemplate Parse(string id, string json)
    {
        using var doc = JsonDocument.Parse(json, new JsonDocumentOptions
        {
            CommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true,
        });
        var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Object) throw new InvalidDataException("this is not a report template");

        if (root.TryGetProperty("format", out var format) && format.ValueKind == JsonValueKind.Number
                                                          && format.TryGetInt32(out var number) && number > CurrentFormat)
        {
            throw new InvalidDataException($"made by a newer version of this app (template format {number})");
        }

        var name = Text(root, "name").Trim();
        if (name.Length == 0) throw new InvalidDataException("it has no name");
        var sql = SqlText(root);
        if (string.IsNullOrWhiteSpace(sql)) throw new InvalidDataException("it has no SQL");
        sql = SqlGuard.Check(sql); // a shared file must be read-only too

        var parameters = NormalizeParameters(ParseParameters(root));
        try
        {
            ValidateParameters(parameters, sql);
        }
        catch (ArgumentException ex)
        {
            throw new InvalidDataException(ex.Message);
        }

        return new ReportTemplate
        {
            Id = id,
            Name = name,
            Description = Text(root, "description").Trim(),
            Sql = sql,
            Parameters = parameters,
            CreatedUtc = Date(root, "created"),
            UpdatedUtc = Date(root, "updated"),
        };
    }

    private static string Text(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : "";

    /// <summary>The SQL is written as an array of lines, but a single string is accepted too.</summary>
    private static string SqlText(JsonElement root)
    {
        if (!root.TryGetProperty("sql", out var sql)) return "";
        if (sql.ValueKind == JsonValueKind.String) return sql.GetString() ?? "";
        if (sql.ValueKind != JsonValueKind.Array) return "";
        return string.Join("\n", sql.EnumerateArray().Where(l => l.ValueKind == JsonValueKind.String).Select(l => l.GetString()));
    }

    private static DateTimeOffset Date(JsonElement root, string name) =>
        DateTimeOffset.TryParse(Text(root, name), out var date) ? date : DateTimeOffset.UnixEpoch;

    private static void Write(string path, ReportTemplate template)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, WriteOptions))
        {
            writer.WriteStartObject();
            writer.WriteNumber("format", CurrentFormat);
            writer.WriteString("name", template.Name);
            writer.WriteString("description", template.Description);
            writer.WriteString("created", template.CreatedUtc);
            writer.WriteString("updated", template.UpdatedUtc);

            if (template.Parameters.Count > 0)
            {
                writer.WriteStartArray("parameters");
                foreach (var p in template.Parameters)
                {
                    writer.WriteStartObject();
                    writer.WriteString("name", p.Name);
                    if (p.Label.Length > 0) writer.WriteString("label", p.Label);
                    if (p.Description.Length > 0) writer.WriteString("description", p.Description);
                    writer.WriteString("type", p.Type.ToString().ToLowerInvariant());
                    if (p.Default.Length > 0) writer.WriteString("default", p.Default);
                    if (!p.Required) writer.WriteBoolean("required", false);
                    if (p.Options.Count > 0)
                    {
                        writer.WriteStartArray("options");
                        foreach (var option in p.Options) writer.WriteStringValue(option);
                        writer.WriteEndArray();
                    }
                    writer.WriteEndObject();
                }
                writer.WriteEndArray();
            }

            writer.WriteStartArray("sql");
            foreach (var line in template.Sql.Replace("\r\n", "\n").Split('\n')) writer.WriteStringValue(line);
            writer.WriteEndArray();
            writer.WriteEndObject();
        }
        stream.WriteByte((byte)'\n');

        // Write beside it and move into place, so a crash never leaves half a file.
        var temp = path + ".tmp";
        File.WriteAllBytes(temp, stream.ToArray());
        File.Move(temp, path, overwrite: true);
    }
}
