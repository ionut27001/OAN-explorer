# Shortcuts, links, and the paths we cannot represent

Everything below was measured on this machine: Windows 11 Pro 26200, .NET 10.0.401, Bitdefender
real-time protection on. C: is the system volume with 8.3 name generation on; D: has it off. The
machine's `LongPathsEnabled` policy is **1**.

Where a number depends on which process asks, the process is named. `RtlAreLongPathsEnabled()` is
`false` in the test host — it has no manifest — and `true` in `Oan.exe`, which does.

## Shortcuts

`Shortcuts.Resolve(path)` reads a `.lnk` through `IShellLink` + `IPersistFile` on a shared STA
thread with a 1500 ms deadline, and a `.url` as the INI file it is. `IShellLink::Resolve` is never
called: that is the one that puts a search dialog on screen and walks the network looking for a
target that moved.

### The machine's own Start Menu

221 shortcuts under `%APPDATA%\...\Start Menu` and `%ProgramData%\...\Start Menu`, read cold in
one pass and then read again:

| | |
|---|---|
| `.lnk` | 202 |
| `.url` | 19 |
| resolved to a target | 216 |
| **broken** — target named, and not there | **6** |
| **refused** — nothing to name | **5** |
| leads to a file | 196 |
| leads to a URL | 19 |
| leads to a folder | 0 |

The five refusals are all the same thing and all correct: `File Explorer.lnk`, `Control Panel.lnk`,
`Run.lnk`, and two `.lnk` files that hold a web address. A shell link can carry an ID list with no
filesystem path at all, `GetPath` then returns nothing, and the honest answer is *"this points at a
place with no file path, so only Windows can open it"* — hand the `.lnk` itself back to the shell.
Guessing a path here is how a file manager ends up opening the wrong thing.

The six broken ones are ordinary rot: four downloads that were deleted, two entries left behind by
an uninstalled game.

### What it costs

| | first in process | cold | warm |
|---|---|---|---|
| Start Menu, 221 links | 57.6 ms | median 1.22 ms, p95 8.19, max 83.2 | median 1.08 ms, p95 6.51, max 11.9 |
| one link on D:, ×200 | — | 1.20 ms | median 0.93 ms, p95 1.31 |
| one link under `%TEMP%`, ×200 | — | 25.5 ms | **median 5.79 ms**, p95 7.76 |

Two things worth keeping.

**The first resolve in a process costs 57 ms** and every one after it costs about one. That is
`CoCreateInstance`, the shell link handler loading, and the STA thread starting — paid once,
and worth paying off the UI thread.

**The same shortcut costs six times more under `%TEMP%` than on D:.** Identical file, identical
target, 5.79 ms against 0.93. The difference is the on-access virus scanner, and it is recorded
because it is the reason the synthetic benchmark and the real Start Menu disagree by 5×. When a
number here looks wrong, check where the file lives before blaming the code.

The 1500 ms deadline is roughly eighteen times the worst thing ever seen (83 ms, one cold Start
Menu link). Anything that reaches it is blocked on something that is not coming back — a share
that has gone — not merely slow.

### Why the target is probed separately

Reading the link is local and fast. Asking whether its target still exists is `Directory.Exists`,
which on a dead UNC path blocks for the SMB timeout. So it is a second job on the same apartment,
with a 500 ms deadline and `abandon: false`: a probe that will not come back costs its own answer
and nothing else, and the next shortcut in the listing still gets a reader. `TargetExists` then
stays null — not false. *Not known* and *not there* are different facts and the UI needs both.

For a `.url` naming a web address, `TargetExists` is **always** null. Finding out would mean a
network request, and a folder listing that made one per `.url` would be a different program.

## Reparse points

The reparse tag comes out of `dwReserved0` in `FindFirstFileW` — the filesystem has already put it
in the directory record, so nothing is opened and, crucially, **no OneDrive placeholder is
downloaded by being asked what it is**. That is why this does not use
`DeviceIoControl(FSCTL_GET_REPARSE_POINT)`, which needs a handle.

| question | call | median | vs `File.Exists` |
|---|---|---|---|
| `Reparse.TagOf` | `FindFirstFileW` | 0.148 ms | 1.00× |
| `Reparse.Of` (tag + target) | + `FileSystemInfo.LinkTarget` | 0.394 ms | 2.7× |
| `HardLinks.CountOf` | `CreateFileW` + `GetFileInformationByHandle` | 0.160 ms | 1.08× |
| `File.Exists`, for scale | | 0.148 ms | — |

0.148 ms is not free: **ten thousand rows is a second and a half**. So the tag is not a per-row
question. It does not need to be — the listing already carries `FileAttributes.ReparsePoint`, and
only the entries that have it need asking, which in a normal folder is none and in `C:\Users` is
two.

### What each kind is, and how to show it

| kind | tag | what it is | show it as |
|---|---|---|---|
| Junction | `MOUNT_POINT` | a directory standing in for another on this machine; `mklink /J`, no privilege needed, may point at its own ancestor | "Junction", with the target |
| Mounted drive | `MOUNT_POINT` with a `\\?\Volume{…}` target | a whole volume grafted into a folder | "Mounted drive" — there is no path to navigate to |
| Folder link | `SYMLINK` on a directory | can cross machines; needs Developer Mode or admin to create | "Folder link", with the target |
| File link | `SYMLINK` on a file | its own length is zero; the bytes belong to the target | "File link" |
| Online | the sixteen cloud-filter tags | **not a link.** The item itself, contents held remotely | "Online only" / "On this device" |
| App alias | `APPEXECLINK` | zero-byte stub that launches a packaged app | "App alias" |
| Deduplicated | `DEDUP` | bytes moved to a chunk store; reads normally | say nothing |
| Compressed | `WOF` | how most of `C:\Windows` ships; transparent | say nothing |
| Linux link | `LX_SYMLINK` | made inside WSL; Windows will not follow it | "Linux link" |

The classification matters because the attribute alone does not distinguish them, and Windows
itself puts a counter-example in plain sight: **`C:\Users\Default User` is a junction and
`C:\Users\All Users` is a directory symbolic link**, side by side, same attribute.
`dir /al` on this machine says `<JUNCTION>` and `<SYMLINKD>`, and the tags agree.
(`FolderSize`'s own comment names All Users as a junction; it is not, though nothing about the
behaviour it justifies changes.)

### Which of them should stop a size walk

`SizeTree` and `FolderSize` refuse every reparse **directory** and count it as `NotFollowed`. Three
of those refusals are right and one is not.

- **Junction, mounted drive, folder link** — refuse. Following them double-counts at best and never
  terminates at worst.
- **Cloud placeholder — descend.** It is not a link. It is the folder itself, its children are real
  entries with real lengths, and enumerating them downloads nothing. A OneDrive folder under the
  current rule measures as empty.

`Reparse.ShouldDescend(kind)` is that rule. This account's OneDrive folder has Files On-Demand
effectively unused — one `desktop.ini` and no placeholders — so the *consequence* could not be
measured here, only the rule stated. That is a gap, and it is named rather than papered over.

### Hard links, honestly

A hard link is not a kind of file. It is a second name for one set of bytes, and the two names are
indistinguishable — neither is the original, and nothing in a directory record marks either. So
"is this a hard link?" has no answer. "How many names does it have?" does, and costs a handle
(0.160 ms), which is a per-item question and never a column.

What is **not** cheap and is therefore not offered:

- **Where the other names are.** `FindFirstFileNameW` walks them, one volume only, returning
  volume-relative paths.
- **De-duplicating a size walk.** Meeting both names counts the bytes twice. Fixing that means
  keeping the file id of every file walked — for the 1.1-million-file profile this project already
  measures, that is memory spent on a rare case. WizTree behaves the same way, for the same reason.

## MAX_PATH

The received wisdom is that shell calls break past 260 characters and `\\?\` is the fix. Measured
here, it is the other way round.

A 334-character path on C: (8.3 names on) and a 323-character path on D: (8.3 names off), written
by .NET without complaint, then handed to each call. Test host, `RtlAreLongPathsEnabled() = false`:

| call | plain long path | `\\?\` prefixed | 8.3 form |
|---|---|---|---|
| `SHGetFileInfo` | **ok** | returns 0 | ok |
| `SHParseDisplayName` | **ok** | `0x80070057` E_INVALIDARG | ok |
| `SHCreateItemFromParsingName` | **ok** | `0x80070057` E_INVALIDARG | ok |
| `SHObjectProperties` | *not measured* | *not measured* | *not measured* |

Same result on both volumes, so the 8.3 fallback is **not** what makes it work — shell32 on this
build reads a long path directly. The `\\?\` prefix fails on short paths too: it is not a shell
parse name, and adding one before a shell call would break every path it touched.

`SHObjectProperties` is the fourth call this project makes and could not be measured: it puts a
modal property sheet on screen and a test that opened one would sit there until somebody closed
it. It takes the same parse name as `SHParseDisplayName` and there is no reason to think it
differs, but that is inference, not a measurement.

Also not measured: a machine with `LongPathsEnabled` off, because turning it off means writing to
`HKLM`. On such a machine the plain long path is expected to fail and `LongPath.Short` is the
fallback — on the volume that has 8.3 names.

### The one that bites

`SHCreateItemFromParsingName` succeeds for a long path, but the item it hands back **never names
the path it was given**:

- on C:, `SIGDN_FILESYSPATH` comes back as the 8.3 form,
  `…\Temp\OA9077~1\DDDDDD~1\…\deep.txt`;
- on D:, with no 8.3 names, it comes back `\\?\`-prefixed.

`ProgressSink.Record` compares exactly that string against the set of items the caller asked for,
so **a copy, move or delete of a path past MAX_PATH is reported as zero items succeeded even when
it worked**. `LongPath.FromShell` is the whole of the fix: strip the prefix, put the long names
back. There is a test asserting the ingredient; the change itself is a wiring note, because
`ProgressSink` is not this engine's file.

### `Paths.Resolve`

It catches `PathTooLongException`, which this runtime does not throw for length at all. The catch
is dead and a long folder typed into the address bar resolves like any other — asserted, so that a
change in either direction is caught here rather than in the address bar.
