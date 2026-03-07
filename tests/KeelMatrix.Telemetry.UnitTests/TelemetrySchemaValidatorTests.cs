// Copyright (c) KeelMatrix

using System.Globalization;
using FluentAssertions;
using KeelMatrix.Telemetry.Events;
using KeelMatrix.Telemetry.Serialization;

namespace KeelMatrix.Telemetry.UnitTests;

public sealed class TelemetrySchemaValidatorTests {
    private static readonly string ValidTimestampUtc = new DateTimeOffset(2026, 02, 27, 0, 0, 0, TimeSpan.Zero)
        .UtcDateTime
        .ToString(TelemetryConfig.TimestampFormat, CultureInfo.InvariantCulture);

    [Fact]
    public void IsValid_ReturnsFalse_WhenSchemaVersionMismatch() {
        var runtimeContext = CreateRuntimeContext("schema_test_tool");

        var evt = CreateActivation(runtimeContext, schemaVersion: TelemetryConfig.SchemaVersion + 1);

        TelemetrySchemaValidator.IsValid(evt, runtimeContext.ToolName).Should().BeFalse();
    }

    [Fact]
    public void IsValid_ReturnsFalse_WhenToolNameDiffersFromRuntimeToolName() {
        var runtimeContext = CreateRuntimeContext("runtime_tool");

        var evt = new ActivationEvent(
            tool: "different_tool",
            toolVersion: runtimeContext.ToolVersion,
            telemetryVersion: "1.0.0",
            schemaVersion: TelemetryConfig.SchemaVersion,
            projectHash: "abc",
            runtime: "dotnet",
            os: "linux",
            ci: false,
            timestamp: ValidTimestampUtc);

        TelemetrySchemaValidator.IsValid(evt, runtimeContext.ToolName).Should().BeFalse();
    }

    [Fact]
    public void IsValid_ReturnsFalse_WhenToolVersionTooLong() {
        var runtimeContext = CreateRuntimeContext("tool_version_len");

        var tooLong = new string('a', TelemetryConfig.ToolVersionMaxLength + 1);

        var evt = new ActivationEvent(
            tool: runtimeContext.ToolName,
            toolVersion: tooLong,
            telemetryVersion: "1.0.0",
            schemaVersion: TelemetryConfig.SchemaVersion,
            projectHash: "abc",
            runtime: "dotnet",
            os: "linux",
            ci: false,
            timestamp: ValidTimestampUtc);

        TelemetrySchemaValidator.IsValid(evt, runtimeContext.ToolName).Should().BeFalse();
    }

    [Fact]
    public void IsValid_ReturnsFalse_WhenTelemetryVersionTooLong() {
        var runtimeContext = CreateRuntimeContext("telemetry_version_len");

        var tooLong = new string('a', TelemetryConfig.ToolVersionMaxLength + 1);

        var evt = new ActivationEvent(
            tool: runtimeContext.ToolName,
            toolVersion: runtimeContext.ToolVersion,
            telemetryVersion: tooLong,
            schemaVersion: TelemetryConfig.SchemaVersion,
            projectHash: "abc",
            runtime: "dotnet",
            os: "linux",
            ci: false,
            timestamp: ValidTimestampUtc);

        TelemetrySchemaValidator.IsValid(evt, runtimeContext.ToolName).Should().BeFalse();
    }

    [Fact]
    public void IsValid_ReturnsFalse_WhenProjectHashTooLong() {
        var runtimeContext = CreateRuntimeContext("project_hash_len");

        var tooLong = new string('a', TelemetryConfig.ProjectHashMaxLength + 1);

        var evt = new ActivationEvent(
            tool: runtimeContext.ToolName,
            toolVersion: runtimeContext.ToolVersion,
            telemetryVersion: "1.0.0",
            schemaVersion: TelemetryConfig.SchemaVersion,
            projectHash: tooLong,
            runtime: "dotnet",
            os: "linux",
            ci: false,
            timestamp: ValidTimestampUtc);

        TelemetrySchemaValidator.IsValid(evt, runtimeContext.ToolName).Should().BeFalse();
    }

    [Theory]
    [MemberData(nameof(GetTooLongRuntimeOrOsCases))]
    public void Activation_Rejects_RuntimeOrOsTooLong(string runtime, string os) {
        var runtimeContext = CreateRuntimeContext("runtime_os_caps");

        var evt = new ActivationEvent(
            tool: runtimeContext.ToolName,
            toolVersion: runtimeContext.ToolVersion,
            telemetryVersion: "1.0.0",
            schemaVersion: TelemetryConfig.SchemaVersion,
            projectHash: "abc",
            runtime: runtime,
            os: os,
            ci: false,
            timestamp: ValidTimestampUtc);

        TelemetrySchemaValidator.IsValid(evt, runtimeContext.ToolName).Should().BeFalse();
    }

    [Theory]
    [MemberData(nameof(GetBadTimestampCases))]
    public void Activation_Rejects_NonUtcOrWrongTimestampFormat(string timestamp) {
        var runtimeContext = CreateRuntimeContext("timestamp_rules");

        var evt = new ActivationEvent(
            tool: runtimeContext.ToolName,
            toolVersion: runtimeContext.ToolVersion,
            telemetryVersion: "1.0.0",
            schemaVersion: TelemetryConfig.SchemaVersion,
            projectHash: "abc",
            runtime: "dotnet",
            os: "linux",
            ci: false,
            timestamp: timestamp);

        TelemetrySchemaValidator.IsValid(evt, runtimeContext.ToolName).Should().BeFalse();
    }

    [Theory]
    [InlineData("2026-W9")]     // week must be 2 digits
    [InlineData("2026-W009")]   // too many digits
    [InlineData("2026-09")]     // missing 'W'
    [InlineData("W09-2026")]    // wrong order
    [InlineData("2026-WAA")]    // non-numeric
    [InlineData("")]            // empty
    public void Heartbeat_Rejects_NonIsoWeekFormat(string week) {
        var runtimeContext = CreateRuntimeContext("week_format");

        var evt = new HeartbeatEvent(
            tool: runtimeContext.ToolName,
            toolVersion: runtimeContext.ToolVersion,
            telemetryVersion: "1.0.0",
            schemaVersion: TelemetryConfig.SchemaVersion,
            projectHash: "abc",
            week: week);

        TelemetrySchemaValidator.IsValid(evt, runtimeContext.ToolName).Should().BeFalse();
    }

    private static TelemetryRuntimeContext CreateRuntimeContext(string toolNameUpper) {
        return new TelemetryRuntimeContext(toolNameUpper, typeof(TelemetrySchemaValidatorTests));
    }

    private static ActivationEvent CreateActivation(
        TelemetryRuntimeContext runtimeContext,
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
            tool: tool ?? runtimeContext.ToolName,
            toolVersion: toolVersion ?? runtimeContext.ToolVersion,
            telemetryVersion: telemetryVersion ?? "1.0.0",
            schemaVersion: schemaVersion ?? TelemetryConfig.SchemaVersion,
            projectHash: projectHash ?? "abc",
            runtime: runtime ?? "dotnet",
            os: os ?? "linux",
            ci: ci ?? false,
            timestamp: timestamp ?? ValidTimestampUtc);
    }

    public static TheoryData<string, string> GetTooLongRuntimeOrOsCases()
    {
        const string okRuntime = "dotnet";
        const string okOs = "linux";

        var data = new TheoryData<string, string>();
        data.Add(new string('r', TelemetryConfig.RuntimeMaxLength + 1), okOs);
        data.Add(okRuntime, new string('o', TelemetryConfig.OsMaxLength + 1));
        data.Add(new string('r', TelemetryConfig.RuntimeMaxLength + 1), new string('o', TelemetryConfig.OsMaxLength + 1));
        return data;
    }

    public static TheoryData<string> GetBadTimestampCases()
    {
        var data = new TheoryData<string>();

        // Wrong format (space instead of 'T')
        data.Add("2026-02-27 00:00:00Z");

        // Wrong format (milliseconds)
        data.Add("2026-02-27T00:00:00.000Z");

        // Wrong format (missing seconds)
        data.Add("2026-02-27T00:00Z");

        // Wrong format (no Z)
        data.Add("2026-02-27T00:00:00");

        // Wrong format (offset instead of literal Z)
        data.Add("2026-02-27T00:00:00+00:00");

        // Empty / whitespace
        data.Add("");
        data.Add("   ");

        // Not a date
        data.Add("not-a-timestamp");

        return data;
    }
}
