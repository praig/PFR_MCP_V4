using System.Text.Json;
using System.Text.Json.Serialization;

namespace Pfr.Core.Storage;

/// <summary>JSON settings for stored data: compact, enums as names, get-only lists filled on read.</summary>
public static class StoreJson
{
    public static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = false,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PreferredObjectCreationHandling = JsonObjectCreationHandling.Populate,
        Converters = { new JsonStringEnumConverter() },
    };
}
