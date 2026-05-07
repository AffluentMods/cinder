# cinder-ram-linux — Cinder RAM Acquisition (Linux)

Linux RAM acquisition uses [LiME](https://github.com/504ensicsLabs/LiME) — the standard
forensic-community kernel module that handles `/dev/crash` differences across kernels and
emits in a volatility3-compatible LiME format.

## Status

Cinder ships a thin wrapper that:
1. Detects the running kernel version (`uname -r`)
2. Looks for a pre-built `lime-<kernel>.ko` next to the binary
3. If present: `insmod lime.ko "path=<output> format=lime"` and waits for completion
4. If absent: prompts the user to build LiME against their kernel headers

There is **no Cinder-authored kernel module** — using LiME upstream means we inherit its
maturity and review history. Cinder contributes back fixes when we find them.

## TODO

- [ ] CI step to pre-build LiME for popular kernels (Ubuntu LTS, Debian stable, Arch -lts)
- [ ] Add Cinder-side entropy/length sanity checks on the captured `.lime` before signalling success
- [ ] Document the `/proc/kcore` fallback for kernels that block kernel modules
