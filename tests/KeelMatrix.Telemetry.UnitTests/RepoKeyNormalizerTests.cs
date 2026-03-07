// Copyright (c) KeelMatrix

using System.Text;
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

    [Theory]
    [InlineData("https://user:token@github.com/Owner/Repo.git", "https://github.com/owner/repo")]
    [InlineData("http://user:token@gitlab.example.com/Group/Repo", "https://gitlab.example.com/group/repo")]
    [InlineData("ssh://buildbot@git.example.com/Owner/Repo.git", "https://git.example.com/owner/repo")]
    [InlineData("git://user:password@git.example.com/Owner/Repo.git", "https://git.example.com/owner/repo")]
    public void TryNormalize_StripsCredentialsFromUrlLikeInputs(string input, string expected) {
        RepoKeyNormalizer.TryNormalize(input, out var normalized).Should().BeTrue();
        normalized.Should().Be(expected);

        Uri.TryCreate(normalized, UriKind.Absolute, out var uri).Should().BeTrue();
        uri!.UserInfo.Should().BeEmpty();
    }

    [Fact]
    public void TryNormalize_FuzzInputs_NeverThrow_AndProducesStableCredentialFreeResults() {
        var random = new Random(12345);

        foreach (var input in GenerateFuzzInputs(random, 300)) {
            bool firstOk = false;
            string? firstNormalized = null;

            var act = () => firstOk = RepoKeyNormalizer.TryNormalize(input, out firstNormalized);
            act.Should().NotThrow($"input '{input}' should never crash normalization");

            var secondOk = RepoKeyNormalizer.TryNormalize(input, out var secondNormalized);
            secondOk.Should().Be(firstOk, $"input '{input}' should be stable across calls");
            secondNormalized.Should().Be(firstNormalized);

            if (!firstOk) {
                firstNormalized.Should().BeNull();
                continue;
            }

            firstNormalized.Should().Be(firstNormalized!.ToLowerInvariant());
            firstNormalized.Should().NotContain("@");

            if (Uri.TryCreate(firstNormalized, UriKind.Absolute, out var normalizedUri)) {
                normalizedUri.Scheme.Should().Be("https");
                normalizedUri.UserInfo.Should().BeEmpty();
            }
        }
    }

    private static IEnumerable<string> GenerateFuzzInputs(Random random, int count) {
        const string pathChars = "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789-._~";

        for (int i = 0; i < count; i++) {
            int shape = i % 6;
            string owner = RandomSegment(random, pathChars, 3, 10);
            string repo = RandomSegment(random, pathChars, 3, 12);
            string host = $"{RandomSegment(random, "abcdefghijklmnopqrstuvwxyz", 4, 8)}.{RandomSegment(random, "abcdefghijklmnopqrstuvwxyz", 3, 6)}";

            yield return shape switch {
                0 => $"https://{host}/{owner}/{repo}.git",
                1 => $"ssh://user:{RandomSegment(random, pathChars, 6, 12)}@{host}/{owner}/{repo}",
                2 => $"git@{host}:{owner}/{repo}.git",
                3 => $"{RandomSegment(random, pathChars + ":/@\\?&% ", 5, 30)}",
                4 => $"http://{host}:{random.Next(1, 65536)}/{owner}/{repo}/",
                _ => $"git://user:password@{host}/{owner}/{repo}.git"
            };
        }
    }

    private static string RandomSegment(Random random, string alphabet, int minLength, int maxLength) {
        int length = random.Next(minLength, maxLength + 1);
        var builder = new StringBuilder(length);

        for (int i = 0; i < length; i++)
            builder.Append(alphabet[random.Next(alphabet.Length)]);

        return builder.ToString();
    }
}
