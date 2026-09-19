# Icons: where they come from, and what was measured

Everything in the window that is an icon *of a thing* — a row, a sidebar place, a drive, a tab —
comes from the shell, through `ShellIcons.Get(path, isDirectory, size)`. Everything that is an
icon *of an action* — the toolbar, the navigation arrows, the window buttons — is a Segoe Fluent
Icons glyph. That font is the Windows 11 icon set; Explorer's own command bar draws the same
codepoints.

## The image list is real art, not upscaling

Measured on this machine by sweeping sharpness across N−1, N, N+1: a local maximum at exactly N
means a native asset exists at N.

| item | 16 | 32 | 48 | 64 | 256 |
|---|---|---|---|---|---|
| folder | real | real | real | **no** | real |
| .txt, .pdf, .exe | real | real | real | **no** | real |
| .docx | real | real | real | stretched | stretched |

So 16/32/48/256 are genuine and 64 is not — which is why those four are the sizes offered. `.docx`
is the case `Icons.cs` crops for: the jumbo list hands back a 256×256 bitmap holding a 42×42
drawing at the origin with the rest transparent, because the handler registered for it on this
machine tops out at 48.

Cost per call, warm, without the cache: `C:\Windows` 0.77 / 0.85 / 0.89 / 1.84 ms at 16 / 32 / 48
/ 256. Items under `%TEMP%` are 3.0–4.2 ms at every size.

## IShellItemImageFactory: measured, and deliberately not used

At 16, 32 and 48 it returns a bitmap **byte-for-byte identical** to the image list's, for every
target tried, at roughly three times the per-item cost. There is no image-quality gain at the
sizes the list uses, so the backend stays as it is.

It is better at two specific jobs, and is the right tool when either becomes the task:

- **Real thumbnails.** A 640×480 photo asked at 256 comes back as the actual picture, 256×192,
  49,128 colours — 21.7 ms cold, 14 ms warm with `SIIGBF_THUMBNAILONLY`. The image list can only
  ever return the generic icon for its type. This is the one visible gap left: in the extra-large
  view a folder of photos shows a row of identical icons where Explorer shows the photos.
- **Arbitrary sizes at fractional DPI**, resampled by the shell rather than blurred by WPF
  afterwards, and centred rather than padded at the origin.

One trap, recorded because it cost a measurement: it paints a faint ~5px frame around an
undersized large icon, which makes a naive bounding-box metric report the full box as "drawn".

## Icon packs: asked for, and the answer is no

1. The file icons already *are* Windows'. Every variant a pack could offer is a step away from
   what was asked for.
2. No pack can cover the list. An `.exe`, a `.lnk`, an `.ico`, a folder with a `desktop.ini`, a
   drive, a mounted image — their icons live inside the item and have no pack equivalent, ever.
   Half the list would be pack and half Windows, permanently.
3. That is precisely the control this project refuses to ship: visible, and silently doing nothing
   for half the rows.
4. Licensing. Papirus and Numix are GPL-3.0, and whether runtime-loaded PNGs are mere aggregation
   is not an argument worth having in a shipped product. Windows' own art is licensed for use on
   this machine, not for redistribution — which is why reading it at runtime is the right design
   and copying bitmaps into a shipped pack is not.

If it is ever wanted, `ShellIcons.Get` is already the single choke point: one lookup keyed by path
and size, with `Clear()` as the whole invalidation story. A different source can be slotted in
behind that signature the day there is a reason, at no cost today.

What would be worth doing instead, if bigger and richer art is the goal: the extra-large view
already exists, and thumbnails are the measured 21.7 ms above.
