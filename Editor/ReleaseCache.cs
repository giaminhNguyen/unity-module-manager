using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using UnityEngine;

namespace ModuleManager
{
    /// <summary>
    /// Lưu danh sách bản phát hành đã Fetch vào Library/ModuleManager/Cache (mỗi nguồn một file), để domain reload
    /// sau khi cài module không làm mất danh sách. Library/ không vào git; cache hỏng thì coi như chưa có.
    /// </summary>
    public static class ReleaseCache
    {
        [Serializable]
        private class Entry
        {
            public ModuleManifest manifest;
            public string changelog;
            public string publishedAt;
            public string assetUrl;
            public string assetName;
            public long size;
        }

        [Serializable]
        private class Data
        {
            public string fetchedAt;
            public List<Entry> releases = new List<Entry>();
        }

        public static string PathOf(string projectRoot, SourceConfig source)
        {
            var raw = source.type + "_" + (source.location ?? "").Trim();
            var key = new string(raw.Select(c => char.IsLetterOrDigit(c) || c == '.' || c == '-' ? c : '_').ToArray());
            return Path.Combine(projectRoot, "Library", "ModuleManager", "Cache", key + ".json");
        }

        public static void Save(string projectRoot, SourceConfig source, IEnumerable<ModuleRelease> releases, DateTime fetchedAt)
        {
            var data = new Data
            {
                fetchedAt = fetchedAt.ToString("o", CultureInfo.InvariantCulture),
                releases = releases.Select(r => new Entry
                {
                    manifest = r.Manifest, changelog = r.Changelog, publishedAt = r.PublishedAt,
                    assetUrl = r.AssetUrl, assetName = r.AssetName, size = r.Size
                }).ToList()
            };
            var path = PathOf(projectRoot, source);
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            File.WriteAllText(path, JsonUtility.ToJson(data));
        }

        /// <summary>Đọc cache của nguồn; chưa có hoặc hỏng thì trả null.</summary>
        public static List<ModuleRelease> Load(string projectRoot, SourceConfig source, out DateTime fetchedAt)
        {
            fetchedAt = default;
            var path = PathOf(projectRoot, source);
            if (!File.Exists(path)) return null;
            try
            {
                var data = JsonUtility.FromJson<Data>(File.ReadAllText(path));
                if (data?.releases == null) return null;
                // Qua Parse để manifest trong cache được kiểm tra như manifest tải từ nguồn.
                var list = data.releases.Select(e => new ModuleRelease
                {
                    Manifest = ModuleManifest.Parse(JsonUtility.ToJson(e.manifest)), Changelog = e.changelog,
                    PublishedAt = e.publishedAt, AssetUrl = e.assetUrl, AssetName = e.assetName, Size = e.size
                }).ToList();
                DateTime.TryParse(data.fetchedAt, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out fetchedAt);
                return list;
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[ModuleManager] Bỏ qua cache hỏng {path}: {e.Message}");
                return null;
            }
        }
    }
}
