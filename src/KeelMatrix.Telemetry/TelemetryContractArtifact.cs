// Copyright (c) KeelMatrix

using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using KeelMatrix.Telemetry.Serialization;

namespace KeelMatrix.Telemetry;

internal static class TelemetryContractArtifact {
    internal const int SchemaVersion = 1;
    internal const string RelativePath = "contracts/telemetry-contract.json";

    private static readonly JsonSerializerOptions SerializerOptions = new() {
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.Default,
        WriteIndented = true
    };

    private static readonly string[] ToolNameInputs = [
        "a",
        "a.b_c-d",
        "z9.tool_name-v1",
        "abbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb",
        "../tool",
        "a/b",
        "A-tool",
        ".tool",
        "-tool",
        "_tool",
        "t" + new string('t', 32),
        "My Tool",
        "My/Tool",
        " Tool ",
        "étool"
    ];

    private static readonly string[] HashInputs = [
        new string('0', 64),
        "abc",
        new string('A', 64),
        new string('g', 64),
        new string('0', 63),
        new string('0', 65)
    ];

    internal static string GenerateJson() {
        var document = new ContractDocument(
            SchemaVersion,
            GetToolNameCases(),
            GetHashCases());

        return JsonSerializer.Serialize(document, SerializerOptions) + Environment.NewLine;
    }

    internal static void WriteTo(string outputPath) {
        var fullPath = Path.GetFullPath(outputPath);
        var directory = Path.GetDirectoryName(fullPath);
        if (!string.IsNullOrWhiteSpace(directory))
            Directory.CreateDirectory(directory);

        File.WriteAllText(fullPath, GenerateJson(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
    }

    internal static IReadOnlyList<ToolNameCase> GetToolNameCases() {
        return [.. ToolNameInputs.Select(value => new ToolNameCase(value, TelemetryConfig.IsValidToolName(value)))];
    }

    internal static IReadOnlyList<HashCase> GetHashCases() {
        return [.. HashInputs.Select(value => new HashCase(
            value,
            TelemetrySchemaValidator.IsValidHash(value, TelemetryConfig.ProjectHashMaxLength)))];
    }

    private sealed record ContractDocument(
        [property: JsonPropertyName("schemaVersion")] int SchemaVersion,
        [property: JsonPropertyName("toolNameCases")] IReadOnlyList<ToolNameCase> ToolNameCases,
        [property: JsonPropertyName("hashCases")] IReadOnlyList<HashCase> HashCases);

    internal readonly record struct ToolNameCase(
        [property: JsonPropertyName("value")] string Value,
        [property: JsonPropertyName("accepted")] bool Accepted);

    internal readonly record struct HashCase(
        [property: JsonPropertyName("value")] string Value,
        [property: JsonPropertyName("accepted")] bool Accepted);
}
