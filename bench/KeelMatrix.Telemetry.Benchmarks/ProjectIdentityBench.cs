// Copyright (c) KeelMatrix

using BenchmarkDotNet.Attributes;
using KeelMatrix.Telemetry.ProjectIdentity;

namespace KeelMatrix.Telemetry.Benchmarks;

[MemoryDiagnoser]
public class ProjectIdentityBench {
    private string tempRoot = null!;
    private string repoDir = null!;
    private BenchmarkRuntimeScope? cachedScope;
    private ProjectIdentityProvider? cachedProvider;
    private enum IdentityScenario {
        Cold,
        Warm
    }

    [GlobalSetup]
    public void GlobalSetup() {
        BenchmarkSupport.ResetTelemetryProcessState();

        tempRoot = Path.Combine(Path.GetTempPath(), "KeelMatrix.Telemetry.ProjectIdentityBench", Guid.NewGuid().ToString("N"));
        repoDir = Path.Combine(tempRoot, "repo");
        Directory.CreateDirectory(Path.Combine(repoDir, ".git"));
        File.WriteAllText(
            Path.Combine(repoDir, ".git", "config"),
            """
            [remote "origin"]
                url = https://github.com/KeelMatrix/Telemetry.git
            """);
        File.WriteAllText(
            Path.Combine(repoDir, "App.csproj"),
            """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net8.0</TargetFramework>
              </PropertyGroup>
            </Project>
            """);
    }

    [IterationSetup(Target = nameof(EnsureResolvedOnWorkerThread_ColdSyntheticGitRepo))]
    public void SetupCold() {
        PrepareProvider(IdentityScenario.Cold);
    }

    [IterationSetup(Target = nameof(EnsureResolvedOnWorkerThread_WarmCachedIdentity))]
    public void SetupWarm() {
        cachedScope?.Dispose();
        PrepareProvider(IdentityScenario.Warm);
    }

    [Benchmark]
    public string? EnsureResolvedOnWorkerThread_ColdSyntheticGitRepo() {
        using var scope = new BenchmarkRuntimeScope("BENCH_PROJECT_ID_COLD", typeof(ProjectIdentityBench));
        using var _ = new StartingPointsOverrideScope(repoDir);
        return new ProjectIdentityProvider(scope.RuntimeContext, scope.RuntimeInfo)
            .EnsureResolvedOnWorkerThread()
            .ProjectHash;
    }

    [Benchmark]
    public string? EnsureResolvedOnWorkerThread_WarmCachedIdentity() =>
        cachedProvider!.EnsureResolvedOnWorkerThread().ProjectHash;

    [GlobalCleanup]
    public void GlobalCleanup() {
        cachedScope?.Dispose();
        BenchmarkSupport.TryDeleteDirectory(tempRoot);
        BenchmarkSupport.RestoreGlobalOverrides();
    }

    private void PrepareProvider(IdentityScenario scenario) {
        BenchmarkSupport.ResetTelemetryProcessState();
        if (scenario == IdentityScenario.Cold)
            return;

        cachedScope = new BenchmarkRuntimeScope("BENCH_PROJECT_ID_WARM", typeof(ProjectIdentityBench));
        using var _ = new StartingPointsOverrideScope(repoDir);
        cachedProvider = new ProjectIdentityProvider(cachedScope.RuntimeContext, cachedScope.RuntimeInfo);
        cachedProvider.EnsureResolvedOnWorkerThread();
    }
}
