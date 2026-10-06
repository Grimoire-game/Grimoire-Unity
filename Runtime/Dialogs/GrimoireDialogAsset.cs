using System;
using System.Collections.Generic;
using UnityEngine;

namespace Grimoire.PluginV2
{
    /// <summary>
    /// A Grimoire dialog baked into the project. Create and update these from
    /// Grimoire Connect &gt; Dialogs; play them with <see cref="GrimoireDialogPlayer"/>.
    /// Builds read only this asset, so no sign-in or network is needed at runtime.
    /// </summary>
    public class GrimoireDialogAsset : ScriptableObject
    {
        [SerializeField] private string _dialogId = "";
        [SerializeField] private string _dialogKey = "";
        [SerializeField] private string _displayName = "";
        [SerializeField] [TextArea] private string _description = "";
        [SerializeField] private string _gameId = "";
        [SerializeField] private string _updatedAt = "";
        [SerializeField] private string _importedAt = "";
        [SerializeField] private string _speakerMode = "none";
        [SerializeField] private string _startingNode = "";
        [SerializeField] private List<GrimoireDialogVariable> _variables = new List<GrimoireDialogVariable>();
        [SerializeField] private List<GrimoireDialogNode> _nodes = new List<GrimoireDialogNode>();

        [SerializeField]
        [Tooltip("Dialogs this one can jump to. Filled in by the importer.")]
        private List<GrimoireDialogAsset> _linkedDialogs = new List<GrimoireDialogAsset>();

        [NonSerialized] private Dictionary<string, GrimoireDialogNode> _nodeIndex;

        public string DialogId => _dialogId;
        public string DialogKey => _dialogKey;
        public string DisplayName => string.IsNullOrEmpty(_displayName) ? name : _displayName;
        public string Description => _description;
        public string GameId => _gameId;
        public string UpdatedAt => _updatedAt;
        public string ImportedAt => _importedAt;
        public string SpeakerMode => _speakerMode;
        public string StartingNode => _startingNode;
        public IReadOnlyList<GrimoireDialogVariable> Variables => _variables;
        public IReadOnlyList<GrimoireDialogNode> Nodes => _nodes;
        public IReadOnlyList<GrimoireDialogAsset> LinkedDialogs => _linkedDialogs;

        public GrimoireDialogNode FindNode(string identifier)
        {
            if (string.IsNullOrEmpty(identifier))
            {
                return null;
            }

            if (_nodeIndex == null)
            {
                _nodeIndex = new Dictionary<string, GrimoireDialogNode>(StringComparer.Ordinal);
                foreach (var node in _nodes)
                {
                    if (node == null)
                    {
                        continue;
                    }

                    if (!string.IsNullOrEmpty(node.identifier))
                    {
                        _nodeIndex[node.identifier] = node;
                    }

                    if (!string.IsNullOrEmpty(node.id) && !_nodeIndex.ContainsKey(node.id))
                    {
                        _nodeIndex[node.id] = node;
                    }
                }
            }

            return _nodeIndex.TryGetValue(identifier, out var found) ? found : null;
        }

        /// <summary>A linked dialog by Grimoire id or dialog key.</summary>
        public GrimoireDialogAsset FindLinkedDialog(string dialogIdOrKey)
        {
            if (string.IsNullOrEmpty(dialogIdOrKey))
            {
                return null;
            }

            if (dialogIdOrKey == _dialogId || dialogIdOrKey == _dialogKey)
            {
                return this;
            }

            foreach (var linked in _linkedDialogs)
            {
                if (linked != null && (linked._dialogId == dialogIdOrKey || linked._dialogKey == dialogIdOrKey))
                {
                    return linked;
                }
            }

            return null;
        }

        public GrimoireDialogVariable FindVariable(string variableName)
        {
            if (string.IsNullOrEmpty(variableName))
            {
                return null;
            }

            foreach (var variable in _variables)
            {
                if (variable != null && string.Equals(variable.name, variableName, StringComparison.Ordinal))
                {
                    return variable;
                }
            }

            return null;
        }

        internal void SetImportedData(
            string dialogId,
            string dialogKey,
            string displayName,
            string description,
            string gameId,
            string updatedAt,
            string speakerMode,
            string startingNode,
            List<GrimoireDialogVariable> variables,
            List<GrimoireDialogNode> nodes)
        {
            _dialogId = dialogId ?? "";
            _dialogKey = dialogKey ?? "";
            _displayName = displayName ?? "";
            _description = description ?? "";
            _gameId = gameId ?? "";
            _updatedAt = updatedAt ?? "";
            _importedAt = DateTime.UtcNow.ToString("o");
            _speakerMode = string.IsNullOrEmpty(speakerMode) ? "none" : speakerMode;
            _startingNode = startingNode ?? "";
            _variables = variables ?? new List<GrimoireDialogVariable>();
            _nodes = nodes ?? new List<GrimoireDialogNode>();
            _nodeIndex = null;
        }

        internal void SetLinkedDialogs(List<GrimoireDialogAsset> linkedDialogs)
        {
            _linkedDialogs = linkedDialogs ?? new List<GrimoireDialogAsset>();
        }

        private void OnValidate()
        {
            _nodeIndex = null;
        }
    }
}
