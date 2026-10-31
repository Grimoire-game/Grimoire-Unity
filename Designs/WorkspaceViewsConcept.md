# Scene, Object, Review — unified Grimoire workspace

These are design concepts for the dockable Unity Editor window. The content is illustrative. [Open the interactive preview](./WorkspaceViewsConcept.html) to switch views and dock widths.

## Shared shell

- **Content Library remains first** in the left navigation; Scene, Object, and Review follow it. The same workspace header keeps company, game, connection state, and Refresh visible.
- A selected row uses the same purple highlight as the Content Library. Lists and details use the same dark surfaces, borders, type scale, and value rows.
- The right detail pane moves below the main list in a narrow dock. The essential action remains at the top of each view.

## Scene

- One searchable list contains linked GameObjects and text links. Each row shows the Unity name, Grimoire key, kind, and status.
- Selecting a row also selects or pings the Unity Hierarchy object. The detail pane previews the current source, status, and key values.
- Scene language and link filtering sit beside search. **Create object** is the single primary action.

## Object

- The selected Unity GameObject stays identified in the heading. The Object Link's data source is visible near the top: project default, connected snapshot, or version database.
- Source values are read-only rows; editable game engine data and local field overrides are clearly labeled below them. Changed rows carry a text marker.
- The detail pane shows the selected value and link context. **Review changes** leads to the shared Review view.

## Review

- One row per pending change shows the Grimoire value beside the Unity value. Changes are grouped by linked item, with individual selection checkboxes.
- **Queue selected for review** is the primary action. A short explanation makes clear that queuing creates a review commit and does not publish it.
- Reset remains a separate secondary action with an object-specific confirmation in the implementation.

## Existing behavior to preserve when implemented

- Keep Unity selection, Undo, prefab overrides, source switching, per-object sync, and task/dialog workflows intact.
- Fetch or refresh data when entering a view, with visible loading, stale, failed, and offline states.
- Use the package's Unity 2021.3 compatible editor UI and create `.meta` files for every new asset.
