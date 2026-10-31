# Dialogs, Tasks, Versions, Runtime — workspace concepts

[Open the interactive preview](./WorkflowViewsConcept.html) to switch views and dock widths. These are illustrative designs, not implemented Unity panels.

## Shared layout

All four views use the same dockable Grimoire shell as [Scene, Object, and Review](./WorkspaceViewsConcept.html): Content Library first in the sidebar, a persistent workspace header, searchable rows, and a detail pane. In a narrow dock the details follow the list vertically.

## Dialogs

- Keep the scene player prerequisite visible as a short state at the top. If missing, this becomes **Add dialog player to scene**; if ready, it links to the Unity Hierarchy object.
- The list shows folder, dialog key, import state, and a concise structure preview. Selecting one reveals its opening line, variables, and the relevant **Import** or **Update** action.
- Use Content Library for general content browsing; this view focuses on the dialog workflow.

## Tasks

- Search and filter by assignee and workflow status, with assigned work easy to spot.
- The selected task shows its linked object, text link, or dialog, description, assignee, and status. Update the status in place or open the linked content.

## Versions

- Show the currently imported export first, then available versions with date, size, and template. Importing a version retains the existing confirmation.
- The selected version explains what it contains and offers **Download & Extract**. **Browse database** opens the imported database browser.
- Object Link data source choice remains in Settings and the Object view.

## Runtime: live change overview

Runtime has two modes in the same view. **Changes** is a newest-first feed of **individual observed changes** in the current Play Mode session. Each entry records time, linked item, field or state path, previous live value, new live value, and the runtime source. Selecting it shows its current live value and a route back to the linked Unity object. **Linked now** is the complete current-value inventory, including links that have not changed.

Capture should cover every observable Grimoire connection:

| Category | Examples |
| --- | --- |
| Objects | Object Link fields, local overrides, generated ObjectRuntime fields |
| Variables | Game variables and dialog-scoped variables |
| Dialogs | Active dialog, node, answer, and dialog variable changes |
| Logic | Active flow and state transitions |
| Text links | Displayed localization and language changes |
| Scene links | Linked object transforms and relevant component state |

The design calls for capture to begin on **Play Mode entry**, even if Runtime is not the visible view, so switching to Runtime does not lose earlier changes. Use event hooks where available and a bounded snapshot comparison for generated runtime data. Deduplicate the same change observed through multiple paths. Keep history in memory for the session; **Pause capture** stops recording, **Clear history** removes recorded entries, and Refresh updates the current snapshot. Before/after refers to consecutive *live observations*, not authored API or version values. Show an explicit unsupported or unavailable state for a linked source that cannot be observed.

The Unity window now implements the four workflows with selectable list and detail panes. Runtime capture starts at Play Mode entry, including when the Grimoire window is closed. Object Link setters, synced variable setters, text changes, dialog lines, and function triggers use direct events. Exported runtime state and linked scene values are compared every 0.75 seconds, so transitions shorter than that interval can be missed. The feed keeps the newest 500 changes in memory for the current session. The prior Runtime inspector remains available through **Inspector**.
