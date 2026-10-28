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
    }
}
