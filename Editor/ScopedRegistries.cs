using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEngine;

namespace ModuleManager
{
    /// <summary>
    /// Thêm scoped registry vào Packages/manifest.json. JsonUtility không đọc được "dependencies" (dictionary),
    /// nên chỉ đọc khối "scopedRegistries" rồi thay đúng đoạn text của nó; phần còn lại của file giữ nguyên.
    /// Không bao giờ gỡ registry: để lại không hại gì, gỡ nhầm thì package khác hỏng.
    /// </summary>
    public static class ScopedRegistries
    {
        // shortcut: chỉ giữ name/url/scopes; field lạ của registry có sẵn (vd overrideBuiltIns) mất khi Manager phải viết lại khối này.
        [Serializable]
        public class Registry
        {
            public string name;
            public string url;
            public string[] scopes;
        }

        [Serializable]
        private class Wrapper
        {
            public Registry[] scopedRegistries;
        }

        private const string Key = "scopedRegistries";

        public static Registry[] Read(string json) =>
            JsonUtility.FromJson<Wrapper>(json)?.scopedRegistries ?? Array.Empty<Registry>();

        /// <summary>
        /// Trả về manifest.json đã có registry <paramref name="url"/> chứa các scope cần; không cần đổi thì trả về đúng chuỗi cũ.
        /// Scope đã thuộc registry khác thì bỏ qua (UPM báo lỗi nếu một scope nằm ở hai registry).
        /// </summary>
        public static string Ensure(string json, string url, IEnumerable<string> scopes)
        {
            url = url.Trim();
            var list = Read(json).ToList();
            var need = scopes.Select(s => s.Trim())
                .Where(s => !list.Any(r => !SameUrl(r.url, url) && (r.scopes ?? Array.Empty<string>()).Contains(s)))
                .ToArray();
            var reg = list.FirstOrDefault(r => SameUrl(r.url, url));
            if (reg == null)
            {
                if (need.Length == 0) return json;
                list.Add(new Registry { name = NameOf(url), url = url, scopes = need });
            }
            else
            {
                var merged = (reg.scopes ?? Array.Empty<string>()).Union(need).ToArray();
                if (merged.Length == (reg.scopes?.Length ?? 0)) return json;
                reg.scopes = merged;
            }

            var block = JsonUtility.ToJson(new Wrapper { scopedRegistries = list.ToArray() }, true);
            var open = block.IndexOf('{');
            var member = block.Substring(open + 1, block.LastIndexOf('}') - open - 1).Trim(); // "scopedRegistries": [...]

            string result;
            if (FindMember(json, Key, out var start, out var end))
                result = json.Substring(0, start) + member + json.Substring(end);
            else
            {
                var brace = json.IndexOf('{');
                if (brace < 0) throw new FormatException("Packages/manifest.json không phải object JSON");
                var empty = json.Substring(brace + 1).Trim() == "}";
                result = json.Insert(brace + 1, "\n  " + member + (empty ? "\n" : ","));
            }

            // Không ghi ra thứ mình không đọc lại được: hỏng ở đây thì manifest.json của project vẫn nguyên.
            var check = Read(result).FirstOrDefault(r => SameUrl(r.url, url));
            if (check == null || need.Except(check.scopes ?? Array.Empty<string>()).Any())
                throw new FormatException("Không chèn được scoped registry vào Packages/manifest.json");
            return result;
        }

        /// <summary>Ghi registry của các package cần vào Packages/manifest.json. Trả về các registry đã thêm/đổi.</summary>
        public static List<string> EnsureFile(string projectRoot, IEnumerable<PackageDependency> packages)
        {
            var path = Path.Combine(projectRoot, "Packages", "manifest.json");
            var text = File.ReadAllText(path);
            var changed = new List<string>();
            foreach (var p in packages.Where(p => p.HasRegistry))
            {
                var next = Ensure(text, p.registry, p.scopes);
                if (ReferenceEquals(next, text)) continue;
                text = next;
                changed.Add($"{p.registry} ({string.Join(", ", p.scopes)})");
            }
            if (changed.Count > 0) File.WriteAllText(path, text);
            return changed;
        }

        private static bool SameUrl(string a, string b) =>
            string.Equals(a?.Trim().TrimEnd('/'), b?.Trim().TrimEnd('/'), StringComparison.OrdinalIgnoreCase);

        private static string NameOf(string url) =>
            Uri.TryCreate(url, UriKind.Absolute, out var u) ? u.Host : url;

        // Tìm member cấp 1 "key": value; start = dấu " mở của key, end = ngay sau value.
        private static bool FindMember(string json, string key, out int start, out int end)
        {
            start = end = -1;
            var depth = 0;
            for (var i = 0; i < json.Length; i++)
            {
                var c = json[i];
                if (c == '"')
                {
                    var close = SkipString(json, i);
                    if (depth == 1 && close - i - 1 == key.Length && string.CompareOrdinal(json, i + 1, key, 0, key.Length) == 0)
                    {
                        var j = SkipSpace(json, close + 1);
                        if (j < json.Length && json[j] == ':')
                        {
                            start = i;
                            end = SkipValue(json, SkipSpace(json, j + 1));
                            return true;
                        }
                    }
                    i = close;
                }
                else if (c == '{' || c == '[') depth++;
                else if (c == '}' || c == ']') depth--;
            }
            return false;
        }

        private static int SkipSpace(string s, int i)
        {
            while (i < s.Length && char.IsWhiteSpace(s[i])) i++;
            return i;
        }

        private static int SkipString(string s, int i)
        {
            for (i++; i < s.Length; i++)
            {
                if (s[i] == '\\') i++;
                else if (s[i] == '"') return i;
            }
            throw new FormatException("Packages/manifest.json hỏng: chuỗi không đóng");
        }

        private static int SkipValue(string s, int i)
        {
            if (i >= s.Length) throw new FormatException("Packages/manifest.json hỏng: thiếu giá trị");
            if (s[i] == '"') return SkipString(s, i) + 1;
            if (s[i] != '[' && s[i] != '{')
            {
                while (i < s.Length && s[i] != ',' && s[i] != '}' && !char.IsWhiteSpace(s[i])) i++;
                return i;
            }
            var depth = 0;
            for (; i < s.Length; i++)
            {
                var c = s[i];
                if (c == '"') i = SkipString(s, i);
                else if (c == '[' || c == '{') depth++;
                else if ((c == ']' || c == '}') && --depth == 0) return i + 1;
            }
            throw new FormatException("Packages/manifest.json hỏng: ngoặc không đóng");
        }
    }
}
