// Copyright (c) KeelMatrix

using System.Runtime.InteropServices;

namespace KeelMatrix.Telemetry {
    internal static class RuntimeInfo {
        internal static string Runtime { get; } = DetectRuntime();
        internal static string Os { get; } = DetectOs();
        internal static bool IsCi { get; } = DetectCi();

        private static string DetectRuntime() {
            try {
                return RuntimeInformation.FrameworkDescription switch {
                    string s when s.Contains(".NET", StringComparison.OrdinalIgnoreCase)
                        => NormalizeRuntimeString(s),
                    _ => "dotnet"
                };
            }
            catch {
                return TelemetryConfig.UnknownSymbol;
            }
        }

        private static string DetectOs() {
            try {
                if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                    return "windows";
                if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
                    return "linux";
                if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
                    return "osx";

                return TelemetryConfig.UnknownSymbol;
            }
            catch {
                return TelemetryConfig.UnknownSymbol;
            }
        }

        private static bool DetectCi() {
            try {
                return HasEnv("CI") ||
                       HasEnv("GITHUB_ACTIONS") ||
                       HasEnv("TF_BUILD") ||
                       HasEnv("BUILD_BUILDID") ||
                       HasEnv("JENKINS_URL");
            }
            catch { return false; }
        }

        private static bool HasEnv(string name)
            => !string.IsNullOrEmpty(Environment.GetEnvironmentVariable(name));

        private static string NormalizeRuntimeString(string value) {
#pragma warning disable IDE0057 // Use range operator
            return value.Length <= TelemetryConfig.RuntimeMaxLength
                ? value
                : value.Substring(0, TelemetryConfig.RuntimeMaxLength);
#pragma warning restore IDE0057
        }
    }
}
