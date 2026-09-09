# SignPath configuration

This directory contains the artifact configuration Cinder uses when
its release pipeline hands a build off to [SignPath.io](https://signpath.io)
for signing under the [SignPath Foundation](https://signpath.org)'s free
signing program for open-source projects.

## Files

| File | Purpose |
| --- | --- |
| `artifact-configuration.xml` | Declares which files inside the uploaded artifact ZIP get signed and how (Authenticode for the Windows PE, Nupkg signing for any NuGet packages). Referenced from the SignPath project as the "artifact configuration slug". |
| `signpath-manifest.yml` | Project metadata (name, upstream repo, release-signing policy) referenced during application review. Not consumed by the SignPath API — it's a human-facing description for the review team. |

## Release pipeline hook

`.github/workflows/release.yml` invokes `signpath/github-action-submit-signing-request@v1`
only when the repo variable `SIGNPATH_ENABLED == 'true'` is set — until SignPath
Foundation approves the application, Release builds ship unsigned and the
maintainer surfaces this in the release notes + README.

## Trust boundary

Only the `release` GitHub Actions workflow submits signing requests, and it
runs only on tag pushes matching `v*.*.*`. Every other workflow (CI, CodeQL,
lint) has no `SIGNPATH_API_TOKEN` access. The signing policy on the
SignPath side further restricts which git refs can be signed as "release"
vs "test".
