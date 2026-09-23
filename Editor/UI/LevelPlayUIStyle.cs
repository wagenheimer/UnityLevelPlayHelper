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
    }
}

