using System.Text.Json;
using System.Text.Json.Serialization;

namespace MouseMotionLab.Core.Contracts;

public sealed record TrajectoryRequest(
    [property: JsonPropertyName("dxCounts")] int DxCounts,
    [property: JsonPropertyName("dyCounts")] int DyCounts,
    [property: JsonPropertyName("dpi")] int Dpi,
    [property: JsonPropertyName("outputHz")] int OutputHz = 1_000,
    [property: JsonPropertyName("seed")] long Seed = 0,
    [property: JsonPropertyName("mode")] string Mode = "diffusion")
{
    public void Validate()
    {
        if (DxCounts == 0 && DyCounts == 0)
        {
            throw new ArgumentException("The requested endpoint cannot be (0, 0).");
        }

        if (Dpi is < 100 or > 100_000)
        {
            throw new ArgumentOutOfRangeException(nameof(Dpi));
        }

        if (OutputHz is < 125 or > 8_000)
        {
            throw new ArgumentOutOfRangeException(nameof(OutputHz));
        }

        if (Mode is not ("baseline" or "diffusion"))
        {
            throw new ArgumentException("Mode must be either 'baseline' or 'diffusion'.", nameof(Mode));
        }
    }
}

public sealed record TrajectorySample(
    [property: JsonPropertyName("dtUs")] int DtUs,
    [property: JsonPropertyName("dx")] int Dx,
    [property: JsonPropertyName("dy")] int Dy);

public sealed record TrajectoryResult(
    [property: JsonPropertyName("schemaVersion")] int SchemaVersion,
    [property: JsonPropertyName("samples")] IReadOnlyList<TrajectorySample> Samples,
    [property: JsonPropertyName("clickAtUs")] long ClickAtUs,
    [property: JsonPropertyName("coverageScore")] double CoverageScore,
    [property: JsonPropertyName("warnings")] IReadOnlyList<string> Warnings,
    [property: JsonPropertyName("modelVersion")] string ModelVersion,
    [property: JsonPropertyName("seed")] long Seed)
{
    public const int CurrentSchemaVersion = 1;

    public (int Dx, int Dy) Endpoint => (Samples.Sum(sample => sample.Dx), Samples.Sum(sample => sample.Dy));

    public void ValidateAgainst(TrajectoryRequest request)
    {
        request.Validate();

        if (SchemaVersion != CurrentSchemaVersion)
        {
            throw new InvalidDataException($"Unsupported trajectory schema version: {SchemaVersion}.");
        }

        if (Samples.Count == 0 || Samples.Any(sample => sample.DtUs <= 0))
        {
            throw new InvalidDataException("A trajectory must contain samples with positive durations.");
        }

        var endpoint = Endpoint;
        if (endpoint.Dx != request.DxCounts || endpoint.Dy != request.DyCounts)
        {
            throw new InvalidDataException(
                $"Generated endpoint ({endpoint.Dx}, {endpoint.Dy}) does not match " +
                $"requested endpoint ({request.DxCounts}, {request.DyCounts}).");
        }

        if (ClickAtUs != Samples.Sum(sample => (long)sample.DtUs))
        {
            throw new InvalidDataException("clickAtUs must equal the sum of sample durations.");
        }
    }

    public static TrajectoryResult FromJson(string json) =>
        JsonSerializer.Deserialize<TrajectoryResult>(json, JsonOptions)
        ?? throw new InvalidDataException("The generator returned an empty trajectory document.");

    public static JsonSerializerOptions JsonOptions { get; } = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };
}

