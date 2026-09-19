// Copyright (c) KeelMatrix

using KeelMatrix.Telemetry;

var outputPath = args.Length switch {
    0 => Path.Combine(Directory.GetCurrentDirectory(), TelemetryContractArtifact.RelativePath),
    2 when args[0].Equals("--output", StringComparison.Ordinal) => args[1],
    _ => throw new ArgumentException("Usage: --output <path>")
};

TelemetryContractArtifact.WriteTo(outputPath);
Console.WriteLine($"Wrote {Path.GetFullPath(outputPath)}");
