using Grimoire.PluginV2.Editor;
using NUnit.Framework;

namespace Grimoire.PluginV2.Tests
{
    public class GrimoireContentLibraryTests
    {
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
