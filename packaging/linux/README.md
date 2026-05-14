# Linux packaging

Drop-in resources for downstream Linux packagers (.deb / .rpm / AppImage / Flatpak).

## Files

| File | Install location | Purpose |
| --- | --- | --- |
| `cinder.desktop` | `/usr/share/applications/cinder.desktop` | Desktop launcher entry — Activities / app menu / file-manager "Open with" |
| `../../assets/branding/png/cinder-16.png` | `/usr/share/icons/hicolor/16x16/apps/cinder.png` | Hicolor icon (matched by `Icon=cinder` in the .desktop) |
| `../../assets/branding/png/cinder-32.png` | `/usr/share/icons/hicolor/32x32/apps/cinder.png` | |
| `../../assets/branding/png/cinder-48.png` | `/usr/share/icons/hicolor/48x48/apps/cinder.png` | |
| `../../assets/branding/png/cinder-64.png` | `/usr/share/icons/hicolor/64x64/apps/cinder.png` | |
| `../../assets/branding/png/cinder-128.png` | `/usr/share/icons/hicolor/128x128/apps/cinder.png` | |
| `../../assets/branding/png/cinder-256.png` | `/usr/share/icons/hicolor/256x256/apps/cinder.png` | |
| `../../assets/branding/png/cinder-512.png` | `/usr/share/icons/hicolor/512x512/apps/cinder.png` | |
| `../../assets/branding/cinder-logo.svg` | `/usr/share/icons/hicolor/scalable/apps/cinder.svg` | Scalable vector for HiDPI desktops |

After install, packagers should run:

```bash
gtk-update-icon-cache -q /usr/share/icons/hicolor || true
update-desktop-database -q /usr/share/applications || true
```

The `Exec=cinder` line assumes the binary is on `$PATH`. If the package installs under `/opt/cinder/`, either symlink `/usr/bin/cinder` → `/opt/cinder/Cinder` or change `Exec=` to the absolute path.

## AppImage

For an AppImage build, the layout inside the AppDir mirrors the system install:

```
Cinder.AppDir/
├── AppRun                     -> usr/bin/Cinder
├── cinder.desktop             -> usr/share/applications/cinder.desktop
├── cinder.png                 -> usr/share/icons/hicolor/256x256/apps/cinder.png
└── usr/
    ├── bin/Cinder             (the .NET single-file publish output)
    ├── share/applications/cinder.desktop
    └── share/icons/hicolor/...apps/cinder.png
```

## Source of truth

The icons in this directory are NOT committed copies — they live under
`assets/branding/png/` and are generated from `assets/branding/cinder-logo.svg`
by `tools/icons` (run `dotnet run` from `tools/icons` to regenerate). Packagers
should copy from `assets/branding/` at build time.
