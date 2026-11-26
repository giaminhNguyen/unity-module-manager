using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEditorInternal;
using UnityEngine;

namespace ModuleManager
{
    /// <summary>
    /// Dòng trạng thái token của nguồn GitHub. Token không bao giờ hiện lại hay sửa tại chỗ:
    /// chỉ thay hẳn bằng token mới qua <see cref="TokenUpdateWindow"/>, hoặc xoá.
    /// </summary>
    internal static class TokenField
    {
        public const string Valid = "hợp lệ (đọc được repo)";

        // Kết quả Kiểm tra gần nhất theo repo, chỉ sống trong phiên Editor.
        private static readonly Dictionary<string, string> Checked = new Dictionary<string, string>();
        private static readonly HashSet<string> Checking = new HashSet<string>();

        private static string Key(SourceConfig s) => s.location?.Trim() ?? "";

        public static void MarkChecked(SourceConfig s, string result) => Checked[Key(s)] = result;

        public static void Draw(SourceConfig source, string hint)
        {
            if (source?.type != SourceConfig.GitHub) return;
            var token = Tokens.Get(source);
            var has = !string.IsNullOrEmpty(token);
            var key = Key(source);
            var busy = Checking.Contains(key);

            using (new EditorGUILayout.HorizontalScope())
            {
                EditorGUILayout.PrefixLabel(new GUIContent("Token GitHub", hint));
                var state = has ? "Đã lưu " + Mask(token) : "Chưa có token";
                if (busy) state += " · đang kiểm tra…";
                else if (has && Checked.TryGetValue(key, out var result)) state += " · " + result;
                GUILayout.Label(new GUIContent(state, hint), EditorStyles.miniLabel);
                GUILayout.FlexibleSpace();

                using (new EditorGUI.DisabledScope(!has || busy))
                {
                    if (GUILayout.Button(new GUIContent("Kiểm tra", "Gọi GitHub 1 lần để xem token còn đọc được repo không"),
                            EditorStyles.miniButton, GUILayout.Width(64)))
                        Check(source, token);
                }
                if (GUILayout.Button(new GUIContent(has ? "Cập nhật token" : "Nhập token", "Nhập token mới, thay hẳn token cũ sau khi kiểm tra"),
                        EditorStyles.miniButton, GUILayout.Width(100)))
                    TokenUpdateWindow.Open(source);
                using (new EditorGUI.DisabledScope(!has || busy))
                {
                    if (GUILayout.Button("Xoá", EditorStyles.miniButton, GUILayout.Width(40)) &&
                        EditorUtility.DisplayDialog("Xoá token", $"Xoá token đã lưu của {key} trên máy này?", "Xoá", "Huỷ"))
                    {
                        Tokens.Set(source, "");
                        Checked.Remove(key);
                    }
                }
            }
        }

        // Token ngắn thì không lộ ký tự nào.
        private static string Mask(string token) => token.Length >= 12 ? "••••" + token.Substring(token.Length - 4) : "••••";

        private static async void Check(SourceConfig source, string token)
        {
            var key = Key(source);
            Checking.Add(key);
            string error;
            try { error = await new GitHubSource(key, token).CheckAccessAsync(); }
            catch (Exception e) { error = e.Message; }
            Checking.Remove(key);
            Checked[key] = error ?? Valid;
            InternalEditorUtility.RepaintAllViews();
        }
    }

    /// <summary>Hộp thoại nhập token mới. Chỉ lưu khi token mới đọc được repo; Huỷ/đóng/lỗi thì giữ token cũ.</summary>
    internal sealed class TokenUpdateWindow : EditorWindow
    {
        private SourceConfig _source;
        private string _input = "";
        private string _error;
        private bool _busy;

        public static void Open(SourceConfig source)
        {
            var w = CreateInstance<TokenUpdateWindow>();
            w._source = source.Clone();
            w.titleContent = new GUIContent("Token GitHub");
            w.minSize = w.maxSize = new Vector2(440, 150);
            w.ShowUtility();
        }

        private void OnGUI()
        {
            // Sau domain reload _source không được lưu lại: đóng luôn, token đang gõ cũng không giữ.
            if (_source == null)
            {
                Close();
                return;
            }

            EditorGUILayout.LabelField("Repo", _source.location);
            EditorGUILayout.HelpBox("Token mới thay hẳn token cũ, chỉ sau khi kiểm tra đọc được repo. " +
                                    "Huỷ, đóng cửa sổ hoặc kiểm tra lỗi thì token cũ giữ nguyên.", MessageType.None);
            using (new EditorGUI.DisabledScope(_busy))
                _input = EditorGUILayout.PasswordField("Token mới", _input);
            if (!string.IsNullOrEmpty(_error)) EditorGUILayout.HelpBox(_error, MessageType.Error);

            GUILayout.FlexibleSpace();
            using (new EditorGUILayout.HorizontalScope())
            {
                GUILayout.FlexibleSpace();
                if (GUILayout.Button("Huỷ", GUILayout.Width(80))) Close();
                using (new EditorGUI.DisabledScope(_busy || string.IsNullOrWhiteSpace(_input)))
                    if (GUILayout.Button(_busy ? "Đang kiểm tra…" : "Kiểm tra & lưu", GUILayout.Width(120)))
                        Save();
            }
        }

        private async void Save()
        {
            _busy = true;
            _error = null;
            var token = _input.Trim();
            string error;
            try { error = await new GitHubSource(_source.location.Trim(), token).CheckAccessAsync(); }
            catch (Exception e) { error = e.Message; }

            if (this == null) return; // Đã đóng cửa sổ trong lúc chờ = huỷ, không lưu.
            _busy = false;
            if (error != null)
            {
                _error = error + ". Token cũ vẫn giữ nguyên.";
                Repaint();
                return;
            }

            Tokens.Set(_source, token);
            TokenField.MarkChecked(_source, TokenField.Valid);
            InternalEditorUtility.RepaintAllViews();
            Close();
        }
    }
}
