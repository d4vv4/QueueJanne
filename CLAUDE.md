# QueueJanne
See README.md for what this repo does, how to install it and its projects. docs/ROADMAP.md lists features and ideas.

## Build and test
- `dotnet build QueueJanne.slnx`
- `dotnet test --solution QueueJanne.slnx` (Microsoft.Testing.Platform, opted in through `global.json`). `tests/QueueJanne.App.Tests` runs the real windows headless on the demo namespace (`TestApp.Run`, `OpenDemoAsync`, `FakeDialogs`); add a test there for UI behaviour.
- UI check without opening a window: `dotnet run --project tools/QueueJanne.Snapshot -- <folder>`, then look at the PNG files. It uses the demo namespace and a temporary store, and asserts that single clicks share one preview tab.
- Driving the real app (Windows): `Import-Module tools/UiDriver/QueueJanne.UiDriver.psm1`, then `Start-QJ -Demo`, `Expand-QJ`, `Select-QJ`, `Invoke-QJ`, `Set-QJText`, `Get-QJDialog`, `Save-QJShot`, `Stop-QJ`. It starts the app with a throwaway `QUEUEJANNE_HOME`, so only the demo namespace is there; never drive it against real connections. Each PowerShell call is a new process; the module reattaches to the running app. `tools/UiDriver/Measure-Demo.ps1` times common steps and reports UI freezes.
- Performance: `QUEUEJANNE_DIAGNOSTICS=<file>` makes the app log UI-thread freezes over 50 ms, with markers from `UiLagMonitor.Note`.
- Packages: `build/publish-windows.ps1` (Windows) and `build/publish-macos.sh` (run on a Mac). Publishing fails while QueueJanne or its MCP server is running, because the exe is locked.
- Base branch: `main`. Every push to `main` publishes a GitHub Release `v{major}.{minor}.{run number}` (major and minor from `Directory.Build.props`), which the app's Check for updates installs. So pushing to `main` ships to everyone who has auto-update on: only push what's tested.
- Updates: `UpdateChecker` reads the latest release of the `UpdateRepository` in `Directory.Build.props`; `UpdateInstaller` renames the running files aside (`*.old-*`, removed at next start) and moves the new ones in. Development builds (a folder with `QueueJanne.dll`) don't update themselves.
- Single-file builds load assemblies from their exe lazily. After an update has replaced the exe, a still-running copy can't load anything new (FileNotFoundException). So `UpdateViewModel` tries out starting a process before swapping, and the MCP server loads all its assemblies at startup. Test updater changes end to end: publish an older version into a temp folder, start it with the UI driver (`Start-QJ -Exe`), and install the latest release.

## Rules
- The tool is generic. Never design around how one team configures Service Bus. Read entity settings (max delivery count, sessions, duplicate detection, forwarding) live and adapt.
- Browsing peeks. Anything that locks, settles or sends messages is an operation: it respects `ConnectionProfile.ReadOnly`, shows warnings worked out from the entity's settings, and asks for the entity name on production. In the app, operations go through `EntityOperations`.
- Every operation that removes messages (resubmit, delete, move, purge) goes through `OperationJournal.RecordAsync`, in the app and in the MCP server, so the messages are backed up before they change.
- Connections are lazy: `ConnectionNodeViewModel` starts disconnected and connects when expanded (`OnExpanded`) or when its overview opens; `DisconnectAsync` disposes the client. Refreshes only touch connected ones. Tests and the snapshot tool expand the demo connection first.
- Keep the UI thread free: build rows off it, add them in batches (`BatchCollection.AddRange`), and remove rows in place after an operation rather than peeking again. The tab view is reused between tabs, so keep per-tab state in `EntityTabViewModel`, not in `EntityTabView`.
- The MCP server must never widen its own access: it can't turn read-only off, take connection strings, or replace connections of another kind. Tools that change messages are marked `Destructive`.
- Secrets never go through the MCP server. Connection strings are added in the app only and stored with DPAPI on Windows.
- Core has no UI dependencies; the app and the MCP server both build on it. Every `INamespaceClient` change needs the demo client too, so the snapshot tool and tests keep working.

## Comments
- Don't leave comments that only make sense in the context of the current task. Every comment must make sense to the next developer who reads the code; often that means no comment at all.

## Pitfalls
- Saved connections live in `~/.queuejanne`, not AppData. Windows gives packaged apps, such as the Claude desktop app, a private copy of AppData, so an MCP server started from them saw a different file than the desktop app. The app copies connections from the old `%APPDATA%\QueueJanne` once on start; the MCP server must not, or it would copy the private copy.
- Agents must not read `~/.queuejanne`; use the MCP tools.
- Avalonia 12 compiles bindings by default: every view and `DataTemplate` needs `x:DataType`. DataGrid columns are built in code (`EntityTabView.BuildColumns`) with one-time `CompiledBinding.Create` bindings; a new row property needs an entry in `CellBindings`.
- Replacing the selected item of the tab list briefly leaves nothing selected, which throws the shown view away. `MainWindowViewModel.Show` inserts and selects the new tab before removing the old one.
- Avalonia's tree items don't support UI Automation's ExpandCollapse pattern; the UI driver presses the expander button instead. Dialogs appear as Window elements inside the main window.
- `xmlns` declarations are only allowed on the root element of a XAML file (error AVLN2000). A failed XAML compile can still leave an old DLL that fails at runtime with "No precompiled XAML found".
- Avalonia 12 can't animate `RenderTransform` directly. Give the control a `RotateTransform` or `ScaleTransform` and animate `RotateTransform.Angle` or `ScaleTransform.ScaleX/ScaleY`.
- Service Bus batch delete (`DeleteMessagesAsync`/`PurgeMessagesAsync`) is internal in Azure.Messaging.ServiceBus 7.21, so purge receives and deletes in batches.
- The MCP server writes the protocol to stdout. All logging goes to stderr; don't add `Console.WriteLine`.
- In headless code (the snapshot tool), don't `await` on the UI thread; start the task and pump the dispatcher until it completes.
