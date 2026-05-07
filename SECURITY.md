# Security policy

## Reporting a vulnerability

**Do not open a public GitHub issue for security vulnerabilities.**

Use [GitHub Security Advisories](https://github.com/AffluentMods/cinder/security/advisories/new) to report privately. This goes to maintainers only and supports a coordinated disclosure timeline.

Alternatively, email **security@cinder.dev** with PGP encryption (key on the website).

## What to include

- Description of the vulnerability
- Steps to reproduce
- Affected versions
- Potential impact (data exposure, code execution, evidence integrity, etc.)
- Suggested mitigation if you have one

## Disclosure timeline

- **Acknowledgment**: within 72 hours
- **Initial assessment**: within 7 days
- **Patch development**: timeline communicated based on severity
- **Public disclosure**: 90 days after report, or earlier if a patch ships sooner

We coordinate disclosure with reporters and credit them in the CHANGELOG and security advisory unless they request anonymity.

## Scope

In scope:
- The Cinder application binaries
- The Cinder.* C# libraries
- Python sidecar workers in `parsers/`
- The Windows kernel write-blocker driver
- GitHub Actions CI/CD configuration
- Build/release pipeline integrity

Out of scope:
- Vulnerabilities in upstream dependencies (report those upstream; we will update once patched)
- Vulnerabilities in third-party AI providers (Ollama, OpenAI, etc.)
- Social engineering of maintainers
- Denial of service via resource exhaustion on intentionally malformed evidence (forensic tools must handle untrusted input by definition; we'll fix crashes but won't treat them as security issues unless they enable code execution)

## Bug bounty

Cinder is a free OSS project with no funding for bounties. Reporters are credited in the CHANGELOG, the GitHub Security Advisory, and a Hall of Fame on the website. We're sorry we can't pay you — and very grateful you reported.
