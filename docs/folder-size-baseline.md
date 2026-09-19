# Folder sizes: what they cost, measured

Machine: Ryzen 7 5800X3D, 8 cores / 16 logical, 32 GB, NVMe, Windows 11 Pro 26200, .NET 10.0.401,
**not elevated**. Warm cache unless stated. Every variant's byte total was asserted equal to the
baseline's, so all of them walked the same work.

Re-run the walk cost with `dotnet test <<filter>> "Category=Benchmark"`.

## The walk, one folder at a time

| | `C:\Users\oance` | `D:\dev` |
|---|---:|---:|
| Child folders | 65 | 10 |
| Files counted | 1,111,763 | 818,584 |
| Bytes | 245.4 GB | 118.8 GB |
| First touch, summed | **58.0 s** | **14.9 s** |
| Second pass | 49.7 s | 4.3 s |

`AppData` alone is 45.2 s of the profile's 58 — 78% of the time, 68% of the files. Every other
folder in the profile put together is 12.8 s.

Second visits range from 1.0x to 11.7x faster depending on what the OS already holds. There is no
warm number the UI can count on.

Two passes over `AppData` 45 s apart differed by 24,565 bytes: on a live profile a cached size is
stale almost immediately.

## What the current privilege level can actually read

Of the profile's 65 children, **10 return nothing at all** — the legacy compatibility junctions
(`Application Data`, `Cookies`, `Local Settings`, `My Documents`, `NetHood`, `PrintHood`,
`Recent`, `SendTo`, `Start Menu`, `Templates`), whose top handle will not open. Another 4 return a
floor rather than a total, all of them reparse points deliberately not descended. `Skipped` was 0
everywhere: nothing was denied part-way through.

That is why a size is `long?` and a floor is marked. Ten blank rows and four `≥` in a listing of
65 is the honest answer, not 10 zeros.

## Going faster without administrator

| variant | `D:\dev\OAN` 130k files | `C:\Windows` 179k files | `.gradle` 47k files |
|---|---:|---:|---:|
| `FileSystemEnumerator`, default buffer | 1.00x | 1.00x | 1.00x |
| same, 64K / 256K buffer | 1.01x / 1.00x | — | — |
| `GetFileInformationByHandleEx`, 64K | 0.93x | — | — |
| `NtQueryDirectoryFile` by hand, 4K–256K | 0.92x | 0.93x | 0.91x |
| `NtOpenFile` relative to the parent handle | 0.97x | — | — |
| **the same enumerator, per directory, on N threads** | **3.2x** | **7.1x** | **7.2x** |

Only one of these is worth anything. Calling the native APIs by hand is a wash, because .NET's
`FileSystemEnumerator` on Windows *is* `NtQueryDirectoryFile`; writing it again only removes the
managed wrapper, which is not where the time goes. Buffer size moves 0-5%, inside the noise: the
syscall count is not the bottleneck, the per-entry work is.

Threads are the whole win, and the gain tracks how deep and wide the tree is rather than how big:
3.2x on a source tree, 7.1-7.2x on the system volume and the profile. Native + threads measured
8.9x against managed + threads at 7.2x — not enough to ship P/Invoke for.

## The WizTree method, and why it is not here

WizTree reads `$MFT` as one sequential stream. Probed on this machine as it stands
(`MftAccessProbe.cs`, read-only, nothing elevated):

| call | result |
|---|---|
| `CreateFileW(\\.\C:, GENERIC_READ)` | **err 5, access denied** |
| `CreateFileW(\\.\C:, FILE_READ_ATTRIBUTES)` or `0` | opens — but no filesystem FSCTL reaches it |
| `\\?\C:\$MFT`, `C:\$Extend\$UsnJrnl:$J` | **err 5** |
| `FSCTL_GET_NTFS_VOLUME_DATA` on the `C:\` root handle | **OK** — cluster 4096, record 1024, 2.31M records |
| `FSCTL_QUERY_USN_JOURNAL` on the root handle | **OK** — the journal is active |
| `FSCTL_ENUM_USN_DATA`, `FSCTL_READ_USN_JOURNAL` | **err 5** at every access level |
| `FSCTL_GET_NTFS_FILE_RECORD` | err 5 on the root handle, err 1 on the device handle |

Token: medium integrity, elevationType 3. `BUILTIN\Administrators` is present but deny-only, so
elevation is a consent prompt rather than a credential one. `Backup Operators` is not in the token
at all. `SeBackupPrivilege`, `SeManageVolumePrivilege`, `SeRestore` and `SeSecurity` are **absent**
from the token, not merely disabled — nothing could be enabled even by asking.

So unelevated you get three things: volume geometry, the fact that a journal exists, and one USN
record per file you already hold a handle to — which is O(files) syscalls again, i.e. the walk.

Note for whoever reads the older note saying `FSCTL_QUERY_USN_JOURNAL` returns err 1: that err 1
came from asking on a `\\.\C:` handle opened without read access, which reaches the volume *device*
rather than the mounted filesystem. Asked on the volume root directory, which any user can open, it
succeeds. The conclusion is unchanged; the reason is not "unsupported", it is a clean access denial
on the bulk-read calls.

Shipping it means one of: a UAC prompt per run (and a file manager running high-IL loses
drag-and-drop from Explorer, because UIPI blocks medium to high); a service installed once (an
installer to maintain, a resident process holding ~2.3M records, and an IPC endpoint that must be
ACL'd, since anything that can connect gets a listing of every file on the disk); or a scheduled
task with highest privileges (which only works because this account is an admin, is the shape of a
known UAC-bypass pattern that AV flags, and uninstalls badly).

None of those is a thing to switch on quietly. The walk, threaded, is what ships until the user
asks for the index.
