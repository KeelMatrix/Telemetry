// Copyright (c) KeelMatrix

namespace KeelMatrix.Telemetry {
    /// <summary>
    /// Holds runtime-scoped telemetry identity and paths for a single client instance.
    /// </summary>
    internal sealed class TelemetryRuntimeContext {
        private readonly string canonicalToolName;
        private string? rootDirectory;

        internal TelemetryRuntimeContext(string toolName, Type toolType) {
            canonicalToolName = toolName.ToLowerInvariant();
            ToolVersion = toolType.Assembly.GetName().Version?.ToString() ?? TelemetryConfig.UnknownSymbol;
            ToolName = canonicalToolName;
            Url = TelemetryConfig.Url;
        }

        internal string ToolVersion { get; }
        internal string ToolName { get; }
        internal Uri Url { get; }

        internal string GetRootDirectory() {
            return Volatile.Read(ref rootDirectory)
                ?? throw new InvalidOperationException("Telemetry root directory has not been resolved yet.");
        }

        internal void EnsureRootDirectoryResolvedOnWorkerThread() {
            if (Volatile.Read(ref rootDirectory) is not null)
                return;

            var computed = TelemetryConfig.ResolveRootDirectory(canonicalToolName);

            // If multiple worker wakes race (or multiple workers exist unexpectedly), keep the first resolved value.
            _ = Interlocked.CompareExchange(ref rootDirectory, computed, null);
        }
    }
}
