// Copyright (c) KeelMatrix

using System.Runtime.InteropServices;

namespace KeelMatrix.Telemetry {
    internal sealed class RuntimeInfo {
        private readonly bool detectedCi = DetectCi();

        internal string Runtime { get; } = DetectRuntime();
        internal string Os { get; } = DetectOs();

        // -1 = no override, 0 = false, 1 = true
        private int ciOverride = -1;

        internal bool IsCi {
            get {
                var o = Volatile.Read(ref ciOverride);
                return o switch {
                    0 => false,
                    1 => true,
                    _ => detectedCi
                };
            }
        }

        internal void SetCiOverrideForTests(bool? isCi) {
#pragma warning disable S3358 // Ternary operators should not be nested
            Volatile.Write(ref ciOverride, isCi is null ? -1 : (isCi.Value ? 1 : 0));
#pragma warning restore S3358
        }

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
