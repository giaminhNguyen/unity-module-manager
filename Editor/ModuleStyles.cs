using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace ModuleManager
{
    /// <summary>
    /// Màu, style và helper vẽ dùng chung cho tab Manager và Publisher. Màu đổi theo skin sáng/tối;
    /// style tạo một lần rồi dùng lại để không cấp phát mỗi frame.
    /// </summary>
    internal static class ModuleStyles
    {
        private static bool Pro => EditorGUIUtility.isProSkin;

        public static Color Green => Pro ? new Color(0.42f, 0.82f, 0.47f) : new Color(0.10f, 0.52f, 0.20f);
        public static Color Amber => Pro ? new Color(0.96f, 0.76f, 0.32f) : new Color(0.72f, 0.46f, 0.00f);
        public static Color Blue => Pro ? new Color(0.47f, 0.70f, 1.00f) : new Color(0.10f, 0.38f, 0.78f);
        public static Color Red => Pro ? new Color(1.00f, 0.47f, 0.47f) : new Color(0.72f, 0.14f, 0.14f);
        public static Color Muted => Pro ? new Color(0.60f, 0.60f, 0.60f) : new Color(0.42f, 0.42f, 0.42f);

        private static Color RowAlt => Pro ? new Color(1f, 1f, 1f, 0.035f) : new Color(0f, 0f, 0f, 0.045f);
        private static Color RowSelected => new Color(0.26f, 0.52f, 0.96f, Pro ? 0.20f : 0.14f);
        private static Color HeaderBg => Pro ? new Color(0f, 0f, 0f, 0.28f) : new Color(0f, 0f, 0f, 0.09f);
        private static Color SubHeaderBg => Pro ? new Color(1f, 1f, 1f, 0.05f) : new Color(0f, 0f, 0f, 0.05f);
        private static Color LineColor => Pro ? new Color(0f, 0f, 0f, 0.45f) : new Color(0f, 0f, 0f, 0.18f);

        // Độ rộng cột cố định; cột tên giãn theo cửa sổ.
        public const float CheckWidth = 18, ArrowWidth = 16, VersionWidth = 66, PickWidth = 86, StatusWidth = 116,
            ActionWidth = 92, IconWidth = 26, RowHeight = 36;

        private static GUIStyle _title, _sub, _cell, _column, _tag, _group, _subGroup, _arrow, _bar, _hint;
        private static readonly Dictionary<Color, GUIStyle> StatusStyles = new Dictionary<Color, GUIStyle>();

        public static GUIStyle Title => _title ??= new GUIStyle(EditorStyles.label)
            { fontStyle = FontStyle.Bold, clipping = TextClipping.Clip };

        public static GUIStyle Sub => _sub ??= new GUIStyle(EditorStyles.miniLabel)
            { clipping = TextClipping.Clip, wordWrap = false, normal = { textColor = Muted } };

        public static GUIStyle Cell => _cell ??= new GUIStyle(EditorStyles.label) { alignment = TextAnchor.MiddleLeft, fixedHeight = 22 };

        public static GUIStyle Column => _column ??= new GUIStyle(EditorStyles.miniBoldLabel) { normal = { textColor = Muted } };

        public static GUIStyle Hint => _hint ??= new GUIStyle(EditorStyles.miniLabel)
            { wordWrap = true, normal = { textColor = Muted } };

        public static GUIStyle Tag => _tag ??= new GUIStyle(EditorStyles.miniBoldLabel)
            { alignment = TextAnchor.MiddleLeft, padding = new RectOffset(6, 0, 1, 0), normal = { textColor = Blue } };

        public static GUIStyle Group => _group ??= new GUIStyle(EditorStyles.foldout)
            { fontSize = 13, fontStyle = FontStyle.Bold, fixedHeight = 22, alignment = TextAnchor.MiddleLeft };

        public static GUIStyle SubGroup => _subGroup ??= new GUIStyle(EditorStyles.foldout)
            { fontStyle = FontStyle.Bold, fixedHeight = 20, alignment = TextAnchor.MiddleLeft };

        public static GUIStyle Arrow => _arrow ??= new GUIStyle(EditorStyles.label)
            { alignment = TextAnchor.MiddleCenter, normal = { textColor = Muted }, padding = new RectOffset(0, 0, 0, 0) };

        public static GUIStyle Bar => _bar ??= new GUIStyle { padding = new RectOffset(8, 8, 5, 5) };

        /// <summary>Chữ trạng thái có chấm màu ở đầu.</summary>
        public static void Status(string text, Color color, string tooltip = null, params GUILayoutOption[] options)
        {
            if (!StatusStyles.TryGetValue(color, out var style))
                StatusStyles[color] = style = new GUIStyle(EditorStyles.label)
                    { fontStyle = FontStyle.Bold, alignment = TextAnchor.MiddleLeft, fixedHeight = 22, normal = { textColor = color } };
            GUILayout.Label(new GUIContent("● " + text, tooltip), style, options);
        }

        /// <summary>Ô một dòng chữ, căn giữa theo chiều dọc của hàng.</summary>
        public static void CellLabel(GUIContent content, float width)
        {
            using (new EditorGUILayout.VerticalScope(GUILayout.Width(width)))
            {
                GUILayout.FlexibleSpace();
                GUILayout.Label(content, Cell);
                GUILayout.FlexibleSpace();
            }
        }

        /// <summary>Nhãn loại plugin, null nếu là module file thường.</summary>
        public static string KindTag(ModuleManifest m)
        {
            if (!string.IsNullOrEmpty(m.unityPackage)) return "UNITYPACKAGE";
            if (m.paths.Length == 0 && m.packages.Length > 0) return "UPM";
            return null;
        }

        /// <summary>Nền dòng: tô xanh khi đang chọn, xen kẽ sáng/tối cho dễ dò theo hàng.</summary>
        public static void RowBackground(Rect rect, int index, bool selected)
        {
            if (Event.current.type != EventType.Repaint) return;
            if (selected) EditorGUI.DrawRect(rect, RowSelected);
            else if (index % 2 == 1) EditorGUI.DrawRect(rect, RowAlt);
        }

        public static void GroupBackground(Rect rect, int depth)
        {
            if (Event.current.type == EventType.Repaint) EditorGUI.DrawRect(rect, depth == 0 ? HeaderBg : SubHeaderBg);
        }

        public static void Separator()
        {
            var r = GUILayoutUtility.GetRect(1f, 1f, GUILayout.ExpandWidth(true));
            if (Event.current.type == EventType.Repaint) EditorGUI.DrawRect(r, LineColor);
        }

        /// <summary>Mở một thanh ngang có nền (toolbar trên/dưới, dải tóm tắt).</summary>
        public static Rect BeginBar()
        {
            var rect = EditorGUILayout.BeginHorizontal(Bar);
            if (Event.current.type == EventType.Repaint) EditorGUI.DrawRect(rect, HeaderBg);
            return rect;
        }

        public static void EndBar() => EditorGUILayout.EndHorizontal();

        public static GUIContent Icon(string name, string text, string tooltip)
        {
            var c = new GUIContent(EditorGUIUtility.IconContent(name)) { text = text, tooltip = tooltip };
            return c;
        }

        /// <summary>Thanh tab lớn ở đầu cửa sổ.</summary>
        public static int Tabs(int selected, string[] tabs)
        {
            GUILayout.Space(6);
            using (new EditorGUILayout.HorizontalScope())
            {
                GUILayout.Space(8);
                selected = GUILayout.Toolbar(selected, tabs, GUILayout.Height(26));
                GUILayout.Space(8);
            }
            GUILayout.Space(6);
            return selected;
        }
    }
}
