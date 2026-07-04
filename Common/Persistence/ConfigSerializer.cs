using System.Text.Json;
using System.Text.Json.Serialization;

namespace Common.Persistence;

// Single source of truth for the JSON serializer options used to read
// and write SimulatorConfig. Indented output is intentional so config
// files diff cleanly in source control.
public static class ConfigSerializer
{
    public static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters =
        {
            new JsonStringEnumConverter(JsonNamingPolicy.CamelCase),
        },
        AllowTrailingCommas = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
    };

    public static string Serialize(SimulatorConfig cfg)
        => JsonSerializer.Serialize(cfg, Options);

    public static SimulatorConfig Deserialize(string json)
    {
        var cfg = JsonSerializer.Deserialize<SimulatorConfig>(json, Options)
                  ?? throw new InvalidDataException("Config JSON deserialised to null");
        // Forward-compat guard only: refuse a file written by a newer build than this one knows.
        // There is no back-version migration - v1 is the baseline.
        if (cfg.Version > SimulatorConfig.CurrentVersion)
            throw new InvalidDataException(
                $"Config version not supported: file={cfg.Version}, "
                + $"newest known={SimulatorConfig.CurrentVersion}");
        return cfg;
    }
}
