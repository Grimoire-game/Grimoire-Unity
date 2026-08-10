# Grimoire Plugin (Unity)

Editor-only Unity plugin built on the **Grimoire Public API v1**.

- Link any GameObject to a Grimoire object through its key (`code_id`) with the
  `GrimoireObjectLink` component.
- Open **Grimoire Connect** to sign in, pick your company and game, then browse
  tasks or inspect linked objects from the scene.
- Tasks and notes attached to an object are listed on the Object tab. Update
  task workflow statuses directly from Unity.

Nothing in this package ships in player builds: the runtime assembly contains
only the serialized link component; all networking and UI live in the editor
assembly.

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
the key-resolution step.

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
object separately from other pending linked objects and can sync one or all.

### Tasks tab

The **Tasks** tab lists all workflow tasks for the selected game via
`GET /api/v1/tasks`. Use the assignee filter to show all tasks, only tasks
assigned to you, or tasks assigned to a specific team member.

Each open task gets a **Complete** button (moves it to the game's done status)
and a status dropdown for other workflow moves.

### Object tab

With Grimoire Connect open, select a linked GameObject in the Hierarchy. The
**Object** tab fetches the object's view document and draws it, followed by
every task and note attached to that object.

Use **Change workspace** in the toolbar to switch company or game for this Unity
project.

## API endpoints used

| Endpoint | Purpose |
| --- | --- |
| `POST /api/v1/auth/login` / `verify-2fa` / `refresh` | User sign-in |
| `GET /api/v1/games` | List games the signed-in user can access |
| `GET /api/v1/users` | Game members for task assignee filters |
| `GET /api/v1/tasks` | List tasks for a game (optional assignee filter) |
| `GET /api/v1/objects` | Object picker + key (`code_id`) resolution |
| `GET /api/v1/objects/{id}` | Object View Document incl. attached tasks |
| `PATCH /api/v1/objects/{id}` | Replace `game_engine_data` (engine instance sync) |
| `GET /api/v1/statuses?domain=tasks` | Valid task workflow statuses |
| `PATCH /api/v1/tasks/{id}` | Update a task's status |
