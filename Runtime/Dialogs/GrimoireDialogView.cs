using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

namespace Grimoire.PluginV2
{
    /// <summary>
    /// Default dialog box: speaker name, line text, a Continue button, and one
    /// button per answer. Works with Unity UI Text and TextMesh Pro.
    ///
    /// It only uses the public GrimoireDialogPlayer API (OnLine, OnChoices,
    /// OnDialogEnded, Next, Choose), so you can copy it as a starting point for
    /// your own UI.
    /// </summary>
    [AddComponentMenu("Grimoire/Grimoire Dialog View")]
    public class GrimoireDialogView : MonoBehaviour
    {
        [Tooltip("The player to display. Found in parents when left empty.")]
        public GrimoireDialogPlayer player;

        [Tooltip("Shown while a dialog plays, hidden otherwise. Use a child object, not this one.")]
        public GameObject panel;

        [Tooltip("Text or TextMesh Pro object for the speaker name.")]
        public GameObject speakerText;

        [Tooltip("Text or TextMesh Pro object for the line.")]
        public GameObject bodyText;

        public Button continueButton;

        [Tooltip("Parent for the answer buttons.")]
        public Transform choiceContainer;

        [Tooltip("Copied once per answer. Keep it inactive.")]
        public Button choiceButtonTemplate;

        [Tooltip("Hide the panel when no dialog is playing.")]
        public bool hideWhenIdle = true;

        public string continueLabel = "Continue";
        public string closeLabel = "Close";

        private readonly List<GameObject> _choiceButtons = new List<GameObject>();
        private GrimoireDialogPlayer _subscribed;

        private void Awake()
        {
            if (player == null)
            {
                player = GetComponentInParent<GrimoireDialogPlayer>();
            }

            if (choiceButtonTemplate != null)
            {
                choiceButtonTemplate.gameObject.SetActive(false);
            }
        }

        private void OnEnable()
        {
            Subscribe();

            if (player != null && player.IsPlaying && player.CurrentLine != null)
            {
                Show(player.CurrentLine);
            }
            else
            {
                Hide();
            }
        }

        private void OnDisable()
        {
            Unsubscribe();
        }

        private void Subscribe()
        {
            if (player == null || _subscribed == player)
            {
                return;
            }

            Unsubscribe();
            _subscribed = player;
            player.OnLine.AddListener(Show);
            player.OnChoices.AddListener(Show);
            player.OnDialogEnded.AddListener(Hide);
            if (continueButton != null)
            {
                continueButton.onClick.AddListener(player.Next);
            }
        }

        private void Unsubscribe()
        {
            if (_subscribed == null)
            {
                return;
            }

            _subscribed.OnLine.RemoveListener(Show);
            _subscribed.OnChoices.RemoveListener(Show);
            _subscribed.OnDialogEnded.RemoveListener(Hide);
            if (continueButton != null)
            {
                continueButton.onClick.RemoveListener(_subscribed.Next);
            }

            _subscribed = null;
        }

        private void Show(DialogLine line)
        {
            if (panel != null)
            {
                panel.SetActive(true);
            }

            if (speakerText != null)
            {
                speakerText.SetActive(line.HasSpeaker);
                SetText(speakerText, line.Speaker);
            }

            SetText(bodyText, line.Text);
            ClearChoices();

            if (line.IsChoice)
            {
                foreach (var choice in line.Choices)
                {
                    AddChoice(choice);
                }
            }

            if (continueButton != null)
            {
                continueButton.gameObject.SetActive(!line.IsChoice);
                SetText(continueButton.gameObject, line.IsLast ? closeLabel : continueLabel);
            }
        }

        private void Hide()
        {
            ClearChoices();
            if (hideWhenIdle && panel != null && panel != gameObject)
            {
                panel.SetActive(false);
            }
        }

        private void AddChoice(DialogChoice choice)
        {
            if (choiceButtonTemplate == null)
            {
                return;
            }

            var parent = choiceContainer != null ? choiceContainer : choiceButtonTemplate.transform.parent;
            var button = Instantiate(choiceButtonTemplate, parent);
            button.gameObject.name = $"Choice {choice.Index + 1}";
            button.gameObject.SetActive(true);
            SetText(button.gameObject, choice.Text);

            var index = choice.Index;
            button.onClick.AddListener(() => player.Choose(index));
            _choiceButtons.Add(button.gameObject);
        }

        private void ClearChoices()
        {
            foreach (var button in _choiceButtons)
            {
                if (button != null)
                {
                    Destroy(button);
                }
            }

            _choiceButtons.Clear();
        }

        private static void SetText(GameObject target, string text)
        {
            if (target == null)
            {
                return;
            }

            if (GrimoireTextTarget.TryApply(target, text))
            {
                return;
            }

            foreach (Transform child in target.transform)
            {
                if (GrimoireTextTarget.TryApply(child.gameObject, text))
                {
                    return;
                }
            }
        }
    }
}
