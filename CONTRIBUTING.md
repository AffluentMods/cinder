# Contributing to Cinder

Thanks for your interest in Cinder. This document covers everything you need to get a dev environment running and contribute code.

## Table of contents

- [Code of conduct](#code-of-conduct)
- [Ways to contribute](#ways-to-contribute)
- [Local development setup](#local-development-setup)
- [Project structure](#project-structure)
- [Running and testing](#running-and-testing)
- [Coding conventions](#coding-conventions)
- [Commit and branch workflow](#commit-and-branch-workflow)
- [Pull request checklist](#pull-request-checklist)
- [Reporting bugs](#reporting-bugs)
- [Requesting parsers](#requesting-parsers)

## Code of conduct

This project follows the [Contributor Covenant 2.1](CODE_OF_CONDUCT.md). Be excellent to each other.

## Ways to contribute

- **Code** — fix bugs, build parsers, polish the UI
- **Docs** — improve the plan, architecture docs, or user guides
- **Test fixtures** — submit forensic test images with provenance
- **Bug reports** — see [Reporting bugs](#reporting-bugs)
- **Parser requests** — see [Requesting parsers](#requesting-parsers)
- **Sponsor** — [GitHub Sponsors](https://github.com/sponsors/AffluentMods) covers signing certs and infra

## Local development setup

### Prerequisites

| Tool | Version | Notes |
|---|---------|---|
| .NET SDK | 10.0+   | [Download](https://dotnet.microsoft.com/download) |
| Python | 3.12+   | For sidecar workers |
| Git | 2.40+   | |
| Git LFS | latest  | For test fixtures |

### IDE

**JetBrains Rider** is recommended (the IntelliJ-platform C# IDE — best support for Avalonia, cross-platform, free for OSS contributors via the [Open Source license program](https://www.jetbrains.com/community/opensource/)). Visual Studio 2022 Community works on Windows. VS Code with the C# Dev Kit is fine for casual edits.

### One-time setup

#### Windows

```powershell
# 1. Install prerequisites via winget
winget install Microsoft.DotNet.SDK.9
winget install Python.Python.3.12
winget install Git.Git
winget install GitHub.GitLFS

# 2. Clone the repo
git clone https://github.com/AffluentMods/cinder.git
cd cinder

# 3. Set up Python venv for parsers
python -m venv parsers/.venv
parsers\.venv\Scripts\Activate.ps1
pip install -e parsers/

# 4. Install Avalonia templates (optional, for new project scaffolding)
dotnet new install Avalonia.Templates

# 5. Restore + build
dotnet restore
dotnet build
```

#### Linux (Ubuntu / Debian / WSL)

```bash
# 1. Install .NET 10 SDK
wget https://packages.microsoft.com/config/ubuntu/24.04/packages-microsoft-prod.deb
sudo dpkg -i packages-microsoft-prod.deb
sudo apt update && sudo apt install -y dotnet-sdk-9.0

# 2. Install Python, Git, build tools
sudo apt install -y python3.12 python3.12-venv python3-pip git git-lfs build-essential libfuse-dev

# 3. Clone the repo
git clone https://github.com/AffluentMods/cinder.git
cd cinder

# 4. Set up Python venv
python3.12 -m venv parsers/.venv
source parsers/.venv/bin/activate
pip install -e parsers/

# 5. Restore + build
dotnet restore
dotnet build
```

#### Linux (Arch)

```bash
sudo pacman -S dotnet-sdk python git git-lfs base-devel fuse3
git clone https://github.com/AffluentMods/cinder.git
cd cinder
python -m venv parsers/.venv
source parsers/.venv/bin/activate
pip install -e parsers/
dotnet restore
dotnet build
```

#### Linux (Fedora)

```bash
sudo dnf install -y dotnet-sdk-9.0 python3.12 git git-lfs gcc-c++ fuse3-devel
git clone https://github.com/AffluentMods/cinder.git
cd cinder
python3.12 -m venv parsers/.venv
source parsers/.venv/bin/activate
pip install -e parsers/
dotnet restore
dotnet build
```

## Project structure

See [docs/plan.md §3](docs/plan.md#3-repository-structure) for the full layout. High-level:

```
src/         C# projects (Cinder.App, Cinder.Core, Cinder.Native.*, etc.)
parsers/     Python sidecar workers (pytsk3, regipy, volatility3, …)
drivers/     Windows kernel write-blocker (separate sub-project)
tests/       Unit and integration tests
fixtures/    Test forensic images (LFS-tracked)
docs/        Plan, architecture, ADRs
.github/     CI workflows, issue templates
```

## Running and testing

### Run the app

```bash
dotnet run --project src/Cinder.App
```

### Run tests

```bash
# All .NET tests
dotnet test

# Single project
dotnet test tests/Cinder.Core.Tests

# Python sidecar tests
cd parsers && pytest

# Coverage
dotnet test /p:CollectCoverage=true /p:CoverletOutputFormat=opencover
```

### Linting / formatting

```bash
# C#
dotnet format

# Python
cd parsers
black .
ruff check .
mypy .
```

## Coding conventions

### C#

- **Target**: .NET 10, C# 14, nullable reference types **enabled**
- `async`/`Task` on every IO method — no `.Result` or `.Wait()`
- `CancellationToken` parameter on every public async method
- `record` over `class` for DTOs and value objects
- One type per file, file name matches type name
- XML doc comments on every public API

### Python

- Black formatting, ruff linting, mypy strict
- Type hints on every signature
- Pydantic v2 for sidecar protocol messages
- Pure functions preferred — sidecars are stateless

### Naming

- `PascalCase` for C# types, methods, properties
- `_camelCase` for private fields with underscore prefix
- `snake_case` for Python
- Filenames: match the primary type/module they contain

## Commit and branch workflow

### Branches

- `main` — always shippable
- `dev` — integration branch
- Feature branches off `dev`: `feat/hex-viewer-overlays`, `fix/mft-parser-timestamp`

### Commits

Use [Conventional Commits](https://www.conventionalcommits.org/):

```
feat: add JPEG signature detection
fix(hex): correct offset display in compact mode
docs: update plan with phase 5 detail
chore: bump avalonia to 11.2.1
test(mft): add fixture for resident attributes
perf(carver): parallelize signature scan
refactor(core): extract case schema migrations
```

### PRs

- Open PRs against `dev`, never directly to `main`
- Squash-merge to `main` at release time
- Title follows conventional commit format

## Pull request checklist

Before requesting review:

- [ ] Tests pass locally (`dotnet test` and `pytest`)
- [ ] `dotnet format` produces no diff
- [ ] Python code passes `black`, `ruff`, `mypy`
- [ ] New parsers have fixture-based regression tests
- [ ] Public APIs have XML doc comments
- [ ] CHANGELOG.md updated under the `[Unreleased]` section
- [ ] If UI changes: screenshots in PR description
- [ ] If breaking changes: noted explicitly in PR description

## Reporting bugs

Use the [bug report template](.github/ISSUE_TEMPLATE/bug_report.yml). Include:

- OS + version (Windows 11 24H2, Ubuntu 24.04, etc.)
- Cinder version (`cinder --version` or About dialog)
- Steps to reproduce, expected vs. actual behavior
- A minimal evidence sample if relevant (sanitized — never upload real case data)
- Full crash bundle from `%APPDATA%\Cinder\crashes\` (Windows) or `~/.config/cinder/crashes/` (Linux)

## Requesting parsers

We get a lot of "can Cinder parse X?" requests. Use the [parser request template](.github/ISSUE_TEMPLATE/parser_request.yml) and include:

- Format name + canonical reference (RFC, vendor doc, blog post)
- Sample data (sanitized) — even one example helps enormously
- Why it matters for forensics
- Existing tools/libraries that already parse it (saves us reinventing the wheel)

The more complete the issue, the faster a parser ships.

## Security

If you find a vulnerability, **do not open a public issue.** See [SECURITY.md](SECURITY.md) for the disclosure policy.

## License

By contributing, you agree your contributions are licensed under [Apache License 2.0](LICENSE).
