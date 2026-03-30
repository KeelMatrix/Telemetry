// Copyright (c) KeelMatrix

using System.Linq;
using System.Text.Json;
using GitDiscovery = KeelMatrix.Telemetry.ProjectIdentity.GitDiscovery;

namespace KeelMatrix.Telemetry {
    internal static class TelemetryDisableResolver {
        private const string RepositoryConfigFileName = "keelmatrix.telemetry.json";
        private const string DotEnvFileName = ".env";
        private const string DotEnvLocalFileName = ".env.local";
        private const int MaxRepositoryConfigBytes = 16 * 1024;
        private const int MaxRepositoryEnvFileBytes = 16 * 1024;

        private static readonly string[] OptOutVariableNames = [
            "KEELMATRIX_NO_TELEMETRY",
            "DOTNET_CLI_TELEMETRY_OPTOUT",
            "DO_NOT_TRACK"
        ];
        private static Func<bool?>? repositoryDisableOverrideForTests;

        internal static bool IsProcessTelemetryDisabled() {
            return GetProcessTelemetryDisableDecision() == true;
        }

        internal static bool? GetProcessTelemetryDisableDecision() {
            return EvaluateProcessEnvironmentDecision() switch {
                { IsEnabled: false } => true,
                { IsEnabled: true } => false,
                _ => null
            };
        }

        internal static RepositoryTelemetryStatus GetEffectiveStatus(string repositoryRoot) {
            var normalizedRepositoryRoot = NormalizeRepositoryRoot(repositoryRoot);
            var processEnvironmentDecision = EvaluateProcessEnvironmentDecision();
            if (processEnvironmentDecision is not null)
                return CreateStatus(normalizedRepositoryRoot, processEnvironmentDecision.Value);

            var repositoryDecision = EvaluateRepositoryDecision(normalizedRepositoryRoot);
            if (repositoryDecision is not null)
                return CreateStatus(normalizedRepositoryRoot, repositoryDecision.Value);

            return new RepositoryTelemetryStatus(
                isEnabled: true,
                winningSourceKind: RepositoryTelemetrySourceKind.None,
                winningPath: null,
                winningVariableName: null,
                scope: RepositoryTelemetryScope.RepoLocalDefault,
                repoRoot: normalizedRepositoryRoot);
        }

        internal static bool IsRepositoryTelemetryDisabledOnWorkerThread() {
            return IsRepositoryTelemetryDisabledOnWorkerThread(GetCandidateRepositoryRoots());
        }

        internal static bool IsRepositoryTelemetryDisabledOnWorkerThread(IReadOnlyList<string> repositoryRoots) {
            var overrideResolver = Volatile.Read(ref repositoryDisableOverrideForTests);
            if (overrideResolver is not null) {
                try {
                    return overrideResolver() == true;
                }
                catch {
                    return false;
                }
            }

#pragma warning disable S3267 // Loops should be simplified with "LINQ" expressions
            foreach (var repositoryRoot in repositoryRoots) {
                if (EvaluateRepositoryDecision(repositoryRoot) is { IsEnabled: false })
                    return true;
            }
#pragma warning restore S3267 // Loops should be simplified with "LINQ" expressions

            return false;
        }

        internal static IReadOnlyList<string> GetCandidateRepositoryRoots() {
            var repositoryRoots = new List<string>();
            var seen = new HashSet<string>(
                Path.DirectorySeparatorChar == '\\'
                    ? StringComparer.OrdinalIgnoreCase
                    : StringComparer.Ordinal);

            foreach (var startingPoint in GitDiscovery.GetStartingPoints()) {
                if (!GitDiscovery.TryFindRepositoryRoot(startingPoint, out var repositoryRoot))
                    continue;

                if (seen.Add(repositoryRoot))
                    repositoryRoots.Add(repositoryRoot);
            }

            return repositoryRoots;
        }

        internal static void SetRepositoryDisableOverrideForTests(Func<bool?>? resolver) {
            Volatile.Write(ref repositoryDisableOverrideForTests, resolver);
        }

        private static TelemetryDecision? EvaluateProcessEnvironmentDecision() {
            bool anyPresent = false;
            string? firstPresentVariable = null;

            foreach (var variableName in OptOutVariableNames) {
                string? value;
                try {
                    value = Environment.GetEnvironmentVariable(variableName);
                }
                catch {
                    continue;
                }

                if (value is null)
                    continue;

                anyPresent = true;
                firstPresentVariable ??= variableName;
                if (IsTruthyValue(value))
                    return new TelemetryDecision(
                        IsEnabled: false,
                        WinningSourceKind: RepositoryTelemetrySourceKind.ProcessEnvironment,
                        Scope: RepositoryTelemetryScope.ProcessEnvironment,
                        WinningPath: null,
                        WinningVariableName: variableName);
            }

            if (!anyPresent || firstPresentVariable is null)
                return null;

            return new TelemetryDecision(
                IsEnabled: true,
                WinningSourceKind: RepositoryTelemetrySourceKind.ProcessEnvironment,
                Scope: RepositoryTelemetryScope.ProcessEnvironment,
                WinningPath: null,
                WinningVariableName: firstPresentVariable);
        }

        private static TelemetryDecision? EvaluateRepositoryDecision(string repositoryRoot) {
            if (string.IsNullOrWhiteSpace(repositoryRoot))
                return null;

            try {
                return EvaluateRepositoryConfig(Path.Combine(repositoryRoot, RepositoryConfigFileName))
                    ?? EvaluateDotEnvFile(
                        Path.Combine(repositoryRoot, DotEnvLocalFileName),
                        RepositoryTelemetrySourceKind.DotEnvLocal)
                    ?? EvaluateDotEnvFile(
                        Path.Combine(repositoryRoot, DotEnvFileName),
                        RepositoryTelemetrySourceKind.DotEnv);
            }
            catch {
                return null;
            }
        }

        private static TelemetryDecision? EvaluateRepositoryConfig(string path) {
            if (!TryReadTextFileCapped(path, MaxRepositoryConfigBytes, out var text))
                return null;

            try {
                using var doc = JsonDocument.Parse(text);
                if (doc.RootElement.ValueKind != JsonValueKind.Object)
                    return null;

                if (!TryGetPropertyCaseInsensitive(doc.RootElement, "disabled", out var disabledElement))
                    return null;

                bool isDisabled = IsTruthyJsonValue(disabledElement);
                return new TelemetryDecision(
                    IsEnabled: !isDisabled,
                    WinningSourceKind: RepositoryTelemetrySourceKind.RepositoryConfig,
                    Scope: RepositoryTelemetryScope.RepoLocal,
                    WinningPath: path,
                    WinningVariableName: null);
            }
            catch {
                return null;
            }
        }

        private static TelemetryDecision? EvaluateDotEnvFile(string path, RepositoryTelemetrySourceKind sourceKind) {
            if (!TryReadTextFileCapped(path, MaxRepositoryEnvFileBytes, out var text))
                return null;

            bool anyRecognizedAssignment = false;
            string? firstRecognizedVariable = null;

            using var reader = new StringReader(text);
            string? line;
            while ((line = reader.ReadLine()) != null) {
                var trimmed = line.Trim();
                if (trimmed.Length == 0 || trimmed[0] == '#')
                    continue;

                if (trimmed.StartsWith("export ", StringComparison.OrdinalIgnoreCase))
                    trimmed = trimmed.Substring("export ".Length).TrimStart();

                int equalsIndex = trimmed.IndexOf('=');
                if (equalsIndex <= 0)
                    continue;

                var key = trimmed.Substring(0, equalsIndex).Trim();
                if (!IsRecognizedOptOutKey(key))
                    continue;

                anyRecognizedAssignment = true;
                firstRecognizedVariable ??= key;

                var value = NormalizeDotEnvValue(trimmed.Substring(equalsIndex + 1));
                if (IsTruthyValue(value))
                    return new TelemetryDecision(
                        IsEnabled: false,
                        WinningSourceKind: sourceKind,
                        Scope: RepositoryTelemetryScope.RepoLocal,
                        WinningPath: path,
                        WinningVariableName: key);
            }

            if (!anyRecognizedAssignment || firstRecognizedVariable is null)
                return null;

            return new TelemetryDecision(
                IsEnabled: true,
                WinningSourceKind: sourceKind,
                Scope: RepositoryTelemetryScope.RepoLocal,
                WinningPath: path,
                WinningVariableName: firstRecognizedVariable);
        }

        private static RepositoryTelemetryStatus CreateStatus(string repositoryRoot, TelemetryDecision decision) {
            return new RepositoryTelemetryStatus(
                isEnabled: decision.IsEnabled,
                winningSourceKind: decision.WinningSourceKind,
                winningPath: decision.WinningPath,
                winningVariableName: decision.WinningVariableName,
                scope: decision.Scope,
                repoRoot: repositoryRoot);
        }

        private static string NormalizeRepositoryRoot(string repositoryRoot) {
            if (string.IsNullOrWhiteSpace(repositoryRoot))
                return string.Empty;

            try {
                return Path.GetFullPath(repositoryRoot);
            }
            catch {
                return repositoryRoot;
            }
        }

        private static bool TryReadTextFileCapped(string path, int maxBytes, out string text) {
            text = string.Empty;

            try {
                var fi = new FileInfo(path);
                if (!fi.Exists)
                    return false;

                if (fi.Length <= 0 || fi.Length > maxBytes)
                    return false;

                text = File.ReadAllText(path);
                return text.Length > 0;
            }
            catch {
                return false;
            }
        }

        private static bool TryGetPropertyCaseInsensitive(JsonElement element, string name, out JsonElement value) {
            var property = element.EnumerateObject()
                .FirstOrDefault(property => property.Name.Equals(name, StringComparison.OrdinalIgnoreCase));

            if (!property.Equals(default(JsonProperty))) {
                value = property.Value;
                return true;
            }

            value = default;
            return false;
        }

        private static string NormalizeDotEnvValue(string value) {
            value = value.Trim();

            if (value.Length >= 2) {
                char first = value[0];
                char last = value[value.Length - 1];
                if ((first == '"' && last == '"') || (first == '\'' && last == '\'')) {
                    value = value.Substring(1, value.Length - 2).Trim();
                }
            }

            return value;
        }

        private static bool IsTruthyJsonValue(JsonElement element) {
            return element.ValueKind switch {
                JsonValueKind.True => true,
                JsonValueKind.String => IsTruthyValue(element.GetString()),
                JsonValueKind.Number => IsTruthyValue(element.GetRawText()),
                _ => false
            };
        }

        private static bool IsRecognizedOptOutKey(string key) {
            return OptOutVariableNames.Any(variableName => key.Equals(variableName, StringComparison.Ordinal));
        }

        private static bool IsTruthyValue(string? value) {
            if (string.IsNullOrWhiteSpace(value))
                return false;

            value = value!.Trim();

            return value == "1"
                || value.Equals("true", StringComparison.OrdinalIgnoreCase)
                || value.Equals("yes", StringComparison.OrdinalIgnoreCase)
                || value.Equals("y", StringComparison.OrdinalIgnoreCase)
                || value.Equals("on", StringComparison.OrdinalIgnoreCase);
        }

        private readonly record struct TelemetryDecision(
            bool IsEnabled,
            RepositoryTelemetrySourceKind WinningSourceKind,
            RepositoryTelemetryScope Scope,
            string? WinningPath,
            string? WinningVariableName);
    }
}
