# Contributing

Thank you for considering contributing to this project! We welcome contributions in the form of bug reports, feature requests, documentation improvements and pull requests.

## Before you begin

1. Ensure there is no existing issue addressing the same problem. If there is, feel free to add additional context or details.
2. Read through our [Code of Conduct](CODE_OF_CONDUCT.md) to understand our expectations for participant behavior.
3. For security related issues, see [SECURITY.md](SECURITY.md) and use the recommended disclosure channels instead of filing a public issue.

## Making Changes

1. Fork the repository and create your feature branch from `main`: `git checkout -b my-feature`.
2. If you've added code that should be tested, add tests.
3. Ensure the test suite passes by running `dotnet test KeelMatrix.Telemetry.slnx --configuration Release`.
4. Ensure the solution builds in release mode: `dotnet build KeelMatrix.Telemetry.slnx --configuration Release`.
5. If you changed packable code or package metadata, verify the NuGet package builds locally:
   `dotnet pack src/KeelMatrix.Telemetry/KeelMatrix.Telemetry.csproj --configuration Release --no-build --include-symbols --p:SymbolPackageFormat=snupkg --output ./artifacts/packages`
6. Update any relevant documentation (README, examples, etc.).

## Submitting a Pull Request

1. Open a pull request against the `main` branch. Describe what your change does and reference any related issues.
2. Fill out the pull request template checklist.
3. One of the project maintainers will review your changes and provide feedback.
4. Make any requested changes and update the pull request.
5. Once approved, your changes will be merged and included in the next release.

We appreciate your time and effort to improve this project! If you're unsure how to get started or have questions, feel free to open an issue to discuss your idea.




## Public API surface

This repository uses **Roslyn Public API Analyzers** to lock down the surface area.
When you add or change a public member in a packable project:

1. Make your code changes.
2. Update the `PublicAPI.Unshipped.txt` file in that project directory with the new API signatures reported by the analyzer.
3. Review the diff and commit it with the code change.
4. When cutting a release, move shipped entries from `PublicAPI.Unshipped.txt` to `PublicAPI.Shipped.txt`.

> Tips
> - We keep a single `PublicAPI.Shipped.txt`/`PublicAPI.Unshipped.txt` pair per project across TFMs.
> - If a member is TFM-specific, append a trailing comment to its line: `// TFM: net8.0` or `// TFM: netstandard2.0`.

## Release and versioning

- Package versioning is centralized in `Directory.Build.props`.
- CI validates pushes and pull requests on Windows, Linux, and macOS, then packs the NuGet package.
- Publishing happens only when a Git tag matching `v*` is pushed. That workflow pushes the package to NuGet.org and creates a GitHub release.
