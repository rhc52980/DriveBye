# iconmaker

Regenerates DriveBye's icon assets from the mascot render. Deliberately not part of
`DiskUtility.sln` — it runs by hand when the artwork changes, not on every build.

## Running it

```
dotnet run --project tools/iconmaker -- Disk_Utility/drive_logo2.png out
```

Then copy the results into the app:

| Output | Copy to | Used for |
| --- | --- | --- |
| `full.ico` | `Disk_Utility/app.ico` | exe icon, taskbar, title bar |
| `full-tile.png` | `Disk_Utility/logo.png` | the header image |
| `full-preview.png` | — | check how it reads at 16–256px before shipping |

Both files are embedded as resources by `DiskUtility.csproj`. The source render is not.

## Why two assets for one picture

WPF resolves an `.ico` `ImageSource` to `Frames[0]`, which is the **16×16** entry here.
Anything drawing the logo larger than that — the 62px header image — gets a 16px picture
stretched, and looks awful. Hence a separate 512px `logo.png`.

`ApplicationIcon` in the csproj only decorates the `.exe`. Windows and `Window.Icon` need
`app.ico` embedded as a `Resource` as well.

## Background removal

A source with an alpha channel is used as-is; its edges beat anything recoverable from a
flat render, and the halo-trimming pass below would eat into them.

Without alpha, the background is flood-filled inward from the frame edges. Each step compares
against the pixel it came from rather than a fixed colour, so a smooth gradient backdrop is
followed without a global threshold, while the subject's outline stops it dead. Anti-aliased
edge pixels are part backdrop, so their alpha is pulled down — against a dark UI they would
otherwise read as a pale halo.

## Crops

By default the square is the subject's bounding box plus 3% margin.

The optional `tightCentreX tightCentreY tightSide` arguments emit a second set from a
hand-placed square, in source-image pixels. Worth reaching for when the subject is wider than
it is tall: squaring the full width of an outstretched pose shrinks the face, and cropping to
the head and shoulders can double it at 32px. There is no way to find "the interesting part"
from an alpha mask, which is why it is a manual coordinate rather than a heuristic.

## Icon container

Seven sizes (16–256), each stored as PNG inside the ICO. Vista and later read PNG entries
directly, which keeps the 256px frame from bloating the file the way a raw BMP entry would.
