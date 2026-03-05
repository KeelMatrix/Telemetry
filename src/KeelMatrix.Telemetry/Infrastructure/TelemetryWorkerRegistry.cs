// Copyright (c) KeelMatrix

using System.Collections.Concurrent;
using System.Globalization;

namespace KeelMatrix.Telemetry.Infrastructure {
    /// <summary>
    /// Process-wide registry of telemetry workers keyed by canonical tool identity.
    /// </summary>
    internal static class TelemetryWorkerRegistry {
        private static readonly ConcurrentDictionary<string, Lazy<TelemetryDeliveryWorker>> Workers =
            new(StringComparer.Ordinal);

        internal static TelemetryDeliveryWorker GetOrCreate(string toolName, Type toolType) {
            var runtimeContext = new TelemetryRuntimeContext(toolName, toolType);
            var runtimeInfo = new RuntimeInfo();
            var toolKey = CreateCanonicalToolKey(runtimeContext.ToolName, runtimeContext.ToolVersion);

            var lazyWorker = Workers.GetOrAdd(
                toolKey,
                _ => new Lazy<TelemetryDeliveryWorker>(
                    () => new TelemetryDeliveryWorker(runtimeContext, runtimeInfo),
                    LazyThreadSafetyMode.ExecutionAndPublication));

            try {
                return lazyWorker.Value;
            }
            catch {
                // Do not keep faulted lazy workers cached forever; allow retry on next client creation.
                if (Workers.TryGetValue(toolKey, out var current) && ReferenceEquals(current, lazyWorker))
                    Workers.TryRemove(toolKey, out _);

                throw;
            }
        }

        internal static string CreateCanonicalToolKey(string toolName, string toolVersion) {
            var canonicalToolName = Canonicalize(toolName, normalizeCase: true);
            var canonicalToolVersion = Canonicalize(toolVersion, normalizeCase: true);

            // Length-prefixed segments avoid delimiter-based collisions.
            return string.Concat(
                "v1|tool:",
                canonicalToolName.Length.ToString(CultureInfo.InvariantCulture),
                ":",
                canonicalToolName,
                "|version:",
                canonicalToolVersion.Length.ToString(CultureInfo.InvariantCulture),
                ":",
                canonicalToolVersion);
        }

        private static string Canonicalize(string? value, bool normalizeCase) {
            var canonical = (value ?? string.Empty).Trim();
            return normalizeCase
                ? canonical.ToLowerInvariant()
                : canonical;
        }
    }
}
