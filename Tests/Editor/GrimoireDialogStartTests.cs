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

        [UnityTest]
        public System.Collections.IEnumerator ConnectedFunctionRunsSharedEffectsOnlyAfterItsDialogCompletes()
        {
            yield return new EnterPlayMode();
            _asset = ScriptableObject.CreateInstance<GrimoireDialogAsset>();
            _asset.SetImportedData("intro", "intro", "Intro", "", "game", "", "none", "hello",
                new List<GrimoireDialogVariable>(), new List<GrimoireDialogNode>
                {
                    new GrimoireDialogNode { identifier = "hello", text = "Hello" }
                });
            _gameObject = new GameObject("Scene dialog player");
            var player = _gameObject.AddComponent<GrimoireDialogPlayer>();

            var link = _gameObject.AddComponent<GrimoireObjectLink>();
            link.DataSource = GrimoireObjectLinkSource.ConnectedSnapshot;
            var snapshot = new GrimoireObjectSnapshot { ObjectId = "npc", ObjectKey = "shopkeeper" };
            snapshot.Fields.Add(new GrimoireCachedField { FieldId = "greeting", FieldType = "dialog", Value = "intro" });
            snapshot.Fields.Add(new GrimoireCachedField { FieldId = "spoken", Kind = "boolean", Value = "false" });
            snapshot.Logic = JsonUtility.FromJson<GrimoireObjectLogic>(@"{
                ""version"":1, ""functions"":[{""name"":""OnInteract"",""steps"":[
                  {""depth"":0, ""conditions"":[{""terms"":[{
                    ""left"":{""kind"":""field"",""value"":""spoken""}, ""op"":""equal"",
                    ""right"":{""kind"":""boolean"",""value"":""false""}}]}],
                    ""dialogs"":[{""field"":""greeting"", ""start"":""hello""}]},
                  {""depth"":1, ""sets"":[{""left"":{""kind"":""field"",""value"":""spoken""},
                    ""op"":""equal"",""right"":{""kind"":""boolean"",""value"":""true""}}]}]}]}");
            var binding = GrimoireLogicDependencies.LoadAsync("game", snapshot, null, null,
                new Dictionary<string, GrimoireDialogAsset> { ["intro"] = _asset });
            while (!binding.IsCompleted) yield return null;
            Assert.IsTrue(binding.Result.Success, binding.Result.Error);
            link.ReplaceSnapshot(snapshot);
            Assert.IsTrue(link.TriggerFunction("OnInteract"));
            Assert.AreSame(_asset, player.CurrentDialog);
            Assert.IsTrue(link.TryGetBool("spoken", out var before));
            Assert.IsFalse(before);

            player.Stop();
            Assert.IsTrue(link.TryGetBool("spoken", out var stopped));
            Assert.IsFalse(stopped);
            Assert.IsTrue(link.TriggerFunction("OnInteract"));
            player.Next();
            Assert.IsFalse(player.IsPlaying);
            Assert.IsTrue(link.TryGetBool("spoken", out var after));
            Assert.IsTrue(after);
            link.OnInteract();
            Assert.IsFalse(player.IsPlaying, "The guard must skip the dialog and its linked steps.");
            yield return new ExitPlayMode();
        }

        [UnityTest]
        public System.Collections.IEnumerator BedCountsCompletedInteractionsWhenItsConditionPasses()
        {
            yield return new EnterPlayMode();
            _asset = ScriptableObject.CreateInstance<GrimoireDialogAsset>();
            _asset.SetImportedData("rest", "rest", "Rest", "", "game", "", "none", "hello",
                new List<GrimoireDialogVariable>(), new List<GrimoireDialogNode>
                {
                    new GrimoireDialogNode { identifier = "hello", text = "Rest here?" }
                });
            _gameObject = new GameObject("Bed");
            var player = _gameObject.AddComponent<GrimoireDialogPlayer>();
            var link = _gameObject.AddComponent<GrimoireObjectLink>();
            link.DataSource = GrimoireObjectLinkSource.ConnectedSnapshot;
            var snapshot = new GrimoireObjectSnapshot { ObjectId = "bed" };
            snapshot.Fields.Add(new GrimoireCachedField { FieldId = "dialogue", FieldType = "dialog", Value = "rest" });
            snapshot.Fields.Add(new GrimoireCachedField { FieldId = "interacted", Kind = "boolean", Value = "false" });
            snapshot.Fields.Add(new GrimoireCachedField { FieldId = "count", Kind = "number", Value = "3" });
            snapshot.LogicDialogs.Add(_asset);
            snapshot.Logic = JsonUtility.FromJson<GrimoireObjectLogic>(@"{
                ""version"":1, ""functions"":[{""name"":""OnInteract"",""steps"":[
                  {""depth"":0,""dialogs"":[{""field"":""dialogue""}]},
                  {""depth"":1,""conditions"":[{""terms"":[{
                    ""left"":{""kind"":""field"",""value"":""interacted""},""op"":""equal"",
                    ""right"":{""kind"":""boolean"",""value"":""true""}}]}],
                   ""sets"":[{""left"":{""kind"":""field"",""value"":""count""},""op"":""add"",
                    ""right"":{""kind"":""number"",""value"":""1""}}]}]}]}");
            link.ReplaceSnapshot(snapshot);

            link.OnInteract();
            Assert.AreSame(_asset, player.CurrentDialog);
            player.Next();
            Assert.IsTrue(link.TryGetNumber("count", out var count));
            Assert.AreEqual(3, count, "A false condition skips the increment.");

            link.OnInteract();
            link.SetFieldValue("interacted", true);
            Assert.IsTrue(link.TryGetNumber("count", out count));
            Assert.AreEqual(3, count, "The count stays unchanged during playback.");
            player.Next();
            Assert.IsTrue(link.TryGetNumber("count", out count));
            Assert.AreEqual(4, count, "Read the condition after the dialog completes.");

            link.OnInteract();
            player.Stop();
            Assert.IsTrue(link.TryGetNumber("count", out count));
            Assert.AreEqual(4, count, "Cancelling skips the increment.");
            link.OnInteract();
            player.Next();
            Assert.IsTrue(link.TryGetNumber("count", out count));
            Assert.AreEqual(5, count, "Each completed interaction adds to the current count.");
            yield return new ExitPlayMode();
        }

        [UnityTest]
        public System.Collections.IEnumerator UnityEventInvokesTheNamedConnectedFunction()
        {
            yield return new EnterPlayMode();
            _gameObject = new GameObject("Merchant");
            var link = _gameObject.AddComponent<GrimoireObjectLink>();
            link.DataSource = GrimoireObjectLinkSource.ConnectedSnapshot;
            var snapshot = new GrimoireObjectSnapshot { ObjectId = "merchant" };
            snapshot.Fields.Add(new GrimoireCachedField { FieldId = "state", Kind = "text", Value = "idle" });
            snapshot.Logic = JsonUtility.FromJson<GrimoireObjectLogic>(@"{
                ""version"":1, ""functions"":[
                {""name"":""Inspect"",""steps"":[{""sets"":[{
                    ""left"":{""kind"":""field"",""value"":""state""},""op"":""equal"",
                    ""right"":{""kind"":""string"",""value"":""inspected""}}]}]},
                {""name"":""Offer trade — Élodie"",""steps"":[{""sets"":[{
                    ""left"":{""kind"":""field"",""value"":""state""},""op"":""equal"",
                    ""right"":{""kind"":""string"",""value"":""trading""}}]}]}]}");
            link.ReplaceSnapshot(snapshot);

            var trade = new UnityEngine.Events.UnityEvent();
            UnityEditor.Events.UnityEventTools.AddStringPersistentListener(trade, link.InvokeFunction, "Offer trade — Élodie");
            trade.Invoke();
            Assert.IsTrue(link.TryGetString("state", out var afterTrade));
            Assert.AreEqual("trading", afterTrade);
            Assert.IsTrue(link.TriggerFunction("Inspect"));
            Assert.IsTrue(link.TryGetString("state", out var afterInspect));
            Assert.AreEqual("inspected", afterInspect);
            yield return new ExitPlayMode();
        }

        [Test]
        public void CopiedStartCodeEscapesNodeIdentifier()
        {
            Assert.AreEqual(
                "GrimoireDialogPlayer.Main.StartDialog(dialogAsset, \"part\\\"two\\\\end\");",
                GrimoireDialogStartCode.ForNode("part\"two\\end"));
        }

        [Test]
        public void ConnectedFunctionUpdatesAnotherObjectAndRemovesOnlyTheChosenReference()
        {
            _gameObject = new GameObject("Item");
            var item = _gameObject.AddComponent<GrimoireObjectLink>();
            item.DataSource = GrimoireObjectLinkSource.ConnectedSnapshot;
            var itemData = new GrimoireObjectSnapshot { ObjectId = "tonic-id", ObjectKey = "items/tonic" };
            itemData.Logic = JsonUtility.FromJson<GrimoireObjectLogic>(@"{
              ""version"":2,""functions"":[{""name"":""Apply"",""steps"":[{
                ""conditions"":[{""terms"":[{""left"":{""kind"":""objectField"",""objectId"":""actor-id"",""value"":""pace""},
                  ""op"":""less_than"",""right"":{""kind"":""number"",""value"":""7""}}]}],
                ""sets"":[{""left"":{""kind"":""objectField"",""objectId"":""actor-id"",""value"":""pace""},
                  ""op"":""equal"",""right"":{""kind"":""number"",""value"":""7""}},
                {""left"":{""kind"":""objectField"",""objectId"":""actor-id"",""value"":""pouch""},
                  ""op"":""subtract"",""right"":{""kind"":""string"",""value"":""tonic-id""}}]}]}]}");
            item.ReplaceSnapshot(itemData);
            var actor = new GrimoireObjectSnapshot { ObjectId = "actor-id" };
            actor.Fields.Add(new GrimoireCachedField { FieldId = "pace", Kind = "number", Value = "2" });
            GrimoireObjectCache.Register(actor);
            try
            {
                LogAssert.Expect(LogType.Warning, "[Grimoire Logic] .Apply: Missing field 'pouch' on 'actor-id'.");
                item.TriggerFunction("Apply");
                Assert.AreEqual("2", actor.Fields[0].Value);
                var pouch = new GrimoireCachedField { FieldId = "pouch", FieldType = "glossaryItem",
                    Multiple = true, Value = "key,items/tonic,map" };
                actor.Fields.Add(pouch);
                Assert.IsTrue(item.TriggerFunction("Apply"));
                Assert.AreEqual("7", actor.Fields[0].Value);
                CollectionAssert.AreEqual(new[] { "key", "map" }, pouch.ReferencedIds());
                pouch.Value = "tonic-id";
                pouch.References.Clear();
                pouch.Values.Clear();
                Assert.IsTrue(item.TriggerFunction("Apply"));
                CollectionAssert.AreEqual(new[] { "tonic-id" }, pouch.ReferencedIds());
            }
            finally { GrimoireObjectCache.Clear(); }
        }
    }
}
