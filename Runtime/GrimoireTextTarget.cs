using System;
using System.Reflection;
using UnityEngine;
using UnityEngine.UI;

namespace Grimoire.PluginV2
{
    /// <summary>
    /// Applies resolved Grimoire copy onto common Unity text components.
    /// TextMesh Pro is supported via reflection when the package is present.
    /// </summary>
    public static class GrimoireTextTarget
    {
        private static Type _tmpTextType;
        private static PropertyInfo _tmpTextProperty;

        /// <summary>Write <paramref name="text"/> onto a supported text component on <paramref name="gameObject"/>.</summary>
        public static bool TryApply(GameObject gameObject, string text)
        {
            if (gameObject == null)
            {
                return false;
            }

            var uiText = gameObject.GetComponent<Text>();
            if (uiText != null)
            {
                uiText.text = text ?? "";
                return true;
            }

            if (TryApplyTmp(gameObject, text))
            {
                return true;
            }

            return false;
        }

        /// <summary>True when a supported text component exists on the GameObject.</summary>
        public static bool HasSupportedTarget(GameObject gameObject)
        {
            if (gameObject == null)
            {
                return false;
            }

            if (gameObject.GetComponent<Text>() != null)
            {
                return true;
            }

            EnsureTmpReflection();
            return _tmpTextType != null && gameObject.GetComponent(_tmpTextType) != null;
        }

        private static bool TryApplyTmp(GameObject gameObject, string text)
        {
            EnsureTmpReflection();
            if (_tmpTextType == null || _tmpTextProperty == null)
            {
                return false;
            }

            var component = gameObject.GetComponent(_tmpTextType);
            if (component == null)
            {
                return false;
            }

            _tmpTextProperty.SetValue(component, text ?? "");
            return true;
        }

        private static void EnsureTmpReflection()
        {
            if (_tmpTextType != null)
            {
                return;
            }

            _tmpTextType = Type.GetType("TMPro.TMP_Text, Unity.TextMeshPro");
            if (_tmpTextType == null)
            {
                return;
            }

            _tmpTextProperty = _tmpTextType.GetProperty("text", BindingFlags.Instance | BindingFlags.Public);
        }
    }
}
