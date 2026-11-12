using NUnit.Framework;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.UIElements;

namespace Grimoire.PluginV2.Editor.Tests
{
    public sealed class GrimoireSceneWorkspaceTests
    {
        [Test]
        public void UnlinkedChildAppearsAndSelectionStaysInSyncWithHierarchy()
        {
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            var root = new GameObject("Grimoire Scene Test Root");
            var child = new GameObject("Unlinked Child");
            child.transform.SetParent(root.transform);
            var view = new GrimoireSceneWorkspaceView(_ => null, _ => null, _ => { },
                () => { }, () => { });
            try
            {
                var host = new VisualElement();
                view.Build(host);
                view.Activate();
                var list = host.Q<ListView>();

                Selection.activeGameObject = child;
                view.RefreshSelection();
                var childIndex = list.selectedIndex;
                Assert.That(childIndex, Is.GreaterThanOrEqualTo(0),
                    "The unlinked child should be in the Scene list.");

                Selection.activeGameObject = root;
                view.RefreshSelection();
                Assert.That(list.selectedIndex, Is.Not.EqualTo(childIndex));

                list.SetSelection(childIndex);
                Assert.That(Selection.activeGameObject, Is.EqualTo(child),
                    "Choosing a Scene row should select that GameObject in Unity.");
            }
            finally
            {
                view.Deactivate();
                Selection.activeGameObject = null;
                Object.DestroyImmediate(root);
            }
        }
    }
}
