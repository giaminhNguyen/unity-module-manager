using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.Networking;

namespace ModuleManager
{
    /// <summary>
    /// Nguồn là GitHub Releases của một repo. Mỗi version của module là một release:
    /// tag &lt;id&gt;/&lt;version&gt;, file &lt;id&gt;-&lt;version&gt;.zip, body = changelog + manifest (base64, nằm trong
    /// comment HTML nên GitHub không hiện). Nhờ vậy chỉ cần một request liệt kê release là đủ dựng danh mục,
    /// không phải giữ file catalog riêng. Repo public không cần token; repo private cần token quyền đọc
    /// (publish cần quyền ghi Contents).
    /// </summary>
    public sealed class GitHubSource : IModuleSource
    {
        private const string Api = "https://api.github.com/repos/";
        private const string Marker = "<!-- module-manifest:";
        private const string MarkerEnd = "-->";
        private const int PageSize = 100;

        private readonly string _repo;
        private readonly string _token;

        public GitHubSource(string repo, string token)
        {
            if (!Regex.IsMatch(repo ?? "", @"^[\w.-]+/[\w.-]+$"))
                throw new ArgumentException($"Repo GitHub phải dạng owner/repo, nhận '{repo}'");
            _repo = repo;
            _token = token;
        }

        // ───────────────────────── DTO của GitHub API ─────────────────────────

        [Serializable]
        private class GhAsset
        {
            public long id;
            public string name;
            public string url;
            public long size;
        }

        [Serializable]
        private class GhRelease
        {
            public long id;
            public string tag_name;
            public string body;
            public bool draft;
            public string published_at;
            public string upload_url;
            public GhAsset[] assets;
        }

        [Serializable]
        private class GhList
        {
            public GhRelease[] items;
        }

        [Serializable]
        private class GhCreate
        {
            public string tag_name;
            public string name;
            public string body;
            public bool draft;
            public bool prerelease;
        }

        // ───────────────────────── Đọc ─────────────────────────

        public async Task<List<ModuleRelease>> FetchAsync()
        {
            var all = new List<ModuleRelease>();
            for (var page = 1; page <= 100; page++)
            {
                var json = await GetText($"{Api}{_repo}/releases?per_page={PageSize}&page={page}");
                all.AddRange(ParseReleases(json, out var raw));
                if (raw < PageSize) break;
            }
            return all;
        }

        /// <summary>Đổi JSON danh sách release thành ModuleRelease. Release không phải module (không có manifest) bị bỏ qua.</summary>
        public static List<ModuleRelease> ParseReleases(string json, out int rawCount)
        {
            var list = JsonUtility.FromJson<GhList>("{\"items\":" + json + "}")?.items ?? Array.Empty<GhRelease>();
            rawCount = list.Length;
            var result = new List<ModuleRelease>();
            foreach (var r in list)
            {
                if (r.draft || !TryParseBody(r.body, out var manifest, out var changelog)) continue;
                var asset = r.assets?.FirstOrDefault(a => a.name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase));
                if (asset == null) continue;
                result.Add(new ModuleRelease
                {
                    Manifest = manifest,
                    Changelog = changelog,
                    PublishedAt = r.published_at,
                    AssetUrl = asset.url,
                    AssetName = asset.name,
                    Size = asset.size
                });
            }
            return result;
        }

        public static string BuildBody(ModuleManifest m, string changelog) =>
            (string.IsNullOrWhiteSpace(changelog) ? "_Không có ghi chú._" : changelog.Trim()) + "\n\n" +
            Marker + Convert.ToBase64String(Encoding.UTF8.GetBytes(m.ToJson())) + MarkerEnd;

        public static bool TryParseBody(string body, out ModuleManifest manifest, out string changelog)
        {
            manifest = null;
            changelog = body ?? "";
            if (string.IsNullOrEmpty(body)) return false;
            var i = body.LastIndexOf(Marker, StringComparison.Ordinal);
            if (i < 0) return false;
            var start = i + Marker.Length;
            var j = body.IndexOf(MarkerEnd, start, StringComparison.Ordinal);
            if (j < 0) return false;
            try
            {
                manifest = ModuleManifest.Parse(Encoding.UTF8.GetString(Convert.FromBase64String(body.Substring(start, j - start).Trim())));
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[ModuleManager] Release có manifest hỏng, bỏ qua: {e.Message}");
                return false;
            }
            changelog = body.Substring(0, i).Trim();
            return true;
        }

        public static string TagOf(ModuleManifest m) => $"{m.id}/{m.version}";

        public async Task<string> DownloadAsync(ModuleRelease release, string destDir)
        {
            byte[] data;
            using (var r = Request("GET", release.AssetUrl, "application/octet-stream", 600))
            {
                // Tự đi theo redirect để token không bị gửi sang host lưu file (URL đó đã ký sẵn).
                r.redirectLimit = 0;
                await Send(r);
                var location = r.GetResponseHeader("Location");
                if (Ok(r)) data = r.downloadHandler.data;
                else if (r.responseCode >= 300 && r.responseCode < 400 && !string.IsNullOrEmpty(location))
                    data = await GetBytes(location, false, release.AssetName);
                else data = await GetBytes(release.AssetUrl, true, release.AssetName);
            }

            Directory.CreateDirectory(destDir);
            var path = Path.Combine(destDir, release.AssetName);
            File.WriteAllBytes(path, data);
            return path;
        }

        // ───────────────────────── Publish ─────────────────────────

        public async Task PublishAsync(ModuleManifest manifest, string changelog, string zipPath)
        {
            var tag = TagOf(manifest);
            var create = JsonUtility.ToJson(new GhCreate
            {
                tag_name = tag,
                name = $"{manifest.Title} {manifest.version}",
                body = BuildBody(manifest, changelog)
            });

            GhRelease rel;
            using (var r = Request("POST", $"{Api}{_repo}/releases", "application/vnd.github+json", 60))
            {
                r.uploadHandler = new UploadHandlerRaw(Encoding.UTF8.GetBytes(create)) { contentType = "application/json" };
                await Send(r);
                if (!Ok(r))
                    throw new Exception(Fail(r, $"Tạo release {tag}") + (r.responseCode == 422 ? " — version này có thể đã publish rồi" : ""));
                rel = JsonUtility.FromJson<GhRelease>(r.downloadHandler.text);
            }

            var uploadUrl = rel.upload_url;
            var brace = uploadUrl.IndexOf('{');
            if (brace >= 0) uploadUrl = uploadUrl.Substring(0, brace);
            var assetName = $"{manifest.id}-{manifest.version}.zip";

            using (var r = Request("POST", uploadUrl + "?name=" + UnityWebRequest.EscapeURL(assetName), "application/vnd.github+json", 600))
            {
                r.uploadHandler = new UploadHandlerRaw(File.ReadAllBytes(zipPath)) { contentType = "application/zip" };
                await Send(r);
                if (Ok(r)) return;
                var error = Fail(r, "Upload " + assetName);
                // Không để release thiếu file nằm lại: Manager sẽ thấy version mà không tải được.
                await Rollback(rel.id, tag);
                throw new Exception(error + " — đã xoá release dở dang, publish lại được");
            }
        }

        private async Task Rollback(long releaseId, string tag)
        {
            using (var r = Request("DELETE", $"{Api}{_repo}/releases/{releaseId}", "application/vnd.github+json", 60))
                await Send(r);
            using (var r = Request("DELETE", $"{Api}{_repo}/git/refs/tags/{tag}", "application/vnd.github+json", 60))
                await Send(r);
        }

        // ───────────────────────── HTTP ─────────────────────────

        private UnityWebRequest Request(string method, string url, string accept, int timeout, bool auth = true)
        {
            var r = new UnityWebRequest(url, method) { downloadHandler = new DownloadHandlerBuffer(), timeout = timeout };
            r.SetRequestHeader("Accept", accept);
            r.SetRequestHeader("User-Agent", "ModuleManager");
            r.SetRequestHeader("X-GitHub-Api-Version", "2022-11-28");
            if (auth && !string.IsNullOrEmpty(_token)) r.SetRequestHeader("Authorization", "Bearer " + _token);
            return r;
        }

        private async Task<string> GetText(string url)
        {
            using (var r = Request("GET", url, "application/vnd.github+json", 60))
            {
                await Send(r);
                if (!Ok(r)) throw new Exception(Fail(r, "Đọc danh sách release"));
                return r.downloadHandler.text;
            }
        }

        private async Task<byte[]> GetBytes(string url, bool auth, string what)
        {
            using (var r = Request("GET", url, "application/octet-stream", 600, auth))
            {
                await Send(r);
                if (!Ok(r)) throw new Exception(Fail(r, "Tải " + what));
                return r.downloadHandler.data;
            }
        }

        private static Task Send(UnityWebRequest r)
        {
            var tcs = new TaskCompletionSource<bool>();
            r.SendWebRequest().completed += _ => tcs.TrySetResult(true);
            return tcs.Task;
        }

        private static bool Ok(UnityWebRequest r) => r.responseCode >= 200 && r.responseCode < 300;

        private string Fail(UnityWebRequest r, string what)
        {
            var text = r.downloadHandler?.text ?? "";
            if (text.Length > 300) text = text.Substring(0, 300) + "…";
            var hint = r.responseCode == 401 || r.responseCode == 403 || r.responseCode == 404
                ? $" (kiểm tra token và quyền của nó trên repo {_repo})"
                : "";
            return $"{what}: HTTP {r.responseCode} {r.error} {text}{hint}";
        }
    }
}
