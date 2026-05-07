# Getting Started — Cinder dev kickoff

Step-by-step from "I have an idea" to "I'm writing the first commit." Tailored for Arlie's setup (Windows 11 + occasional Linux dev via the Proxmox homelab).

---

## TL;DR

1. Reserve names (GitHub org, repo, domain) — 15 min
2. Install dev tools (Rider, .NET 10, Python 3.12, Git LFS) — 30 min
3. Apply for free OSS licenses (JetBrains, SignPath later) — 10 min
4. Init the repo with the starter kit files — 10 min
5. Push to GitHub — 5 min
6. Hand Phase 0 to Claude Code — let it scaffold the actual app
7. Open in Rider, hit run — see Cinder shell appear

You can be at step 6 by tonight.

---

## 1. Reserve names (do this first, before anything is public)

### GitHub

1. Go to [github.com/organizations/new](https://github.com/organizations/new)
2. Create the **Affluent Labs** organization (free plan is fine)
3. Inside the org, create a new repo: **`cinder`**
   - Description: *"Open-source forensics toolkit. What remains tells the story."*
   - Public
   - Do **not** initialize with README, .gitignore, or license — you'll push the starter kit
4. Reserve `AffluentMods` GitHub Sponsors profile too while you're there

### Domain

Register **`cinder.dev`** through Cloudflare Registrar (cheapest for `.dev` domains, around $14/yr, no markup). Optional companion: `cinderforensics.com` if you want a full website later.

### Trademark check (10 minutes, free)

[USPTO TESS search](https://tmsearch.uspto.gov/) for "Cinder" in International Class 9 (computer software). The C++ creative-coding library `cinder.io` is a different category and shouldn't conflict, but verify. File a trademark application before v0.1 ships if you want protection — it's around $250 per class.

---

## 2. Install dev tools

### IDE: JetBrains Rider (recommended)

**Why Rider over IntelliJ IDEA**: IntelliJ IDEA is JetBrains' Java/Kotlin IDE. Rider is the C#/.NET IDE on the same IntelliJ platform — same shortcuts, same feel, but actually built for .NET. It's the best Avalonia experience available, runs on both Windows and Linux, and integrates with the JetBrains AI Assistant if you want it.

**Visual Studio 2022 Community** also works on Windows but is Windows-only — useless when you want to dev from your Proxmox Linux VMs.

#### Install Rider

**Windows:**
```powershell
winget install JetBrains.Toolbox
```
Open Toolbox → install Rider. Toolbox manages updates and lets you run multiple versions side-by-side.

**Linux (Arch):**
```bash
yay -S jetbrains-toolbox
```
Same flow — install Rider through Toolbox.

#### Apply for the JetBrains OSS license

Once your `cinder` repo is public on GitHub:

1. Go to [jetbrains.com/community/opensource](https://www.jetbrains.com/community/opensource/)
2. Apply with the repo URL
3. Free license arrives in 1–3 business days
4. Activate Rider with it

While you wait, the 30-day trial covers you.

### .NET 10 SDK

**Windows:**
```powershell
winget install Microsoft.DotNet.SDK.9
```

**Linux (Ubuntu 24.04 / Debian 12):**
```bash
wget https://packages.microsoft.com/config/ubuntu/24.04/packages-microsoft-prod.deb
sudo dpkg -i packages-microsoft-prod.deb
sudo apt update
sudo apt install -y dotnet-sdk-10.0
```

**Linux (Arch):**
```bash
sudo pacman -S dotnet-sdk
```

Verify:
```bash
dotnet --version
# should output 10.0.x
```

### Python 3.12

**Windows:**
```powershell
winget install Python.Python.3.12
```

**Linux (Ubuntu/Debian):**
```bash
sudo apt install -y python3.12 python3.12-venv python3-pip
```

**Linux (Arch):**
```bash
sudo pacman -S python python-pip
```

Verify:
```bash
python --version  # or python3 --version
# should output 3.12.x
```

### Git + Git LFS

**Windows:**
```powershell
winget install Git.Git
winget install GitHub.GitLFS
```

**Linux:**
```bash
# Ubuntu/Debian
sudo apt install -y git git-lfs
# Arch
sudo pacman -S git git-lfs
```

Configure once:
```bash
git config --global user.name "Arlie Nordlund"
git config --global user.email "your@email.com"
git lfs install
```

### Avalonia templates

```bash
dotnet new install Avalonia.Templates
```

This gives you `dotnet new avalonia.app`, `dotnet new avalonia.mvvm`, etc. for scaffolding.

---

## 3. Free service signups (do these now, useful later)

### GitHub Sponsors

Apply at [github.com/sponsors](https://github.com/sponsors). For an organization (Affluent Labs), this requires a tax form — takes a couple weeks to process. Start now so it's ready when v0.1 ships.

### SignPath.io (apply after v0.1.0)

Free Windows code signing for OSS projects. Apply at [signpath.io/foss](https://signpath.io/foss). They want to see an actual project with users, so wait until you have v0.1.0 released and a few stars. Without signing, Windows SmartScreen will scare users on download — this is your fix.

### SonarCloud (free for public repos)

Sign up at [sonarcloud.io](https://sonarcloud.io) with your GitHub account. Add the `cinder` repo. The CI workflow will push code quality results there automatically.

---

## 4. Initialize the repo with the starter kit

You have the starter kit (this folder you're reading right now). Drop it into your local clone:

```bash
# Clone the empty repo you created in step 1
git clone https://github.com/AffluentMods/cinder.git
cd cinder

# Copy the starter kit contents into the clone
# (adjust the source path to wherever you saved it)
cp -r /path/to/cinder-starter-kit/. .

# Initialize Git LFS for fixtures
git lfs install
git lfs track "fixtures/*.dd" "fixtures/*.E01" "fixtures/*.raw" "fixtures/*.bin"

# Initial commit
git add .
git commit -m "chore: initial scaffold from starter kit"
git push origin main
```

Also push the project plan into `docs/plan.md`:

```bash
mkdir -p docs
cp /path/to/cinder-project-plan.md docs/plan.md
git add docs/plan.md
git commit -m "docs: add v0.2 project plan"
git push
```

---

## 5. Hand Phase 0 to Claude Code

Open Rider and a terminal in the repo root.

### Install Claude Code

Follow [claude.com/claude-code](https://claude.com/claude-code) for the latest install command. Quick version (current at time of writing — verify):

```bash
npm install -g @anthropic-ai/claude-code
```

Then in the repo root:

```bash
claude
```

### Give Claude Code the plan

Once Claude Code is running, paste this:

> Read `docs/plan.md` carefully — sections 1 through 7 are the foundational context, and Phase 0 in section 9 is what you're building now. Scaffold the repo according to the structure in section 3 and the conventions in section 4. Specifically, for Phase 0:
>
> - Create the `Cinder.sln` with all project skeletons listed in section 3
> - Set up the Avalonia 11 shell in `src/Cinder.App` with the three-pane layout from section 5
> - Wire FluentAvalonia theme with the Cinder color palette as theme resources
> - Implement the SQLite case schema and chain-of-custody append-only log in `src/Cinder.Core`
> - Build the JSON-RPC over stdio sidecar protocol in `src/Cinder.Sidecar` with a Python echo worker in `parsers/echo/`
> - Streaming hash service supporting MD5, SHA-1, SHA-256, BLAKE3 in `src/Cinder.Core`
> - Serilog structured logging
> - Local crash bundle handler (no upload)
> - Verify acceptance criteria from Phase 0 are met
>
> Stop and ask before adding any package not listed in the plan's library choices table. Use central package management via `Directory.Packages.props`.

Claude Code will scaffold everything. Let it work — it'll create projects, write code, and run tests. Review the diff before committing.

### What Phase 0 should produce

When Claude Code finishes Phase 0, you should be able to:

```bash
dotnet run --project src/Cinder.App
```

And see the empty Cinder shell open with the three-pane layout, dark theme, command palette stub, and a working "New Case" button that writes a row to SQLite with a hash-chained custody entry.

CI should be green on the first PR.

---

## 6. Day-to-day workflow

Once Phase 0 is in:

1. Open the repo in Rider
2. Open Claude Code in the integrated terminal
3. Pick the next phase from `docs/plan.md`
4. Tell Claude Code: *"Read docs/plan.md Phase N. Build it. Run tests. Open a PR against dev."*
5. Review the PR, request changes, merge
6. Repeat

For your own coding (the parts you want to write personally, not delegate):

- Hex viewer is your calling card — write it yourself in Phase 1, it's worth the learning
- Use Claude Code for boilerplate, parsers, test generation, doc updates
- Use Rider's debugger heavily — Avalonia hot-reload + debugger is excellent

---

## 7. Useful commands cheat sheet

```bash
# Run the app
dotnet run --project src/Cinder.App

# Run all tests
dotnet test

# Format
dotnet format

# Add a package (use central versioning)
dotnet add src/Cinder.Core package Serilog

# Update Avalonia
dotnet add src/Cinder.App package Avalonia --version 11.2.*

# Run a specific Python parser
cd parsers
source .venv/bin/activate
python -m parsers.filesystem.ntfs --image fixtures/ntfs-basic.dd

# Build a release for Windows
dotnet publish src/Cinder.App -c Release -r win-x64 --self-contained

# Build a release for Linux
dotnet publish src/Cinder.App -c Release -r linux-x64 --self-contained
```

---

## 8. When to ask for help

- **Stuck on Avalonia**: their [Discord](https://discord.gg/avaloniaui) is responsive
- **Stuck on a forensic format**: [r/computerforensics](https://reddit.com/r/computerforensics) and [Forensic Focus forums](https://forensicfocus.com/forums/) are excellent
- **Stuck on Cinder architecture**: open a GitHub Discussion in your own repo and pin it
- **Stuck on Python forensic libs**: [DFIR.training](https://dfir.training/) has links to active Slack/Discord communities

---

## 9. Realistic timeline

If you're putting in 5–10 hours/week alongside the Network+ prep and TINFO 443:

- **Week 1**: setup, Phase 0 scaffold, first commit
- **Weeks 2–5**: Phase 1 (hex viewer + hashing) — your hands-on chunk
- **v0.1.0 release** end of week 5 or 6 — apply to SignPath, post to r/computerforensics
- **Weeks 6–14**: Phases 2–4 (imaging, filesystem, Windows artifacts) — heavily Claude-Code-assisted
- **End of TINFO 443 semester**: Cinder has parity with Autopsy on Windows artifacts, runs on Linux too

That's a real, demoable, portfolio-grade OSS project by end of semester.

---

## You're ready

Reserve the names. Install the tools. Drop the starter kit. Push. Hand Phase 0 to Claude Code. Watch the shell open.

Then come back and we'll tackle the hex viewer in Phase 1 — which is the one you'll want to write yourself.
