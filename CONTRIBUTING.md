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

## Releasing

1. Set `<Version>` in `src/Directory.Build.props` (semantic versioning).
2. Commit, then tag and push: `git tag v0.1.0 && git push origin v0.1.0`.

The release workflow refuses a tag that does not match `<Version>`.
