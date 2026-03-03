// Copyright (c) KeelMatrix

using FluentAssertions;

namespace KeelMatrix.Telemetry.UnitTests;

public sealed class NullTelemetryClientTests {
    [Fact]
    public void TrackActivation_NoThrow_NoOp() {
        var client = new NullTelemetryClient();
        var act = () => client.TrackActivation();
        act.Should().NotThrow();
    }

    [Fact]
    public void TrackHeartbeat_NoThrow_NoOp() {
        var client = new NullTelemetryClient();
        var act = () => client.TrackHeartbeat();
        act.Should().NotThrow();
    }
}
