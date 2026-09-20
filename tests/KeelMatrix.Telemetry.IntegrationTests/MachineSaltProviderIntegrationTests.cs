// Copyright (c) KeelMatrix

using System.Diagnostics;
using System.Reflection;
using System.Runtime.Loader;
using System.Text;
using FluentAssertions;
using KeelMatrix.Telemetry.ProjectIdentity;

namespace KeelMatrix.Telemetry.IntegrationTests;

[CollectionDefinition(Name, DisableParallelization = true)]
public static class MachineSaltProviderIntegrationTestsCollectionDefinition {
    public const string Name = $"{nameof(MachineSaltProviderIntegrationTests)}.NonParallel";
}

[Collection(TelemetryDeliveryWorkerIntegrationTestsCollectionDefinition.Name)]
public sealed class MachineSaltProviderIntegrationTests {
    private const string EnvKeelMatrixNoTelemetry = "KEELMATRIX_NO_TELEMETRY";
    private const string EnvDotNetCliTelemetryOptOut = "DOTNET_CLI_TELEMETRY_OPTOUT";
    private const string EnvDoNotTrack = "DO_NOT_TRACK";
    private const string SaltChildRoleVariable = "KEELMATRIX_SALT_CHILD";
    private const string SaltChildToolVariable = "KEELMATRIX_SALT_TOOL";
    private const string SaltChildOutputVariable = "KEELMATRIX_SALT_OUTPUT";

    [Fact]
    public void GetOrCreateMachineSaltBytes_CreatesFileAndReturns32Bytes() {
        using var _ = new EnvironmentVariableSnapshot(EnvKeelMatrixNoTelemetry, EnvDotNetCliTelemetryOptOut, EnvDoNotTrack);
        ClearOptOutVars();

        using var runtime = TestRuntimeScope.Create(typeof(MachineSaltProviderIntegrationTests));

        var bytes = runtime.CreateMachineSaltProvider().GetOrCreateMachineSaltBytes();

        bytes.Should().NotBeNull();
        bytes.Length.Should().Be(TelemetryConfig.ExpectedSaltBytes);

        File.Exists(runtime.SaltPath).Should().BeTrue();

        var text = File.ReadAllText(runtime.SaltPath, Encoding.UTF8).Trim();
        text.Length.Should().Be(TelemetryConfig.ExpectedSaltBytes * 2, "persisted salt must be hex of 32 bytes");

        var decoded = Convert.FromHexString(text);
        decoded.Length.Should().Be(TelemetryConfig.ExpectedSaltBytes);
        decoded.Should().Equal(bytes);
    }

    [Fact]
    public void GetOrCreateMachineSaltBytes_IsStableAcrossCalls() {
        using var _ = new EnvironmentVariableSnapshot(EnvKeelMatrixNoTelemetry, EnvDotNetCliTelemetryOptOut, EnvDoNotTrack);
        ClearOptOutVars();

        using var runtime = TestRuntimeScope.Create(typeof(MachineSaltProviderIntegrationTests));

        var provider = runtime.CreateMachineSaltProvider();
        var first = provider.GetOrCreateMachineSaltBytes();
        var second = provider.GetOrCreateMachineSaltBytes();

        first.Should().Equal(second);

        // Also ensure the persisted value remains consistent.
        var text = File.ReadAllText(runtime.SaltPath, Encoding.UTF8).Trim();
        Convert.FromHexString(text).Should().Equal(first);
    }

    [Fact]
    public async Task IndependentlyCreatedProviders_ConvergeOnOnePersistedSalt() {
        using var _ = new EnvironmentVariableSnapshot(EnvKeelMatrixNoTelemetry, EnvDotNetCliTelemetryOptOut, EnvDoNotTrack);
        ClearOptOutVars();

        using var runtime = TestRuntimeScope.Create(typeof(MachineSaltProviderIntegrationTests));
        var results = await Task.WhenAll(Enumerable.Range(0, 32).Select(_ => Task.Run(() =>
            runtime.CreateMachineSaltProvider().GetOrCreateMachineSaltBytes())));

        results.Should().NotBeEmpty();
        foreach (var result in results)
            result.Should().Equal(results[0]);

        Convert.FromHexString(File.ReadAllText(runtime.SaltPath, Encoding.UTF8).Trim()).Should().Equal(results[0]);
    }

    [Fact]
    public async Task IndependentlyCreatedProvidersAcrossProcesses_ConvergeOnOnePersistedSalt() {
        using var _ = new EnvironmentVariableSnapshot(EnvKeelMatrixNoTelemetry, EnvDotNetCliTelemetryOptOut, EnvDoNotTrack);
        ClearOptOutVars();

        using var runtime = TestRuntimeScope.Create(typeof(MachineSaltProviderIntegrationTests));
        var outputPaths = Enumerable.Range(0, 8)
            .Select(index => Path.Combine(runtime.RootDir, $"salt-child-{index}.txt"))
            .ToArray();
        var children = outputPaths.Select(path => StartSaltChildProcess(runtime.ToolNameUpper, path)).ToArray();

        try {
            await Task.WhenAll(children.Select(WaitForSaltChildAsync));

            var results = outputPaths
                .Select(path => Convert.FromHexString(File.ReadAllText(path, Encoding.UTF8).Trim()))
                .ToArray();

            results.Should().NotBeEmpty();
            foreach (var result in results)
                result.Should().Equal(results[0]);

            Convert.FromHexString(File.ReadAllText(runtime.SaltPath, Encoding.UTF8).Trim()).Should().Equal(results[0]);
        }
        finally {
            foreach (var child in children) {
                try {
                    if (!child.HasExited)
                        child.Kill(entireProcessTree: true);
                }
                catch {
                    // The child may have exited between HasExited and Kill.
                }

                child.Dispose();
            }
        }
    }

    [Fact]
    public void SaltChild_CreatesMachineSalt() {
        if (!string.Equals(Environment.GetEnvironmentVariable(SaltChildRoleVariable), "1", StringComparison.Ordinal))
            return;

        var toolName = Environment.GetEnvironmentVariable(SaltChildToolVariable)
            ?? throw new InvalidOperationException("Salt child tool name is missing.");
        var outputPath = Environment.GetEnvironmentVariable(SaltChildOutputVariable)
            ?? throw new InvalidOperationException("Salt child output path is missing.");
        var runtimeContext = new TelemetryRuntimeContext(toolName, typeof(MachineSaltProviderIntegrationTests));
        runtimeContext.EnsureRootDirectoryResolvedOnWorkerThread();
        var bytes = new MachineSaltProvider(runtimeContext).GetOrCreateMachineSaltBytes();

        File.WriteAllText(outputPath, Convert.ToHexString(bytes).ToLowerInvariant(), Encoding.UTF8);
    }

    [Fact]
    public void CorruptSaltFile_IsRegeneratedAndRewritten() {
        using var _ = new EnvironmentVariableSnapshot(EnvKeelMatrixNoTelemetry, EnvDotNetCliTelemetryOptOut, EnvDoNotTrack);
        ClearOptOutVars();

        using var runtime = TestRuntimeScope.Create(typeof(MachineSaltProviderIntegrationTests));

        Directory.CreateDirectory(runtime.RootDir);
        File.WriteAllText(runtime.SaltPath, "not-hex", Encoding.UTF8);

        var bytes = runtime.CreateMachineSaltProvider().GetOrCreateMachineSaltBytes();

        bytes.Length.Should().Be(TelemetryConfig.ExpectedSaltBytes);

        var rewritten = File.ReadAllText(runtime.SaltPath, Encoding.UTF8).Trim();
        rewritten.Should().NotBe("not-hex");
        rewritten.Length.Should().Be(TelemetryConfig.ExpectedSaltBytes * 2);

        Convert.FromHexString(rewritten).Should().Equal(bytes);
    }

    [Fact]
    public void HeldPublicationLock_DisablesTelemetryAndReturnsWithoutThrowing() {
        using var _ = new EnvironmentVariableSnapshot(EnvKeelMatrixNoTelemetry, EnvDotNetCliTelemetryOptOut, EnvDoNotTrack);
        ClearOptOutVars();

        using var runtime = TestRuntimeScope.Create(typeof(MachineSaltProviderIntegrationTests));
        Directory.CreateDirectory(runtime.RootDir);
        using var heldLock = new FileStream(
            runtime.SaltPath + ".lock",
            FileMode.OpenOrCreate,
            FileAccess.ReadWrite,
            FileShare.None,
            bufferSize: 1,
            options: FileOptions.None);

        var stopwatch = Stopwatch.StartNew();
        var action = () => runtime.CreateMachineSaltProvider().GetOrCreateMachineSaltBytes();

        action.Should().NotThrow();
        stopwatch.Stop();

        stopwatch.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(7));
        TelemetryConfig.IsTelemetryDisabled().Should().BeTrue();
        File.Exists(runtime.SaltPath).Should().BeFalse();
    }

    [Fact]
    public void Netstandard2MachineSaltProvider_ExecutesPublicationBranch() {
        using var _ = new EnvironmentVariableSnapshot(EnvKeelMatrixNoTelemetry, EnvDotNetCliTelemetryOptOut, EnvDoNotTrack);
        ClearOptOutVars();

        var assemblyPath = FindNetstandardAssemblyPath();
        var loadContext = new AssemblyLoadContext(
            "KeelMatrix.Telemetry.netstandard-salt-test-" + Guid.NewGuid().ToString("N"),
            isCollectible: true);
        var rootDirectory = string.Empty;

        try {
            var assembly = loadContext.LoadFromAssemblyPath(assemblyPath);
            var runtimeContextType = assembly.GetType("KeelMatrix.Telemetry.TelemetryRuntimeContext")!;
            var providerType = assembly.GetType("KeelMatrix.Telemetry.ProjectIdentity.MachineSaltProvider")!;
            var toolName = "NETSTANDARD_SALT_" + Guid.NewGuid().ToString("N")[..16];
            var runtimeContext = runtimeContextType
                .GetConstructor(
                    BindingFlags.Instance | BindingFlags.NonPublic,
                    binder: null,
                    [typeof(string), typeof(Type)],
                    modifiers: null)!
                .Invoke([toolName, typeof(MachineSaltProviderIntegrationTests)]);

            Invoke(runtimeContext, "EnsureRootDirectoryResolvedOnWorkerThread");
            rootDirectory = (string)Invoke(runtimeContext, "GetRootDirectory")!;
            var provider = providerType
                .GetConstructor(
                    BindingFlags.Instance | BindingFlags.NonPublic,
                    binder: null,
                    [runtimeContextType],
                    modifiers: null)!
                .Invoke([runtimeContext]);

            var bytes = (byte[])Invoke(provider, "GetOrCreateMachineSaltBytes")!;

            bytes.Should().HaveCount(TelemetryConfig.ExpectedSaltBytes);
            File.Exists(Path.Combine(rootDirectory, "telemetry.salt")).Should().BeTrue();
        }
        finally {
            loadContext.Unload();
            TestCleanup.TryDeleteDirectory(rootDirectory);
        }
    }

    [Fact]
    public void WhenTelemetryDisabled_DoesNotDoIO_ReturnsRandomSalt() {
        using var _ = new EnvironmentVariableSnapshot(EnvKeelMatrixNoTelemetry, EnvDotNetCliTelemetryOptOut, EnvDoNotTrack);
        ClearOptOutVars();
        Environment.SetEnvironmentVariable(EnvKeelMatrixNoTelemetry, "1");

        using var runtime = TestRuntimeScope.Create(typeof(MachineSaltProviderIntegrationTests));

        // Ensure the filesystem is clean before the call.
        Directory.Exists(runtime.RootDir).Should().BeFalse();
        File.Exists(runtime.SaltPath).Should().BeFalse();

        var bytes = runtime.CreateMachineSaltProvider().GetOrCreateMachineSaltBytes();

        bytes.Length.Should().Be(TelemetryConfig.ExpectedSaltBytes);

        // Disabled policy: must not create directories or touch the salt file.
        Directory.Exists(runtime.RootDir).Should().BeFalse();
        File.Exists(runtime.SaltPath).Should().BeFalse();
    }

    private static void ClearOptOutVars() {
        Environment.SetEnvironmentVariable(EnvKeelMatrixNoTelemetry, null);
        Environment.SetEnvironmentVariable(EnvDotNetCliTelemetryOptOut, null);
        Environment.SetEnvironmentVariable(EnvDoNotTrack, null);
    }

    private static Process StartSaltChildProcess(string toolName, string outputPath) {
        var startInfo = new ProcessStartInfo {
            FileName = "dotnet",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        startInfo.ArgumentList.Add("vstest");
        startInfo.ArgumentList.Add(typeof(MachineSaltProviderIntegrationTests).Assembly.Location);
        startInfo.ArgumentList.Add(
            $"--TestCaseFilter:FullyQualifiedName~{typeof(MachineSaltProviderIntegrationTests).FullName}.{nameof(SaltChild_CreatesMachineSalt)}");
        startInfo.Environment[SaltChildRoleVariable] = "1";
        startInfo.Environment[SaltChildToolVariable] = toolName;
        startInfo.Environment[SaltChildOutputVariable] = outputPath;
        startInfo.Environment.Remove(EnvKeelMatrixNoTelemetry);
        startInfo.Environment.Remove(EnvDotNetCliTelemetryOptOut);
        startInfo.Environment.Remove(EnvDoNotTrack);

        return Process.Start(startInfo)
            ?? throw new InvalidOperationException("Could not start salt child process.");
    }

    private static async Task WaitForSaltChildAsync(Process process) {
        var standardOutput = process.StandardOutput.ReadToEndAsync();
        var standardError = process.StandardError.ReadToEndAsync();

        try {
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30));
        }
        catch {
            try {
                if (!process.HasExited)
                    process.Kill(entireProcessTree: true);
            }
            catch {
                // The child may have exited between HasExited and Kill.
            }

            throw;
        }

        var output = await standardOutput;
        var error = await standardError;
        process.ExitCode.Should().Be(0, "salt child output: stdout={0}; stderr={1}", output, error);
    }

    private static string FindNetstandardAssemblyPath() {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null) {
            if (File.Exists(Path.Combine(current.FullName, "KeelMatrix.Telemetry.slnx"))) {
                var configuration = Directory.GetParent(AppContext.BaseDirectory)!.Name;
                var candidate = Path.Combine(
                    current.FullName,
                    "src",
                    "KeelMatrix.Telemetry",
                    "bin",
                    configuration,
                    "netstandard2.0",
                    "KeelMatrix.Telemetry.dll");
                if (File.Exists(candidate))
                    return candidate;

                foreach (var fallbackConfiguration in new[] { "Release", "Debug" }) {
                    candidate = Path.Combine(
                        current.FullName,
                        "src",
                        "KeelMatrix.Telemetry",
                        "bin",
                        fallbackConfiguration,
                        "netstandard2.0",
                        "KeelMatrix.Telemetry.dll");
                    if (File.Exists(candidate))
                        return candidate;
                }
            }

            current = current.Parent;
        }

        throw new FileNotFoundException("The built netstandard2.0 Telemetry assembly was not found.");
    }

    private static object? Invoke(object target, string methodName, params object?[] args) {
        var method = target.GetType()
            .GetMethods(BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
            .Single(candidate =>
                candidate.Name == methodName &&
                candidate.GetParameters().Length == args.Length);
        return method.Invoke(target, args);
    }

    private sealed class EnvironmentVariableSnapshot : IDisposable {
        private readonly (string Name, string? Value)[] snapshot;

        public EnvironmentVariableSnapshot(params string[] names) {
            snapshot = new (string, string?)[names.Length];
            for (int i = 0; i < names.Length; i++) {
                var name = names[i];
                snapshot[i] = (name, Environment.GetEnvironmentVariable(name));
            }
        }

        public void Dispose() {
            for (var i = snapshot.Length - 1; i >= 0; i--) {
                var (Name, Value) = snapshot[i];
                Environment.SetEnvironmentVariable(Name, Value);
            }
        }
    }

}
