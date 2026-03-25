// Copyright (c) KeelMatrix

using BenchmarkDotNet.Running;

namespace KeelMatrix.Telemetry.Benchmarks;

public static class Program {
    public static void Main(string[] args) =>
        BenchmarkSwitcher.FromAssembly(typeof(Program).Assembly).Run(args, new CiAwareConfig());
}
