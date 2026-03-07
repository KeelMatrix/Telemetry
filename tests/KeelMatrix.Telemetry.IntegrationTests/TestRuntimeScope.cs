// Copyright (c) KeelMatrix

using KeelMatrix.Telemetry.Infrastructure;
using KeelMatrix.Telemetry.ProjectIdentity;

namespace KeelMatrix.Telemetry.IntegrationTests;

internal sealed class TestRuntimeScope : IDisposable {
    private TestRuntimeScope(string toolNameUpper, Type toolType) {
        ResetProcessDisabledForTests();

        ToolNameUpper = toolNameUpper;
        RuntimeContext = new TelemetryRuntimeContext(toolNameUpper, toolType);
        RuntimeInfo = new RuntimeInfo();
        ProjectHash = "proj_" + Guid.NewGuid().ToString("N");
        CurrentWeek = TelemetryClock.GetCurrentIsoWeek();

        RuntimeContext.EnsureRootDirectoryResolvedOnWorkerThread();
        RootDir = RuntimeContext.GetRootDirectory();

        TryDeleteDirectory(RootDir);
    }

    public string ToolNameUpper { get; }
    public TelemetryRuntimeContext RuntimeContext { get; }
    public RuntimeInfo RuntimeInfo { get; }
    public string RootDir { get; }
    public string ProjectHash { get; }
    public string CurrentWeek { get; }
    public string MarkerDir => Path.Combine(RootDir, "markers");
    public string QueueRootDir => Path.Combine(RootDir, "telemetry.queue");
    public string PendingDir => Path.Combine(QueueRootDir, "pending");
    public string ProcessingDir => Path.Combine(QueueRootDir, "processing");
    public string DeadDir => Path.Combine(QueueRootDir, "dead");
    public string SaltPath => Path.Combine(RootDir, "telemetry.salt");

    public static TestRuntimeScope Create(Type toolType, string prefix = "INTEGRATIONTEST_") {
        return new TestRuntimeScope(prefix + Guid.NewGuid().ToString("N"), toolType);
    }

    public TelemetryDeliveryWorker CreateWorker() {
        return new TelemetryDeliveryWorker(RuntimeContext, RuntimeInfo);
    }

    public ITelemetryQueue CreateQueue() {
        return DurableTelemetryQueue.CreateSafe(RuntimeContext);
    }

    public MachineSaltProvider CreateMachineSaltProvider() {
        return new MachineSaltProvider(RuntimeContext);
    }

    public void Dispose() {
        TryDeleteDirectory(RootDir);
    }

    private static void TryDeleteDirectory(string dir) {
        try {
            if (Directory.Exists(dir))
                Directory.Delete(dir, recursive: true);
        }
        catch {
            // swallow
        }
    }

    private static void ResetProcessDisabledForTests() {
        var field = typeof(TelemetryConfig).GetField("processDisabled", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
        if (field is not null)
            field.SetValue(null, 0);
    }
}
