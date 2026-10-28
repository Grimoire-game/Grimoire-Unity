using UnityEditor;
using UnityEngine;

namespace Grimoire.PluginV2.Editor
{
    /// <summary>
    /// Compatibility entry point that opens the Runtime view in Grimoire.
    /// </summary>
    public static class GrimoireRuntimeInspector
    {
        public static void ShowWindow()
        {
            GrimoireConnectWindow.OpenRuntime();
        }
    }
}
