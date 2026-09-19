// Copyright (c) KeelMatrix

using FluentAssertions;

namespace KeelMatrix.Telemetry.UnitTests;

public sealed class TelemetryPayloadFixtureTests {
    [Fact]
    public void SerializeActivation_ProducesGoldenJson() {
        var json = TelemetrySerializerContractTestData.SerializeActivationJson();

        json.Should().Be(TelemetrySerializerContractTestData.ExpectedActivationJson);
    }

    [Fact]
    public void SerializeHeartbeat_ProducesGoldenJson() {
        var json = TelemetrySerializerContractTestData.SerializeHeartbeatJson();

        json.Should().Be(TelemetrySerializerContractTestData.ExpectedHeartbeatJson);
    }

    [Fact]
    public void GenerateCanonicalActivationFixture_MatchesExpectedJson() {
        var canonicalJson = TelemetrySerializerContractTestData.GenerateCanonicalJson(
            TelemetrySerializerContractTestData.SerializeActivationJson());

        canonicalJson.Should().Be(TelemetrySerializerContractTestData.ExpectedActivationJson);
    }

    [Fact]
    public void GenerateCanonicalHeartbeatFixture_MatchesExpectedJson() {
        var canonicalJson = TelemetrySerializerContractTestData.GenerateCanonicalJson(
            TelemetrySerializerContractTestData.SerializeHeartbeatJson());

        canonicalJson.Should().Be(TelemetrySerializerContractTestData.ExpectedHeartbeatJson);
    }

    [Fact]
    public void WorkerActivationFixture_MatchesSerializerOutput() {
        ReadWorkerFixture("activation.json")
            .Should().Be(TelemetrySerializerContractTestData.SerializeActivationJson());
    }

    [Fact]
    public void WorkerHeartbeatFixture_MatchesSerializerOutput() {
        ReadWorkerFixture("heartbeat.json")
            .Should().Be(TelemetrySerializerContractTestData.SerializeHeartbeatJson());
    }

    private static string ReadWorkerFixture(string fileName) {
        var path = Path.Combine(AppContext.BaseDirectory, "Fixtures", fileName);
        return File.ReadAllText(path).TrimEnd('\r', '\n');
    }
}
