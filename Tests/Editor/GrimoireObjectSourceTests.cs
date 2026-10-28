using System;
using System.Collections.Generic;
using Grimoire.PluginV2;
using NUnit.Framework;
using UnityEngine;

namespace Grimoire.PluginV2.Tests
{
    public class GrimoireObjectSourceTests
    {
        private GameObject _object;
        private GrimoireObjectLink _link;
        private GrimoireObjectSourceSettings _settings;

        [SetUp]
        public void SetUp()
        {
            FakeRuntime.Values.Clear();
            FakeLogic.LastKey = null;
            FakeLogic.LastFunction = null;
            GrimoireVersionObjectAdapter.SetTestSources(
                new Dictionary<string, object>
                {
                    ["characters/aragorn"] = new FakeItem
                    {
                        Health = 42, Enabled = true, friend_id = "characters/legolas",
                        Mood = new FakeVariableElement { Id = "mood-happy", Name = "Happy" },
                        Tags = new[] { "hero", "ranger" },
                        Title = new TranslationKey(),
                    },
                    ["characters/legolas"] = new FakeItem { Health = 20, Enabled = true },
                },
                typeof(FakeRuntime), typeof(FakeLogic));
            _settings = ScriptableObject.CreateInstance<GrimoireObjectSourceSettings>();
            GrimoireObjectSources.Settings = _settings;
            _object = new GameObject("Grimoire source test");
            _link = _object.AddComponent<GrimoireObjectLink>();
            _link.ObjectKey = "characters/aragorn";
            var snapshot = new GrimoireObjectSnapshot { ObjectId = "api-1", ObjectKey = _link.ObjectKey, Name = "Aragorn" };
            snapshot.Fields.Add(new GrimoireCachedField
            {
                FieldId = "api-health-id", Label = "Health", Kind = "number", Value = "10",
            });
            _link.ReplaceSnapshot(snapshot);
        }

        [TearDown]
        public void TearDown()
        {
            UnityEngine.Object.DestroyImmediate(_object);
            UnityEngine.Object.DestroyImmediate(_settings);
            GrimoireObjectSources.Settings = null;
            GrimoireVersionObjectAdapter.ResetForTests();
            GrimoireObjectCache.Clear();
        }

        [Test]
        public void ProjectSourceSwitchesReadsWithoutChangingGameCall()
        {
            Assert.IsTrue(_link.TryGetNumber("Health", out var connected));
            Assert.AreEqual(10, connected);

            _settings.DefaultSource = GrimoireObjectDataSource.VersionDatabase;
            Assert.IsTrue(_link.TryGetNumber("Health", out var version));
            Assert.AreEqual(42, version);
            Assert.IsTrue(_link.TryGetNumber("api-health-id", out var sameFieldByApiId));
            Assert.AreEqual(42, sameFieldByApiId);
            Assert.AreEqual("characters/aragorn", _link.Snapshot.ObjectKey);

            _link.DataSource = GrimoireObjectLinkSource.ConnectedSnapshot;
            Assert.IsTrue(_link.TryGetNumber("Health", out var overridden));
            Assert.AreEqual(10, overridden);
        }

        [Test]
        public void VersionReadsReferencesAndWritesThroughSameLink()
        {
            _settings.DefaultSource = GrimoireObjectDataSource.VersionDatabase;
            var references = _link.GetReferences("friend");
            Assert.AreEqual(1, references.Count);
            Assert.AreEqual("characters/legolas", references[0].CodeId);
            Assert.IsTrue(_link.TryGetReferencedObject("friend", 0, out var friend));
            Assert.IsTrue(friend.TryGetNumber("Health", out var health));
            Assert.AreEqual(20, health);

            Assert.IsTrue(_link.SetFieldValue("Health", 47d));
            Assert.IsTrue(_link.TryGetNumber("Health", out var changed));
            Assert.AreEqual(47, changed);
            Assert.AreEqual(47, FakeRuntime.Values["characters/aragorn:Health"]);
            FakeRuntime.SetNumber("characters/aragorn", "Health", 52);
            Assert.IsTrue(_link.TryGetNumber("Health", out var externalChange));
            Assert.AreEqual(52, externalChange);
        }

        [Test]
        public void LocalLinkDeviationOverridesVersionValue()
        {
            _settings.DefaultSource = GrimoireObjectDataSource.VersionDatabase;
            _link.ReplaceLinkedFields(new List<GrimoireLinkedField>
            {
                new GrimoireLinkedField
                {
                    FieldId = "api-health-id", Label = "Health", GrimoireValue = "10", LocalValue = "15",
                },
            });
            Assert.IsTrue(_link.TryGetNumber("Health", out var value));
            Assert.AreEqual(15, value);
        }

        [Test]
        public void VersionVariableElementUsesSameLookupMethod()
        {
            _settings.DefaultSource = GrimoireObjectDataSource.VersionDatabase;
            Assert.IsTrue(_link.TryGetVariableElement("Mood", out var element));
            Assert.AreEqual("mood-happy", element.Id);
            Assert.AreEqual("Happy", element.Name);
            Assert.IsTrue(_link.Snapshot.TryGetField("Tags", out var tags));
            CollectionAssert.AreEqual(new[] { "hero", "ranger" }, tags.Values);
        }

        [Test]
        public void VersionDisplayTextUsesRequestedLanguage()
        {
            _settings.DefaultSource = GrimoireObjectDataSource.VersionDatabase;
            Assert.IsTrue(_link.TryGetDisplayString("Title", out var dutch, "nl"));
            Assert.AreEqual("Held", dutch);
            Assert.IsTrue(_link.SetFieldValue("Title", "Local title"));
            Assert.IsTrue(_link.TryGetDisplayString("Title", out var local, "nl"));
            Assert.AreEqual("Local title", local);
        }

        [Test]
        public void ManualAliasKeepsGameCallStableWhenExportRenamesField()
        {
            _settings.DefaultSource = GrimoireObjectDataSource.VersionDatabase;
            _link.VersionFieldAliases.Add(new GrimoireVersionFieldAlias
            {
                ApiField = "hitPoints", VersionField = "Health",
            });
            Assert.IsTrue(_link.TryGetNumber("hitPoints", out var value));
            Assert.AreEqual(42, value);
        }

        [Test]
        public void MissingVersionKeyDoesNotSilentlyUseApiSnapshot()
        {
            _link.DataSource = GrimoireObjectLinkSource.VersionDatabase;
            _link.VersionKey = "missing/key";
            Assert.IsFalse(_link.TryGetNumber("Health", out _));
            Assert.IsFalse(_link.HasSnapshot);
            Assert.IsNotNull(_link.ApiSnapshot);
        }

        [Test]
        public void ExplicitVersionKeyCanDifferFromConnectedObjectKey()
        {
            _link.DataSource = GrimoireObjectLinkSource.VersionDatabase;
            _link.VersionKey = "characters/legolas";
            Assert.IsTrue(_link.TryGetNumber("Health", out var value));
            Assert.AreEqual(20, value);
            Assert.AreEqual("characters/aragorn", _link.ObjectKey);
        }

        [Test]
        public void FunctionCallUsesSelectedVersionAndFiresCommonEvent()
        {
            _settings.DefaultSource = GrimoireObjectDataSource.VersionDatabase;
            string observed = null;
            Action<GrimoireObjectLink, string> handler = (link, name) => observed = name;
            GrimoireObjectLink.FunctionTriggered += handler;
            try
            {
                Assert.IsTrue(_link.TriggerFunction("OnOpen"));
                Assert.AreEqual("characters/aragorn", FakeLogic.LastKey);
                Assert.AreEqual("OnOpen", FakeLogic.LastFunction);
                Assert.AreEqual("OnOpen", observed);
            }
            finally { GrimoireObjectLink.FunctionTriggered -= handler; }
        }

        private sealed class FakeItem
        {
            public double Health;
            public bool Enabled;
            public string friend_id;
            public FakeVariableElement Mood;
            public string[] Tags;
            public TranslationKey Title;
        }

        private sealed class TranslationKey
        {
            public string this[string language] => language == "nl" ? "Held" : "Hero";
            public override string ToString() => "Hero";
        }

        private abstract class VariableElement
        {
            public string Id { get; set; }
            public string Name { get; set; }
        }

        private sealed class FakeVariableElement : VariableElement { }

        private static class FakeRuntime
        {
            public static readonly Dictionary<string, double> Values = new Dictionary<string, double>();
            public static bool TryGetNumber(string key, string field, out double value) =>
                Values.TryGetValue(key + ":" + field, out value);
            public static void SetNumber(string key, string field, double value) =>
                Values[key + ":" + field] = value;
        }

        private static class FakeLogic
        {
            public static string LastKey;
            public static string LastFunction;
            public static void TriggerFunction(string key, string function)
            {
                LastKey = key;
                LastFunction = function;
            }
        }
    }
}
