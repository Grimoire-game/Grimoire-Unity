using System;
using System.Collections.Generic;
using Grimoire.PluginV2.Editor;
using Grimoire.PluginV2.Internal;
using NUnit.Framework;
using UnityEngine;

namespace Grimoire.PluginV2.Tests
{
    public class GrimoireLocalizationTests
    {
        [TearDown]
        public void TearDown()
        {
            GrimoireLocalization.EditorLanguageResolver = null;
            GrimoireLocalization.SetRuntimeLanguage("");
        }

        [Test]
        public void ResolveUsesEditorLanguageAndFallsBackToSource()
        {
            GrimoireLocalization.EditorLanguageResolver = () => "de";
            var rows = new List<GrimoireLocalizedText>
            {
                new GrimoireLocalizedText("de", "Hallo"),
            };

            Assert.AreEqual("Hallo", GrimoireLocalization.Resolve("Hello", rows));
            Assert.AreEqual("Hello", GrimoireLocalization.Resolve("Hello", rows, "fr"));
        }

        [Test]
        public void RuntimeLanguageRaisesChangeAndResolves()
        {
            var changed = 0;
            Action onChanged = () => changed++;
            GrimoireLocalization.LanguageChanged += onChanged;

            try
            {
                GrimoireLocalization.SetRuntimeLanguage("fr");

                Assert.AreEqual("fr", GrimoireLocalization.ActiveLanguage);
                Assert.AreEqual(1, changed);
            }
            finally
            {
                GrimoireLocalization.LanguageChanged -= onChanged;
            }
        }

        [Test]
        public void ObjectDisplayValueDoesNotReplaceRawLogicValue()
        {
            var snapshot = new GrimoireObjectSnapshot();
            var field = new GrimoireCachedField
            {
                FieldId = "title",
                Kind = "text",
                Value = "Source",
            };
            field.SetLocalizedValues("de", "Übersetzt", new List<string>());
            snapshot.Fields.Add(field);

            Assert.IsTrue(snapshot.TryGetString("title", out var raw));
            Assert.IsTrue(snapshot.TryGetDisplayString("title", out var display, "de"));
            Assert.AreEqual("Source", raw);
            Assert.AreEqual("Übersetzt", display);
        }

        [Test]
        public void VariableElementUsesLocalizedDisplayButSourceLookupName()
        {
            var element = new GrimoireVariableElement(
                "mood-happy",
                "Happy",
                "",
                null,
                "happy-string",
                localizedNames: new List<GrimoireLocalizedText>
                {
                    new GrimoireLocalizedText("de", "Glücklich"),
                });

            Assert.AreEqual("Happy", element.Name);
            Assert.AreEqual("Glücklich", element.GetDisplayName("de"));
        }

        [Test]
        public void DialogLocalizationFallsBackToSource()
        {
            var rows = new List<GrimoireDialogTranslation>
            {
                new GrimoireDialogTranslation { languageCode = "de", text = "Weiter" },
            };

            Assert.AreEqual("Weiter", GrimoireDialogLocalization.Text("Continue", rows, "de"));
            Assert.AreEqual("Continue", GrimoireDialogLocalization.Text("Continue", rows, "fr"));
        }

        [Test]
        public void SyncEverythingDeduplicatesUnityObjects()
        {
            var gameObject = new GameObject("translation-test");
            try
            {
                var distinct = GrimoireSyncEverything.Distinct(new[]
                {
                    gameObject,
                    gameObject,
                    (GameObject)null,
                });
                Assert.AreEqual(1, distinct.Count);
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(gameObject);
            }
        }

        [Test]
        public void ObjectRefreshKeepsOnlyActualLocalDeviations()
        {
            var gameObject = new GameObject("object-refresh-test");
            try
            {
                var link = gameObject.AddComponent<GrimoireObjectLink>();
                GrimoireLinkedFieldStore.ApplyFromDocument(
                    link, EditableTextDocument("old"), preserveLocalEdits: false);
                GrimoireLinkedFieldStore.ApplyFromDocument(
                    link, EditableTextDocument("new"), preserveLocalEdits: true);

                Assert.IsTrue(link.TryGetLinkedField("title", out var field));
                Assert.AreEqual("new", field.GrimoireValue);
                Assert.AreEqual("new", field.LocalValue);
                Assert.IsFalse(field.IsDeviated);
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(gameObject);
            }
        }

        [Test]
        public void TextRefreshRetainsLocallyEditedRemoteMissingLocale()
        {
            var gameObject = new GameObject("text-refresh-test");
            try
            {
                var link = gameObject.AddComponent<GrimoireTextLink>();
                link.ReplaceTranslations(new List<GrimoireLinkedTranslation>
                {
                    new GrimoireLinkedTranslation
                    {
                        LanguageCode = "de",
                        GrimoireText = "Server",
                        LocalText = "Local edit",
                    },
                });

                GrimoireTextLinkStore.ApplyFromResource(
                    link,
                    new StringResource
                    {
                        id = "line",
                        source_text = "Source",
                        translations = new StringTranslation[0],
                    },
                    preserveLocalEdits: true);

                Assert.AreEqual(1, link.Translations.Count);
                Assert.AreEqual("Local edit", link.Translations[0].LocalText);
                Assert.IsTrue(link.Translations[0].IsDeviated);
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(gameObject);
            }
        }

        private static ObjectViewDocument EditableTextDocument(string text)
        {
            return new ObjectViewDocument
            {
                sections = new[]
                {
                    new ViewSection
                    {
                        fields = new[]
                        {
                            new ViewField
                            {
                                id = "title",
                                label = "Title",
                                kind = "text",
                                hints = new FieldHints { game_engine_editable = true },
                                values = new[]
                                {
                                    new ViewValue
                                    {
                                        plain = text,
                                        text = new TextPayload { content = text },
                                    },
                                },
                            },
                        },
                    },
                },
            };
        }
    }
}
