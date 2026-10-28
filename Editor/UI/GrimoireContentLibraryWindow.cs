using System;
using UnityEditor;
using UnityEngine;

namespace Grimoire.PluginV2.Editor
{
    /// <summary>Picker host; the main library lives inside the Grimoire workspace.</summary>
    public sealed class GrimoireContentLibraryWindow : EditorWindow
    {
        private GrimoireContentLibraryView _view;
        private Action<GrimoireContentLibraryView> _configure;

        public static void Open() => GrimoireConnectWindow.OpenLibrary();

        public static void OpenDialog(string dialogId) => GrimoireConnectWindow.OpenLibrary(dialogId);

        internal static string Folder(string root, string path) => GrimoireContentLibraryView.Folder(root, path);

        public static void OpenObjectPicker(Action<ObjectSummary> onPicked) =>
            OpenPicker("Pick Grimoire Object", view => view.ConfigureObjectPicker(onPicked, null));

        public static void OpenDialogPicker(Action<GrimoireDialogAsset> onPicked) =>
            OpenPicker("Pick Grimoire Dialog", view => view.ConfigureDialogPicker(onPicked, null));

        /// <summary>Pick a game variable id or a dialog-scoped key.</summary>
        public static void OpenVariablePicker(Action<string> onPicked) =>
            OpenPicker("Pick Grimoire Variable", view => view.ConfigureVariablePicker(onPicked, null));

        private static void OpenPicker(string title, Action<GrimoireContentLibraryView> configure)
        {
            var window = CreateInstance<GrimoireContentLibraryWindow>();
            window.titleContent = new GUIContent(title);
            window._configure = configure;
            window.minSize = new Vector2(430, 440);
            window.ShowUtility();
        }

        public void CreateGUI()
        {
            _view?.Deactivate();
            _view = new GrimoireContentLibraryView();
            _configure?.Invoke(_view);
            _view.SetClosePicker(Close);
            _view.Activate();
            _view.Build(rootVisualElement, false);
        }

        private void OnDisable()
        {
            _view?.Deactivate();
            _view = null;
        }
    }
}
