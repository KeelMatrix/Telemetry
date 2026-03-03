// Copyright (c) KeelMatrix

using System.Reflection;
using FluentAssertions;

namespace KeelMatrix.Telemetry.CiEnvironmentTests;

// Shares the non-parallel CI environment collection with other tests in this project.
[Collection("CI environment")]
public sealed class RuntimeInfoCiDetectionTests {
    [Theory]
    [InlineData("CI", "true")]
    [InlineData("GITHUB_ACTIONS", "1")]
    [InlineData("TF_BUILD", "1")]
    [InlineData("BUILD_BUILDID", "123")]
    [InlineData("JENKINS_URL", "https://jenkins.example")]
    public void IsCi_IsTrue_WhenCommonCiEnvVarsSet(string name, string value) {
        using var _ = EnvVarScope.Clean((name, value));

        // Validate the CI detection logic directly. RuntimeInfo caches CI detection in a static
        // readonly field at type init time, so tests must not rely on RuntimeInfo.IsCi reflecting
        // mid-process environment changes.
        InvokeDetectCi().Should().BeTrue();
    }

    private static bool InvokeDetectCi() {
        var mi = typeof(RuntimeInfo).GetMethod(
            "DetectCi",
            BindingFlags.NonPublic | BindingFlags.Static);

        mi.Should().NotBeNull("RuntimeInfo.DetectCi must exist for CI detection");

        return (bool)mi!.Invoke(null, null)!;
    }

    private sealed class EnvVarScope : IDisposable {
        private static readonly string[] KnownCiDetectVars = [
            "CI",
            "GITHUB_ACTIONS",
            "TF_BUILD",
            "BUILD_BUILDID",
            "JENKINS_URL"
        ];

        public static EnvVarScope Clean(params (string Name, string? Value)[] changes) {
            // Clear all vars first to avoid test pollution from the host environment.
            var allChanges = new List<(string Name, string? Value)>(KnownCiDetectVars.Length + changes.Length);
            foreach (var n in KnownCiDetectVars) allChanges.Add((n, null));
            allChanges.AddRange(changes);
            return new EnvVarScope([.. allChanges]);
        }

        private readonly (string Name, string? Value)[] saved;

        public EnvVarScope(params (string Name, string? Value)[] changes) {
            saved = new (string, string?)[changes.Length];

            for (var i = 0; i < changes.Length; i++) {
                var (name, value) = changes[i];
                saved[i] = (name, Environment.GetEnvironmentVariable(name));
                Environment.SetEnvironmentVariable(name, value);
            }
        }

        public void Dispose() {
            for (var i = 0; i < saved.Length; i++) {
                var (name, value) = saved[i];
                Environment.SetEnvironmentVariable(name, value);
            }
        }
    }
}
