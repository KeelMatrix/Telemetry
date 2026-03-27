// Copyright (c) KeelMatrix

using System.Text;
using System.Text.Json;
using KeelMatrix.Telemetry.Events;
using KeelMatrix.Telemetry.Serialization;

namespace KeelMatrix.Telemetry.UnitTests;

internal static class TelemetrySerializerContractTestData {
    internal const string Tool = "fixture_tool";
    internal const string ToolVersion = "2.3.4";
    internal const string TelemetryVersion = "9.8.7";
    internal const string ProjectHash = "project_hash_123";
    internal const string InstallationHash = "installation_hash_456";
    internal const string Runtime = "dotnet";
    internal const string Os = "windows";
    internal const string Timestamp = "2026-02-27T00:00:00Z";
    internal const string Week = "2026-W09";

    internal static readonly string[] CamelCaseWireFields = [
        "toolVersion",
        "telemetryVersion",
        "schemaVersion",
        "projectHash",
        "installationHash"
    ];

    internal static readonly string[] ActivationPropertyNames = [
        "runtime",
        "os",
        "ci",
        "timestamp",
        "event",
        "tool",
        "tool_version",
        "telemetry_version",
        "schema_version",
        "project_hash",
        "installation_hash"
    ];

    internal static readonly string[] HeartbeatPropertyNames = [
        "week",
        "event",
        "tool",
        "tool_version",
        "telemetry_version",
        "schema_version",
        "project_hash",
        "installation_hash"
    ];

    internal const string ExpectedActivationJson =
        "{\"runtime\":\"dotnet\",\"os\":\"windows\",\"ci\":true,\"timestamp\":\"2026-02-27T00:00:00Z\",\"event\":\"activation\",\"tool\":\"fixture_tool\",\"tool_version\":\"2.3.4\",\"telemetry_version\":\"9.8.7\",\"schema_version\":1,\"project_hash\":\"project_hash_123\",\"installation_hash\":\"installation_hash_456\"}";

    internal const string ExpectedHeartbeatJson =
        "{\"week\":\"2026-W09\",\"event\":\"heartbeat\",\"tool\":\"fixture_tool\",\"tool_version\":\"2.3.4\",\"telemetry_version\":\"9.8.7\",\"schema_version\":1,\"project_hash\":\"project_hash_123\",\"installation_hash\":\"installation_hash_456\"}";

    internal static ActivationEvent CreateActivation() {
        return new ActivationEvent(
            tool: Tool,
            toolVersion: ToolVersion,
            telemetryVersion: TelemetryVersion,
            schemaVersion: TelemetryConfig.SchemaVersion,
            projectHash: ProjectHash,
            installationHash: InstallationHash,
            runtime: Runtime,
            os: Os,
            ci: true,
            timestamp: Timestamp);
    }

    internal static HeartbeatEvent CreateHeartbeat() {
        return new HeartbeatEvent(
            tool: Tool,
            toolVersion: ToolVersion,
            telemetryVersion: TelemetryVersion,
            schemaVersion: TelemetryConfig.SchemaVersion,
            projectHash: ProjectHash,
            installationHash: InstallationHash,
            week: Week);
    }

    internal static string SerializeActivationJson() {
        return TelemetrySerializer.Serialize(CreateActivation(), Tool)!;
    }

    internal static string SerializeHeartbeatJson() {
        return TelemetrySerializer.Serialize(CreateHeartbeat(), Tool)!;
    }

    internal static IReadOnlyList<string> ReadPropertyNames(string json) {
        using var doc = JsonDocument.Parse(json);
        return [.. doc.RootElement.EnumerateObject().Select(property => property.Name)];
    }

    internal static string GenerateCanonicalJson(string json) {
        using var doc = JsonDocument.Parse(json);
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream)) {
            doc.RootElement.WriteTo(writer);
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }
}
