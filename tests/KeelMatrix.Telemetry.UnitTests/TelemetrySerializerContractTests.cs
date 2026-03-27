// Copyright (c) KeelMatrix

using FluentAssertions;

namespace KeelMatrix.Telemetry.UnitTests;

public sealed class TelemetrySerializerContractTests {
    [Fact]
    public void SerializeActivation_UsesSnakeCaseFieldNames() {
        var names = TelemetrySerializerContractTestData.ReadPropertyNames(
            TelemetrySerializerContractTestData.SerializeActivationJson());

        names.Should().Equal(TelemetrySerializerContractTestData.ActivationPropertyNames);
        names.Should().OnlyContain(name => name.Equals(name, StringComparison.InvariantCultureIgnoreCase));
        names.Should().OnlyContain(name => System.Text.RegularExpressions.Regex.IsMatch(name, "^[a-z0-9]+(_[a-z0-9]+)*$"));
        names.Should().NotContain(TelemetrySerializerContractTestData.CamelCaseWireFields);
    }

    [Fact]
    public void SerializeHeartbeat_UsesSnakeCaseFieldNames() {
        var names = TelemetrySerializerContractTestData.ReadPropertyNames(
            TelemetrySerializerContractTestData.SerializeHeartbeatJson());

        names.Should().Equal(TelemetrySerializerContractTestData.HeartbeatPropertyNames);
        names.Should().OnlyContain(name => name.Equals(name, StringComparison.InvariantCultureIgnoreCase));
        names.Should().OnlyContain(name => System.Text.RegularExpressions.Regex.IsMatch(name, "^[a-z0-9]+(_[a-z0-9]+)*$"));
        names.Should().NotContain(TelemetrySerializerContractTestData.CamelCaseWireFields);
    }

    [Fact]
    public void SerializeActivation_ContainsExpectedFieldsOnly() {
        var names = TelemetrySerializerContractTestData.ReadPropertyNames(
            TelemetrySerializerContractTestData.SerializeActivationJson());

        names.Should().BeEquivalentTo(TelemetrySerializerContractTestData.ActivationPropertyNames);
        names.Should().HaveCount(TelemetrySerializerContractTestData.ActivationPropertyNames.Length);
    }

    [Fact]
    public void SerializeHeartbeat_ContainsExpectedFieldsOnly() {
        var names = TelemetrySerializerContractTestData.ReadPropertyNames(
            TelemetrySerializerContractTestData.SerializeHeartbeatJson());

        names.Should().BeEquivalentTo(TelemetrySerializerContractTestData.HeartbeatPropertyNames);
        names.Should().HaveCount(TelemetrySerializerContractTestData.HeartbeatPropertyNames.Length);
    }
}
