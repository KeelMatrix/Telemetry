// Copyright (c) KeelMatrix

using System.Text.Json;

#if NETSTANDARD2_0
namespace System.Runtime.CompilerServices
{
    // This type is required for C# 9.0 init-only setters support on .NET Standard 2.0.
    internal static class IsExternalInit {
        // Prevent instantiation.
        static IsExternalInit() { }
    }
}
#endif

namespace KeelMatrix.Telemetry.Storage {
    /// <summary>
    /// Immutable envelope representing a queued telemetry payload.
    /// Stored on disk as JSON.
    /// </summary>
    internal sealed class TelemetryEnvelope {
        internal string Id { get; }
        internal string PayloadJson { get; }
        internal DateTimeOffset EnqueuedUtc { get; }
        internal int Attempts { get; init; }

        internal TelemetryEnvelope(string payloadJson)
            : this(Guid.NewGuid().ToString("N"), payloadJson, DateTimeOffset.UtcNow) {
        }

        internal TelemetryEnvelope(string id, string payloadJson, DateTimeOffset enqueuedUtc) {
            Id = id;
            PayloadJson = payloadJson;
            EnqueuedUtc = enqueuedUtc;
        }

        internal static TelemetryEnvelope Deserialize(string json) {
            return JsonSerializer.Deserialize<TelemetryEnvelope>(json)
                ?? throw new InvalidOperationException("Invalid envelope.");
        }

        internal string Serialize() {
            return JsonSerializer.Serialize(this);
        }
    }
}
