using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

namespace Wagenheimer.LevelPlayHelper.Editor
{
    internal static class LevelPlayUIStyle
    {
        public const string PackageStylePath = "Packages/com.wagenheimer.levelplayhelper/Editor/UI/LevelPlayCommon.uss";

        public static void Apply(VisualElement element)
        {
            if (element == null) return;

            var sheet = AssetDatabase.LoadAssetAtPath<StyleSheet>(PackageStylePath);
            if (sheet == null)
            {
                var guids = AssetDatabase.FindAssets("LevelPlayCommon t:StyleSheet");
                if (guids != null && guids.Length > 0)
                {
                    var path = AssetDatabase.GUIDToAssetPath(guids[0]);
                    sheet = AssetDatabase.LoadAssetAtPath<StyleSheet>(path);
                }
            }

            if (sheet != null && !element.styleSheets.Contains(sheet))
            {
                element.styleSheets.Add(sheet);
            }
        }

        public static VisualElement CreateCard(string title, string subtitle = null, VisualElement headerRight = null)
        {
            var card = new VisualElement();
            card.AddToClassList("lp-card");

            var header = new VisualElement();
            header.AddToClassList("lp-card-header");

            var titleLabel = new Label(title);
            titleLabel.AddToClassList("lp-card-title");
            header.Add(titleLabel);

            if (headerRight != null)
            {
                header.Add(headerRight);
            }

            card.Add(header);

            if (!string.IsNullOrEmpty(subtitle))
            {
                var sub = new Label(subtitle);
                sub.AddToClassList("lp-card-subtitle");
                card.Add(sub);
            }

            return card;
        }

        public static VisualElement CreateCallout(string text, string severity = "info")
        {
            var box = new VisualElement();
            box.AddToClassList("lp-callout");
            box.AddToClassList("lp-callout--" + severity);

            var label = new Label(text);
            label.AddToClassList("lp-callout-text");
            box.Add(label);

            return box;
        }

        public static Label CreateBadge(string text, string type = "ok")
        {
            var badge = new Label(text);
            badge.AddToClassList("lp-badge");
            badge.AddToClassList("lp-badge--" + type);
            return badge;
        }

        public static void SetBadge(Label badge, string text, string type)
        {
            if (badge == null) return;
            badge.text = text;
            badge.RemoveFromClassList("lp-badge--ok");
            badge.RemoveFromClassList("lp-badge--warn");
            badge.RemoveFromClassList("lp-badge--fail");
            badge.RemoveFromClassList("lp-badge--info");
            badge.AddToClassList("lp-badge--" + type);
        }

        /// <summary>
        /// Renders <paramref name="text"/> on the button, splitting a leading icon (emoji/symbol) into its own
        /// element with a reserved width (a fallback emoji glyph draws wider than it measures on Windows).
        /// </summary>
        public static void ApplyIconText(Button button, string text)
        {
            for (int i = button.childCount - 1; i >= 0; i--)
            {
                var child = button[i];
                if (child.ClassListContains("wui-btn-icon") || child.ClassListContains("wui-btn-text"))
                    child.RemoveFromHierarchy();
            }

            SplitLeadingIcon(text, out var icon, out var label);

            if (string.IsNullOrEmpty(icon))
            {
                button.text = text;
                return;
            }

            button.text = string.Empty;
            button.style.flexDirection = FlexDirection.Row;
            button.style.alignItems = Align.Center;
            button.style.justifyContent = Justify.Center;

            var iconElement = new Label(icon);
            iconElement.AddToClassList("wui-btn-icon");
            iconElement.style.minWidth = 14;
            iconElement.style.marginRight = string.IsNullOrEmpty(label) ? 0 : 6;
            iconElement.style.flexShrink = 0;
            iconElement.style.unityTextAlign = TextAnchor.MiddleCenter;
            iconElement.pickingMode = PickingMode.Ignore;
            button.Add(iconElement);

            if (!string.IsNullOrEmpty(label))
            {
                var textLabel = new Label(label);
                textLabel.AddToClassList("wui-btn-text");
                textLabel.style.flexShrink = 0;
                textLabel.pickingMode = PickingMode.Ignore;
                button.Add(textLabel);
            }
        }

        public static VisualElement CreateIconLabel(string text)
        {
            SplitLeadingIcon(text, out var icon, out var rest);
            if (string.IsNullOrEmpty(icon))
                return new Label(text);

            var row = new VisualElement();
            row.style.flexDirection = FlexDirection.Row;
            row.style.alignItems = Align.Center;

            var iconElement = new Label(icon);
            iconElement.AddToClassList("wui-btn-icon");
            iconElement.style.minWidth = 14;
            iconElement.style.marginRight = string.IsNullOrEmpty(rest) ? 0 : 6;
            iconElement.style.flexShrink = 0;
            iconElement.style.unityTextAlign = TextAnchor.MiddleCenter;
            iconElement.pickingMode = PickingMode.Ignore;
            row.Add(iconElement);

            var label = new Label(rest);
            label.pickingMode = PickingMode.Ignore;
            row.Add(label);
            return row;
        }

        internal static void SplitLeadingIcon(string text, out string icon, out string label)
        {
            icon = null;
            label = text;
            if (string.IsNullOrEmpty(text)) return;

            int i = 0;
            while (i < text.Length)
            {
                int codePoint = char.IsHighSurrogate(text[i]) && i + 1 < text.Length
                    ? char.ConvertToUtf32(text[i], text[i + 1])
                    : text[i];

                if (!IsIconCodePoint(codePoint)) break;
                i += char.IsHighSurrogate(text[i]) ? 2 : 1;
            }

            if (i == 0) return;

            icon = text.Substring(0, i).TrimEnd();
            label = text.Substring(i).TrimStart();
        }

        private static bool IsIconCodePoint(int codePoint) =>
            (codePoint >= 0x2190 && codePoint <= 0x2BFF)
            || (codePoint >= 0x1F000 && codePoint <= 0x1FAFF)
            || codePoint == 0xFE0F
            || codePoint == 0x20E3;
    }
}

