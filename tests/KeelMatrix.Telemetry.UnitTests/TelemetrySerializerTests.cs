// Copyright (c) KeelMatrix

using System.Globalization;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using KeelMatrix.Telemetry.Events;
using KeelMatrix.Telemetry.Serialization;

namespace KeelMatrix.Telemetry.UnitTests;

// TelemetryConfig.Runtime is global static state. Keep these tests non-parallel.
[CollectionDefinition(Name, DisableParallelization = true)]
public static class TelemetrySerializerTestsCollectionDefinition {
    public const string Name = $"{nameof(TelemetrySerializerTests)}.NonParallel";
}

[Collection(TelemetrySerializerTestsCollectionDefinition.Name)]
public sealed class TelemetrySerializerTests {
    private static readonly string ValidTimestampUtc = new DateTimeOffset(2026, 02, 27, 0, 0, 0, TimeSpan.Zero)
        .UtcDateTime
        .ToString(TelemetryConfig.TimestampFormat, CultureInfo.InvariantCulture);

    [Fact]
    public void Serialize_ReturnsNull_WhenSchemaValidatorFails() {
        // Valid runtime tool
        SetRuntimeTool("serializer_invalid");

        // Make the event invalid by mismatching schemaVersion.
        var invalid = CreateActivation(schemaVersion: TelemetryConfig.SchemaVersion + 1);

        TelemetrySerializer.Serialize(invalid).Should().BeNull();
    }

    [Fact]
    public void Serialize_UsesSnakeCaseLowerKeys() {
        SetRuntimeTool("serializer_keys");

        var evt = CreateActivation();
        var json = TelemetrySerializer.Serialize(evt);
        json.Should().NotBeNull();

        using var doc = JsonDocument.Parse(json!);
        doc.RootElement.ValueKind.Should().Be(JsonValueKind.Object);

        var names = doc.RootElement.EnumerateObject().Select(p => p.Name).ToHashSet(StringComparer.Ordinal);


#pragma warning disable S125 // Sections of code should not be commented out
        // Expected snake_case lower keys (net8 uses JsonNamingPolicy.SnakeCaseLower;
#pragma warning restore S125
        // netstandard uses custom SnakeCaseLowerNamingPolicy).
        names.Should().BeEquivalentTo(new HashSet<string>(StringComparer.Ordinal) {
            "event",
            "tool",
            "tool_version",
            "telemetry_version",
            "schema_version",
            "project_hash",
            "runtime",
            "os",
            "ci",
            "timestamp"
        });

        // Defensive: ensure every key is lowercase snake_case.
        foreach (var n in names) {
            n.Should().Be(n.ToLowerInvariant());
            n.Should().MatchRegex("^[a-z0-9]+(_[a-z0-9]+)*$");
        }
    }

    [Fact]
    public void Serialize_ReturnsNull_WhenPayloadExceedsMaxPayloadBytes() {
        // Create a valid event with an extremely long tool name.
        // Tool name has no schema cap; it will inflate payload size beyond 512 bytes.
        var toolUpper = "TOOL_" + new string('A', 2000);
        SetRuntimeTool(toolUpper);

        var evt = CreateActivation();
        TelemetrySerializer.Serialize(evt).Should().BeNull();
    }

    [Fact]
    public void Serialize_AllowsExactlyMaxPayloadBytes() {
        // Find a tool name length that produces exactly MaxPayloadBytes.
        // ToolNameUpper is preserved in payload (lowercased), so length changes payload size 1:1 in UTF-8.
        var toolUpper = FindToolNameUpperProducingExactPayloadBytes(TelemetryConfig.MaxPayloadBytes);

        SetRuntimeTool(toolUpper);
        var evt = CreateActivation();

        var json = TelemetrySerializer.Serialize(evt);
        json.Should().NotBeNull();
        Encoding.UTF8.GetByteCount(json!).Should().Be(TelemetryConfig.MaxPayloadBytes);

        // Boundary +1 should be rejected.
        var toolUpperPlusOne = toolUpper + "A";
        SetRuntimeTool(toolUpperPlusOne);
        var evtPlusOne = CreateActivation();
        TelemetrySerializer.Serialize(evtPlusOne).Should().BeNull();
    }

    private static void SetRuntimeTool(string toolNameUpper) {
        TelemetryConfig.Runtime.Set(toolNameUpper, typeof(TelemetrySerializerTests));
    }

    private static ActivationEvent CreateActivation(
        int? schemaVersion = null,
        string? tool = null,
        string? toolVersion = null,
        string? telemetryVersion = null,
        string? projectHash = null,
        string? runtime = null,
        string? os = null,
        bool? ci = null,
        string? timestamp = null) {
        return new ActivationEvent(
            tool: tool ?? TelemetryConfig.Runtime.ToolName,
            toolVersion: toolVersion ?? "1.0.0",
            telemetryVersion: telemetryVersion ?? "1.0.0",
            schemaVersion: schemaVersion ?? TelemetryConfig.SchemaVersion,
            projectHash: projectHash ?? "abc",
            runtime: runtime ?? "dotnet",
            os: os ?? "linux",
            ci: ci ?? false,
            timestamp: timestamp ?? ValidTimestampUtc);
    }

    private static string FindToolNameUpperProducingExactPayloadBytes(int targetBytes) {
        // Use a stable prefix to make debugging easier.
        const string prefix = "UNITTEST_PAYLOAD_";

        for (int padLen = 0; padLen <= 4096; padLen++) {
            var toolUpper = prefix + new string('A', padLen);
            SetRuntimeTool(toolUpper);

            var evt = CreateActivation();
            var json = TelemetrySerializer.Serialize(evt);

            if (json is null)
                continue;

            var bytes = Encoding.UTF8.GetByteCount(json);
            if (bytes == targetBytes)
                return toolUpper;

            // Once we pass the target (without hitting it), something is off; fail fast.
            if (bytes > targetBytes)
                break;
        }

        throw new InvalidOperationException($"Could not produce a payload of exactly {targetBytes} bytes.");
    }
}
