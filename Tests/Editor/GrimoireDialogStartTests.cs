using System.Collections.Generic;
using Grimoire.PluginV2.Editor;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace Grimoire.PluginV2.Tests
{
    public class GrimoireDialogStartTests
    {
        private GameObject _gameObject;
        private GrimoireDialogAsset _asset;

        [TearDown]
        public void TearDown()
        {
            if (_gameObject != null) Object.DestroyImmediate(_gameObject);
            if (_asset != null) Object.DestroyImmediate(_asset);
        }

        [Test]
        public void PlayerStartsAtRequestedNodeAndKeepsCurrentDialogOnInvalidNode()
        {
            _asset = ScriptableObject.CreateInstance<GrimoireDialogAsset>();
            _asset.SetImportedData("dialog-id", "intro", "Intro", "", "game-id", "", "none", "first",
                new List<GrimoireDialogVariable>(), new List<GrimoireDialogNode>
                {
                    new GrimoireDialogNode { identifier = "first", text = "First" },
                    new GrimoireDialogNode { identifier = "second", text = "Second" },
                });
            _gameObject = new GameObject("Dialog test player");
            var player = _gameObject.AddComponent<GrimoireDialogPlayer>();

            player.StartDialog(_asset, "second");
            Assert.AreEqual("second", player.CurrentLine.NodeId);

            LogAssert.Expect(LogType.Warning, "[Grimoire Dialog] Node 'missing' was not found in 'Intro'.");
            player.StartDialog(_asset, "missing");
            Assert.AreEqual("second", player.CurrentLine.NodeId);

            player.StartDialog(_asset);
            Assert.AreEqual("first", player.CurrentLine.NodeId);
        }

        [Test]
        public void CopiedStartCodeEscapesNodeIdentifier()
        {
            Assert.AreEqual(
                "GrimoireDialogPlayer.Main.StartDialog(dialogAsset, \"part\\\"two\\\\end\");",
                GrimoireDialogStartCode.ForNode("part\"two\\end"));
        }
    }
}
