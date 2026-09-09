# Screenshot capture protocol

The README references four screenshots. This is how to take them so they look
consistent, and so nothing in them can come back to bite the project.

## Hard rules

1. **Never capture real case material.** Every shot uses synthetic evidence you
   generated yourself, or a public test corpus with a licence that permits
   redistribution (NIST CFReDS, Digital Corpora). If you cannot say where a byte
   in the frame came from, do not publish the frame.
2. **No personal data in the chrome either** — check the window title, the
   recents list, the path bar, and the case name. The recents list is the one
   people forget; it persists across runs and will happily show
   `C:\Users\<your-name>\Cases\real-client-matter`.
3. **No API keys, tokens, or provider names** in the AI copilot panel.

## Setup

- **Window size:** 1600 × 1000 logical pixels. Wide enough that no panel
  collapses, small enough that text stays readable when GitHub scales the image
  into a two-column table.
- **Theme:** dark. It is the default and it is what the design system is tuned
  for.
- **Scaling:** capture at 2× (HiDPI) and downscale to 1600px wide, so the text
  edges stay crisp on retina displays.
- **Format:** PNG. Strip metadata before committing — `exiftool -all= *.png`.
  A screenshot carries the capturing machine's details otherwise.

## The four shots

| File | Tool | What must be visible |
|---|---|---|
| `home.png` | Home dashboard | Recent cases, recent evidence, the quick-start guide card. Populate with 3–4 synthetic cases so it doesn't look empty. |
| `hex.png` | Hex viewer | A large image open, the inspector panel decoding a value at the caret, and at least one bookmark. Show the offset column with a non-zero offset so the virtualization is evident. |
| `evtx.png` | Event Log | A parsed `.evtx` with the filter box in use and the channel column populated. |
| `report.png` | Reports | A generated PDF preview showing the cover, a section, and an exhibit card. |

Save all four to `assets/screenshots/`, then uncomment the image block in
[README.md](../README.md).

## Generating synthetic evidence

- **Event logs:** on a throwaway Windows VM, run a few dozen benign actions and
  export `Security.evtx` / `System.evtx`.
- **Registry hives:** copy `NTUSER.DAT` from a fresh VM profile.
- **Disk image:** create a small VHD, format it NTFS, drop in a handful of
  public-domain files, then image it. A 256 MB image is plenty — the hex viewer
  shows the same thing at 256 MB as at 100 GB, and the file stays small enough
  to keep around.
- **Prefetch / LNK:** likewise from the throwaway VM.

Keep the generated corpus out of the repository — it is large, and the point is
that anyone can regenerate it. Note in the PR which corpus a screenshot used.
