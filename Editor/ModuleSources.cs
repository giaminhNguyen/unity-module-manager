using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using UnityEditor;
using UnityEngine;

namespace ModuleManager
{
    /// <summary>Một version đã phát hành của một module.</summary>
    public sealed class ModuleRelease
    {
        public ModuleManifest Manifest;
        public string Changelog;
        public string PublishedAt;

        /// <summary>Địa chỉ file zip: URL API với GitHub, đường dẫn file với nguồn thư mục.</summary>
        public string AssetUrl;

        public string AssetName;
        public long Size;

        public string Id => Manifest.id;
        public string Version => Manifest.version;
    }

    /// <summary>Nơi chứa bản phát hành. Thêm kiểu nguồn mới = thêm một class implement interface này.</summary>
    public interface IModuleSource
    {
        Task<List<ModuleRelease>> FetchAsync();

        /// <summary>Tải zip về <paramref name="destDir"/>, trả đường dẫn file zip.</summary>
        Task<string> DownloadAsync(ModuleRelease release, string destDir);

        Task PublishAsync(ModuleManifest manifest, string changelog, string zipPath);
    }

    [Serializable]
    public class SourceConfig
    {
        public const string GitHub = "GitHub";
        public const string Folder = "Folder";

        [Tooltip("Tên hiển thị của nguồn.")]
        public string name;

        [Tooltip("Kiểu nguồn: GitHub (release của repo) hoặc Folder (thư mục trên máy, dùng để thử offline).")]
        public string type = GitHub;

        [Tooltip("GitHub: owner/repo. Folder: đường dẫn thư mục (tuyệt đối hoặc tương đối root project).")]
        public string location;
    }

    /// <summary>Cấu hình theo project, lưu ở ProjectSettings/ModuleManagerSettings.json (commit cùng project).</summary>
    [Serializable]
    public class ManagerSettings
    {
        public const string FilePath = "ProjectSettings/ModuleManagerSettings.json";

        [Tooltip("Các nguồn module project này dùng.")]
        public List<SourceConfig> sources = new List<SourceConfig>();

        [Tooltip("Bật ở project nguồn (nơi viết code module): khoá Cài/Gỡ để không ghi đè source, mở Publisher.")]
        public bool devMode;

        [Header("Cấu hình dev (soạn module)")]
        [Tooltip("Project path (vd Assets/Modules) nơi tạo file manifest mới khi bấm Tạo module.")]
        public string manifestFolder;

        [Tooltip("Tiền tố id của module mới (vd myframework.). Id = tiền tố + tên thư mục đầu tiên của module.")]
        public string idPrefix;

        [Tooltip("Project path để soát file chưa thuộc module nào. Để trống = thư mục cha của manifestFolder.")]
        public string auditRoot;

        /// <summary>Đọc cấu hình; chưa có file hoặc file hỏng thì cấu hình trống.</summary>
        public static ManagerSettings Load(string projectRoot)
        {
            var path = Path.Combine(projectRoot, FilePath);
            ManagerSettings s = null;
            if (File.Exists(path))
            {
                try { s = JsonUtility.FromJson<ManagerSettings>(File.ReadAllText(path)); }
                catch (Exception e) { Debug.LogWarning($"[ModuleManager] Đọc cấu hình lỗi, dùng cấu hình trống: {e.Message}"); }
            }
            s ??= new ManagerSettings();
            s.sources ??= new List<SourceConfig>();
            return s;
        }

        public void Save(string projectRoot) =>
            File.WriteAllText(Path.Combine(projectRoot, FilePath), JsonUtility.ToJson(this, true));
    }

    public static class SourceFactory
    {
        public static IModuleSource Create(SourceConfig c, string projectRoot)
        {
            if (c == null || string.IsNullOrWhiteSpace(c.location)) throw new InvalidOperationException("Nguồn chưa khai location");
            switch (c.type)
            {
                case SourceConfig.GitHub: return new GitHubSource(c.location.Trim(), Tokens.Get(c));
                case SourceConfig.Folder: return new FolderSource(Path.GetFullPath(Path.Combine(projectRoot, c.location.Trim())));
                default: throw new InvalidOperationException($"Kiểu nguồn '{c.type}' không hỗ trợ");
            }
        }
    }

    /// <summary>Token lưu theo máy (EditorPrefs), không nằm trong project để không lọt vào git.</summary>
    public static class Tokens
    {
        private static string Key(SourceConfig c) => "ModuleManager.Token." + c.location?.Trim();
        public static string Get(SourceConfig c) => c == null ? "" : EditorPrefs.GetString(Key(c), "");
        public static void Set(SourceConfig c, string token) => EditorPrefs.SetString(Key(c), token ?? "");
    }

    /// <summary>
    /// Nguồn là một thư mục: mỗi version gồm &lt;id&gt;-&lt;version&gt;.zip và &lt;id&gt;-&lt;version&gt;.release.json.
    /// Dùng để thử toàn bộ luồng publish/cài mà không cần mạng, hoặc chia sẻ qua ổ mạng.
    /// </summary>
    public sealed class FolderSource : IModuleSource
    {
        [Serializable]
        private class Entry
        {
            public ModuleManifest manifest;
            public string changelog;
            public string publishedAt;
            public string zip;
        }

        private readonly string _dir;

        public FolderSource(string dir) => _dir = dir;

        public Task<List<ModuleRelease>> FetchAsync()
        {
            var list = new List<ModuleRelease>();
            if (!Directory.Exists(_dir)) return Task.FromResult(list);
            foreach (var f in Directory.GetFiles(_dir, "*.release.json"))
            {
                try
                {
                    var e = JsonUtility.FromJson<Entry>(File.ReadAllText(f));
                    var zip = Path.Combine(_dir, e.zip);
                    list.Add(new ModuleRelease
                    {
                        Manifest = ModuleManifest.Parse(JsonUtility.ToJson(e.manifest)),
                        Changelog = e.changelog,
                        PublishedAt = e.publishedAt,
                        AssetUrl = zip,
                        AssetName = e.zip,
                        Size = File.Exists(zip) ? new FileInfo(zip).Length : 0
                    });
                }
                catch (Exception ex)
                {
                    Debug.LogWarning($"[ModuleManager] Bỏ qua {Path.GetFileName(f)}: {ex.Message}");
                }
            }
            return Task.FromResult(list);
        }

        public Task<string> DownloadAsync(ModuleRelease release, string destDir)
        {
            if (!File.Exists(release.AssetUrl)) throw new FileNotFoundException("Không thấy file zip", release.AssetUrl);
            return Task.FromResult(release.AssetUrl);
        }

        public Task PublishAsync(ModuleManifest manifest, string changelog, string zipPath)
        {
            var name = $"{manifest.id}-{manifest.version}";
            var json = Path.Combine(_dir, name + ".release.json");
            if (File.Exists(json)) throw new InvalidOperationException($"{manifest.id} {manifest.version} đã có trong {_dir}");
            Directory.CreateDirectory(_dir);
            File.Copy(zipPath, Path.Combine(_dir, name + ".zip"), true);
            File.WriteAllText(json, JsonUtility.ToJson(new Entry
            {
                manifest = manifest,
                changelog = changelog ?? "",
                publishedAt = DateTime.UtcNow.ToString("u"),
                zip = name + ".zip"
            }, true));
            return Task.CompletedTask;
        }
    }
}
