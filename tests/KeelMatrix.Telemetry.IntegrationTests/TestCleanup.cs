// Copyright (c) KeelMatrix

using System.Collections.Concurrent;
using System.Reflection;
using KeelMatrix.Telemetry.Infrastructure;

namespace KeelMatrix.Telemetry.IntegrationTests;

internal static class TestCleanup {
    private static readonly ConcurrentDictionary<string, CleanupRegistration> Registrations = new(StringComparer.Ordinal);

    internal static void RegisterToolForFinalCleanup(string toolName, Type toolType, string? rootDir) {
        try {
            var runtimeContext = new TelemetryRuntimeContext(toolName, toolType);
            var toolKey = TelemetryWorkerRegistry.CreateCanonicalToolKey(runtimeContext.ToolName, runtimeContext.ToolVersion);

            Registrations[toolKey] = new CleanupRegistration(toolName, toolType, rootDir);
        }
        catch {
            // swallow
        }
    }

    internal static void DisposeCachedWorkerForTool(string toolName, Type toolType) {
        try {
            var runtimeContext = new TelemetryRuntimeContext(toolName, toolType);
            var toolKey = TelemetryWorkerRegistry.CreateCanonicalToolKey(runtimeContext.ToolName, runtimeContext.ToolVersion);

            var workersField = typeof(TelemetryWorkerRegistry).GetField("Workers", BindingFlags.NonPublic | BindingFlags.Static);
            var workers = workersField?.GetValue(null);
            if (workers is null)
                return;

            var lazyWorkerType = workers.GetType().GenericTypeArguments[1];
            var tryRemove = workers.GetType().GetMethod("TryRemove", [typeof(string), lazyWorkerType.MakeByRefType()]);
            if (tryRemove is null)
                return;

            object?[] args = [toolKey, null];
            if (!Equals(tryRemove.Invoke(workers, args), true))
                return;

            if (args[1] is not Lazy<TelemetryDeliveryWorker> lazyWorker || !lazyWorker.IsValueCreated)
                return;

            try {
                lazyWorker.Value.Dispose();
            }
            catch {
                // swallow
            }
        }
        catch {
            // swallow
        }
    }

    internal static void TryDeleteDirectory(string? dir) {
        if (string.IsNullOrWhiteSpace(dir))
            return;

        try {
            if (Directory.Exists(dir))
                Directory.Delete(dir, recursive: true);
        }
        catch {
            // swallow
        }
    }

    internal static void TryDeleteDirectoryEventually(string? dir, int attempts = 20, int delayMs = 100) {
        if (string.IsNullOrWhiteSpace(dir))
            return;

        for (int attempt = 0; attempt < attempts; attempt++) {
            try {
                if (Directory.Exists(dir))
                    Directory.Delete(dir, recursive: true);
            }
            catch {
                // swallow and retry
            }

            if (!Directory.Exists(dir))
                return;

            if (attempt < attempts - 1)
                Thread.Sleep(delayMs);
        }

        try {
            if (Directory.Exists(dir))
                Directory.Delete(dir, recursive: true);
        }
        catch {
            // swallow
        }
    }

    internal static void RunRegisteredCleanup() {
        foreach (var registration in Registrations.Values) {
            DisposeCachedWorkerForTool(registration.ToolName, registration.ToolType);
        }

        var pendingRoots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var registration in Registrations.Values) {
            if (!string.IsNullOrWhiteSpace(registration.RootDir)) {
                pendingRoots.Add(registration.RootDir);
                continue;
            }

            try {
                var runtimeContext = new TelemetryRuntimeContext(registration.ToolName, registration.ToolType);
                runtimeContext.EnsureRootDirectoryResolvedOnWorkerThread();
                pendingRoots.Add(runtimeContext.GetRootDirectory());
            }
            catch {
                // swallow
            }
        }

        for (int attempt = 0; attempt < 50 && pendingRoots.Count > 0; attempt++) {
            foreach (var rootDir in pendingRoots.ToArray()) {
                TryDeleteDirectory(rootDir);
                if (!Directory.Exists(rootDir))
                    pendingRoots.Remove(rootDir);
            }

            if (pendingRoots.Count > 0 && attempt < 49)
                Thread.Sleep(100);
        }
    }

    private sealed record CleanupRegistration(string ToolName, Type ToolType, string? RootDir);
}

public sealed class IntegrationTestCleanupFixture : IDisposable {
    public void Dispose() {
        TestCleanup.RunRegisteredCleanup();
    }
}
