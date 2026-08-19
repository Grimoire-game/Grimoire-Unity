# Grimoire Plugin (Unity)

Unity plugin built on the **Grimoire Public API v1**, with an optional export
workflow for offline logic, translations, variables, and objects.

- Link any GameObject to a Grimoire object through its key (`code_id`) with the
  `GrimoireObjectLink` component.
- Open **Grimoire Connect** to sign in, pick your company and game, then browse
  tasks, inspect linked objects, or sync engine transforms.
- Optionally download a Unity export ZIP into `Assets/Grimoire/` and use
  `GrimoireBootstrap` at runtime for language, variables, objects, and logic.

Connect networking and UI are editor-only. The runtime assembly ships
`GrimoireObjectLink` plus optional components (`GrimoireBootstrap`,
`GrimoireSessionTracker`) for projects that import an export. Skip the Export
tab if you only use Connect.

## Requirements

- Unity **2021.3** or newer
- `com.unity.nuget.newtonsoft-json` (installed automatically as a dependency)
- A Grimoire user account with access to at least one game

## Installation

Add the package to your project via the Package Manager:

- **From disk:** `Window > Package Manager > + > Add package from disk...` and
  select this folder's `package.json`.
- **From git:** add the repository URL (pointing at this folder) to your
  project's `manifest.json`.

## Setup

1. Open `Window > Grimoire > Grimoire Connect`.
2. **Sign in** with your Grimoire account (2FA is supported).
3. **Choose your company and game** from the dropdowns.

The selected company and game are stored per Unity project. Settings (API base
URL, locale) are stored per-user in `EditorPrefs`; nothing is written to files
that could be committed.

## Usage

### Linking a GameObject

Add the `Grimoire > Grimoire Object Link` component to a GameObject and click
**Pick from Grimoire...** to search and select an object.

The resolved object UUID is cached on the component so subsequent lookups skip
the key-resolution step. **Pick** / **Refresh from Grimoire** also stores an API
snapshot of every field (including lists of related objects such as `levels`).
Use **Prefetch references** so nested objects are available in Play Mode and
builds without a live API call.

Other scripts read that snapshot directly — no exported C# database required:

```csharp
var link = GetComponent<GrimoireObjectLink>();
link.TryGetNumber("health", out var hp);
var levels = link.GetReferences("levels");
var level = await link.GetReferencedObjectAsync("levels", 0);
level.TryGetString("title", out var title);
```

Linking also upserts this scene instance into the object's `game_engine_data`
(so Grimoire knows it exists in the engine). Removing the component, deleting
the GameObject, or clicking **Unlink** removes that entry again.

On the component, choose which fields to sync (defaults on):

- **Position** → `location`
- **Rotation** → `rotation`
- **Scale** → `scale`
- **Id / Name** → `engine_instance_id` (Unity `GlobalObjectId` + GameObject name)

Use **Sync now** on the component, or the **Sync** tab in Grimoire Connect, to
push transform changes after moving objects. The Sync tab lists the selected
object separately from other pending linked objects and can sync or reset one
or all.

**Reset to Grimoire** (on the Object → Game Engine Data tab and the Sync tab)
restores the Unity transform from the values currently saved in Grimoire.

### Tasks tab

The **Tasks** tab lists all workflow tasks for the selected game via
`GET /api/v1/tasks`. Use the assignee filter to show all tasks, only tasks
assigned to you, or tasks assigned to a specific team member.

Each open task gets a **Complete** button (moves it to the game's done status)
and a status dropdown for other workflow moves.

### Object tab

With Grimoire Connect open, select a linked GameObject in the Hierarchy. The
**Object** tab fetches the object's view document and exposes three sub-tabs:

- **Info** — object overview, informational fields
  (`hints.game_engine_editable: false`), and attached tasks/notes
- **Editable** — game-engine fields (`hints.game_engine_editable: true`) with
  editors; use **Sync to Grimoire** to push changes back so the web app stays
  aligned
- **Game Engine Data** — linked scene instance transforms

Use **Change workspace** in the toolbar to switch company or game for this Unity
project.

### Export tab (optional)

Use the **Versions** tab in Grimoire Connect (or `Window > Grimoire > Export Importer`)
to list Unity export versions for the selected game, download a ZIP, and extract
it to `Assets/Grimoire/`. Then use the **Runtime** tab (next to Versions) to finish
setup and inspect live values.

### Runtime tab

The **Runtime** tab lists scene Object Links from their API snapshots (an imported
export is optional for this list). After importing a Unity export from **Versions**:

1. Use **Add to scene** for Bootstrap (required for export runtimes) and Session Tracker (optional).
2. Paste a playthrough session ID if you want events streamed to the platform.
3. Press **Play** — live variables, objects, dialogs, and logic appear in the same tab.

`Window > Grimoire > Runtime Inspector` opens Connect on this tab.
`Window > Grimoire > Database Browser` explores the imported C# data.

Projects that only use Connect can ignore Versions and Runtime entirely—no export
folder or Bootstrap component is required.

## API endpoints used

| Endpoint | Purpose |
| --- | --- |
| `POST /api/v1/auth/login` / `verify-2fa` / `refresh` | User sign-in |
| `GET /api/v1/games` | List games the signed-in user can access |
| `GET /api/v1/users` | Game members for task assignee filters |
| `GET /api/v1/tasks` | List tasks for a game (optional assignee filter) |
| `GET /api/v1/objects` | Object picker + key (`code_id`) resolution |
| `GET /api/v1/objects/{id}` | Object View Document incl. attached tasks |
| `PATCH /api/v1/objects/{id}` | Replace `game_engine_data`, or update `game_engine_editable` field values |
| `GET /api/v1/statuses?domain=tasks` | Valid task workflow statuses |
| `PATCH /api/v1/tasks/{id}` | Update a task's status |
| `GET /api/exports/versions` | List Unity export ZIPs for a game |
