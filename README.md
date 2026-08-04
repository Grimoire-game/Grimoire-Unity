# Grimoire Plugin (Unity)

Editor-only Unity plugin built on the **Grimoire Public API v1**.

- Link any GameObject to a Grimoire object through its key (`code_id`) with the
  `GrimoireObjectLink` component.
- Select a linked GameObject and the **Grimoire Object Widget** shows the full,
  render-ready object data (sections, fields, references, media, ...).
- Tasks and notes attached to the object are listed in the widget. Sign in with
  your Grimoire account to complete tasks or change their workflow status.

Nothing in this package ships in player builds: the runtime assembly contains
only the serialized link component; all networking and UI live in the editor
assembly.

## Requirements

- Unity **2021.3** or newer
- `com.unity.nuget.newtonsoft-json` (installed automatically as a dependency)
- A Grimoire **company API key** with at least the `objects:read` and
  `tasks:read` scopes (Grimoire platform → Settings → API Keys)
- To update task statuses: a Grimoire user account (`tasks:write` scope on the
  key is not enough — the API requires a signed-in user)

## Installation

Add the package to your project via the Package Manager:

- **From disk:** `Window > Package Manager > + > Add package from disk...` and
  select this folder's `package.json`.
- **From git:** add the repository URL (pointing at this folder) to your
  project's `manifest.json`.

## Setup

1. Open `Window > Grimoire > Object Widget 2`.
2. Open the **Settings** foldout and fill in:
   - **API base URL** — default `https://api.usegrimoire.com`
   - **Game ID** — the UUID of your game
   - **API key / secret** — company API key credentials
3. (Optional) Click **Sign in...** and log in with your Grimoire account to be
   able to complete tasks. 2FA is supported.

Settings are stored per-user and per-project in `EditorPrefs`; nothing is
written to files that could be committed.

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

When signed in, each open task gets a **Complete** button (moves it to the
game's done status) and a status dropdown for other workflow moves.

## API endpoints used

| Endpoint | Purpose |
| --- | --- |
| `GET /api/v1/objects` | Object picker + key (`code_id`) resolution |
| `GET /api/v1/objects/{id}` | Object View Document incl. attached tasks |
| `GET /api/v1/statuses?domain=tasks` | Valid task workflow statuses |
| `PATCH /api/v1/tasks/{id}` | Update a task's status (signed-in user) |
| `POST /api/v1/auth/login` / `verify-2fa` / `refresh` | User sign-in |
