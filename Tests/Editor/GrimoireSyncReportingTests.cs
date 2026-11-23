using System;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Grimoire.PluginV2.Editor;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace Grimoire.PluginV2.Tests
{
    public class GrimoireSyncReportingTests
    {
        private const string GameId = "sync-reporting-test-game";

        [SetUp]
        public void SetUp()
        {
            GrimoireDialogImporter.LastImportNotes.Clear();
        }

        [TearDown]
        public void TearDown()
        {
            GrimoireDialogImporter.LastImportNotes.Clear();
            GrimoireObjectKeyResolver.InvalidateCache(GameId);
        }

        [TestCase("resource_not_accessible", 403, "This object is not accessible with your credentials", "selected game")]
        [TestCase("not_found", 404, "Object not found", "deleted or replaced")]
        [TestCase("rate_limited", 429, "Rate limit exceeded", "Rate limit exceeded")]
        [TestCase("internal_error", 500, "Failed to build object view", "Failed to build object view")]
        public async Task FailedObjectLookupReportsItsCauseOnceWithoutClaimingTheCodeIdIsMissing(
            string code, int status, string error, string expectedHint)
        {
            var calls = 0;
            var first = Reference("first-field");
            var second = Reference("second-field");
            LogAssert.Expect(LogType.Warning, new Regex("^\\[Grimoire Dialog\\] Could not load object"));

            await GrimoireDialogImporter.ResolveObjectFieldsAsync(GameId, new[]
            {
                new GrimoireDialogNode { conditionSubjectRef = first, conditionCheckRef = second }
            }, null, null, id =>
            {
                calls++;
                return Task.FromResult(ApiResult<GrimoireLocalizedObjectLoader.LoadedObject>.Fail(error, code, status));
            });

            Assert.AreEqual(1, calls, "Multiple fields on the same object should share one lookup.");
            Assert.AreEqual(1, GrimoireDialogImporter.LastImportNotes.Count);
            var note = GrimoireDialogImporter.LastImportNotes[0];
            StringAssert.Contains(error, note);
            StringAssert.Contains(code, note);
            StringAssert.Contains("HTTP " + status, note);
            StringAssert.Contains(GameId, note);
            StringAssert.Contains(first.objectId, note);
            StringAssert.Contains(expectedHint, note);
            StringAssert.DoesNotContain("has no Code ID", note);
        }

        [Test]
        public async Task FullPullReusesSuccessfulObjectReadsAcrossSeparateDialogImports()
        {
            var shared = new System.Collections.Generic.Dictionary<string, GrimoireLocalizedObjectLoader.LoadedObject>();
            var calls = 0;
            for (var dialog = 0; dialog < 3; dialog++)
            {
                var reference = Reference("field");
                await GrimoireDialogImporter.ResolveObjectFieldsAsync(GameId, new[]
                {
                    new GrimoireDialogNode { conditionSubjectRef = reference }
                }, null, null, id =>
                {
                    calls++;
                    return Task.FromResult(ApiResult<GrimoireLocalizedObjectLoader.LoadedObject>.Ok(
                        new GrimoireLocalizedObjectLoader.LoadedObject
                        {
                            Source = new ObjectViewDocument { @object = new ObjectSummary { id = id, code_id = "bed" } }
                        }));
                }, shared);
                Assert.AreEqual("bed", reference.objectKey);
            }
            Assert.AreEqual(1, calls);
        }

        [Test]
        public async Task FailedLookupKeepsKnownKeyWithoutAddingAMissingCodeIdWarning()
        {
            var reference = Reference("field");
            GrimoireObjectKeyResolver.RememberSummary(GameId, new ObjectSummary
            {
                id = reference.objectId, code_id = "house_bed", name = "bed"
            });
            LogAssert.Expect(LogType.Warning, new Regex("^\\[Grimoire Dialog\\] Could not load object"));

            await GrimoireDialogImporter.ResolveObjectFieldsAsync(GameId, new[]
            {
                new GrimoireDialogNode { conditionSubjectRef = reference }
            }, null, null, id => Task.FromResult(
                ApiResult<GrimoireLocalizedObjectLoader.LoadedObject>.Fail("Locale could not be loaded", "internal_error", 500)));

            Assert.AreEqual("house_bed", reference.objectKey);
            Assert.AreEqual(1, GrimoireDialogImporter.LastImportNotes.Count);
            StringAssert.Contains("Locale could not be loaded", GrimoireDialogImporter.LastImportNotes[0]);
        }

        [TestCase(null, 1)]
        [TestCase("house_bed", 0)]
        public async Task SuccessfulLookupWarnsOnlyWhenTheObjectActuallyHasNoCodeId(string codeId, int notes)
        {
            var reference = Reference("field");
            var other = Reference("other-field");
            await GrimoireDialogImporter.ResolveObjectFieldsAsync(GameId, new[]
            {
                new GrimoireDialogNode { conditionSubjectRef = reference, conditionCheckRef = other }
            }, null, null, id => Task.FromResult(ApiResult<GrimoireLocalizedObjectLoader.LoadedObject>.Ok(
                new GrimoireLocalizedObjectLoader.LoadedObject
                {
                    Source = new ObjectViewDocument
                    {
                        @object = new ObjectSummary { id = id, name = "bed", code_id = codeId }
                    }
                })));

            Assert.AreEqual(codeId ?? "", reference.objectKey);
            Assert.AreEqual(notes, GrimoireDialogImporter.LastImportNotes.Count);
            if (notes > 0) StringAssert.Contains("'bed' has no Code ID", GrimoireDialogImporter.LastImportNotes[0]);
        }

        [Test]
        public void SuccessfulDialogWithNotesIsImportedAndCountedAsWarnings()
        {
            var summary = new GrimoireSyncEverything.Summary();
            summary.AddImportedDialog("bed dialog", new[] { "Object access denied", "Translations unavailable" });
            summary.AddImportedDialog("intro", Array.Empty<string>());

            Assert.AreEqual(2, summary.Dialogs);
            Assert.AreEqual(0, summary.Skipped);
            Assert.AreEqual(0, summary.Failed);
            Assert.IsEmpty(summary.Errors);
            CollectionAssert.AreEqual(new[]
            {
                "Dialog 'bed dialog': Object access denied",
                "Dialog 'bed dialog': Translations unavailable"
            }, summary.Warnings);
            StringAssert.Contains("2 dialogs", summary.ToString());
            StringAssert.Contains("Warnings 2.", summary.ToString());
            StringAssert.DoesNotContain("Skipped", summary.ToString());
            StringAssert.DoesNotContain("Failed", summary.ToString());
        }

        [Test]
        public void WarningsDoNotHideGenuinelySkippedItemsOrFailures()
        {
            var summary = new GrimoireSyncEverything.Summary { Skipped = 1, Failed = 2 };
            summary.AddImportedDialog("bed dialog", new[] { "Object lookup failed" });

            Assert.AreEqual(1, summary.Skipped);
            Assert.AreEqual(2, summary.Failed);
            StringAssert.Contains("Skipped 1.", summary.ToString());
            StringAssert.Contains("Failed 2.", summary.ToString());
            StringAssert.Contains("Warnings 1.", summary.ToString());
        }

        private static GrimoireDialogValueRef Reference(string fieldId) => new GrimoireDialogValueRef
        {
            source = GrimoireDialogValueRef.SourceObjectField,
            objectId = "object-id",
            fieldId = fieldId,
            label = "bed: " + fieldId,
        };
    }
}
