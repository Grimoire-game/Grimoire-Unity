# Unified Content Library concept

The [clickable concept](./ContentLibraryConcept.html) shows a single dockable Unity Editor window for finding **objects, dialogs, and variables**. Its sample names, values, and access paths are illustrative. [Wide view](./ContentLibraryWide.png), [dialog view](./ContentLibraryDialogs.png), [variable view](./ContentLibraryVariables.png), [dialog picker](./ContentLibraryDialogPicker.png), and [narrow variable picker](./ContentLibraryVariablePicker.png) are static previews.

## The main interaction

1. Open `Window > Grimoire > Content Library`, or launch the same window from a field that needs a Grimoire item.
2. Choose a folder or type a name, key, or value in the global search. Search returns matching content from all three types; clearing it returns to the selected folder.
3. Click one row. The right pane immediately shows its key, source, status, and useful values. Copy a key, a field value, or an access path from there.
4. When launched as a picker, show only compatible items. The primary action becomes **Use this object**, **Use this dialog**, or **Use this variable**. Double-click or Enter can choose the selected result. The context selector in the mockup exists so reviewers can preview all three picker cases; a real Unity field would supply its context automatically.

## Folder and value rules

| Content | Folder source | Values shown | Main action |
| --- | --- | --- | --- |
| Objects | `ObjectSummary.folder` from Grimoire; place empty folders in **Unfiled** | Fields from the object view or cached object snapshot, labeled as stored/cached | Pick object, copy key or field value |
| Dialogs | `DialogSummary.folder` from Grimoire; **Unfiled** when absent | Sections and imported dialog variable initial values | Pick imported dialog asset; offer import when missing |
| Game variables | Categories parsed from imported `Variables.cs`; present categories as virtual folders | Exported default value and type; live value only in Play Mode | Copy generated access path or pick for a compatible variable reference |
| Dialog variables | Owning imported dialog asset, grouped under **Variables / Dialog** | Initial value and type; live value only in Play Mode | Copy name/path or pick for a compatible variable reference |

Object fields remain visible inside their object instead of being duplicated as standalone game variables. A search for a field name or value still finds its object. This avoids two entries that appear to refer to the same value but have different lifecycles.

## Behaviors that keep it easy

- Remember the last folder and selected item per Unity project. Preserve keyboard focus when data refreshes.
- Open to the content type expected by the calling field; ordinary browsing opens **All content**.
- Put the value and copy action in the first visible part of the details pane. Keep status and source beside them so a cached or exported default cannot be mistaken for a live value.
- Show loading and error states per source. Objects and dialogs may load even if exports are missing; the Variables folder then explains how to import an export.
- In a narrow dock, use a folder dropdown above the list and stack details below it. Both list and details remain scrollable.
- Use labels with color: Object, Dialog, Variable; Draft, Imported, Update available. Do not rely on color alone.
- If a dialog has not been imported, **Import dialog** comes before it can be picked as a Unity asset.

## Loading a large game

The current object and dialog API calls use `limit` and `offset`. Build the folder index incrementally from those pages, fetch details only for the selected row, and debounce global search. Merge remote object/dialog search results with local imported variable results. Until every page is loaded, show **Loading more** instead of an exact folder total. A failed source gets its own retry state while other sources remain usable.

## Fit with the current plugin

This concept brings together the existing `GrimoireObjectLinkEditor` object picker, `GrimoireDialogsPanel`, `GrimoireVariablesRenderer`, and exported `GrimoireDatabaseBrowser`. The existing object and dialog API models already include folder metadata. Exported game variables and dialog asset variables are currently different sources; the design keeps their source visible. Picking a variable into an arbitrary Unity field would be a new integration, so the picker would only appear where a compatible reference field is implemented.

The proposed window can use Unity 2021.3 UI Toolkit for the folder tree, virtualized result list, search, and detail pane. It does not require Odin Inspector to be installed. The earlier [workspace-wide concept](./GrimoireEditorConcept.html) can remain the shell for tasks, sync review, versions, and runtime tools; the Content Library becomes its primary content destination.
