# The size map: what was measured before it was built

Machine: Ryzen 7 5800X3D, 8 cores / 16 logical, 32 GB, NVMe, Windows 11 26200, .NET 10, Release,
not elevated. Same machine as `docs/folder-size-baseline.md`.

## Why squarified

Canvas 1360×820, lognormal sizes (median ~4 KB, long tail), median of 21–101 runs, zero
allocation in every case.

| n | squarified | strip | slice-and-dice |
|---|---:|---:|---:|
| 1,000 | **0.022 ms** | 0.207 ms | 0.018 ms |
| 10,000 | **0.104 ms** | 9.2 ms | 0.022 ms |
| 100,000 | **0.873 ms** | 359.6 ms | 0.224 ms |

Speed is not why it won. Quality at 100,000:

| | median aspect | p95 aspect | worse than 8:1 |
|---|---:|---:|---:|
| squarified | **1.01** | **1.02** | **0** |
| strip | 1.03 | 2.86 | 2,165 |
| slice-and-dice | 1,158,546 | 61,295,300 | 100,000 |

And the case that actually decides it — one file holding 90% of the folder:

| n | squarified over 8:1 | strip | slice-and-dice p95 |
|---|---:|---:|---:|
| 1,000 | **0** | 194 | 3,900,345 |
| 10,000 | **0** | 1,144 | 50,199,874 |
| 100,000 | **0** | 6,641 | 612,845,859 |

Squarified confines the skew to one rectangle instead of thinning every rectangle, which is why
it stays readable. Strip's one advantage is that it preserves reading order, so the picture can be
scanned like a list; it costs 400× at 100k and still produces slivers. Slice-and-dice flat is a
barcode — its stability under refresh only exists nested, and that is what ordered and pivot
treemaps are for, if flicker on refresh ever becomes a real complaint.

## Drawing

n = 100,000, rasterised through `RenderTargetBitmap` — WPF's **software** rasteriser, so the
raster column is an upper bound rather than a frame time. Build time, memory and the shape of the
growth are honest.

| | drawn | build | raster | held |
|---|---:|---:|---:|---:|
| one `DrawingVisual`, one `RenderOpen` | 100,000 | 11.29 ms | 165.3 ms | 17.2 MB |
| **same, culled to ≥2×2 px** | **21,047** | **1.13 ms** | **39.7 ms** | — |
| one `DrawingVisual` per rectangle | 100,000 | 96.14 ms | 432.9 ms | 49.9 MB (499 B each) |
| `WriteableBitmap`, direct pixels | 100,000 | 1.43 ms | 0.27 ms blit | 4.46 MB |

A visual per rectangle is 8.5× slower to build and holds 2.9× the memory; the only thing it buys
is WPF's hit testing, which a loop over the boxes does for nothing. Cull first: at 100k on a
1360×820 canvas only **21.0%** of rectangles are ≥2×2 px, and 59.0% are under one pixel on a side.

## The nested picture, which is not built yet

The map ships one level at a time: the current folder's children, drawn from the sizes the Size
column already measured. Nothing new is walked, so switching to it is free.

The full WizTree picture — every file, to the leaves, in one image — needs the whole tree, and the
walk for it was measured too. The finding is worth keeping, because it is the opposite of what it
sounds like:

**The whole tree is cheaper than the size column already shipping.**

| | per-child `FolderSize` (today) | one collecting walk |
|---|---:|---:|
| `D:\dev` — 818,869 files | 1,837 ms | **479 ms** |
| `C:\Users\oance` — 1,111,014 files | 7,150–14,436 ms | **6,679–7,073 ms** |

One 16-thread walk over the whole root parallelises better than N sequential threaded walks over
each child. And the per-file size is already in hand — `FolderSize.Level.TransformEntry` returns
it and the total swallows it one line later.

Memory, measured: **12.1–12.6 bytes per file** as flat `long[] size` + `int[] parent` arrays, so
14 MB for the profile. With names it is 95–135 B/file, 105–149 MB — so names are not kept; the one
directory under the pointer is re-read on hover instead.

### The honesty problem the nested version must solve first

A treemap's areas must sum to the whole. "We could not read it" has no area. On this machine 10 of
the profile's 65 children return nothing at all and 4 more return a floor. A map that silently
omits them draws a picture where 100% of the visible area is 100% of *what it could read* — a lie
the size column does not tell, because `—` is visible and an absent rectangle is not.

So the nested version needs a hatched block for the unreadable, with its count in the header, and
a marked edge on floors. That is not decoration; without it the picture is wrong.

Staleness, measured: two walks of the profile 13.7 s apart differed by 1 file / 186 bytes; an
earlier pair 7 s apart differed by 10 files / 2.1 MB. The rationing in `FolderSizesViewModel` —
no sooner than 20× what the last walk cost — is the right precedent and should govern the map too.
