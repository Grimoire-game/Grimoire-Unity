using System;
using UnityEditor;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;
using Object = UnityEngine.Object;

namespace Grimoire.PluginV2.Editor
{
    /// <summary>
    /// Builds the scene's dialog setup: one <see cref="GrimoireDialogPlayer"/>
    /// with a child canvas holding the default <see cref="GrimoireDialogView"/>,
    /// plus an EventSystem when the scene has none. Dialogs are passed to the
    /// player from code with StartDialog(asset).
    /// </summary>
    public static class GrimoireDialogSceneSpawner
    {
        private static readonly Color PanelColor = new Color(0.07f, 0.06f, 0.12f, 0.92f);
        private static readonly Color SpeakerColor = new Color(0.72f, 0.6f, 1f);
        private static readonly Color ButtonColor = new Color(0.431f, 0.208f, 1f);
        private static readonly Color ChoiceColor = new Color(0.18f, 0.16f, 0.26f);

        public const string RootName = "Grimoire Dialog";

        /// <summary>The dialog player in the open scenes, or null.</summary>
        public static GrimoireDialogPlayer FindExisting()
        {
#if UNITY_2023_1_OR_NEWER
            return Object.FindFirstObjectByType<GrimoireDialogPlayer>(FindObjectsInactive.Include);
#else
            return Object.FindObjectOfType<GrimoireDialogPlayer>(true);
#endif
        }

        /// <summary>
        /// Selects the scene's dialog player, creating the player + default dialog
        /// box first when the scene has none. One player plays every dialog.
        /// </summary>
        public static GrimoireDialogPlayer AddOrSelect()
        {
            var existing = FindExisting();
            if (existing != null)
            {
                Selection.activeGameObject = existing.gameObject;
                EditorGUIUtility.PingObject(existing.gameObject);
                return existing;
            }

            return Spawn();
        }

        public static GrimoireDialogPlayer Spawn()
        {
            var root = new GameObject(RootName);
            var player = root.AddComponent<GrimoireDialogPlayer>();

            var canvasObject = new GameObject("Dialog Canvas", typeof(RectTransform), typeof(Canvas), typeof(CanvasScaler), typeof(GraphicRaycaster));
            canvasObject.transform.SetParent(root.transform, false);
            var canvas = canvasObject.GetComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = 100;
            var scaler = canvasObject.GetComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920, 1080);
            scaler.matchWidthOrHeight = 0.5f;

            var panel = CreateRect("Dialog Box", canvasObject.transform);
            panel.anchorMin = new Vector2(0f, 0f);
            panel.anchorMax = new Vector2(1f, 0f);
            panel.pivot = new Vector2(0.5f, 0f);
            panel.sizeDelta = new Vector2(-240f, 0f);
            panel.anchoredPosition = new Vector2(0f, 48f);
            var panelImage = panel.gameObject.AddComponent<Image>();
            panelImage.color = PanelColor;
            ApplyRoundedSprite(panelImage);
            var panelLayout = panel.gameObject.AddComponent<VerticalLayoutGroup>();
            panelLayout.padding = new RectOffset(40, 40, 28, 28);
            panelLayout.spacing = 14f;
            panelLayout.childControlWidth = true;
            panelLayout.childControlHeight = true;
            panelLayout.childForceExpandWidth = true;
            panelLayout.childForceExpandHeight = false;
            panel.gameObject.AddComponent<ContentSizeFitter>().verticalFit = ContentSizeFitter.FitMode.PreferredSize;

            var speaker = CreateText("Speaker", panel, "Speaker", 30, FontStyle.Bold, SpeakerColor, TextAnchor.MiddleLeft);
            var body = CreateText("Line", panel, "Dialog text", 32, FontStyle.Normal, Color.white, TextAnchor.UpperLeft);
            body.gameObject.AddComponent<LayoutElement>().minHeight = 90f;

            var choices = CreateRect("Choices", panel);
            var choicesLayout = choices.gameObject.AddComponent<VerticalLayoutGroup>();
            choicesLayout.spacing = 10f;
            choicesLayout.childControlWidth = true;
            choicesLayout.childControlHeight = true;
            choicesLayout.childForceExpandWidth = true;
            choicesLayout.childForceExpandHeight = false;
            var template = CreateButton("Choice Template", choices, "Answer", ChoiceColor, TextAnchor.MiddleLeft);
            template.GetComponent<LayoutElement>().preferredHeight = 60f;
            template.gameObject.SetActive(false);

            var footer = CreateRect("Footer", panel);
            var footerLayout = footer.gameObject.AddComponent<HorizontalLayoutGroup>();
            footerLayout.childAlignment = TextAnchor.MiddleRight;
            footerLayout.childControlWidth = true;
            footerLayout.childControlHeight = true;
            footerLayout.childForceExpandWidth = false;
            footerLayout.childForceExpandHeight = false;
            var continueButton = CreateButton("Continue Button", footer, "Continue", ButtonColor, TextAnchor.MiddleCenter);
            var continueLayout = continueButton.GetComponent<LayoutElement>();
            continueLayout.preferredWidth = 240f;
            continueLayout.preferredHeight = 60f;

            var view = canvasObject.AddComponent<GrimoireDialogView>();
            view.player = player;
            view.panel = panel.gameObject;
            view.speakerText = speaker.gameObject;
            view.bodyText = body.gameObject;
            view.choiceContainer = choices;
            view.choiceButtonTemplate = template;
            view.continueButton = continueButton;

            Undo.RegisterCreatedObjectUndo(root, "Add Grimoire dialog");
            EnsureEventSystem();

            Selection.activeGameObject = root;
            EditorGUIUtility.PingObject(root);
            return player;
        }

        private static void EnsureEventSystem()
        {
#if UNITY_2023_1_OR_NEWER
            if (Object.FindFirstObjectByType<EventSystem>() != null)
#else
            if (Object.FindObjectOfType<EventSystem>() != null)
#endif
            {
                return;
            }

            var eventSystem = new GameObject("EventSystem", typeof(EventSystem));
            var inputSystemModule = Type.GetType("UnityEngine.InputSystem.UI.InputSystemUIInputModule, Unity.InputSystem");
            if (inputSystemModule != null)
            {
                eventSystem.AddComponent(inputSystemModule);
            }
            else
            {
                eventSystem.AddComponent<StandaloneInputModule>();
            }

            Undo.RegisterCreatedObjectUndo(eventSystem, "Add EventSystem");
        }

        private static RectTransform CreateRect(string name, Transform parent)
        {
            var go = new GameObject(name, typeof(RectTransform));
            go.transform.SetParent(parent, false);
            return (RectTransform)go.transform;
        }

        private static Text CreateText(
            string name, Transform parent, string text, int size, FontStyle style, Color color, TextAnchor alignment)
        {
            var rect = CreateRect(name, parent);
            var label = rect.gameObject.AddComponent<Text>();
            label.font = DefaultFont();
            label.text = text;
            label.fontSize = size;
            label.fontStyle = style;
            label.color = color;
            label.alignment = alignment;
            label.supportRichText = true;
            label.horizontalOverflow = HorizontalWrapMode.Wrap;
            label.verticalOverflow = VerticalWrapMode.Overflow;
            label.raycastTarget = false;
            return label;
        }

        private static Button CreateButton(string name, Transform parent, string label, Color color, TextAnchor alignment)
        {
            var rect = CreateRect(name, parent);
            var image = rect.gameObject.AddComponent<Image>();
            image.color = color;
            ApplyRoundedSprite(image);

            var button = rect.gameObject.AddComponent<Button>();
            button.targetGraphic = image;

            rect.gameObject.AddComponent<LayoutElement>();

            var text = CreateText("Label", rect, label, 28, FontStyle.Normal, Color.white, alignment);
            var textRect = (RectTransform)text.transform;
            textRect.anchorMin = Vector2.zero;
            textRect.anchorMax = Vector2.one;
            textRect.offsetMin = new Vector2(24f, 6f);
            textRect.offsetMax = new Vector2(-24f, -6f);
            return button;
        }

        private static void ApplyRoundedSprite(Image image)
        {
            var sprite = AssetDatabase.GetBuiltinExtraResource<Sprite>("UI/Skin/UISprite.psd");
            if (sprite != null)
            {
                image.sprite = sprite;
                image.type = Image.Type.Sliced;
            }
        }

        private static Font DefaultFont()
        {
#if UNITY_2022_2_OR_NEWER
            return Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
#else
            return Resources.GetBuiltinResource<Font>("Arial.ttf");
#endif
        }
    }
}
