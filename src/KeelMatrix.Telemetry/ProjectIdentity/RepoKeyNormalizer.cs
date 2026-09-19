// Copyright (c) KeelMatrix

using System.Globalization;

namespace KeelMatrix.Telemetry.ProjectIdentity {
    internal static class RepoKeyNormalizer {
        /// <summary>
        /// Normalizes CI/Git repo identity into a stable "repo key" form:
        /// - lower-case host
        /// - strip credentials/tokens if present
        /// - normalize ssh/https patterns into: https://host/owner/repo
        /// - remove trailing .git
        /// - trim trailing /
        /// </summary>
        internal static bool TryNormalize(string? raw, out string? normalizedRepoKey) {
            normalizedRepoKey = null;

            if (string.IsNullOrWhiteSpace(raw))
                return false;

            raw = raw!.Trim();

            // SCP-like: git@host:owner/repo(.git). A numeric first path segment is
            // repository path text; explicit SSH ports use ssh://host:port/path.
            if (LooksLikeScpSsh(raw, out var host, out var path)) {
                return TryNormalizeHostAndPath(host, path, out normalizedRepoKey);
            }

            // URL-like: https://..., http://..., ssh://..., git://...
            if (Uri.TryCreate(raw, UriKind.Absolute, out var uri)) {
                return TryNormalizeFromUri(uri, out normalizedRepoKey);
            }

            return false;
        }

        private static bool LooksLikeScpSsh(string raw, out string host, out string path) {
            host = string.Empty;
            path = string.Empty;

            // Must contain user@host:path and must NOT contain :// (to avoid confusing with URLs).
            int schemeSep = raw.IndexOf("://", StringComparison.Ordinal);
            if (schemeSep >= 0)
                return false;

            int at = raw.IndexOf('@');
            if (at <= 0 || at == raw.Length - 1)
                return false;

            int colon = raw.IndexOf(':', at + 1);
            if (colon <= at + 1 || colon == raw.Length - 1)
                return false;

#pragma warning disable IDE0057 // Use range operator
            host = raw.Substring(at + 1, colon - (at + 1));
            path = raw.Substring(colon + 1);
#pragma warning restore IDE0057

            return !string.IsNullOrWhiteSpace(host) && !string.IsNullOrWhiteSpace(path);
        }

        private static bool TryNormalizeFromUri(Uri uri, out string? normalized) {
            normalized = null;

            // Only accept well-known schemes we can safely map.
            var scheme = uri.Scheme?.ToLowerInvariant() ?? string.Empty;
            if (scheme != "http" && scheme != "https" && scheme != "ssh" && scheme != "git")
                return false;

            string host = uri.Host;
            if (string.IsNullOrWhiteSpace(host))
                return false;

            string path = uri.AbsolutePath;
            if (string.IsNullOrWhiteSpace(path))
                return false;

            // Canonicalize to https.
            host = host.ToLowerInvariant();

            // Include non-default port to avoid collisions in self-hosted setups.
            string hostWithPort = host;
            if (!uri.IsDefaultPort) {
                hostWithPort = host + ":" + uri.Port.ToString(CultureInfo.InvariantCulture);
            }

            path = path.Trim('/');
            if (path.Length == 0)
                return false;

            path = StripDotGitSuffix(path);
            if (path.Length == 0)
                return false;

            path = path.ToLowerInvariant();
            normalized = "https://" + hostWithPort + "/" + path.TrimEnd('/');
            return true;
        }

        private static bool TryNormalizeHostAndPath(string host, string path, out string? normalized) {
            normalized = null;

            host = (host ?? string.Empty).Trim();
            path = (path ?? string.Empty).Trim();

            if (host.Length == 0 || path.Length == 0)
                return false;

            host = host.ToLowerInvariant();
            path = path.Trim('/');

            if (path.Length == 0)
                return false;

            path = StripDotGitSuffix(path);
            if (path.Length == 0)
                return false;

            path = path.ToLowerInvariant();
            normalized = "https://" + host + "/" + path.TrimEnd('/');
            return true;
        }

        private static string StripDotGitSuffix(string path) {
            if (path.EndsWith(".git", StringComparison.OrdinalIgnoreCase)) {
#pragma warning disable IDE0057 // Use range operator
                path = path.Substring(0, path.Length - 4);
#pragma warning restore IDE0057
            }
            return path;
        }
    }
}
