namespace Pfr.Core.Nios.Model;

/// <summary>Where an effective TTL comes from, nearest first.</summary>
public enum TtlSource
{
    /// <summary>Set on the record itself.</summary>
    Record,
    /// <summary>Set on the host object that generates the record.</summary>
    Host,
    /// <summary>Inherited from the zone's default TTL (zone override).</summary>
    Zone,
    /// <summary>Inherited from the grid's default TTL.</summary>
    Grid,
}

/// <summary>An effective TTL and where it was set. Seconds is null when the backup doesn't record one.</summary>
public readonly record struct Ttl(int? Seconds, TtlSource Source)
{
    /// <summary>True when the record doesn't set its own TTL.</summary>
    public bool IsInherited => Source != TtlSource.Record;

    /// <summary>"28800 (8h)", "3600 (1h)", "" when unknown.</summary>
    public string Display => Seconds is { } s ? $"{s} ({Human(s)})" : "";

    /// <summary>BIND-style duration: 3600 → "1h", 90061 → "1d1h1m1s", 604800 → "1w".</summary>
    public static string Human(int seconds)
    {
        if (seconds <= 0) return "0s";
        var parts = new List<string>();
        foreach (var (unit, size) in new[] { ("w", 604800), ("d", 86400), ("h", 3600), ("m", 60), ("s", 1) })
        {
            if (seconds < size) continue;
            parts.Add($"{seconds / size}{unit}");
            seconds %= size;
        }
        return string.Concat(parts);
    }
}
