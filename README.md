# Grimoire Plugin (Unity)

Editor-only Unity plugin built on the **Grimoire Public API v1**.

- Link any GameObject to a Grimoire object through its key (`code_id`) with the
  `GrimoireObjectLink` component.
- Select a linked GameObject and the **Grimoire Object Widget** shows the full,
  render-ready object data (sections, fields, references, media, ...).
- Tasks and notes attached to the object are listed in the widget. Update task
  workflow statuses directly from Unity.

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

1. Open `Window > Grimoire > Object Widget 2`.
2. **Sign in** with your Grimoire account (2FA is supported).
3. **Select a game** from the list of games you have access to.

The selected game is stored per Unity project. Settings (API base URL, locale)
are stored per-user in `EditorPrefs`; nothing is written to files that could
be committed.

## Usage

### Linking a GameObject

Add the `Grimoire > Grimoire Object Link` component to a GameObject and either:

- type the object key (`code_id`, e.g. `characters/aragorn`) directly, or
- click **Pick from Grimoire...** to search and select an object.

The resolved object UUID is cached on the component so subsequent lookups skip
the key-resolution step.

### Viewing data and tasks

With the widget window open, click a linked GameObject in the Hierarchy. The
widget fetches the object's view document and draws it, followed by every task
and note attached to the object.

Each open task gets a **Complete** button (moves it to the game's done status)
and a status dropdown for other workflow moves.

Use **Change game** in the toolbar to switch which Grimoire game this Unity
project connects to.

## API endpoints used

| Endpoint | Purpose |
| --- | --- |
| `POST /api/v1/auth/login` / `verify-2fa` / `refresh` | User sign-in |
| `GET /api/v1/games` | List games the signed-in user can access |
| `GET /api/v1/objects` | Object picker + key (`code_id`) resolution |
| `GET /api/v1/objects/{id}` | Object View Document incl. attached tasks |
| `GET /api/v1/statuses?domain=tasks` | Valid task workflow statuses |
| `PATCH /api/v1/tasks/{id}` | Update a task's status |
