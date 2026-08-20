using System;
using UnityEngine;

namespace Grimoire.PluginV2
{
    /// <summary>
    /// One locale row cached on a <see cref="GrimoireTextLink"/>.
    /// </summary>
    [Serializable]
    public class GrimoireLinkedTranslation
    {
        [SerializeField]
        private string _languageCode = "";

        [SerializeField]
        private string _localText = "";

        [SerializeField]
        private string _grimoireText = "";

        public string LanguageCode
        {
            get => _languageCode;
            set => _languageCode = value ?? "";
        }

        public string LocalText
        {
            get => _localText;
            set => _localText = value ?? "";
        }

        public string GrimoireText
        {
            get => _grimoireText;
            set => _grimoireText = value ?? "";
        }

        public bool IsDeviated =>
            !string.Equals(_localText ?? "", _grimoireText ?? "", StringComparison.Ordinal);
    }
}
