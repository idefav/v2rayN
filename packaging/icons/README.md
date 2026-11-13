# Application branding

The original “对向接续” mark is two rounded, rotationally symmetric open paths.
The blue application tile replaces the former italic V on every supported platform.

- `app-icon.svg`: 1024-unit application master, transparent corners, 48-unit inset.
- `../../v2rayN/v2rayN.Desktop/Assets/BrandGlyph.svg`: matching 24-unit, 3-unit-stroke mark used directly by the desktop header and About page.
- `generate.mjs`: exports the macOS ICNS, Linux PNG, and Avalonia/WPF application and notification ICOs from these sources. It rejects divergent path geometry and external SVG resources.

Install the pinned `sharp` dependency in this directory, then run `npm run build`.
Run `npm run check` to compare all 12 committed binary assets with fresh in-memory exports.
An existing installation of sharp 0.35.3 can be selected with `V2RAYN_SHARP_MODULE`.
`--preview-dir=/absolute/path` additionally exports PNG previews at 16–1024 pixels.
Only generated binary assets are written by this script; edit vector/source files with a patch.

ICO sizes: 16, 20, 24, 32, 40, 48, 64, 128, 256. Each image uses 32-bit RGBA PNG data.
ICNS includes 1× and Retina PNG representations through 1024 pixels; Linux's installed launcher image is 256 pixels.

Notification indices retain the existing system-proxy policy mapping: 1 clear (slate), 2 automatic (blue), 3 unchanged (muted violet), 4 PAC (amber). These are policy indicators, not proof of connectivity. User-supplied icon overrides are still respected. macOS retains the separate, observed P/T status renderer and its existing colors; a static application logo must not imply a connected proxy or TUN.

`package-osx.sh` restores these canonical application assets after merging core archives. The final bundle must contain the same ICNS at `Contents/Resources/AppIcon.icns` and `Contents/MacOS/v2rayN.icns`.

The isolated desktop startup regression decodes every embedded ICO frame using Skia and exercises Avalonia's WindowIcon decoder. A separately bundled native review uses `V2RAYN_REVIEW_BUNDLE_ICON` (canonical PNG), `V2RAYN_REVIEW_BUNDLE_ICNS` (canonical ICNS), and `V2RAYN_REVIEW_LEGACY_ICON` (old PNG negative control) to verify the actual AppKit application icon. It checks exact bundle bytes and normalized symbol geometry, allowing system-added inset and highlights, and confirms that the old V is rejected. It observes the icon without setting or replacing it, and never installs the application.
