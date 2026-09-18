# OAN

A Windows file manager. C# / .NET 10 / WPF.

```
Oan.slnx
  src/Oan.Core     no UI, no WPF reference — listing, places, history, file operations
  src/Oan.App      the window: XAML, view models, shell hosting
  tests/Oan.Tests  xunit, against Oan.Core
```

Build and run:

```
dotnet build
dotnet run --project src/Oan.App
```

## Two rules the code follows

**Unknown is null, never zero.** A size that could not be read is `null` and renders as `—`.
It is never `0 bytes`. A listing that skipped unreadable entries carries `Skipped`; one that
failed outright carries `Error`. "Empty" and "we do not know" are different answers and the
window must not give the same one for both.

**Nothing in the window is inert.** A button that is visible and does nothing is worse than a
missing feature, because the user cannot tell it from a fast success. Controls that cannot act
are `IsEnabled=false`.

## Shell integration

File operations go through **IFileOperation**, the same interface Explorer uses. That is what
gives the Recycle Bin, the progress and conflict dialogs, undo in Explorer's own Ctrl+Z stack,
and the permanent-delete confirmation — which lives outside this process and so cannot be
spoofed or suppressed by anything in the app. Icons and the context menu come from the shell
for the same reason.

The one trap worth knowing: `IFileOperation.PerformOperations` returns `S_OK` when the user
cancels and when individual items fail. `GetAnyOperationsAborted()` and an
`IFileOperationProgressSink` are what tell you which actually happened.

## Not in this version

Instant folder sizes (WizTree-style), indexed search, archives, ISO mounting, FTP, multiple
panes, user themes. The MFT index that makes instant sizes possible needs administrator rights
— measured, there is no unelevated path — so it belongs in a separate elevated helper, later.
