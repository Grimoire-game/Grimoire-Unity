using Grimoire.PluginV2.Editor;
using NUnit.Framework;
using System.Collections.Generic;
using UnityEngine;

namespace Grimoire.PluginV2.Tests
{
    public class GrimoireContentLibraryTests
    {
        [TestCase(false, null, null, "Missing")]
        [TestCase(true, null, "2026-11-22T17:00:00Z", "Unknown")]
        [TestCase(true, "2026-11-22T17:00:00Z", null, "Unknown")]
        [TestCase(true, "2026-11-22T17:00:00Z", "2026-11-22T18:00:00.000+01:00", "Current")]
        [TestCase(true, "2026-11-22T17:00:00Z", "2026-11-22T17:00:00.000001Z", "Changed")]
        public void UpdateCheckComparesServerRevisions(bool present, string saved, string listed, string expected)
        {
            Assert.AreEqual(expected, GrimoireContentLibraryView.CompareRevision(present, saved, listed).ToString());
        }

        [Test]
        public void ObjectCheckIncludesEveryMatchingLinkAndRecognizesLegacySnapshots()
        {
            var first = new GameObject();
            var second = new GameObject();
            try
            {
                var item = new ObjectSummary { id = "id", code_id = "door", updated_at = "2026-11-22T17:00:00Z" };
                var saved = first.AddComponent<GrimoireObjectLink>();
                saved.ObjectKey = "door";
                saved.CachedObjectId = "other";
                Assert.IsFalse(GrimoireSyncEverything.MatchesObject(saved, item));
                saved.CachedObjectId = item.id;
                saved.ApiSnapshot.ObjectId = item.id;
                saved.ApiSnapshot.SyncedSourceRevision = item.updated_at;
                var legacy = second.AddComponent<GrimoireObjectLink>();
                legacy.ObjectKey = "Door";
                legacy.ApiSnapshot.ObjectId = item.id;
                Assert.IsTrue(GrimoireSyncEverything.MatchesObject(legacy, item));
                Assert.AreEqual(GrimoireContentLibraryView.Revision.Unknown,
                    GrimoireContentLibraryView.ObjectRevision(item, new[] { legacy, saved }));
                legacy.ApiSnapshot.SyncedSourceRevision = item.updated_at;
                Assert.AreEqual(GrimoireContentLibraryView.Revision.Current,
                    GrimoireContentLibraryView.ObjectRevision(item, new[] { saved, legacy }));
            }
            finally
            {
                Object.DestroyImmediate(first);
                Object.DestroyImmediate(second);
            }
        }

        [Test]
        public void SyncOneVariableReplacesOnlyItsElementsAndPreservesTheFullSyncTimestamp()
        {
            var library = ScriptableObject.CreateInstance<GrimoireVariableLibrary>();
            GrimoireVariable Variable(string id, string element) => new GrimoireVariable(id, id, "type", "", "", "",
                new List<GrimoireVariableElement> { new GrimoireVariableElement(element, element, "", null, "") });
            try
            {
                library.SetSyncedData("game", new List<GrimoireVariable> { Variable("a", "old"), Variable("b", "keep") });
                var lastFullSync = library.SyncedAt;
                Assert.IsTrue(library.TryGetElement("old", out _));
                library.SetSyncedVariable("game", Variable("a", "new"));
                library.SetSyncedVariable("game", Variable("c", "added"));
                Assert.IsFalse(library.TryGetElement("old", out _));
                Assert.IsTrue(library.TryGetElement("keep", out _));
                Assert.IsTrue(library.TryGetElement("new", out _));
                Assert.IsTrue(library.TryGetElement("added", out _));
                Assert.AreEqual(lastFullSync, library.SyncedAt);
                Assert.Throws<System.InvalidOperationException>(() => library.SetSyncedVariable("other", Variable("b", "bad")));
            }
            finally { Object.DestroyImmediate(library); }
        }

        [TestCase("OnInteract", "\"OnInteract\"")]
        [TestCase("Ask \"Élodie\"\\trade\nagain", "\"Ask \\\"Élodie\\\"\\\\trade\\nagain\"")]
        public void FunctionCallCodeCopiesTheCallWithTheExactFunctionName(string name, string literal)
        {
            Assert.AreEqual($"GetComponent<Grimoire.PluginV2.GrimoireObjectLink>().TriggerFunction({literal});",
                GrimoireObjectLinkEditor.FunctionCallCode(name));
        }

        [TestCase("Objects", "Characters/Heroes", "Objects/Characters/Heroes")]
        [TestCase("Dialogs", "Dialogs\\Quest\\Intro", "Dialogs/Quest/Intro")]
        [TestCase("Variables", "", "Variables/Unfiled")]
        [TestCase("Objects", "../Characters//Heroes", "Objects/Characters/Heroes")]
        public void Folder_NormalizesRemotePaths(string root, string path, string expected)
        {
            Assert.AreEqual(expected, GrimoireContentLibraryWindow.Folder(root, path));
        }

        [Test]
        public void FolderNames_ResolveNestedIdsByContentType()
        {
            var paths = GrimoireContentLibraryView.CreateFolderPathMap(new[]
            {
                new GameFolderSection
                {
                    type = "library",
                    folders = new[]
                    {
                        new GameFolderNode
                        {
                            id = "1776458565530", name = "Characters",
                            subfolder = new[] { new GameFolderNode { id = "1776462141860", name = "Heroes" } }
                        }
                    }
                },
                new GameFolderSection
                {
                    type = "dialogs",
                    folders = new[] { new GameFolderNode { id = "1776458565530", name = "Main story" } }
                }
            });

            Assert.AreEqual("Characters/Heroes", paths["Objects:1776462141860"]);
            Assert.AreEqual("Main story", paths["Dialogs:1776458565530"]);
        }
    }
}
