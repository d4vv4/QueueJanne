# What QueueJanne does, and what's next

✅ done · 🟡 partly · ❌ not yet

## Browsing and inspecting

| Feature | Status |
|---|---|
| Tree of queues, topics and subscriptions with counts | ✅ with dead-letter badges, and Messages and Dead-letter nodes per entity |
| Several connections and tabs | ✅ plus preview tabs on single click |
| Server groups and folders | ❌ (environment tags and colours instead) |
| Grouping entities by name prefix | ❌ |
| Progressive loading with Top N | ✅ |
| JSON, XML, text and hex viewers | ✅ |
| GZip/Deflate bodies | ✅ unpacked automatically for viewing and search |
| WCF and .NET-serialized bodies, choosing text encodings | ❌ (UTF-8 and UTF-16 are detected) |
| Configurable columns, including application properties | ✅ saved and shared by all tabs |
| Columns from JSON, XPath or regex expressions | ❌ |
| Sort, search and filter loaded messages | ✅ text search plus a filter bar (reason, subject, property = value, body text) |
| Group rows by a value | ✅ by reason, error, subject, content type or any property, with counts |
| Saved views per entity | 🟡 column layout is saved, filters aren't |
| Auto-refresh that highlights changes | ✅ rows pulse, coloured by direction |
| Sessions and session state | ❌ (session ID column only) |
| Subscription rules | 🟡 read-only through the MCP server |
| Dark and light theme, adjustable text size | ✅ |

## Changing messages

| Feature | Status |
|---|---|
| Resubmit dead letters to their source | ✅ with warnings for topic fan-out and duplicate detection, optional new message ID |
| Delete messages | ✅ cancels scheduled ones; warns about locks on active queues |
| Purge | ✅ choose active, dead-letter and transfer dead-letter |
| Copy and move between entities and namespaces | ✅ Copy or move to… (no drag and drop yet) |
| Edit a message and send it again | ✅ with warnings from the target's settings |
| Send test messages | ✅ with properties, up to 100 copies |
| Scheduled and deferred messages | 🟡 scheduled messages can be cancelled |
| Save and load message files | 🟡 export to JSON and the operation journal, no import yet |
| Export to CSV, bodies to files | ❌ |
| Read-only connections | ✅ refuse changes; production needs a typed confirmation |
| Operation journal | ✅ every change backs up the messages first, from the app and the MCP server |

## Managing entities

| Feature | Status |
|---|---|
| Create, edit and delete queues, topics, subscriptions and rules | ❌ |
| Compare definitions between namespaces | ❌ |
| Service Bus emulator | ❌ untested (its connection string may already work) |

## Connecting

| Feature | Status |
|---|---|
| Entra ID and shared access keys | ✅ Entra ID with an optional tenant ID; connection strings encrypted per user |
| AMQP over WebSockets | ✅ |
| Single-entity access | 🟡 namespace level only |
| Windows, macOS, Linux | ✅ Windows and macOS (Linux builds but isn't packaged) |

## What makes it different

- **An MCP server, so Claude can use it.** It lists, peeks, filters, summarises, resubmits, deletes, purges, sends and copies. Every change is confirmed and respects read-only flags that Claude can't turn off, and secrets never pass through it.
- **Dead-letter summary**: groups a dead-letter queue by reason, with the most common errors and subjects.
- **Warnings worked out from each entity's live settings** (max delivery count, lock duration, duplicate detection, topic fan-out) before anything changes.
- **Plain-language connection errors**, for example a missing role, the wrong tenant or no sign-in.
- **An operation journal** with a backup of every message before it's resubmitted, deleted, moved or purged.
- **A live feel**: changed counts pulse, a "Live" indicator shows auto-refresh is on, and busy icons spin.

## Ideas

Roughly in order of value:

1. **Drag and drop and templates.** Drag messages between tree nodes or namespaces, paste a production dead letter into a test namespace to reproduce it, and send from templates built from recent messages with the same subject.
2. **Follow a correlation ID.** Search every subscription in a namespace for one correlation ID, or any property value, to see where a message went and where it got stuck.
3. **Ask Claude from the app.** An "Explain this dead letter" button that sends the message, its reason and the entity settings to Claude, and suggests a fix or the right action.
4. **Desktop notifications.** A tray icon that warns when a dead-letter queue grows past a threshold, per namespace and environment.
5. **Subscription rules view.** Show each subscription's filters, and for a selected message, which subscriptions it would reach.
6. **Sessions view.** List sessions from the peeked messages, with each session's messages and its state.
7. **Namespace discovery.** Sign in once and pick from every Service Bus namespace your account can see in Azure, instead of typing names.
8. **Trend sparklines** per entity from Azure Monitor metrics (incoming, outgoing, dead-lettered over 24 hours).
9. **More body decoders**: Base64 inside JSON and other encodings.
10. **Import** from QueueJanne's JSON export and from CSV, to replay messages in another environment.
11. **Entity management and definition diff.** Create and edit entities and rules, and compare test with production definitions.
12. **Saved views**: filters and grouping kept per entity.
13. **Easier installs**: signed releases, automatic updates, and the MCP server as a `dotnet tool`.
