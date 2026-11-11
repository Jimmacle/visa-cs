# Contributing

## Building and testing

```
dotnet build VisaCs.slnx
dotnet test tests/VisaCs.Tests
```

The default suite needs no hardware: it runs against the simulated instruments in `VisaCs.Simulation`. The optional
suites (real serial ports, kernel USB through USB/IP, the GPIB binding against a stand-in driver, PCI) are described
in [docs/simulator-and-testing.md](docs/simulator-and-testing.md).

## Continuous integration

`.github/workflows/ci.yml` builds and tests on Windows, Linux and macOS for every push to `main` and every pull
request from a branch of this repository. `.github/workflows/release.yml` runs when a `vX.Y.Z` tag is pushed: it
tests and packs the libraries, publishes the packages to this repository's GitHub Packages feed and creates a GitHub
release. Nothing is published to nuget.org.

### Pull requests from forks

CI runs the code of the pull request (build scripts, tests), so it must never run for code nobody with write access
has reviewed. The protection is in the repository settings, not in the workflow files, because a `pull_request`
run uses the workflow files of the pull request itself:

- Settings → Actions → General → "Fork pull request workflows": workflows from fork pull requests are **not run**
  (the default for private repositories). If the repository is made public, set "Require approval for all external
  contributors" and approve a run only after reading the change, including any change under `.github/`.
- Workflow permissions: read-only `GITHUB_TOKEN`; Actions cannot approve pull requests.
- Allowed actions: GitHub's own actions only, pinned to full commit SHAs.

The workflows add a second line of defense: they use `pull_request` (never `pull_request_target` or
`workflow_run`), skip jobs for pull requests from forks, give jobs a read-only token that is not left in the
checkout, use no secrets or caches, and run on GitHub-hosted runners only. Never add a self-hosted runner to this
repository.

To test a contribution from a fork, review it first, then push its commits to a branch of this repository and run
CI on that branch:

```
gh pr checkout <number>
git push origin HEAD:refs/heads/pr-<number>
gh workflow run ci.yml --ref pr-<number>
```

## Component libraries

`VisaCs.Serial`, `VisaCs.Usb`, `VisaCs.Gpib`, `VisaCs.Lan`, `VisaCs.Pci` and `VisaCs.Formatting` are usable on their
own. They follow these rules:

- **Dependencies.** A component library references no other VisaCs project and no third-party package. It may link
  internal helpers from `src/Shared/` as source. `VisaCs` (the VISA library) references all of them.
  `tests/VisaCs.Tests/PackageLayoutTests.cs` enforces this: a component assembly references no assembly whose name
  starts with `VisaCs`.
- **Namespaces.** Every public type of a component library is in that library's namespace (`VisaCs.Serial`,
  `VisaCs.Usb`, ...) or a sub-namespace of it (`VisaCs.Lan.Rpc`). `PackageLayoutTests` checks this too.
- **Errors.**
  - Device, OS and protocol failures throw the library's own exception, derived from `System.IO.IOException` and
    named `<Area>Exception` (`SerialPortException`, `UsbException`, `GpibException`, `LanException`,
    `PciException`). It has an `<Area>Error` enum property `Error` that names the failure in the library's own terms
    (not VISA names), and `int? NativeError` (the OS or driver error code) when known. A library may add a few
    subclasses for protocol-specific detail (for example a VXI-11 error code, or a security failure).
  - Timeouts throw `<Area>TimeoutException : System.TimeoutException`, with `int BytesTransferred`.
  - Programming errors use the BCL types: the `ArgumentException` family, `ObjectDisposedException`,
    `InvalidOperationException`, `PlatformNotSupportedException`.
  - Cancellation by the caller throws `OperationCanceledException`.
  - `VisaCs.Formatting` throws `FormatException` for invalid formats and input, and `NotSupportedException` for
    unsupported conversions.
- **Mapping to VISA.** `VisaCs` translates each library's exceptions to `VisaException`/`VisaTimeoutException` with
  the matching `VisaStatus` (both defined in `VisaCs`, under `src/VisaCs/Errors/`), keeping `NativeError`, `BytesTransferred` and the original exception as
  `InnerException`. Each library's mapping is one internal file under `src/VisaCs/Internal/` (for example
  `SerialErrorMapping.cs`). The VISA library's behavior and status codes don't change.
- **Timeouts.** An `int timeoutMilliseconds` parameter means: negative (`Timeout.Infinite`) = no limit; 0 = don't
  wait (the operation fails with a timeout unless it can complete immediately); positive = milliseconds. Every I/O
  method has a synchronous form and an asynchronous form that also takes a `CancellationToken`.
- **Shared sources.** Files in `src/Shared/` never reference library or VISA types. They create and recognize
  exceptions only through `SharedErrors` (`src/Shared/SharedErrors.cs`), which each assembly that links them
  implements in its own `Internal/SharedErrors.Implementation.cs`.
- **Tests.** Component libraries don't grant `InternalsVisibleTo` to `VisaCs.Tests` (internal Shared types compiled
  into several assemblies would clash). Test them through their public API, or in a separate test project, as
  `tests/VisaCs.Lan.Tests` does.

## Releasing

1. Set `<Version>` in `src/Directory.Build.props` (semantic versioning).
2. Commit, then tag and push: `git tag v0.1.0 && git push origin v0.1.0`.

The release workflow refuses a tag that does not match `<Version>`.
