// Copyright (c) KeelMatrix

using FluentAssertions;
using KeelMatrix.Telemetry.ProjectIdentity;

namespace KeelMatrix.Telemetry.UnitTests;

public sealed class RepoKeyNormalizerTests {
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("\t")]
    [InlineData("\r\n")]
    public void TryNormalize_ReturnsFalse_OnNullOrWhitespace(string? input) {
        RepoKeyNormalizer.TryNormalize(input, out var normalized).Should().BeFalse();
        normalized.Should().BeNull();
    }

    [Theory]
    [InlineData("git@github.com:Owner/Repo.git", "https://github.com/owner/repo")]
    [InlineData("git@github.com:Owner/Repo", "https://github.com/owner/repo")]
    [InlineData("git@GitHub.com:OWNER/REPO.git", "https://github.com/owner/repo")]
    [InlineData("git@gitlab.example.com:Group/SubGroup/MyRepo.git", "https://gitlab.example.com/group/subgroup/myrepo")]
    public void TryNormalize_ConvertsScpLikeSsh_ToHttps(string input, string expected) {
        RepoKeyNormalizer.TryNormalize(input, out var normalized).Should().BeTrue();
        normalized.Should().Be(expected);
    }

    [Theory]
    // http/https stay but normalized to https
    [InlineData("http://github.com/Owner/Repo", "https://github.com/owner/repo")]
    [InlineData("https://github.com/Owner/Repo", "https://github.com/owner/repo")]

    // ssh:// and git:// normalized to https://
    [InlineData("ssh://github.com/Owner/Repo", "https://github.com/owner/repo")]
    [InlineData("git://github.com/Owner/Repo", "https://github.com/owner/repo")]

    // common with .git and trailing slash
    [InlineData("https://github.com/Owner/Repo.git/", "https://github.com/owner/repo")]
    [InlineData("ssh://github.com/Owner/Repo.git/", "https://github.com/owner/repo")]

    // preserve explicit port
    [InlineData("http://git.example.com:8080/Owner/Repo", "https://git.example.com:8080/owner/repo")]
    [InlineData("ssh://git.example.com:2222/Owner/Repo.git", "https://git.example.com:2222/owner/repo")]
    public void TryNormalize_ConvertsHttpHttpsSshGitSchemes_ToHttps(string input1, string expected) {
        RepoKeyNormalizer.TryNormalize(input1, out var normalized).Should().BeTrue();
        normalized.Should().Be(expected);
    }

    [Theory]
    [InlineData("https://github.com/owner/repo.git", "https://github.com/owner/repo")]
    [InlineData("https://github.com/owner/repo.git/", "https://github.com/owner/repo")]
    [InlineData("https://github.com/owner/repo/", "https://github.com/owner/repo")]
    [InlineData("git@github.com:owner/repo.git", "https://github.com/owner/repo")]
    public void TryNormalize_StripsDotGitSuffix_AndTrailingSlash(string input2, string expected) {
        RepoKeyNormalizer.TryNormalize(input2, out var normalized).Should().BeTrue();
        normalized.Should().Be(expected);
    }

    [Theory]
    [InlineData("https://GitHub.com/OWNER/Repo", "https://github.com/owner/repo")]
    [InlineData("http://GITLAB.EXAMPLE.COM/Group/SubGroup/Repo", "https://gitlab.example.com/group/subgroup/repo")]
    [InlineData("git@BitBucket.org:TEAM/Repo.git", "https://bitbucket.org/team/repo")]
    public void TryNormalize_LowercasesHostAndPath(string input3, string expected) {
        RepoKeyNormalizer.TryNormalize(input3, out var normalized).Should().BeTrue();
        normalized.Should().Be(expected);
    }

    [Theory]
    [InlineData("https://git.example.com:8080/owner/repo", "https://git.example.com:8080/owner/repo", ":8080")]
    [InlineData("http://git.example.com:8080/owner/repo", "https://git.example.com:8080/owner/repo", ":8080")]
    [InlineData("ssh://git.example.com:2222/owner/repo", "https://git.example.com:2222/owner/repo", ":2222")]
    [InlineData("git@git.example.com:2222/owner/repo.git", "https://git.example.com:2222/owner/repo", ":2222")]
    public void TryNormalize_PreservesNonDefaultPort(string input, string expected, string expectedPort) {
        RepoKeyNormalizer.TryNormalize(input, out var normalized).Should().BeTrue();
        normalized.Should().Be(expected);

        normalized.Should().Contain(expectedPort);
    }
}
