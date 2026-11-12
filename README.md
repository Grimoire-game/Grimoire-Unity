# Grimoire Plugin (Unity)

Unity plugin built on the **Grimoire Public API v1**, with an optional export
workflow for offline logic, translations, variables, and objects.

- Link any GameObject to a Grimoire object through its key (`code_id`) with the
  `GrimoireObjectLink` component.
- Open **Grimoire** to sign in, pick your company and game, then browse content,
  tasks, linked objects, or sync engine transforms.
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

1. Open `Window > Grimoire`.
2. **Sign in** with your Grimoire account (2FA is supported).
3. **Choose your company and game** from the dropdowns.

The selected company and game are stored per Unity project. Settings (API base
URL, game language) are stored per-user in `EditorPrefs`; nothing is written to
files that could be committed.

## Usage

### Content Library

Open `Window > Grimoire` and choose **Content Library** in the sidebar. This view
shows the selected game's objects, dialogs and
variables together. Choose a folder, search by name or key, then select an item
to inspect its fields, dialog nodes or variable elements. Values appear in
separate, wrapping cards grouped by section. With an unlinked GameObject
selected in the scene, select a Grimoire object and click **Bind to selected**
in its details to add an Object Link and load its fields.

The library loads each resource in pages and also shows imported dialog assets
and synced variables in the project. You can import or update a dialog, or sync
variables, directly from its details. The Grimoire Object Link inspector uses
the same browser as its object picker; the Dialog Player inspector can pick an
imported dialog from it.

The browser resolves folder IDs against the selected game's folder tree so the
sidebar shows folder names. If that metadata is unavailable, affected items
appear under **Other folders** and the original ID remains visible in details.

### Choosing where Object Links read values

In **Grimoire > Settings**, choose the project's **Object data source**:

- **Connected Snapshot** uses values synced from the API onto each Object Link.
  Player builds read that saved snapshot; they do not need a live API session.
- **Version Database** uses the imported version's `Database.Library` and live
  `ObjectRuntime` values. Import a Runtime Content Database version first.

The choice is saved in `Assets/Resources/GrimoireObjectSourceSettings.asset`, so
it also applies in builds. Each Object Link can inherit the project choice or
override it in its **Object values** section. When a version's Library key differs
from the API object's `code_id`, use **Pick** beside **Version key** to select the
matching entry. A missing version or key shows a warning and returns no value;
it does not silently read the other source.
Matching API and version field names are recognized automatically. If an export
renamed a field, add a **Field alias** on the Object Link to map the name used by
game code to the version field name.

Game scripts keep calling the same `GrimoireObjectLink` methods in either mode:

```csharp
link.TryGetNumber("health", out var health);
link.TryGetBool("hasBeard", out var hasBeard);
var friends = link.GetReferences("friends");
link.SetFieldValue("health", 30);
link.TriggerFunction("OnOpen");
```

Local editable values that differ from their Grimoire baseline override the
selected source. In Version Database mode, `SetFieldValue` also writes to
`ObjectRuntime` when it is available. `TriggerFunction` calls the imported
`LogicRuntime` in Version Database mode and fires
`GrimoireObjectLink.FunctionTriggered` in either mode. In Connected Snapshot
mode, subscribe to that event to implement function behavior; the API snapshot
does not contain an executable logic runtime.

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
level.TryGetDisplayString("title", out var localizedTitle); // active language
```

Linking also upserts this scene instance into the object's `game_engine_data`
(so Grimoire knows it exists in the engine). Removing the component, deleting
the GameObject, or clicking **Unlink** removes that entry again.

### Creating an object

**Create in Grimoire...** on a Grimoire Object Link, **Create object** on the
Scene view creates a library object with
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

Use **Sync now** on the component, or **Review** in Grimoire, to
push transform changes after moving objects. Review shows pending object and
text link counts, then the selected item and other changes. Commit actions
appear when there are object changes; translation actions stay with text links.

The first time you sign in and select a game, Grimoire automatically pulls its
content and shows progress above the workspace and in **Content Library**.
Use **Content Library > Pull from Grimoire > Sync everything** to run it again.
The pull downloads the game's language and string catalogs
once, then updates the runtime string and variable libraries, all game dialogs,
scene/prefab text links, object links, and cached nested objects. Every configured
language is stored in Unity for offline builds. Unrelated remote objects are not
created as assets. Local text and editable-field deviations are preserved, and
the final summary lists anything skipped or failed.

**Reset to Grimoire** (on the Object → Game Engine Data tab and the Sync tab)
restores the Unity transform from the values currently saved in Grimoire.

### Tasks tab

The **Tasks** tab lists all workflow tasks for the selected game via
`GET /api/v1/tasks`. Its Filters dropdown can show several assignees and
statuses at once.
Search the task list and select a row to inspect its status, assignee, and linked
content. The detail pane lets you update status or open a linked scene object.

Each open task gets a **Complete** button (moves it to the game's done status)
and a status dropdown for other workflow moves.

### Scene tab

The **Scene** tab combines scene browsing and object inspection in one workspace.
The left pane groups the loaded Unity scenes by hierarchy root, the middle pane
lists every GameObject (including unlinked objects), and the right pane shows
the selected object's values. Clicking a row selects that GameObject in Unity's
Hierarchy; selecting one in the Hierarchy updates the Scene workspace. Search
matches object names, paths, Grimoire keys, and cached field values. The
multi-select Filters dropdown narrows Object Links, Text Links, unlinked objects,
or objects with pending changes. An unlinked object's details provide **Bind from
Content Library**, which opens the library while keeping the GameObject selected.

The Grimoire sidebar groups Content Library, Scene, and Dialogs under
**Browse**; Tasks and Review under **Workflows**; Versions and Runtime under
**Database**; and Strings under **Artifacts**.

### Strings artifact

Open **Artifacts > Strings** to browse the selected game's source strings and
translations. Search keys or text, then use the multi-select **Types** filter to
show UI, Dialog, Object, Variable, or custom String Tags. **Copy shown keys**
copies the filtered list. Select one string to copy its key or a complete C#
lookup call. The game string library is saved by **Sync everything** at
`Assets/GrimoireStrings/Resources/GrimoireStrings.asset` for use in builds:

```csharp
var label = Grimoire.PluginV2.GrimoireStrings.Get("general/UI_Start_001");
```

The lookup follows the active game language and falls back to source text when
that translation is missing. A specific language can be passed as the second
argument.

**Game language** is selected once in Grimoire Connect **Settings**. Scene shows
the active language as read only. The dropdown offers the languages configured
for the selected game in Grimoire (Settings > Game Management), read from
`GET /api/v1/games`; the first entry shows the game's source text. Picking one:

- stores it as the shared editor language for this Unity project,
- clears legacy per-object preview overrides,
- downloads the game's translations from `GET /api/v1/strings` and refreshes
  linked scene text and object snapshots from the API while preserving local edits,
- updates the copy shown on Unity UI Text / TextMesh Pro targets and translated
  strings in Content Library, Dialogs, and Variables.

Links without copy for the chosen language preview as `not translated`, so gaps
are visible while authoring; players still fall back to the source text.

Use **Refresh game languages and translations** in Settings after editing
translations on the web. Missing translations show source text in content
details; scene Text Links show `not translated` while authoring.

### Scene object details

Select a GameObject with a `GrimoireObjectLink` in Scene or Unity's Hierarchy.
Its detail pane has these views:

- **Values** — current fields from the Object Link's selected data source,
  displayed in the game language configured in Settings
- **Edit** — game-engine fields (`hints.game_engine_editable: true`) with
  editors; use **Sync to Grimoire** to push changes back so the web app stays
  aligned
- **Tasks** — tasks and notes attached to the Grimoire object
- **Use in code** — copy field access code through the Object Link or object key;
  use a field card's shortcut to narrow the list to that field
- **Full details** — the complete API view, including linked scene instances

Scene objects with a `GrimoireTextLink` show a preview in the selected game
language. Unlinked GameObjects show their transform and Unity components.

Use **Change workspace** in Settings to switch company or game for this Unity
project.

### Dialogs tab

Play dialogs written in the Grimoire dialog editor without writing a login,
download, or parser yourself.

**Dialogs in 3 steps**

1. Open `Window > Grimoire`, choose **Dialogs** in the sidebar,
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
The Dialogs view now keeps the scene player, import filter, dialog list, and
selected dialog details together. Imported dialogs show their node and variable
counts and starting line.

The language follows the Connect game language in the editor and the language
selected by `GrimoireBootstrap` in builds. Set **Language** on the player to
force one. Lines without a translation fall back to the source text. Calling
`GrimoireBootstrap.ChangeLanguage` reapplies text links and republishes the
current dialog line immediately.

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

`DialogLine` also carries `SpeakerId`, `VoiceUrl`, `NodeId`, `NodeName`,
`NodeType`, `StringId`, `Language`, and `IsLast`. `line.Node` is the imported
node itself (untranslated text, all translations, every option including hidden
ones) for when you need more; read it but don't change it.

**Reading the current line from anywhere**

`GrimoireDialog` is a static shortcut to the player in the scene, so any script
can ask what is on screen without a player reference:

```csharp
if (GrimoireDialog.IsPlaying)
{
    Debug.Log($"{GrimoireDialog.Speaker}: {GrimoireDialog.Text}");
    foreach (var choice in GrimoireDialog.Choices)
    {
        Debug.Log($"  {choice.Index}: {choice.Text}");
    }
}

if (GrimoireDialog.TryGetCurrent(out var line))
{
    portrait.Show(line.SpeakerId);              // everything from DialogLine
}

// React to every line, question, start, and end:
GrimoireDialog.LineShown += line => Debug.Log(line.Text);
GrimoireDialog.Started += (player, dialog) => { };
GrimoireDialog.Ended += (player, reason) => { };
```

It also has `Current`, `CurrentDialog`, `SpeakerId`, `NodeId`, and `IsChoice`.
Unsubscribe from the events in `OnDisable` like any C# event.

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
the player falls back to `ObjectRuntime`. **Type element** references use the
synced variables (see below), or these hooks when variables aren't synced. A
value that can't be found is treated as empty and logged once. A condition where
no branch matches also logs a warning, because the dialog ends there.

**Variables (types such as Mood)**

Grimoire stores a `types` field as an element id, so a setter like
`Bofur: Mood → Angry` writes `d8797c69-…`, not "Angry". Sync the game's variables
on the **Variables** tab (see below; importing a dialog syncs them too). Then turn
ids into names anywhere:

```csharp
if (bofurLink.TryGetVariableElement("Mood", out var mood))
{
    Debug.Log(mood.Name);                        // "Angry", also after a dialog setter
    Debug.Log(mood.Variable?.Name);              // "Mood"
}

GrimoireVariables.ElementName(id);               // "Angry"
GrimoireVariables.TryFindElement("Mood", "Angry", out var angry);
GrimoireVariables.GetValue<float>("Difficulty", "Easy");
```

Dialog setters that write a variable element change its value for the play
session (`GrimoireVariables.SetValue`, with a `ValueChanged` event); the asset is
never modified. Range conditions on range variables use the synced min and max.

Speaker names are filled in for **object** and **free text** speaker modes. In
**type** mode only `DialogLine.SpeakerId` is set.

In Play Mode the player's inspector lets you pick any dialog asset and start it,
and shows the current node, Next / answer buttons, and the live variable values.

### Variables tab

Choose **Content Library > Variables** in Grimoire to browse the game's
variables, such as Mood with Neutral, Happy and
Angry. **Sync variables** saves them to
`Assets/GrimoireVariables/Resources/GrimoireVariables.asset`; builds read that
asset, so no sign-in is needed. The first visit syncs automatically.

Variables are grouped like on the Grimoire Variables page and can be searched by
variable name, element name or element id. Each element has **Copy** (a ready
code snippet, such as checking whether an object's Mood field is Angry, or
reading a number with `GrimoireVariables.GetValue<float>`) and **Copy ID** (the
element id stored in object fields and dialog setters).

### Export tab (optional)

Use **Versions** in the Grimoire sidebar
to list Unity export versions for the selected game, download a ZIP, and extract
it to `Assets/Grimoire/`. Then use the **Runtime** tab (next to Versions) to finish
setup and inspect live values.
The Versions view shows the imported version (when it was imported through this
window), searchable available versions, and details for the selected version.

### Runtime tab

The **Runtime** tab lists scene Object Links from their API snapshots (an imported
export is optional for this list). After importing a Unity export from **Versions**:

1. Use **Add to scene** for Bootstrap (required for export runtimes) and Session Tracker (optional).
2. Paste a playthrough session ID if you want events streamed to the platform.
3. Press **Play** — live variables, objects, dialogs, and logic appear in the same tab.

Choose **Runtime** in the sidebar to inspect live values. **Browse imported database**
in Versions explores the imported C# data.

During Play Mode, **Changes** shows timestamped before and after values for each
observed change. **Linked now** shows current values, including linked data that
has not changed. The capture starts when Play Mode begins, even if the Grimoire
window is closed. Object Link setters, synced variable setters, text updates,
dialog lines, and function calls are captured through events; generated runtimes
and scene state are compared every 0.75 seconds. Shorter changes between polls
may not appear. Pause stops recording history while current values continue to
update. History is kept in memory for the current Play Mode session, with a
500-change limit. The previous advanced inspector remains available in Runtime.

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
| `GET /api/v1/variables` | Variables (types) with their elements on the Variables tab, synced to `Assets/GrimoireVariables/Resources` |
| `GET /api/v1/strings` | String picker + code (abbrev) resolution, with translations (also used for dialog translations) |
| `PATCH /api/v1/strings/{id}/translations/{language_code}` | Push edited translations |
| `GET /api/v1/statuses?domain=tasks` | Valid task workflow statuses |
| `PATCH /api/v1/tasks/{id}` | Update a task's status |
| `GET /api/exports/versions` | List Unity export ZIPs for a game |
