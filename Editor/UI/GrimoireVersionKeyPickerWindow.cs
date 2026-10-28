using System;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace Grimoire.PluginV2.Editor
{
    internal sealed class GrimoireVersionKeyPickerWindow : EditorWindow
    {
        private Action<string> _onPicked;
        private string _search = "";
        private Vector2 _scroll;

        public static void Open(string current, Action<string> onPicked)
        {
            var window = CreateInstance<GrimoireVersionKeyPickerWindow>();
            window.titleContent = new GUIContent("Pick version object");
            window._search = current ?? "";
            window._onPicked = onPicked;
            window.minSize = new Vector2(340, 330);
            window.ShowUtility();
            window.Focus();
        }

        private void OnGUI()
        {
            EditorGUILayout.LabelField("Database.Library objects", EditorStyles.boldLabel);
            GUI.SetNextControlName("VersionSearch");
            EditorGUILayout.BeginHorizontal();
            _search = EditorGUILayout.TextField("Search", _search);
            if (GUILayout.Button("All", GUILayout.Width(38))) _search = "";
            EditorGUILayout.EndHorizontal();
            var keys = GrimoireObjectLink.AvailableVersionKeys;
            if (keys.Count == 0)
            {
                EditorGUILayout.HelpBox("Import a Runtime Content Database version to pick an object.", MessageType.Info);
                return;
            }
            var matches = keys.Where(key => string.IsNullOrWhiteSpace(_search) ||
                key.IndexOf(_search.Trim(), StringComparison.OrdinalIgnoreCase) >= 0).ToArray();
            EditorGUILayout.LabelField($"{matches.Length} matches", EditorStyles.miniLabel);
            _scroll = EditorGUILayout.BeginScrollView(_scroll);
            foreach (var key in matches.Take(200))
            {
                if (GUILayout.Button(key, EditorStyles.miniButton))
                {
                    _onPicked?.Invoke(key);
                    Close();
                    break;
                }
            }
            EditorGUILayout.EndScrollView();
            if (matches.Length > 200)
                EditorGUILayout.LabelField("Showing first 200. Search to narrow the list.", EditorStyles.miniLabel);
        }
    }
}
