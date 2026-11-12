# Grimoire Unity Editor redesign — concept

## Latest: combined Scene and Object workspace

The [combined Scene concept](./SceneObjectUnifiedConcept.html) replaces the
separate Scene and Object destinations with one Content Library-style browser.
Its left pane follows loaded Unity scenes and hierarchy groups, the middle pane
lists every GameObject (including unlinked objects), and the right pane shows
the selected GameObject's Grimoire values, editing controls, or code example.
Selecting a row would also select that GameObject in Unity; a selection made in
Unity's Hierarchy would highlight the same row. Unlinked GameObjects have a
Content Library binding action. Text Links preview the game language chosen in
Settings. In a narrow dock, the list sits above the selected object's details.

Previews: [linked Object Link](./UnifiedSceneLinkedConcept.png),
[unlinked GameObject](./UnifiedSceneUnlinkedConcept.png),
[Text Link](./UnifiedSceneTextConcept.png),
[Use in code](./UnifiedSceneCodeConcept.png), and
[narrow dock](./UnifiedSceneNarrowConcept.png). These use illustrative data.
The plugin implementation has not changed for this concept.

## Object workspace update

The [revised Object tab concept](./ObjectTabRedesign.html) brings the selected
Unity GameObject into the same header, search, filter, and split-pane layout as
Content Library. It shows source values, editable fields, linked scene instances,
tasks, and variables as sections of one Object workspace. A **Use in code**
helper stays at the top of the right pane: select a field and copy a complete
`GrimoireObjectLink` example. That snippet reads through the configured data
source, so game code does not need to switch between API snapshots and imported
versions. In a narrow dock, **Use in code** is available from the section list.
The right pane also keeps the selected scene link and its transform visible.
Static
previews: [Overview](./ObjectTabOverviewDesign.png),
[Editable fields](./ObjectTab-fields-Design.png),
[Linked instances](./ObjectTab-instances-Design.png),
[Use in code](./ObjectTab-code-Design.png),
[Tasks and variables](./ObjectTab-work-Design.png), and
[narrow layout](./ObjectTab-narrow-Design.png).

This is an illustrative design for review. The Object tab implementation has
not been changed for this concept.

## Current recommendation: Content Library

The focused [Content Library concept](./ContentLibraryConcept.html) puts objects, dialogs, and variables in one folder-based browser with immediate value previews and context-aware picking. See its [design notes](./ContentLibraryConcept.md). The wider workspace concept below remains useful for tasks, sync review, versions, and runtime tools.

The revised [Scene, Object, and Review concepts](./WorkspaceViewsConcept.html) show those workflows in the same dockable shell and visual style as Content Library. See the [design notes](./WorkspaceViewsConcept.md) or static previews: [Scene](./SceneWorkspaceConcept.png), [Object](./ObjectWorkspaceConcept.png), [Review](./ReviewWorkspaceConcept.png), and [narrow Object](./ObjectWorkspaceNarrowConcept.png).

The [Dialogs, Tasks, Versions, and Runtime concepts](./WorkflowViewsConcept.html) extend that shell. The [runtime design notes](./WorkflowViewsConcept.md) define a Play Mode change feed and live linked-value inventory across every observable Grimoire link. Static previews: [Dialogs](./DialogsWorkspaceConcept.png), [Tasks](./TasksWorkspaceConcept.png), [Versions](./VersionsWorkspaceConcept.png), [Runtime changes](./RuntimeWorkspaceConcept.png), [Runtime linked now](./RuntimeLinkedNowConcept.png), and [narrow Runtime](./RuntimeWorkspaceNarrowConcept.png).

These concepts guided the current single-window Grimoire workspace. Open [GrimoireEditorConcept.html](./GrimoireEditorConcept.html) in a browser to review the clickable mockups. The mockup content is illustrative, not live project data. Static previews: [Scene browser](./SceneBrowserConcept.png), [Review changes](./ReviewChangesConcept.png), and [Narrow Object view](./NarrowObjectConcept.png).

## Direction

Use one dockable **Grimoire** workspace with a compact navigation rail, a persistent project and scene context bar, and a right-side detail pane when space allows. The same content collapses to one column in a narrow dock. Keep Unity's Hierarchy selection as the source of truth for the Object view; selecting a row in Grimoire also selects or pings the Unity object.

Odin Inspector informed the structure: related fields in clear groups; tabs for distinct tasks; searchable, scannable lists; inline details; and concise validation at the field or action. These are design patterns, not a proposed Odin package dependency. The current package has no Odin dependency, so the proposed implementation can use Unity Editor UI Toolkit and existing IMGUI integrations without changing consumer project requirements.

## Information architecture

| Destination | Main purpose | Existing surface |
| --- | --- | --- |
| Scene | Find linked objects and text, switch scene language, create an object | Scene tab |
| Object | Inspect selected link, edit allowed fields, see variables and tasks | Object tab, Object Link inspector |
| Review | Compare local and Grimoire values, queue changes, reset with confirmation | Sync tab, per-object sync controls |
| Dialogs | Check scene player, browse and import dialog assets | Dialogs tab |
| Tasks | Filter assigned work and update status | Tasks tab |
| Versions | Download and extract a selected export version | Versions tab |
| Runtime | Inspect session, variables, dialogs, and scene snapshot in Play Mode | Runtime tab |

## Key interactions

1. **Scene → Object:** Search or filter linked content, select a row, and show its details. The selected Unity GameObject stays visibly identified. A text link can be inspected without pretending it has object fields.
2. **Object → Review:** Show read-only source values separately from editable game-engine fields. Changes get a visible pending marker beside the field and in the navigation. The review screen shows old and new values and the exact object that will be queued.
3. **Review action:** The current code's “Commit” queues changes for review. Label the primary action **Queue for review** and explain the destination. Reset is a separate secondary action with a confirmation that names the affected objects or text links.
4. **Dialogs:** The scene-player prerequisite is a short, actionable setup state. Imported, update available, and missing requirements are explicit row statuses.
5. **Versions / Runtime:** Keep infrequent and Play Mode-specific tools accessible without consuming the default workspace. Runtime remains visible outside Play Mode with an honest empty state and an Enter Play Mode action.

## Unity implementation constraints

- Target Unity 2021.3. UI Toolkit can own the window shell, navigation, list virtualization, and responsive layout. Existing custom IMGUI drawers can be hosted during migration where appropriate.
- Use `Selection.selectionChanged`, `EditorApplication.hierarchyChanged`, and project events already present in the plugin to keep lists and details current. Avoid replacing Unity's Inspector or intercepting normal selection.
- Test dock widths around 360, 600, and 900 editor pixels; collapse the detail pane below roughly 680 pixels. Keep navigation keyboard reachable and preserve focus through refreshes.
- Derive surfaces and text from Unity editor light/dark theme. Use Grimoire purple for the selected destination and primary action, teal only for success/linked state. Pair every color with a label or icon.
- Fetch on entry or explicit refresh as today. Do not imply server data is live; show loading, stale, failed, and offline states with retry.
- Keep creation and reset confirmations explicit because they write draft objects or discard local changes. Preserve Unity Undo and prefab override behavior for editable scene data.

## Proposed order after design approval

1. Shared window shell and Scene browser.
2. Selected Object view and Object Link inspector alignment.
3. Review and sync flow.
4. Dialogs, Tasks, Versions, Runtime, and setup states.

## Reference

- [Odin group attributes](https://odininspector.com/tutorials/using-attributes/group-attributes)
- [Odin searchable lists](https://odininspector.com/attributes/searchable-attribute)
- [Odin editor windows](https://odininspector.com/tutorials/custom-editor-windows/odin-editor-windows)
- [Unity 2021.3 UI Toolkit editor window guide](https://docs.unity3d.com/2021.3/Documentation/Manual/UIE-HowTo-CreateEditorWindow.html)

