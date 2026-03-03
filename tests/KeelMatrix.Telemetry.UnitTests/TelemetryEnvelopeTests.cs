// Copyright (c) KeelMatrix

using System.Text.Json;
using FluentAssertions;

namespace KeelMatrix.Telemetry.UnitTests;

public sealed class TelemetryEnvelopeTests {
    [Fact]
    public void Serialize_Then_Deserialize_RoundTrips_AllFields() {
        const string id = "0123456789abcdef0123456789abcdef";
        const string payloadJson = "{\"event\":\"activation\",\"v\":1}";
        var enqueuedUtc = new DateTimeOffset(2026, 02, 28, 17, 41, 11, TimeSpan.Zero);
        const int attempts = 3;

        var env = new Storage.TelemetryEnvelope(id, payloadJson, enqueuedUtc) {
            Attempts = attempts
        };

        var json = env.Serialize();
        var roundTripped = Storage.TelemetryEnvelope.Deserialize(json);

        roundTripped.Id.Should().Be(id);
        roundTripped.PayloadJson.Should().Be(payloadJson);
        roundTripped.EnqueuedUtc.Should().Be(enqueuedUtc);
        roundTripped.Attempts.Should().Be(attempts);
    }

    [Fact]
    public void Deserialize_ValidJsonWithAttempts_SetsInitOnlyProperty() {
        const string json = "{" +
                   "\"Id\":\"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\"," +
                   "\"PayloadJson\":\"{}\"," +
                   "\"EnqueuedUtc\":\"2026-02-28T17:41:11+00:00\"," +
                   "\"Attempts\":7" +
                   "}";

        var env = Storage.TelemetryEnvelope.Deserialize(json);

        env.Attempts.Should().Be(7);
    }

    [Fact]
    public void Deserialize_InvalidJson_ThrowsJsonException() {
        const string malformed = "{\"Id\":\"x\",\"PayloadJson\":\"{}\""; // missing closing brace + remaining properties

        var act = () => Storage.TelemetryEnvelope.Deserialize(malformed);

        act.Should().Throw<JsonException>();
    }

    [Fact]
    public void Deserialize_JsonThatDeserializesToNull_ThrowsInvalidOperationException() {
        var act = () => Storage.TelemetryEnvelope.Deserialize("null");

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("Invalid envelope.*");
    }

    [Fact]
    public void Serialize_OutputIsValidJsonObject() {
        var env = new Storage.TelemetryEnvelope("{}");

        var json = env.Serialize();

        using var doc = JsonDocument.Parse(json);
        doc.RootElement.ValueKind.Should().Be(JsonValueKind.Object);

        doc.RootElement.TryGetProperty("Id", out _).Should().BeTrue();
        doc.RootElement.TryGetProperty("PayloadJson", out _).Should().BeTrue();
        doc.RootElement.TryGetProperty("EnqueuedUtc", out _).Should().BeTrue();
        doc.RootElement.TryGetProperty("Attempts", out _).Should().BeTrue();
    }

    [Fact]
    public void Constructor_GeneratesId_As32HexNoDashes() {
        var env = new Storage.TelemetryEnvelope("{}");

        env.Id.Should().MatchRegex("^[0-9a-fA-F]{32}$");
    }

    [Fact]
    public void Constructor_SetsEnqueuedUtc_CloseToUtcNow() {
        var before = DateTimeOffset.UtcNow;

        var env = new Storage.TelemetryEnvelope("{}");

        var after = DateTimeOffset.UtcNow;

        // Allow a generous window to avoid flakiness across slow CI machines.
        env.EnqueuedUtc.Should().BeOnOrAfter(before.AddSeconds(-1));
        env.EnqueuedUtc.Should().BeOnOrBefore(after.AddSeconds(5));
    }
}
