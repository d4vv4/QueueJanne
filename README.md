# QueueJanne

QueueJanne is a fast desktop explorer for Azure Service Bus. It runs on Windows and macOS, keeps several namespaces open at once, and makes dead-letter queues quick to find, read, resubmit and clean up. An MCP server built on the same core lets Claude list entities, peek messages and work with dead-letter queues too.

Status: **0.6**. Browsing, the dead-letter overview, filtering and grouping, resubmitting, deleting, purging, sending, editing and resending, copying and moving all work in the app and through the MCP server. Every change is journalled with a backup of the messages. [docs/ROADMAP.md](docs/ROADMAP.md) lists what it does and ideas for what's next.

## Install

The builds are self-contained, so you don't need .NET to run QueueJanne.

### Windows: download a build (easiest)

Every push to `main` is built and tested by GitHub Actions (`.github/workflows/build.yml`) and published as a [release](https://github.com/d4vv4/QueueJanne/releases), numbered `0.6.<build>`.

1. Open the [latest release](https://github.com/d4vv4/QueueJanne/releases/latest) and download `QueueJanne-windows-x64-<version>.zip`.
2. Unzip it to a folder of your own, for example `%LOCALAPPDATA%\Programs\QueueJanne`, and start `QueueJanne.exe`. Pin it to the Start menu or taskbar if you like.
3. Windows SmartScreen may warn the first time because the exe isn't signed: click **More info → Run anyway**.

### Updates

QueueJanne looks for a newer release when it starts and every 4 hours while it runs, and a green **Update to …** button appears in the status bar when there is one. **Help → Check for updates…** checks right away. Installing downloads the release, checks it against its published SHA-256 checksum, puts it in place of the program files (including the MCP server next to it) and restarts. Connections and settings in `~/.queuejanne` aren't touched. Turn off the automatic check under **Help → Check for updates automatically**.

The first start after an update takes a little longer while Windows scans the new exe. A running MCP server keeps the old version until its Claude session restarts.

macOS builds are in the workflow too, but switched off. Add a repository variable `BUILD_MACOS` with the value `true` (Settings → Secrets and variables → Actions → Variables) to turn them on; releases then also carry `QueueJanne-macos-arm64-<version>.zip` (Apple silicon) and `QueueJanne-macos-x64-<version>.zip` (Intel). Until then, build on the Mac as below.

### Windows: build it yourself

1. Install the .NET 10 SDK (once):
   ```
   winget install Microsoft.DotNet.SDK.10
   ```
   No admin rights? Use Microsoft's install script for your user only instead: `powershell -c "& ([scriptblock]::Create((irm https://dot.net/v1/dotnet-install.ps1))) -Channel 10.0"`, then add `%LOCALAPPDATA%\Microsoft\dotnet` to your PATH.
2. Clone the repo:
   ```
   git clone https://github.com/d4vv4/QueueJanne.git
   cd QueueJanne
   ```
3. Build and install. This puts QueueJanne in `%LOCALAPPDATA%\Programs\QueueJanne` and adds it to the Start menu:
   ```
   powershell -ExecutionPolicy Bypass -File build\publish-windows.ps1 -Install
   ```
   Without `-Install`, the files stay in `artifacts\win-x64`.
4. Start **QueueJanne** from the Start menu.

To update later: close QueueJanne, `git pull`, and run step 3 again.

### macOS (Apple silicon or Intel)

1. Install the .NET 10 SDK (once), with [Homebrew](https://brew.sh):
   ```
   brew install --cask dotnet-sdk
   ```
   Or download the installer from https://dotnet.microsoft.com/download/dotnet/10.0.
2. Clone the repo:
   ```
   git clone https://github.com/d4vv4/QueueJanne.git
   cd QueueJanne
   ```
3. Build and install. This creates `QueueJanne.app` and copies it to `~/Applications`:
   ```
   chmod +x build/publish-macos.sh
   ./build/publish-macos.sh --install
   ```
4. Open **QueueJanne** from `~/Applications` (or with Spotlight). The app is signed ad hoc, not by an Apple developer account. If macOS still says it can't be opened, right-click it, choose **Open**, then **Open** again.

To update later: quit QueueJanne, `git pull`, and run step 3 again.

### First start

- **File → Add connection** adds a namespace. Choose **Microsoft Entra ID** to use your Azure sign-in (`az login` or Visual Studio). Your account needs **Azure Service Bus Data Owner** on the namespace to list queues with counts. Fill in **Tenant ID** only if the namespace is in another tenant.
- Tag production namespaces `prod` and keep them **Read-only**.
- **File → Open demo namespace** shows made-up data if you want to look around first.

### Use it from Claude Code (optional)

The build also produces an MCP server, so Claude can use QueueJanne. Register it once; the build script prints this command with your path filled in:

- Windows:
  ```
  claude mcp add --scope user queuejanne -- "%LOCALAPPDATA%\Programs\QueueJanne\queuejanne-mcp.exe"
  ```
- macOS:
  ```
  claude mcp add --scope user queuejanne -- ~/Applications/QueueJanne.app/Contents/MacOS/queuejanne-mcp
  ```

Start a new Claude Code session afterwards. The MCP server uses the connections you saved in the app.

### Run from source

For working on QueueJanne itself: `dotnet run --project src/QueueJanne.App`.

## Deleting and purging

- **Delete** removes the selected messages (button, right-click menu, or Del anywhere in the tab except a text box). Confirmation dialogs take Enter to confirm and Esc to cancel. In a dead-letter queue nothing else is affected. In an active queue, finding the messages means locking the ones in front of them for a moment, which raises their delivery count. The confirmation shows the entity's live max delivery count, so you can see whether that could dead-letter anything.
- **Purge…** empties a queue or subscription. Pick any of active messages, the dead-letter queue and the transfer dead-letter queue. It's in the tab toolbar and on right-click in the Servers tree. In an active queue it stops at messages that arrived after the purge started. A dead-letter queue is emptied completely.
- Both refuse read-only connections, and on production they ask you to type the entity name.

## Transfer dead letters

Every queue and subscription has a second, rarely used dead-letter queue: the transfer dead-letter queue. Service Bus puts a message there when it couldn't pass the message on to another entity: auto-forwarding to a queue or topic that's gone, disabled or full, a forwarding chain longer than four hops, or a send-via transaction that failed. Ordinary dead letters (max delivery count, expiry, a consumer dead-lettering a message) never go there.

QueueJanne only shows it when it holds messages: a Transfer dead-letter node in the tree, a Transfer DL button in the tab and a column in the overview. When something shows up there, it usually means a forwarding rule is broken. Resubmitting from it works like from the dead-letter queue: the messages go back to the queue, or for a subscription to its topic.

## Sending, editing and moving messages

- **Send** in the toolbar (or right-click a queue, topic or subscription) sends a test message: subject, content type, IDs, application properties (`name = value`, one per line) and a body, up to 100 copies.
- **Edit and resend…** (right-click a message, or the Message menu) opens the selected message in the same dialog, addressed to where a resubmit would send it. Change what you need and send it; the original stays where it is.
- **Copy or move to…** sends copies of the selected messages to any queue or topic, also in another connection. **Move** deletes the originals after the copies were sent, so a failure can leave a duplicate but never loses a message.
- The dialog warns from the target's live settings: duplicate detection drops a message ID it has seen recently, and session queues need a session ID. Read-only connections aren't offered, and production targets need their name typed out.

## Operation journal

Every resubmit, delete, move and purge, from the app or from Claude through the MCP server, is written to `~/.queuejanne/journal` first: one JSON file per operation with who, when, where, the outcome, and a copy of the messages (up to 5,000) in QueueJanne's export format. If something goes wrong you can find the messages there. **View → Operation journal…** opens the folder. Entries are kept for 90 days.

## Finding things fast

- **Go to…** (Ctrl+K) finds any queue, subscription or dead-letter queue in any connection by typing part of its name. With nothing typed it lists the biggest dead-letter queues.
- **Ctrl+F** jumps to the search box of the open tab. Search matches sequence numbers, IDs, subject, properties and body text.
- **Ctrl+plus / Ctrl+minus / Ctrl+0** change the text size of the whole window; it's kept between runs.
- Bodies compressed with GZip or Deflate are unpacked for viewing and searching; the title says so.

## Resubmitting dead letters

Open a dead-letter queue (double-click its node in the tree, or Ctrl+D). Select messages and click **Resubmit**, or use **Resubmit all…**. The same commands are in the right-click menu and the Message menu.

- Each copy is sent to the queue, or for a subscription to its topic, before the original is removed from the dead-letter queue. A failure can leave a duplicate but never loses a message.
- The copy keeps the body and properties, without the dead-letter reason. You can choose to give it a new message ID; the old ID goes into `OriginalMessageId`.
- The confirmation lists what to know first:
  - Resubmitting from a subscription goes to the topic, so every subscription whose rules match gets a copy.
  - When the target has duplicate detection, copies with a known message ID are dropped, so a new ID is suggested.
- Read-only connections refuse it. Right-click the connection and choose **Read-only on/off** to change that. On production, turning it off asks first, and each resubmit asks you to type the entity name.

## What it does today

- Connections to any number of namespaces, with Microsoft Entra ID (your Azure CLI or Visual Studio sign-in) or a connection string. Connection strings are saved encrypted with Windows DPAPI.
- Connections start **not connected**, so namespaces you don't look at get no load: expanding one (or opening its overview) connects it. A dot shows the state: grey not connected, amber connecting, green connected, red can't connect. Right-click a connection and choose **Connect / disconnect** to close it again; its tabs close too.
- A Servers tree with queues, topics and subscriptions, active counts and dead-letter badges. Each queue and subscription has Messages and Dead-letter child nodes, and a Transfer dead-letter node when that queue holds messages (see below). Counts of connected namespaces refresh with F5, or with **Auto-refresh** at the interval you pick next to it: 5, 10, 15 or 30 seconds, 1 or 5 minutes. Auto-refresh updates counts, not the open message lists.
- A tab per queue or subscription. Peeking never locks messages or changes delivery counts. Choose how many messages to load (top 100 up to all) and switch between active, dead-letter and transfer dead-letter.
- A message grid with the dead-letter reason and description, a filter over everything loaded (IDs, subjects, properties and body text), and a body viewer with JSON and XML colouring, text and hex.
- **Dead-letter overview**: the dashboard button next to a connection shows every queue and subscription with dead letters, biggest first, with the change since the last refresh. **Overview** in the toolbar shows all connections at once and opens when the app starts. Double-click a row to open its dead-letter queue.
- **Filter bar and group by** in every tab: filter by dead-letter reason, subject, application property = value, or body text, and group by reason, error, subject, content type or any property, with a count per group. With a filter set, **Resubmit matching…** and **Delete matching…** act on every match in the sub-queue, not only the loaded messages.
- Properties per message: system properties and application properties. The pane can be closed, and its size is kept.
- Editable columns (the Columns button or the grid's right-click menu): show or hide built-in columns, add a column for any application property, and reorder them in the dialog or by dragging headers. Layout and widths are shared by all tabs and kept between runs.
- A single click in the tree opens a preview tab that the next click reuses. Double-click to keep a tab open.
- Copy body, message ID or messages as JSON; export messages to a JSON file.
- Environment tags (dev, test, prod) with colours. A connection tagged production is read-only by default.
- Light, dark or system theme.
- A demo namespace with made-up data (File → Open demo namespace) for trying it without Azure.


## Azure permissions

- **Entra ID sign-in**: Service Bus data roles on the namespace. Listing entities with counts uses the administration API, which needs **Azure Service Bus Data Owner**. Peeking needs **Azure Service Bus Data Receiver**.
- **Connection string**: listing needs a key with **Manage** rights. Peeking needs **Listen**.
- Networks that block AMQP port 5671 can use AMQP over WebSockets (a checkbox on the connection).

## MCP server for Claude

`src/QueueJanne.Mcp` is an MCP server over stdio. It reads the same saved connections as the app.

| Tool | What it does |
|---|---|
| `list_connections` | Saved namespaces |
| `add_entra_connection` | Saves a namespace that uses your Azure sign-in. New connections are read-only by default. It can't turn read-only off or replace connections of another kind, and connection strings can only be added in the app. |
| `list_entities` | Queues, topics and subscriptions with counts and the settings that affect operations |
| `get_entity` | Settings and counts of one entity, with subscription rules (for a topic: every subscription and its rules) |
| `peek_messages` | Peeks messages with paging and an optional filter. Large results get shorter bodies, with a note saying so. |
| `summarize_dead_letters` | Groups a dead-letter queue by reason, with the most common descriptions and subjects |
| `send_message` | Sends a test message, with properties and an optional schedule, to a queue or topic |
| `resubmit_dead_letters` | Resubmits dead letters chosen by sequence numbers, a filter or `all`, and returns the same warnings as the app |
| `delete_messages` | Deletes messages chosen by sequence numbers or a filter, and cancels scheduled ones |
| `copy_messages` | Copies or moves messages to another queue or topic, also in another saved connection |
| `purge` | Empties one sub-queue. You have to repeat the entity path in `confirmEntity`. |

Filters match on the dead-letter reason, text in the error description, the subject, an application property and its value, text in the body, and the enqueued time.

The tools that change messages are marked destructive, so Claude Code asks before each call. They refuse read-only connections, and they write to the same [operation journal](#operation-journal) as the app. To register the server, see [Use it from Claude Code](#use-it-from-claude-code-optional). Don't register it with `dotnet run`: build output on stdout breaks the MCP protocol.

## Design rules

- **Generic.** QueueJanne never assumes how a namespace is set up. It reads each entity's settings (max delivery count, lock duration, sessions, duplicate detection, forwarding) every time and shows them in the tab.
- **Reading is peeking.** Browsing never locks a message, so consumers and delivery counts are untouched.
- **Secrets stay local.** Connection strings are encrypted per user and never logged or sent through the MCP server.

## Projects

| Project | What it is |
|---|---|
| `src/QueueJanne.Core` | Connections, entity snapshots, peeking, body formatting, dead-letter summaries, the demo namespace. No UI. |
| `src/QueueJanne.App` | The Avalonia desktop app (MVVM with CommunityToolkit.Mvvm). |
| `src/QueueJanne.Mcp` | The MCP server. |
| `tests/QueueJanne.Core.Tests` | xUnit v3 tests for the core. |
| `tests/QueueJanne.App.Tests` | UI tests: the real windows and view models run headless on the demo namespace, with keyboard input. |
| `tools/QueueJanne.Snapshot` | Renders the main window with the demo namespace to PNG files, without showing a window. |
| `tools/UiDriver` | PowerShell module that drives the running app through Windows UI Automation, and a script that times common steps and reports UI freezes. |

Saved connections and view settings live in `%USERPROFILE%\.queuejanne` (`~/.queuejanne` on macOS). It is deliberately not AppData: Windows gives packaged apps, such as the Claude desktop app, a private copy of AppData, so an MCP server they start would not see your connections. Version 0.3 and earlier kept them in `%APPDATA%\QueueJanne`; the desktop app copies them over on its first start. Set `QUEUEJANNE_HOME` to use another folder.

## Build and test

```
dotnet build QueueJanne.slnx
dotnet test --solution QueueJanne.slnx
dotnet run --project tools/QueueJanne.Snapshot -- snapshots
powershell -File tools/UiDriver/Measure-Demo.ps1
```

`Measure-Demo.ps1` starts the app on the demo namespace with a throwaway settings folder, opens tabs, loads 15,230 messages, filters and groups, and prints how long each step took and how long the UI froze. Set `QUEUEJANNE_DIAGNOSTICS` to a file path to log UI freezes over 50 ms from any run of the app.

## Roadmap

1. **Next:** import from file (the journal and export format), drag and drop between entities, following a correlation ID across a namespace, sessions and session state, scheduled and deferred messages, and a subscription rules view.
2. **Then:** namespace discovery through Azure Resource Manager, desktop notifications for growing dead-letter queues, macOS Keychain for connection strings, and server-side batch delete when the Service Bus SDK exposes it.

## License

MIT. See [LICENSE](LICENSE).
