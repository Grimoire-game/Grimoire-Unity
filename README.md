# Grimoire Plugin (Unity)

Unity plugin built on the **Grimoire Public API v1**, with an optional export
workflow for offline logic, translations, variables, and objects.

- Link any GameObject to a Grimoire object through its key (`code_id`) with the
  `GrimoireObjectLink` component.
- Open **Grimoire Connect** to sign in, pick your company and game, then browse
  tasks, inspect linked objects, or sync engine transforms.
- Add a Grimoire dialog to the scene from the **Dialogs** tab with one click,
  and play it with `GrimoireDialogPlayer`.
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
URL, scene language) are stored per-user in `EditorPrefs`; nothing is written to
files that could be committed.

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

### Creating an object

**Create in Grimoire...** on a Grimoire Object Link, **Create object** on the
Scene tab, or `Window > Grimoire > Create Object` creates a library object with
`POST /api/v1/objects`.

Creation saves a **draft** in the selected game as soon as you confirm. You
choose a name and either a blank object (Grimoire's default Title field) or an
existing template. A Code ID is optional; leave it empty and Grimoire generates
one. If that Code ID is already used, creation stops.

The confirmation names the game, whether the object is blank or built from a
template, and any object that already has the same name. Linking the GameObject
is a separate checkbox. It stays off unless you turn it on, and the transform
sync that follows is still queued for review. Publish the draft in Grimoire
when it is ready.

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

### Scene tab

The **Scene** tab lists everything in the loaded scenes that is linked to
Grimoire — `GrimoireObjectLink` objects and `GrimoireTextLink` copy — with change
badges, search, and filters for kind / changed / template.

**Scene language** switches the whole scene at once. The dropdown offers the
languages configured for the game in Grimoire (Settings > Game Management), read
from `GET /api/v1/games`; the first entry is the game's source language, meaning
no translation. Picking one:

- stores it as the shared editor language for this Unity project,
- clears any per-object **Preview language** so nothing keeps its own locale,
- refreshes the copy shown on every Unity UI Text / TextMesh Pro target,
- and loads translatable object fields in that language.

Links without copy for the chosen language preview as `not translated`, so gaps
are visible while authoring; players still fall back to the source text.

To preview a single object in a different language, set **Preview language** on
its Grimoire Text Link. The next scene-language switch clears it again.

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

### Dialogs tab

Play dialogs written in the Grimoire dialog editor without writing a login,
download, or parser yourself.

**Dialogs in 3 steps**

1. Open `Window > Grimoire > Dialogs` (or the **Dialogs** tab in Grimoire Connect)
   and click **Add Dialog to scene** at the top. Do this once per scene.
2. Click **Import** on each dialog you need.
3. Play one from code by passing the asset to the player:
   `GrimoireDialogPlayer.Main.StartDialog(bedDialog);`

**Add Dialog to scene** adds one `Grimoire Dialog` object to the open scene. It
holds a **Grimoire Dialog Player** and a child canvas with a **Grimoire Dialog
View** (speaker name, line, Continue button, one button per answer). An
EventSystem is added when the scene has none. That single player plays every
dialog; when the scene already has one, the button selects it instead.

**Import** saves the dialog as an asset in `Assets/GrimoireDialogs/`. The asset
contains the full node graph, the translations of every line and answer, and
the speaker names. Builds read only this asset, so players never sign in or go
online. Dialogs that this one jumps to are imported with it. When a dialog
changes in Grimoire, click **Update**; **Select asset** shows it in the Project
window.

The language follows the Connect scene language in the editor and the export's
`TranslationKey.CurrentLanguage` in builds. Set **Language** on the player to
force one. Lines without a translation fall back to the source text.

**Driving a dialog from code**

`GrimoireDialogPlayer` is the only script you need. Give your own script a
`GrimoireDialogAsset` field, drag the asset in from `Assets/GrimoireDialogs/`,
and pass it to the player:

```csharp
using Grimoire.PluginV2;
using UnityEngine;

public class Bed : MonoBehaviour
{
    public GrimoireDialogAsset bedDialog;

    public void Interact()
    {
        var dialogs = GrimoireDialogPlayer.Main;   // the player in the scene
        if (!dialogs.IsPlaying)
        {
            dialogs.StartDialog(bedDialog);        // begin at the starting node
        }
    }
}

// dialogs.Next();                              // continue after a normal line
// dialogs.Choose(0);                           // pick an answer on a question
// dialogs.Stop();                              // end early
// dialogs.SetVariable(bedDialog, "hasKey", true); // set before StartDialog
// dialogs.GetVariable<bool>("hasKey");         // variable of the playing dialog
```

`GrimoireDialogPlayer.Main` is set when the player is enabled, so use it from
`Start` or later (or keep a serialized reference to the player instead).
Starting a dialog while another one plays stops the first one. Unless **Keep
Variables Between Runs** is on, dialog variables go back to their Grimoire values
when a dialog ends.

**Your own UI**

The default view only uses the player's events, so replacing it takes a few
lines:

```csharp
using Grimoire.PluginV2;
using UnityEngine;

public class MyDialogUi : MonoBehaviour
{
    public GrimoireDialogPlayer player;

    void OnEnable()
    {
        player.OnLine.AddListener(ShowLine);       // normal line: call Next()
        player.OnChoices.AddListener(ShowChoices); // question: call Choose(index)
        player.OnDialogEnded.AddListener(Hide);
    }

    void ShowLine(DialogLine line)
    {
        Debug.Log($"{line.Speaker}: {line.Text}");
        // When the player clicks: player.Next();
    }

    void ShowChoices(DialogLine line)
    {
        Debug.Log(line.Text);
        foreach (var choice in line.Choices)
        {
            Debug.Log($"  {choice.Index}: {choice.Text}");
        }
        // When the player picks one: player.Choose(choice.Index);
    }

    void Hide() { }
}
```

`DialogLine` also carries `SpeakerId`, `VoiceUrl`, `NodeId`, and `IsLast`.

**What plays automatically**

Line and question nodes wait for the player. Context, condition, setter, jump,
and external-dialog nodes are resolved on their own. Answer visibility
conditions and "pick once" questions behave as in the Grimoire play view. An end
node with text is shown before the dialog closes.

Conditions and setters that use dialog variables work out of the box. Ones that
reference an **object field** (for example `bed: interactionCount`) read and
write the imported export's `ObjectRuntime`, so import a Unity export too. To use
your own source instead (a save game, for example), assign
`player.ReadExternalValue` / `player.WriteExternalValue`. When they return null,
the player falls back to `ObjectRuntime`. **Type element** references always need
these hooks. A value that can't be found is treated as empty and logged once. A
condition where no branch matches also logs a warning, because the dialog ends
there.

Speaker names are filled in for **object** and **free text** speaker modes. In
**type** mode only `DialogLine.SpeakerId` is set.

In Play Mode the player's inspector lets you pick any dialog asset and start it,
and shows the current node, Next / answer buttons, and the live variable values.

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
| `GET /api/v1/games` | List games the signed-in user can access, with each game's supported languages |
| `GET /api/v1/users` | Game members for task assignee filters |
| `GET /api/v1/tasks` | List tasks for a game (optional assignee filter) |
| `GET /api/v1/objects` | Object picker + key (`code_id`) resolution |
| `POST /api/v1/objects` | Create a draft library object (blank or from a template) |
| `GET /api/v1/objects/{id}` | Object View Document incl. attached tasks |
| `PATCH /api/v1/objects/{id}` | Replace `game_engine_data`, or update `game_engine_editable` field values |
| `GET /api/v1/dialogs` | Dialog list on the Dialogs tab |
| `GET /api/v1/dialogs/{id}` | Dialog node graph for importing into an asset |
| `GET /api/v1/strings` | String picker + code (abbrev) resolution, with translations (also used for dialog translations) |
| `PATCH /api/v1/strings/{id}/translations/{language_code}` | Push edited translations |
| `GET /api/v1/statuses?domain=tasks` | Valid task workflow statuses |
| `PATCH /api/v1/tasks/{id}` | Update a task's status |
| `GET /api/exports/versions` | List Unity export ZIPs for a game |
