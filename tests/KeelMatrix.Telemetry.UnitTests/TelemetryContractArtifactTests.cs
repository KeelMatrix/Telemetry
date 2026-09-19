// Copyright (c) KeelMatrix

using System.Text.Json;
using FluentAssertions;

namespace KeelMatrix.Telemetry.UnitTests;

public sealed class TelemetryContractArtifactTests {
    [Fact]
    public void CanonicalArtifactMatchesValidatorGeneratedContract() {
        var repositoryRoot = FindRepositoryRoot();
        var artifactPath = Path.Combine(repositoryRoot, TelemetryContractArtifact.RelativePath);

        File.Exists(artifactPath).Should().BeTrue();
        var committedArtifact = File.ReadAllText(artifactPath);
        committedArtifact.Should().Be(TelemetryContractArtifact.GenerateJson());

        using var document = JsonDocument.Parse(committedArtifact);
        document.RootElement.GetProperty("schemaVersion").GetInt32()
            .Should().Be(TelemetryContractArtifact.SchemaVersion);
        document.RootElement.GetProperty("toolNameCases").GetArrayLength()
            .Should().Be(TelemetryContractArtifact.GetToolNameCases().Count);
        document.RootElement.GetProperty("hashCases").GetArrayLength()
            .Should().Be(TelemetryContractArtifact.GetHashCases().Count);
    }

    private static string FindRepositoryRoot() {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null) {
            if (File.Exists(Path.Combine(current.FullName, "KeelMatrix.Telemetry.slnx")))
                return current.FullName;

            current = current.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate the repository root.");
    }
}
