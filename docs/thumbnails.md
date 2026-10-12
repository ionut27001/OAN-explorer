# Thumbnails and icon overlays: what was measured

Machine: Windows 11 Pro 26200, 32 GB. Corpus built under `%TEMP%` by
`tests/Oan.Tests/ThumbnailCorpus.cs`, so Windows has never seen those files and a "cold" number is
genuinely cold. Every figure is the median of three or five runs on an STA thread, after one warm
pass, from `tests/Oan.Tests/ThumbnailBenchmark.cs`:

```
dotnet test --filter "Category=Benchmark&FullyQualifiedName~Thumbnail" --logger "console;verbosity=detailed"
```

## Which kinds have a picture on this machine

| kind | 16 | 32 | 48 | 256 | what the shell returns |
|---|---|---|---|---|---|
| `.jpg` | 16x12 | 32x24 | 48x36 | **256x192** | the photograph |
| `.png` | 16x16 | 32x32 | 48x48 | **256x256** | the image |
| `.mp4` | 16x9 | 32x18 | 48x27 | **256x144** | a frame |
| folder with pictures in it | 16x16 | 32x32 | 48x48 | **256x256** | a composite of them |
| `.pdf` | — | — | — | — | nothing registered |
| `.docx` | — | — | — | — | nothing registered |
| `.exe` | — | — | — | — | no provider; the icon is the answer |
| `.lnk`, `.url` | — | — | — | — | no provider; the icon plus its badge is the answer |
| empty folder | — | — | — | — | nothing to compose |
| no extension | — | — | — | — | nothing registered |

`.pdf` and `.docx` have no `ShellEx\{e357fccd-…}` key under the extension or its ProgID on this
machine, and the extraction agrees with the registry. The `.docx` in the corpus is synthesised and
carries no `docProps/thumbnail`, which is also true of most real ones — Word only stores a picture
of page one when "Save Thumbnail" was ticked. The PDF is a valid one-page file with a correct
cross-reference table, written that way so a refusal cannot be blamed on the input.

Note `.png`: there is no thumbnail provider under `.png` or `pngfile` either, yet PNG thumbnails
come out. The provider is reached through the perceived type, not the extension, which is why the
registry is a poor way to answer this question and extraction is the right one.

## Thumbnail against icon, per kind and size

Milliseconds, median. `icon` is `ShellIcons.Get` with its cache cleared, so a full shell lookup.
`in-cache` is `SIIGBF_THUMBNAILONLY | SIIGBF_INCACHEONLY`. `extract` is `SIIGBF_THUMBNAILONLY` on a
copy Windows has never seen, a fresh copy per repetition.

| kind | size | icon | in-cache (cold) | extract | in-cache (warm) | thumbnail bytes |
|---|---|---|---|---|---|---|
| jpeg | 32 | 0.29 | 6.2 | **15.7** | 5.5 | 3 072 |
| jpeg | 48 | 0.38 | 6.9 | **15.5** | 5.5 | 6 912 |
| jpeg | 256 | 1.10 | 6.6 | **16.7** | 6.7 | 196 608 |
| png | 256 | 1.00 | 6.5 | **15.9** | 6.3 | 262 144 |
| mp4 (949 MB) | 256 | 1.11 | 3.0 | 2.96 | 2.9 | 147 456 |
| folder of pictures | 256 | 4.15 | 6.6 | **26.8** | 7.3 | 262 144 |
| pdf | 256 | 1.28 | 6.7 | 8.4 | 5.5 | 0 |
| docx | 256 | 1.29 | 7.7 | 9.6 | 5.2 | 0 |
| exe | 256 | 2.01 | 9.0 | 11.2 | 1.8 | 0 |
| no extension | 256 | 1.50 | 8.5 | 12.0 | 6.7 | 0 |
| shortcut (.lnk) | 256 | 6.40 | 7.7 | 19.6 | 6.6 | 0 |
| empty folder | 256 | 4.48 | 7.5 | 19.5 | 7.0 | 0 |

The mp4 is the one row that is not cold: at 949 MB it is not copied per repetition, so Windows had
already cached it and every column is a warm one.

Three things fall out of this table.

**A picture costs about fifteen times an icon, and that is the real price.** 16 ms against 1 ms at
256 for a JPEG. For a folder of four hundred photographs the first visit is about six seconds of
background work, and the second is about two and a half — which is the in-cache column, not the
extract one.

**Asking and being told "no" is not free.** Every "no picture" row still costs 8–12 ms:
`SHCreateItemFromParsingName` plus a `GetImage` that fails. A folder of four hundred text files
would spend four seconds learning nothing. That is why `ThumbnailKind.None` is cached per file —
it is paid once per file, not once per look — and why `Thumbnails.Smallest` keeps the 16 px details
view out of this entirely.

**Bytes are the reason the cache is bounded in bytes.** One 256 px thumbnail is 196–262 KB of
Pbgra32 against 1–9 KB for an icon. The 4096 entries `ShellIcons` allows itself would be a
gigabyte here, so `Thumbnails` holds a 64 MB budget — about 250 pictures at 256 px — and evicts
oldest-first rather than clearing wholesale, because a wholesale clear mid-scroll would re-pay the
extract column for every row on screen.

## Why 32 is the smallest size that gets a picture

A photograph asked for at 16 comes back **16x12**: fewer pixels than the 16x16 icon it would
replace, at fifty times the cost. At 32 it is 32x24 and you can tell which photograph it is. OAN's
four views are 16, 32, 48 and 256, so the details view keeps its icons and everything above it gets
pictures. That is `Thumbnails.Smallest`.

## The in-cache-only pass

Measured against `%USERPROFILE%\Pictures\Screenshots`, 200 of 1 346 files, a folder Explorer has
browsed:

| size asked for | Windows' cache held | too small to use | used | rate | per file |
|---|---|---|---|---|---|
| 32 | 79 | 0 | 79 | **39.5 %** | 7.4 ms |
| 48 | 79 | 0 | 79 | **39.5 %** | 7.8 ms |
| 256 | 79 | **78** | 1 | **0.5 %** | 7.8 ms |

Two findings, and the second one is the sharper.

The hit rate is a property of what the user has browsed, not of the engine: 39.5 % here, 27.8 % on
a Downloads folder of 18 files, 0 % on files Windows has never seen, 100 % immediately after an
extraction — measured at 8–12 ms after the extract returns, so the write is not deferred.

**`SIIGBF_INCACHEONLY` will hand back a picture much smaller than the one asked for.** Windows keeps
a handful of fixed tiers and returns the nearest it has, never scaling up. Asking for 256 in that
folder found 79 cached pictures of which 78 were at most 128 px — in a 256 px grid, a photograph
blurred past recognition, delivered confidently. So `Thumbnails` treats a cached picture whose
longest side is under half the box as a miss and extracts instead. The same effect shows up within
one session: the in-cache column for "folder of pictures" at 48 returns the 32 px picture the
previous row had just cached.

## IShellItemImageFactory against the image list, for items with no picture

`docs/icons.md` records an earlier measurement: byte-for-byte identical at 16, 32 and 48, at about
three times the cost. **Re-measured, the first half of that does not hold and the second is an
understatement.** Icons only, `SIIGBF_ICONONLY` against `ShellIcons.Get`:

| kind | size | image list | factory | ratio | pixels differing | worst channel |
|---|---|---|---|---|---|---|
| no extension | 32 | 0.31 | 7.33 | **23x** | 18 / 1 024 | 139 |
| pdf | 32 | 0.31 | 7.20 | **23x** | 88 / 1 024 | 238 |
| docx | 48 | 0.43 | 7.42 | **17x** | 171 / 2 304 | 204 |
| folder | 32 | 3.08 | 6.90 | 2.2x | 9 / 1 024 | 160 |
| exe | 32 | 0.93 | 2.10 | 2.3x | 232 / 1 024 | 234 |
| docx | 256 | 1.43 | 9.01 | 6.3x | 48x48 against 256x256 | — |

Few pixels differ, but they differ by nearly a full channel — this is the faint frame around an
undersized icon that `icons.md` already warns about, plus the shell's own resampling on `.exe`. The
ratio is 1.3x to 24x depending on kind and size, not a flat 3x: the factory has a floor of about
7 ms per call that the image list, answering out of a process-wide list by extension, does not.

So the cheap path stays exactly where it is. `ShellIcons` remains the whole icon story;
`Thumbnails` never asks for an icon and never falls back to one, because the caller already has a
better icon than the factory would return.

## Overlays

Two things had to be measured before any of this worked.

**`SHGFI_OVERLAYINDEX` modifies `SHGFI_ICON` and nothing else.** Paired with `SHGFI_SYSICONINDEX` —
which is what `ShellIcons` uses, and the obvious pairing — a shortcut comes back with overlay 0.
Paired with `SHGFI_ICON` the same shortcut comes back with overlay 2. The failure is silent: a
correct-looking call that reports "no badge" for everything. So `ShellOverlays.IndexFor` pays for an
`HICON` it destroys immediately, and that is the price of the answer.

**`IImageList::GetOverlayImage` does not work here.** It answers `S_OK` with index 1 for all fifteen
slots, on all four lists. That is not a mis-declared vtable: `GetIconSize` fourteen slots earlier
returns 16/32/48/256 correctly and `GetImageCount` returns 242 on all four, matching the icon
indices `SHGetFileInfo` hands out. The shell's image lists simply do not carry the overlay art.

What does work is the mask. `ImageList_GetIcon(list, i, ILD_TRANSPARENT | (overlay << 8))` returns
the icon with the badge composited on top, and it differs from the same icon without. So the badge
is read out of the difference between the two — once per overlay slot per size, sixty pictures for
the life of the process, and never on a row.

| what | cost |
|---|---|
| `IndexFor` on an ordinary file | **2.3 – 3.8 ms** |
| `IndexFor` on a `.lnk` / `.url` | 5.6 / 6.7 ms |
| `ShellIcons.Get`, cache hit, for comparison | 0.001 ms |
| building a badge, first time, 16 / 32 / 48 / 256 | 2.8 / 2.0 / 1.7 / 9.3 ms |
| a badge after that | ~0.01 ms |
| ink in the badge at 32 | 169 of 1 024 pixels, one corner |

**This is the expensive part of the whole feature, and it is per row.** About 3 ms for every item in
every folder, whether or not it has a badge — 1.2 seconds of background work for four hundred rows,
on top of icons. It is the same order as the per-path icon lookup folders and executables already
pay, and it is on the pump thread, but it is not nothing. If it ever needs to come down, the
listing already knows each item's `FileAttributes`, and asking only for directories, `.lnk`, `.url`
and items carrying `ReparsePoint` or `Offline` would skip most rows — at the cost of missing
third-party handlers such as TortoiseGit that badge ordinary files.

## Invalidation, and what it costs

A thumbnail of a file that has since changed is a wrong answer shown confidently. The cache key is
`{size}|{path}|{lastWriteUtc.Ticks}:{length}`, so a file that changed cannot be served the old
picture: it misses, and the stale entry ages out under the byte budget. One `FileInfo` per lookup
pays for it — a single find on disk giving both fields, inside the noise of the 6–16 ms the shell
call costs either way.

Windows keys its own thumbnail cache the same way, so `SIIGBF_INCACHEONLY` does not return stale
pictures either. `ThumbnailTests.A_file_that_changed_is_not_served_its_old_picture` is the test.

Two gaps, stated rather than papered over:

- **A folder's picture goes stale.** A folder's own timestamp does not move when a picture inside
  it is edited, so the key cannot see it. Refreshing the folder is what corrects it.
- **An overlay goes stale.** `ShellOverlays` caches nothing per item on purpose, so a badge is
  exactly as fresh as the listing it belongs to — but `FolderChanges` watches
  `FileName | DirectoryName | Size`, and a file that starts or stops being shared, or that OneDrive
  pins, changes none of those. Until `NotifyFilters.Attributes` is added, that change is noticed on
  the next navigation and not before.

## What could not be measured on this machine

- **OneDrive status badges.** `%USERPROFILE%\OneDrive` holds one `desktop.ini`. The overlay path is
  exercised with the shortcut arrow, which is the same mechanism and the same slot table, but the
  cloud-provider badges themselves were not seen.
- **A thumbnail provider that hangs.** One cannot be installed from a test. The deadline,
  the abandoned apartment and the per-kind "stuck" table are the same mechanisms
  `ShellContextMenu` already uses and `ContextMenuTests` already covers with a fake; the thumbnail
  path exercises them only in the happy direction.
- **A `.docx` with an embedded thumbnail.** No real one exists on this machine, so "Word documents
  have no preview" is really "Word documents that were not saved with one have no preview".
