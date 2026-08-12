using UnityEditor;
using UnityEngine;

namespace Grimoire.PluginV2.Editor
{
    /// <summary>
    /// Menu entry that opens the Runtime tab inside Grimoire Connect.
    /// Kept so existing bookmarks to <c>Window/Grimoire/Runtime Inspector</c> still work.
    /// </summary>
    public static class GrimoireRuntimeInspector
    {
        [MenuItem("Window/Grimoire/Runtime Inspector")]
        public static void ShowWindow()
        {
            GrimoireConnectWindow.OpenRuntime();
        }
    }
}
