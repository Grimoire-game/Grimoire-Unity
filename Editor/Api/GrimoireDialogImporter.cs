using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using UnityEditor;
using UnityEngine;

namespace Grimoire.PluginV2.Editor
{
    /// <summary>
    /// Downloads a dialog (plus the dialogs it jumps to, its translations, and
    /// speaker names) and bakes it into a <see cref="GrimoireDialogAsset"/>.
    /// Assets live outside Assets/Grimoire because the export importer replaces
    /// that folder.
    /// </summary>
    public static class GrimoireDialogImporter
    {
        public const string AssetsFolder = "Assets/GrimoireDialogs";

        private const int MaxDialogsPerImport = 32;

        public static event Action<GrimoireDialogAsset> Imported;

        /// <summary>Imported dialog assets in the project, keyed by Grimoire dialog id.</summary>
        public static Dictionary<string, GrimoireDialogAsset> FindAllImported()
        {
            var result = new Dictionary<string, GrimoireDialogAsset>(StringComparer.Ordinal);
            foreach (var guid in AssetDatabase.FindAssets("t:" + nameof(GrimoireDialogAsset)))
            {
                var asset = AssetDatabase.LoadAssetAtPath<GrimoireDialogAsset>(AssetDatabase.GUIDToAssetPath(guid));
                if (asset != null && !string.IsNullOrEmpty(asset.DialogId) && !result.ContainsKey(asset.DialogId))
                {
                    result[asset.DialogId] = asset;
                }
            }

            return result;
        }

        public static GrimoireDialogAsset FindImported(string dialogId)
        {
            if (string.IsNullOrEmpty(dialogId))
            {
                return null;
            }

            return FindAllImported().TryGetValue(dialogId, out var asset) ? asset : null;
        }

        /// <summary>
        /// Import (or update) <paramref name="dialogId"/> and every dialog it links to.
        /// Returns the asset for <paramref name="dialogId"/>.
        /// </summary>
        public static async Task<ApiResult<GrimoireDialogAsset>> ImportAsync(
            string gameId, string dialogId, Action<string> progress = null)
        {
            if (string.IsNullOrEmpty(gameId) || string.IsNullOrEmpty(dialogId))
            {
                return ApiResult<GrimoireDialogAsset>.Fail("Choose a game and a dialog first.");
            }

            if (!await GrimoireAuthSession.EnsureFreshTokenAsync())
            {
                return ApiResult<GrimoireDialogAsset>.Fail("Your Grimoire session has expired. Sign in again.");
            }

            var fetched = new Dictionary<string, DialogResource>(StringComparer.Ordinal);
            var nodesById = new Dictionary<string, List<DialogNodeDto>>(StringComparer.Ordinal);
            var startById = new Dictionary<string, string>(StringComparer.Ordinal);
            var queue = new Queue<string>();
            var queued = new HashSet<string>(StringComparer.Ordinal) { dialogId };
            queue.Enqueue(dialogId);

            while (queue.Count > 0)
            {
                var id = queue.Dequeue();
                progress?.Invoke(fetched.Count == 0 ? "Downloading dialog..." : $"Downloading linked dialog {fetched.Count}...");

                var result = await GrimoireApiClient.GetDialogAsync(gameId, id);
                if (!result.Success)
                {
                    if (id == dialogId)
                    {
                        var detail = result.HttpStatus > 0 ? $"{result.Error} (HTTP {result.HttpStatus})" : result.Error;
                        return ApiResult<GrimoireDialogAsset>.Fail(detail, result.Code, result.HttpStatus);
                    }

                    Debug.LogWarning($"[Grimoire Dialog] Could not download linked dialog '{id}': {result.Error}");
                    continue;
                }

                fetched[id] = result.Data;
                nodesById[id] = NormalizeNodes(result.Data.data, out var start);
                startById[id] = start;

                foreach (var linkedId in CollectLinkedDialogIds(nodesById[id]))
                {
                    if (queued.Count >= MaxDialogsPerImport)
                    {
                        break;
                    }

                    if (queued.Add(linkedId))
                    {
                        queue.Enqueue(linkedId);
                    }
                }
            }

            progress?.Invoke("Downloading translations...");
            var strings = await LoadStringsAsync(gameId);

            if (fetched.Values.Any(d => d.data?.speakerMode == "object"))
            {
                progress?.Invoke("Resolving speakers...");
                await GrimoireObjectKeyResolver.EnsureLibraryCachedAsync(gameId);
            }

            var builtNodes = new Dictionary<string, List<GrimoireDialogNode>>(StringComparer.Ordinal);
            foreach (var pair in fetched)
            {
                builtNodes[pair.Key] = BuildNodes(nodesById[pair.Key], pair.Value.data?.speakerMode, gameId, strings);
            }

            await ResolveObjectFieldsAsync(gameId, builtNodes.Values.SelectMany(n => n), progress);

            progress?.Invoke("Writing assets...");
            EnsureFolder();

            var existing = FindAllImported();
            var assets = new Dictionary<string, GrimoireDialogAsset>(StringComparer.Ordinal);
            foreach (var pair in fetched)
            {
                var resource = pair.Value;
                if (!existing.TryGetValue(pair.Key, out var asset))
                {
                    asset = ScriptableObject.CreateInstance<GrimoireDialogAsset>();
                    var path = AssetDatabase.GenerateUniqueAssetPath($"{AssetsFolder}/{FileNameFor(resource)}.asset");
                    AssetDatabase.CreateAsset(asset, path);
                    existing[pair.Key] = asset;
                }

                var data = resource.data ?? new DialogDataDto();
                asset.SetImportedData(
                    resource.id,
                    resource.dialog_key,
                    resource.name,
                    resource.description,
                    gameId,
                    resource.updated_at,
                    data.speakerMode,
                    startById[pair.Key],
                    BuildVariables(data.variables),
                    builtNodes[pair.Key]);
                assets[pair.Key] = asset;
            }

            foreach (var pair in assets)
            {
                var linked = new List<GrimoireDialogAsset>();
                foreach (var linkedId in CollectLinkedDialogIds(nodesById[pair.Key]))
                {
                    if (existing.TryGetValue(linkedId, out var target) && target != pair.Value && !linked.Contains(target))
                    {
                        linked.Add(target);
                    }
                }

                pair.Value.SetLinkedDialogs(linked);
                EditorUtility.SetDirty(pair.Value);
            }

            AssetDatabase.SaveAssets();

            var root = assets[dialogId];
            Imported?.Invoke(root);
            return ApiResult<GrimoireDialogAsset>.Ok(root);
        }

        // -----------------------------------------------------------------
        // Graph normalization
        // -----------------------------------------------------------------

        /// <summary>
        /// The node list for a dialog. Legacy section dialogs are flattened: each
        /// field becomes a node, chained to the next field and then the next section.
        /// </summary>
        internal static List<DialogNodeDto> NormalizeNodes(DialogDataDto data, out string startingNode)
        {
            startingNode = null;
            if (data == null)
            {
                return new List<DialogNodeDto>();
            }

            if (data.nodes != null && data.nodes.Length > 0)
            {
                var nodes = data.nodes.Where(n => n != null).ToList();
                foreach (var node in nodes)
                {
                    node.node_identifier = FirstNonEmpty(node.node_identifier, node.id);
                }

                startingNode = FirstNonEmpty(data.startingNode, data.startingSection, nodes.FirstOrDefault()?.node_identifier);
                return nodes;
            }

            var sections = (data.sections ?? Array.Empty<DialogSectionDto>())
                .Where(s => s != null)
                .OrderBy(s => s.order_index ?? 0)
                .ToList();

            var result = new List<DialogNodeDto>();
            var firstIdentifiers = sections.Select(s => FirstNonEmpty(s.section_identifier, s.id)).ToList();

            for (var si = 0; si < sections.Count; si++)
            {
                var fields = OrderedFields(sections[si]);
                for (var fi = 0; fi < fields.Count; fi++)
                {
                    var field = fields[fi];
                    field.node_identifier = fi == 0 ? firstIdentifiers[si] : $"{firstIdentifiers[si]}__{fi + 1}";
                    if (string.IsNullOrEmpty(field.name))
                    {
                        field.name = sections[si].name;
                    }

                    var hasExplicitLink = !string.IsNullOrEmpty(FirstNonEmpty(field.next_node, field.next_section, field.next_dialog));
                    var flowsOn = field.type != "question" && field.type != "condition" &&
                                  field.type != "end-dialog" && field.type != "end-text";
                    if (!hasExplicitLink && flowsOn)
                    {
                        if (fi < fields.Count - 1)
                        {
                            field.next_node = $"{firstIdentifiers[si]}__{fi + 2}";
                        }
                        else if (si < sections.Count - 1)
                        {
                            field.next_node = firstIdentifiers[si + 1];
                        }
                    }

                    result.Add(field);
                }
            }

            startingNode = FirstNonEmpty(data.startingSection, data.startingNode, firstIdentifiers.FirstOrDefault());
            return result;
        }

        private static List<DialogNodeDto> OrderedFields(DialogSectionDto section)
        {
            var fields = (section.fields ?? Array.Empty<DialogNodeDto>()).Where(f => f != null).ToList();
            if (section.fieldOrder == null || section.fieldOrder.Length == 0)
            {
                return fields.OrderBy(f => f.order_index ?? 0).ToList();
            }

            var ordered = new List<DialogNodeDto>();
            foreach (var id in section.fieldOrder)
            {
                var match = fields.FirstOrDefault(f => f.id == id);
                if (match != null && !ordered.Contains(match))
                {
                    ordered.Add(match);
                }
            }

            ordered.AddRange(fields.Where(f => !ordered.Contains(f)).OrderBy(f => f.order_index ?? 0));
            return ordered;
        }

        private static IEnumerable<string> CollectLinkedDialogIds(List<DialogNodeDto> nodes)
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var node in nodes)
            {
                var candidates = new List<string> { node.next_dialog, node.next_dialog_true, node.next_dialog_false };
                if (node.options != null)
                {
                    candidates.AddRange(node.options.Where(o => o != null).Select(o => o.next_dialog));
                }

                if (node.conditionOptions != null)
                {
                    candidates.AddRange(node.conditionOptions.Where(o => o != null).Select(o => o.next_dialog));
                }

                foreach (var id in candidates)
                {
                    if (!string.IsNullOrEmpty(id) && seen.Add(id))
                    {
                        yield return id;
                    }
                }
            }
        }

        // -----------------------------------------------------------------
        // DTO -> runtime data
        // -----------------------------------------------------------------

        private static List<GrimoireDialogNode> BuildNodes(
            List<DialogNodeDto> source,
            string speakerMode,
            string gameId,
            Dictionary<string, StringResource> strings)
        {
            var nodes = new List<GrimoireDialogNode>(source.Count);
            foreach (var dto in source)
            {
                var str = FindString(strings, dto.string_id);
                var node = new GrimoireDialogNode
                {
                    id = dto.id ?? "",
                    identifier = dto.node_identifier ?? "",
                    name = dto.name ?? "",
                    type = MapType(dto.type),
                    text = FirstNonEmpty(dto.text, str?.source_text) ?? "",
                    stringId = dto.string_id ?? "",
                    voiceUrl = str?.voice_url ?? "",
                    translations = ToTranslations(str),
                    speakerId = dto.speaker ?? "",
                    speakerName = SpeakerName(dto.speaker, speakerMode, gameId),
                    next = ToLink(FirstNonEmpty(dto.next_node, dto.next_section), dto.next_dialog,
                        FirstNonEmpty(dto.next_dialog_node, dto.next_dialog_section)),
                    pickOnce = dto.pickOnce ?? false,
                    conditionVariable = dto.condition?.variable ?? "",
                    conditionSubjectRef = ToRef(dto.condition?.subjectRef),
                    whenTrue = ToLink(dto.next_section_true, dto.next_dialog_true, dto.next_dialog_section_true),
                    whenFalse = ToLink(dto.next_section_false, dto.next_dialog_false, dto.next_dialog_section_false),
                    setter = ToAction(dto.setter),
                };

                if (dto.options != null)
                {
                    foreach (var option in dto.options.Where(o => o != null).OrderBy(o => o.order_index ?? 0))
                    {
                        var optionString = FindString(strings, option.string_id);
                        node.options.Add(new GrimoireDialogOption
                        {
                            id = option.id ?? "",
                            text = FirstNonEmpty(option.option_text, optionString?.source_text) ?? "",
                            stringId = option.string_id ?? "",
                            translations = ToTranslations(optionString),
                            link = ToLink(FirstNonEmpty(option.next_node, option.next_section), option.next_dialog,
                                FirstNonEmpty(option.next_dialog_node, option.next_dialog_section)),
                            action = ToAction(option.variableAction),
                            visibleWhen = ToCondition(option.visibilityCondition),
                            alwaysAvailable = option.alwaysAvailable ?? false,
                        });
                    }
                }

                if (dto.conditionOptions != null)
                {
                    foreach (var branch in dto.conditionOptions.Where(o => o != null).OrderBy(o => o.order_index ?? 0))
                    {
                        node.conditionBranches.Add(new GrimoireDialogConditionBranch
                        {
                            op = string.IsNullOrEmpty(branch.@operator) ? "==" : branch.@operator,
                            value = GrimoireDialogValue.From(branch.condition_value),
                            valueRef = ToRef(branch.valueRef),
                            link = ToLink(FirstNonEmpty(branch.next_node, branch.next_section), branch.next_dialog,
                                FirstNonEmpty(branch.next_dialog_node, branch.next_dialog_section)),
                        });
                    }
                }

                nodes.Add(node);
            }

            return nodes;
        }

        private static List<GrimoireDialogVariable> BuildVariables(DialogVariableDto[] source)
        {
            var variables = new List<GrimoireDialogVariable>();
            if (source == null)
            {
                return variables;
            }

            foreach (var dto in source)
            {
                if (dto == null || string.IsNullOrEmpty(dto.name))
                {
                    continue;
                }

                variables.Add(new GrimoireDialogVariable
                {
                    name = dto.name,
                    type = string.IsNullOrEmpty(dto.type) ? "text" : dto.type,
                    initialValue = GrimoireDialogValue.From(dto.initialValue),
                    description = dto.description ?? "",
                    hasRange = dto.rangeMin.HasValue && dto.rangeMax.HasValue,
                    rangeMin = dto.rangeMin ?? 0d,
                    rangeMax = dto.rangeMax ?? 0d,
                });
            }

            return variables;
        }

        private static GrimoireDialogNodeType MapType(string type)
        {
            switch (type)
            {
                case "question": return GrimoireDialogNodeType.Question;
                case "context": return GrimoireDialogNodeType.Context;
                case "condition": return GrimoireDialogNodeType.Condition;
                case "setter": return GrimoireDialogNodeType.Setter;
                case "jump": return GrimoireDialogNodeType.Jump;
                case "external-dialog": return GrimoireDialogNodeType.ExternalDialog;
                case "end-dialog":
                case "end-text":
                    return GrimoireDialogNodeType.End;
                default:
                    return GrimoireDialogNodeType.Line;
            }
        }

        private static GrimoireDialogLink ToLink(string nextNode, string nextDialog, string nextDialogNode)
        {
            return new GrimoireDialogLink
            {
                nextNode = nextNode ?? "",
                nextDialogId = nextDialog ?? "",
                nextDialogNode = nextDialogNode ?? "",
            };
        }

        private static GrimoireDialogValueRef ToRef(ValueRefDto dto)
        {
            if (dto == null || string.IsNullOrEmpty(dto.source))
            {
                return new GrimoireDialogValueRef();
            }

            return new GrimoireDialogValueRef
            {
                source = dto.source,
                literal = GrimoireDialogValue.From(dto.value),
                variableName = dto.variableName ?? "",
                typeId = dto.typeId ?? "",
                elementId = dto.elementId ?? "",
                objectId = dto.objectId ?? "",
                sectionId = dto.sectionId ?? "",
                fieldId = dto.fieldId ?? "",
                label = dto.label ?? "",
            };
        }

        private static GrimoireDialogVariableAction ToAction(VariableActionDto dto)
        {
            if (dto == null)
            {
                return new GrimoireDialogVariableAction();
            }

            return new GrimoireDialogVariableAction
            {
                variable = dto.variable ?? "",
                op = string.IsNullOrEmpty(dto.@operator) ? "set" : dto.@operator,
                value = GrimoireDialogValue.From(dto.value),
                targetRef = ToRef(dto.targetRef),
                valueRef = ToRef(dto.valueRef),
                enforceRange = dto.enforceRange ?? true,
            };
        }

        private static GrimoireDialogCondition ToCondition(VariableConditionDto dto)
        {
            if (dto == null)
            {
                return new GrimoireDialogCondition();
            }

            return new GrimoireDialogCondition
            {
                variable = dto.variable ?? "",
                op = string.IsNullOrEmpty(dto.@operator) ? "==" : dto.@operator,
                value = GrimoireDialogValue.From(dto.value),
                subjectRef = ToRef(dto.subjectRef),
                valueRef = ToRef(dto.valueRef),
            };
        }

        // -----------------------------------------------------------------
        // Object field references
        // -----------------------------------------------------------------

        /// <summary>
        /// The dialog API references object fields by UUID, but the export's
        /// ObjectRuntime is keyed by object code id and field name. Bake both in.
        /// </summary>
        private static async Task ResolveObjectFieldsAsync(
            string gameId, IEnumerable<GrimoireDialogNode> nodes, Action<string> progress)
        {
            var refs = nodes.SelectMany(AllRefs)
                .Where(r => r != null && r.source == GrimoireDialogValueRef.SourceObjectField && !string.IsNullOrEmpty(r.objectId))
                .ToList();
            if (refs.Count == 0)
            {
                return;
            }

            progress?.Invoke("Resolving object fields...");
            await GrimoireObjectKeyResolver.EnsureLibraryCachedAsync(gameId);

            var views = new Dictionary<string, ObjectViewDocument>(StringComparer.Ordinal);
            foreach (var reference in refs)
            {
                if (!views.TryGetValue(reference.objectId, out var view))
                {
                    var result = await GrimoireApiClient.GetObjectViewAsync(gameId, reference.objectId);
                    view = result.Success ? result.Data : null;
                    views[reference.objectId] = view;
                    if (!result.Success)
                    {
                        Debug.LogWarning($"[Grimoire Dialog] Could not load object '{reference.label}' ({reference.objectId}): {result.Error}");
                    }
                }

                reference.objectKey = GrimoireObjectKeyResolver.TryGetSummary(gameId, reference.objectId, null, out var summary) &&
                                      !string.IsNullOrEmpty(summary.code_id)
                    ? summary.code_id
                    : view?.@object?.code_id ?? "";

                var field = view?.sections?
                    .Where(s => s?.fields != null)
                    .SelectMany(s => s.fields)
                    .FirstOrDefault(f => f != null && f.id == reference.fieldId);
                reference.fieldName = FirstNonEmpty(field?.label, LabelFieldPart(reference.label)) ?? "";
            }
        }

        private static IEnumerable<GrimoireDialogValueRef> AllRefs(GrimoireDialogNode node)
        {
            yield return node.conditionSubjectRef;
            yield return node.setter?.targetRef;
            yield return node.setter?.valueRef;

            foreach (var branch in node.conditionBranches)
            {
                yield return branch?.valueRef;
            }

            foreach (var option in node.options)
            {
                yield return option?.action?.targetRef;
                yield return option?.action?.valueRef;
                yield return option?.visibleWhen?.subjectRef;
                yield return option?.visibleWhen?.valueRef;
            }
        }

        // Dialog editor labels look like "bed: interactionCount".
        private static string LabelFieldPart(string label)
        {
            if (string.IsNullOrEmpty(label))
            {
                return null;
            }

            var colon = label.LastIndexOf(':');
            return colon >= 0 ? label.Substring(colon + 1).Trim() : null;
        }

        // -----------------------------------------------------------------
        // Strings and speakers
        // -----------------------------------------------------------------

        private static async Task<Dictionary<string, StringResource>> LoadStringsAsync(string gameId)
        {
            var map = new Dictionary<string, StringResource>(StringComparer.Ordinal);
            var result = await GrimoireApiClient.ListStringsAsync(gameId, includeTranslations: true);
            if (!result.Success)
            {
                Debug.LogWarning($"[Grimoire Dialog] Translations could not be downloaded, so only the source text is imported: {result.Error}");
                return map;
            }

            foreach (var str in result.Data)
            {
                if (str != null && !string.IsNullOrEmpty(str.id))
                {
                    map[str.id] = str;
                }
            }

            return map;
        }

        private static StringResource FindString(Dictionary<string, StringResource> strings, string stringId)
        {
            return !string.IsNullOrEmpty(stringId) && strings.TryGetValue(stringId, out var str) ? str : null;
        }

        private static List<GrimoireDialogTranslation> ToTranslations(StringResource str)
        {
            var rows = new List<GrimoireDialogTranslation>();
            if (str?.translations == null)
            {
                return rows;
            }

            foreach (var translation in str.translations)
            {
                if (translation == null || string.IsNullOrEmpty(translation.language_code))
                {
                    continue;
                }

                if (string.IsNullOrEmpty(translation.translated_text) && string.IsNullOrEmpty(translation.voice_url))
                {
                    continue;
                }

                rows.Add(new GrimoireDialogTranslation
                {
                    languageCode = translation.language_code,
                    text = translation.translated_text ?? "",
                    voiceUrl = translation.voice_url ?? "",
                });
            }

            return rows;
        }

        // Speaker type elements have no Public API endpoint yet, so "type" mode
        // keeps only the raw id in speakerId.
        private static string SpeakerName(string speaker, string speakerMode, string gameId)
        {
            if (string.IsNullOrEmpty(speaker))
            {
                return "";
            }

            switch (speakerMode)
            {
                case "free-text":
                    return speaker;
                case "object":
                    return GrimoireObjectKeyResolver.TryGetSummary(gameId, speaker, speaker, out var summary) &&
                           !string.IsNullOrEmpty(summary?.name)
                        ? summary.name
                        : "";
                default:
                    return "";
            }
        }

        // -----------------------------------------------------------------
        // Helpers
        // -----------------------------------------------------------------

        private static void EnsureFolder()
        {
            if (!AssetDatabase.IsValidFolder(AssetsFolder))
            {
                AssetDatabase.CreateFolder("Assets", AssetsFolder.Substring("Assets/".Length));
            }
        }

        private static string FileNameFor(DialogResource resource)
        {
            var raw = FirstNonEmpty(resource.dialog_key, resource.name, resource.id) ?? "Dialog";
            var clean = Regex.Replace(raw, @"[^A-Za-z0-9_\- ]+", "_").Trim();
            return string.IsNullOrEmpty(clean) ? "Dialog" : clean;
        }

        private static string FirstNonEmpty(params string[] values)
        {
            foreach (var value in values)
            {
                if (!string.IsNullOrWhiteSpace(value))
                {
                    return value.Trim();
                }
            }

            return null;
        }
    }
}
