using System.Globalization;
using System.Net;
using System.Text;

namespace Pfr.Core.Network;

/// <summary>An answer written the way dig writes it, or as dig's "+short" form.</summary>
public static class DigFormatter
{
    /// <param name="response">The answer.</param>
    /// <param name="shortForm">Only the data of each answer record, one per line (dig +short).</param>
    /// <param name="when">The time shown for the query (now by default).</param>
    public static string Format(DnsResponse response, bool shortForm = false, DateTimeOffset? when = null)
    {
        var message = response.Message;
        if (shortForm) return string.Concat(message.Answers.Select(a => a.Data + "\n"));

        var text = new StringBuilder();
        var question = response.Question;
        text.AppendLine($"; <<>> PFR dig <<>> @{response.Server.Address} {question.Name} {DnsTypes.Name(question.Type)}"
                        + (question.Class != DnsClass.IN ? $" {DnsTypes.Name(question.Class)}" : ""));
        if (response.RetriedOverTcp) text.AppendLine(";; Truncated, retrying in TCP mode.");

        text.AppendLine($";; ->>HEADER<<- opcode: {DnsTypes.OpcodeName(message.Flags.Opcode)}, status: {response.Status}, id: {message.Id}");
        text.AppendLine($";; flags: {message.Flags.Names}; QUERY: {message.QuestionCount}, ANSWER: {message.AnswerCount}, " +
                        $"AUTHORITY: {message.AuthorityCount}, ADDITIONAL: {message.AdditionalCount}");
        text.AppendLine();

        if (message.Edns is { } edns)
        {
            text.AppendLine(";; OPT PSEUDOSECTION:");
            text.AppendLine($"; EDNS: version: {edns.Version}, flags:{(edns.DnssecOk ? " do" : "")}; udp: {edns.UdpPayload}");
            foreach (var option in edns.Options)
                text.AppendLine($"; OPT={option.Code}: {Convert.ToHexString(option.Data)}");
        }

        text.AppendLine(";; QUESTION SECTION:");
        foreach (var q in message.Questions)
            text.AppendLine($";{q.Name}\t\t{DnsTypes.Name(q.Class)}\t{DnsTypes.Name(q.Type)}");
        text.AppendLine();

        Section(text, "ANSWER", message.Answers);
        Section(text, "AUTHORITY", message.Authorities);
        Section(text, "ADDITIONAL", message.Additionals);

        text.AppendLine($";; Query time: {response.Elapsed.TotalMilliseconds:0} msec");
        text.AppendLine($";; SERVER: {response.Server.Address}#{response.Server.Port}({response.Server.Address}) ({(response.UsedTcp ? "TCP" : "UDP")})");
        text.AppendLine($";; WHEN: {(when ?? DateTimeOffset.Now).ToString("ddd MMM dd HH:mm:ss yyyy", CultureInfo.InvariantCulture)}");
        text.AppendLine($";; MSG SIZE  rcvd: {response.Size}");
        return text.ToString();
    }

    private static void Section(StringBuilder text, string title, List<DnsResourceRecord> records)
    {
        if (records.Count == 0) return;
        text.AppendLine($";; {title} SECTION:");
        foreach (var record in records) text.AppendLine(record.ToString());
        text.AppendLine();
    }
}

/// <summary>One server's answer to the question, boiled down so it can be compared with the others.</summary>
public sealed record DnsComparisonRow(
    DnsServerChoice Server, string Status, bool Authoritative, IReadOnlyList<string> Answers,
    uint? LowestTtl, TimeSpan? Elapsed, string? Error)
{
    /// <summary>What has to be the same for two servers to agree: the status and the set of answers. TTLs and order are ignored.</summary>
    public string Signature => Error is not null ? "no answer: " + Error : Status + "\n" + string.Join("\n", Answers);
}

public sealed class DnsComparison(DnsQuestion question, IReadOnlyList<DnsComparisonRow> rows)
{
    public DnsQuestion Question { get; } = question;
    public IReadOnlyList<DnsComparisonRow> Rows { get; } = rows;

    /// <summary>Servers that gave the same answer, together.</summary>
    public IReadOnlyList<IReadOnlyList<DnsComparisonRow>> Groups =>
        Rows.GroupBy(r => r.Signature).Select(g => (IReadOnlyList<DnsComparisonRow>)g.ToList()).ToList();

    public bool AllAgree => Groups.Count <= 1;
}

/// <summary>Asks the same question of several servers and shows where they differ (spec §11, compare mode).</summary>
public static class DnsCompare
{
    public static async Task<DnsComparison> RunAsync(
        DnsQuestion question, IReadOnlyList<DnsServerChoice> servers, DnsQueryOptions template,
        IDnsTransport? transport = null, CancellationToken ct = default)
    {
        using var gate = new SemaphoreSlim(8);
        var rows = await Task.WhenAll(servers.Select(async server =>
        {
            await gate.WaitAsync(ct);
            try
            {
                return await AskAsync(question, server, template, transport, ct);
            }
            finally
            {
                gate.Release();
            }
        }));
        return new DnsComparison(question, rows);
    }

    private static async Task<DnsComparisonRow> AskAsync(
        DnsQuestion question, DnsServerChoice server, DnsQueryOptions template, IDnsTransport? transport, CancellationToken ct)
    {
        try
        {
            var response = await DnsLookup.QueryAsync(question, template with { Server = server.Address }, transport, ct);
            var answers = response.Message.Answers
                .Select(a => $"{a.Name} {DnsTypes.Name(a.Type)} {a.Data}")
                .Order(StringComparer.OrdinalIgnoreCase)
                .ToList();
            uint? lowest = response.Message.Answers.Count == 0 ? null : response.Message.Answers.Min(a => a.Ttl);
            return new DnsComparisonRow(server, response.Status, response.Message.Flags.Authoritative, answers, lowest, response.Elapsed, null);
        }
        catch (DnsQueryException ex)
        {
            return new DnsComparisonRow(server, "", false, [], null, null, ex.Message);
        }
    }

    public static string Text(DnsComparison comparison)
    {
        var text = new StringBuilder();
        var question = comparison.Question;
        text.AppendLine($"Question: {question.Name} {DnsTypes.Name(question.Class)} {DnsTypes.Name(question.Type)}");

        var width = comparison.Rows.Max(r => r.Server.Label.Length + r.Server.Address.ToString().Length + 3);
        foreach (var row in comparison.Rows)
        {
            var who = $"{row.Server.Label} ({row.Server.Address})".PadRight(width);
            text.AppendLine(row.Error is not null
                ? $"{who}  {row.Error}"
                : $"{who}  {row.Status,-9} {(row.Authoritative ? "aa" : "  ")}  {Join(row.Answers)}  [{row.Elapsed!.Value.TotalMilliseconds:0} ms]");
        }

        text.AppendLine();
        var groups = comparison.Groups;
        if (groups.Count <= 1)
        {
            text.AppendLine(comparison.Rows.Count == 1
                ? "Only one server was asked."
                : $"All {comparison.Rows.Count} servers give the same answer.");
        }
        else
        {
            text.AppendLine($"The servers do not agree: {groups.Count} different answers.");
            var number = 1;
            foreach (var group in groups)
            {
                var first = group[0];
                var what = first.Error is not null ? first.Error : first.Answers.Count == 0 ? $"{first.Status}, no records" : $"{first.Status}: {Join(first.Answers)}";
                text.AppendLine($"  {number++}. {string.Join(", ", group.Select(r => r.Server.Label))} - {what}");
            }
        }
        return text.ToString();
    }

    private static string Join(IReadOnlyList<string> answers) => answers.Count == 0 ? "(no records)" : string.Join("; ", answers);
}

/// <summary>One line of a batch: the question to ask, and how it was written.</summary>
public sealed record DnsBatchItem(DnsQuestion Question, string Written);

public sealed record DnsBatchResult(DnsBatchItem Item, DnsResponse? Response, string? Error);

/// <summary>Many lookups from a list: forward names with an optional type, and addresses (looked up in reverse).</summary>
public static class DnsBatch
{
    /// <summary>
    /// One question per line: "name", "name type", or an IP address (asked as a PTR in its reverse zone). Blank
    /// lines and "#" comments are skipped. A line that cannot be read is reported and skipped.
    /// </summary>
    public static (List<DnsBatchItem> Items, List<string> Problems) Parse(string? text, DnsType defaultType = DnsType.A)
    {
        var items = new List<DnsBatchItem>();
        var problems = new List<string>();
        var lines = (text ?? "").Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            var words = lines[i].Split('#')[0].Split([' ', '\t', ',', '\r'], StringSplitOptions.RemoveEmptyEntries);
            if (words.Length == 0) continue;

            if (IPAddress.TryParse(words[0], out var address))
            {
                items.Add(new DnsBatchItem(new DnsQuestion(DnsNames.ReverseName(address), DnsType.PTR), words[0]));
                continue;
            }

            var type = defaultType;
            if (words.Length > 1 && !DnsTypes.TryParse(words[1], out type))
            {
                problems.Add($"Line {i + 1}: \"{words[1]}\" is not a record type.");
                continue;
            }
            items.Add(new DnsBatchItem(new DnsQuestion(words[0], type), words[0]));
        }
        return (items, problems);
    }

    public static async Task<IReadOnlyList<DnsBatchResult>> RunAsync(
        IReadOnlyList<DnsBatchItem> items, DnsQueryOptions options, IDnsTransport? transport = null,
        IProgress<DnsBatchResult>? progress = null, CancellationToken ct = default)
    {
        var results = new DnsBatchResult[items.Count];
        using var gate = new SemaphoreSlim(8);
        await Task.WhenAll(items.Select(async (item, index) =>
        {
            await gate.WaitAsync(ct);
            try
            {
                DnsBatchResult result;
                try
                {
                    result = new DnsBatchResult(item, await DnsLookup.QueryAsync(item.Question, options, transport, ct), null);
                }
                catch (Exception ex) when (ex is DnsQueryException or ArgumentException)
                {
                    result = new DnsBatchResult(item, null, ex.Message);
                }
                results[index] = result;
                progress?.Report(result);
            }
            finally
            {
                gate.Release();
            }
        }));
        return results;
    }

    public static string Text(IReadOnlyList<DnsBatchResult> results)
    {
        if (results.Count == 0) return "Nothing to look up.\n";

        var rows = results.Select(r => new[]
        {
            r.Item.Written, DnsTypes.Name(r.Item.Question.Type),
            r.Error is not null ? "error" : r.Response!.Status,
            r.Response is null ? "" : $"{r.Response.Elapsed.TotalMilliseconds:0} ms",
            r.Error ?? (r.Response!.Message.Answers.Count == 0 ? "(no records)" : string.Join("; ", r.Response.Message.Answers.Select(a => a.Data))),
        }).ToList();
        string[] headers = ["Query", "Type", "Status", "Time", "Answer"];

        // The answer is last and as long as it needs to be; the other columns line up.
        var widths = headers.Select((h, i) => i == 4 ? 0 : Math.Max(h.Length, rows.Max(r => r[i].Length))).ToArray();
        var text = new StringBuilder();
        void Line(string[] cells) =>
            text.AppendLine(string.Join("  ", cells.Select((c, i) => i == 4 ? c : c.PadRight(widths[i]))).TrimEnd());

        Line(headers);
        foreach (var row in rows) Line(row);
        return text.ToString();
    }
}
