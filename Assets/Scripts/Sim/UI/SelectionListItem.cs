using System;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace CirrusFS.Sim
{
    /// One row or tile in a selection list. The same prefab script backs both panels: the
    /// aircraft tiles, which show an icon, and the airport rows, which do not.
    ///
    /// Selection colour is driven here rather than through the Button's own colour tint,
    /// because the tint would fight the selected state every time the pointer leaves. Set the
    /// Button's Transition to None on the prefab.
    [DisallowMultipleComponent]
    public class SelectionListItem : MonoBehaviour, IPointerEnterHandler, IPointerExitHandler
    {
        /// What a row displays. Keeps Bind() readable at the call site.
        public readonly struct Content
        {
            public readonly string Title;
            public readonly string Subtitle;
            public readonly string Tag;
            public readonly Sprite Icon;

            public Content(string title, string subtitle = null, string tag = null, Sprite icon = null)
            {
                Title = title;
                Subtitle = subtitle;
                Tag = tag;
                Icon = icon;
            }
        }

        [Header("Wiring (auto-filled from children if left empty)")]
        public Button button;
        public Image background;
        public Image iconImage;
        public TMP_Text titleText;
        public TMP_Text subtitleText;
        public TMP_Text tagText;

        [Tooltip("Optional tick badge shown on the selected item, as in the X-Plane aircraft grid.")]
        public GameObject selectedBadge;

        [Header("Colours")]
        public Color normalColor = new Color(0.10f, 0.13f, 0.18f, 0.85f);
        public Color hoverColor = new Color(0.16f, 0.22f, 0.30f, 0.90f);
        public Color selectedColor = new Color(0.16f, 0.42f, 0.85f, 0.95f);

        /// The data this row stands for: an AircraftEntry or an AirportRef.
        public object Payload { get; private set; }

        /// Lower-cased haystack the search box matches against.
        public string SearchKey { get; private set; } = string.Empty;

        public bool IsSelected { get; private set; }

        Action<SelectionListItem> clicked;
        bool hovered;

        void Reset()
        {
            AutoWire();
        }

        void Awake()
        {
            AutoWire();

            if (button != null)
            {
                button.onClick.AddListener(HandleClick);
            }
            else
            {
                Debug.LogError($"[SelectionListItem] {name} has no Button; the row cannot be clicked.");
            }
        }

        void OnDestroy()
        {
            if (button != null) button.onClick.RemoveListener(HandleClick);
        }

        void AutoWire()
        {
            if (button == null) button = GetComponent<Button>();
            if (button == null) button = GetComponentInChildren<Button>(true);
            if (background == null) background = GetComponent<Image>();

            if (titleText == null)
            {
                TMP_Text[] texts = GetComponentsInChildren<TMP_Text>(true);
                if (texts.Length > 0) titleText = texts[0];
                if (texts.Length > 1 && subtitleText == null) subtitleText = texts[1];
                if (texts.Length > 2 && tagText == null) tagText = texts[2];
            }
        }

        /// Fills the row in and hooks up its click. Safe to call repeatedly: rows are pooled
        /// and rebound rather than destroyed and recreated.
        public void Bind(Content content, object payload, Action<SelectionListItem> onClicked)
        {
            Payload = payload;
            clicked = onClicked;

            if (titleText != null) titleText.text = content.Title ?? string.Empty;

            if (subtitleText != null)
            {
                subtitleText.text = content.Subtitle ?? string.Empty;
                subtitleText.gameObject.SetActive(!string.IsNullOrEmpty(content.Subtitle));
            }

            if (tagText != null)
            {
                tagText.text = content.Tag ?? string.Empty;
                tagText.gameObject.SetActive(!string.IsNullOrEmpty(content.Tag));
            }

            if (iconImage != null)
            {
                iconImage.sprite = content.Icon;
                iconImage.enabled = content.Icon != null;
            }

            SearchKey = $"{content.Title} {content.Subtitle} {content.Tag}".ToLowerInvariant();

            hovered = false;
            SetSelected(false);
        }

        public void SetSelected(bool selected)
        {
            IsSelected = selected;
            if (selectedBadge != null) selectedBadge.SetActive(selected);
            ApplyColor();
        }

        public void SetVisible(bool visible)
        {
            if (gameObject.activeSelf != visible) gameObject.SetActive(visible);
        }

        /// True when the row matches a search term. An empty term matches everything.
        public bool Matches(string lowerCaseTerm)
        {
            return string.IsNullOrEmpty(lowerCaseTerm) || SearchKey.Contains(lowerCaseTerm);
        }

        public T PayloadAs<T>() => Payload is T typed ? typed : default;

        void HandleClick() => clicked?.Invoke(this);

        public void OnPointerEnter(PointerEventData eventData)
        {
            hovered = true;
            ApplyColor();
        }

        public void OnPointerExit(PointerEventData eventData)
        {
            hovered = false;
            ApplyColor();
        }

        void ApplyColor()
        {
            if (background == null) return;

            background.color = IsSelected ? selectedColor : hovered ? hoverColor : normalColor;
        }
    }
}
