// Copyright (c) KeelMatrix

using System.Globalization;
using FluentAssertions;
using KeelMatrix.Telemetry.Events;
using KeelMatrix.Telemetry.Serialization;

namespace KeelMatrix.Telemetry.UnitTests;

// TelemetryConfig.Runtime is global static state. Keep these tests non-parallel.
[CollectionDefinition(Name, DisableParallelization = true)]
public static class TelemetrySchemaValidatorTestsCollectionDefinition {
    public const string Name = $"{nameof(TelemetrySchemaValidatorTests)}.NonParallel";
}

[Collection(TelemetrySchemaValidatorTestsCollectionDefinition.Name)]
public sealed class TelemetrySchemaValidatorTests {
    private static readonly string ValidTimestampUtc = new DateTimeOffset(2026, 02, 27, 0, 0, 0, TimeSpan.Zero)
        .UtcDateTime
        .ToString(TelemetryConfig.TimestampFormat, CultureInfo.InvariantCulture);

    [Fact]
    public void IsValid_ReturnsFalse_WhenSchemaVersionMismatch() {
        SetRuntimeTool("schema_test_tool");

        var evt = CreateActivation(schemaVersion: TelemetryConfig.SchemaVersion + 1);

        TelemetrySchemaValidator.IsValid(evt).Should().BeFalse();
    }

    [Fact]
    public void IsValid_ReturnsFalse_WhenToolNameDiffersFromRuntimeToolName() {
        SetRuntimeTool("runtime_tool");

        var evt = new ActivationEvent(
            tool: "different_tool",
            toolVersion: "1.0.0",
            telemetryVersion: "1.0.0",
            schemaVersion: TelemetryConfig.SchemaVersion,
            projectHash: "abc",
            runtime: "dotnet",
            os: "linux",
            ci: false,
            timestamp: ValidTimestampUtc);

        TelemetrySchemaValidator.IsValid(evt).Should().BeFalse();
    }

    [Fact]
    public void IsValid_ReturnsFalse_WhenToolVersionTooLong() {
        SetRuntimeTool("tool_version_len");

        var tooLong = new string('a', TelemetryConfig.ToolVersionMaxLength + 1);

        var evt = new ActivationEvent(
            tool: TelemetryConfig.Runtime.ToolName,
            toolVersion: tooLong,
            telemetryVersion: "1.0.0",
            schemaVersion: TelemetryConfig.SchemaVersion,
            projectHash: "abc",
            runtime: "dotnet",
            os: "linux",
            ci: false,
            timestamp: ValidTimestampUtc);

        TelemetrySchemaValidator.IsValid(evt).Should().BeFalse();
    }

    [Fact]
    public void IsValid_ReturnsFalse_WhenTelemetryVersionTooLong() {
        SetRuntimeTool("telemetry_version_len");

        var tooLong = new string('a', TelemetryConfig.ToolVersionMaxLength + 1);

        var evt = new ActivationEvent(
            tool: TelemetryConfig.Runtime.ToolName,
            toolVersion: "1.0.0",
            telemetryVersion: tooLong,
            schemaVersion: TelemetryConfig.SchemaVersion,
            projectHash: "abc",
            runtime: "dotnet",
            os: "linux",
            ci: false,
            timestamp: ValidTimestampUtc);

        TelemetrySchemaValidator.IsValid(evt).Should().BeFalse();
    }

    [Fact]
    public void IsValid_ReturnsFalse_WhenProjectHashTooLong() {
        SetRuntimeTool("project_hash_len");

        var tooLong = new string('a', TelemetryConfig.ProjectHashMaxLength + 1);

        var evt = new ActivationEvent(
            tool: TelemetryConfig.Runtime.ToolName,
            toolVersion: "1.0.0",
            telemetryVersion: "1.0.0",
            schemaVersion: TelemetryConfig.SchemaVersion,
            projectHash: tooLong,
            runtime: "dotnet",
            os: "linux",
            ci: false,
            timestamp: ValidTimestampUtc);

        TelemetrySchemaValidator.IsValid(evt).Should().BeFalse();
    }

    [Theory]
    [MemberData(nameof(GetTooLongRuntimeOrOsCases))]
    public void Activation_Rejects_RuntimeOrOsTooLong(string runtime, string os) {
        SetRuntimeTool("runtime_os_caps");

        var evt = new ActivationEvent(
            tool: TelemetryConfig.Runtime.ToolName,
            toolVersion: "1.0.0",
            telemetryVersion: "1.0.0",
            schemaVersion: TelemetryConfig.SchemaVersion,
            projectHash: "abc",
            runtime: runtime,
            os: os,
            ci: false,
            timestamp: ValidTimestampUtc);

        TelemetrySchemaValidator.IsValid(evt).Should().BeFalse();
    }

    [Theory]
    [MemberData(nameof(GetBadTimestampCases))]
    public void Activation_Rejects_NonUtcOrWrongTimestampFormat(string timestamp) {
        SetRuntimeTool("timestamp_rules");

        var evt = new ActivationEvent(
            tool: TelemetryConfig.Runtime.ToolName,
            toolVersion: "1.0.0",
            telemetryVersion: "1.0.0",
            schemaVersion: TelemetryConfig.SchemaVersion,
            projectHash: "abc",
            runtime: "dotnet",
            os: "linux",
            ci: false,
            timestamp: timestamp);

        TelemetrySchemaValidator.IsValid(evt).Should().BeFalse();
    }

    [Theory]
    [InlineData("2026-W9")]     // week must be 2 digits
    [InlineData("2026-W009")]   // too many digits
    [InlineData("2026-09")]     // missing 'W'
    [InlineData("W09-2026")]    // wrong order
    [InlineData("2026-WAA")]    // non-numeric
    [InlineData("")]            // empty
    public void Heartbeat_Rejects_NonIsoWeekFormat(string week) {
        SetRuntimeTool("week_format");

        var evt = new HeartbeatEvent(
            tool: TelemetryConfig.Runtime.ToolName,
            toolVersion: "1.0.0",
            telemetryVersion: "1.0.0",
            schemaVersion: TelemetryConfig.SchemaVersion,
            projectHash: "abc",
            week: week);

        TelemetrySchemaValidator.IsValid(evt).Should().BeFalse();
    }

    private static void SetRuntimeTool(string toolNameUpper) {
        // Runtime.Set lowercases ToolName; validator requires telemetryEvent.Tool == Runtime.ToolName.
        TelemetryConfig.Runtime.Set(toolNameUpper, typeof(TelemetrySchemaValidatorTests));
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
        // Assumes Runtime has already been set.
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

    public static IEnumerable<object[]> GetTooLongRuntimeOrOsCases() {
        var okRuntime = "dotnet";
        var okOs = "linux";

        yield return [new string('r', TelemetryConfig.RuntimeMaxLength + 1), okOs];
        yield return [okRuntime, new string('o', TelemetryConfig.OsMaxLength + 1)];
        yield return [new string('r', TelemetryConfig.RuntimeMaxLength + 1), new string('o', TelemetryConfig.OsMaxLength + 1)];
    }

    public static IEnumerable<object[]> GetBadTimestampCases() {
        // Wrong format (space instead of 'T')
        yield return ["2026-02-27 00:00:00Z"];

        // Wrong format (milliseconds)
        yield return ["2026-02-27T00:00:00.000Z"];

        // Wrong format (missing seconds)
        yield return ["2026-02-27T00:00Z"];

        // Wrong format (no Z)
        yield return ["2026-02-27T00:00:00"];

        // Wrong format (offset instead of literal Z)
        yield return ["2026-02-27T00:00:00+00:00"];

        // Empty / whitespace
        yield return [""];
        yield return ["   "];

        // Not a date
        yield return ["not-a-timestamp"];
    }
}
