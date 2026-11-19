using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace ModuleManager
{
    /// <summary>
    /// Soạn module ở project nguồn: tạo manifest mới, soát file chưa thuộc module, thêm path vào module có sẵn.
    /// Chỉ dùng System.IO (không AssetDatabase) nên test được bằng thư mục tạm.
    /// Mọi "project path" dạng "Assets/..." với dấu '/'.
    /// </summary>
    public static class ModuleAuthoring
    {
        /// <summary>Đường dẫn từ thư mục <paramref name="fromDir"/> tới <paramref name="toPath"/> (vd "../Toppic/Popup"). Giống nhau thì trả ".".</summary>
        public static string RelativePath(string fromDir, string toPath)
        {
            var from = Split(fromDir);
            var to = Split(toPath);
            var common = 0;
            while (common < from.Length && common < to.Length && from[common] == to[common]) common++;

            var parts = Enumerable.Repeat("..", from.Length - common).Concat(to.Skip(common)).ToArray();
            return parts.Length == 0 ? "." : string.Join("/", parts);
        }

        private static string[] Split(string p) =>
            ModuleFiles.Norm(p).Split(new[] { '/' }, StringSplitOptions.RemoveEmptyEntries);

        /// <summary>Dựng manifest cho các path đã chọn. Id/tên lấy từ path đầu tiên; version 1.0.0, chưa có phụ thuộc.</summary>
        public static ModuleManifest CreateManifest(string manifestFolder, IList<string> projectPaths, string idPrefix)
        {
            if (projectPaths == null || projectPaths.Count == 0) throw new ArgumentException("Chưa chọn thư mục/file nào cho module");

            var first = Split(projectPaths[0]);
            var name = first[first.Length - 1];
            return new ModuleManifest
            {
                id = (idPrefix ?? "") + Sanitize(name),
                displayName = name,
                category = first.Length >= 2 ? first[first.Length - 2] : "",
                version = "1.0.0",
                paths = projectPaths.Select(p => RelativePath(manifestFolder, p)).ToArray(),
                dependencies = Array.Empty<ModuleDependency>(),
                packages = Array.Empty<PackageDependency>()
            };
        }

        /// <summary>Chữ thường, ký tự ngoài [a-z0-9.-] đổi thành '-'.</summary>
        private static string Sanitize(string s)
        {
            var sb = new StringBuilder();
            foreach (var c in s.ToLowerInvariant())
                sb.Append(c >= 'a' && c <= 'z' || c >= '0' && c <= '9' || c == '.' || c == '-' ? c : '-');
            return sb.ToString();
        }

        /// <summary>Ghi <c>&lt;manifestFolder&gt;/&lt;id&gt;.module.json</c>, trả project path. Đã có file thì ném lỗi.</summary>
        public static string WriteNewManifest(string projectRoot, string manifestFolder, ModuleManifest m)
        {
            var projectPath = ModuleFiles.Norm(manifestFolder).TrimEnd('/') + "/" + m.id + ModuleManifest.FileSuffix;
            var full = Path.Combine(projectRoot, projectPath);
            if (File.Exists(full)) throw new InvalidOperationException($"Đã có {projectPath}");
            Directory.CreateDirectory(Path.GetDirectoryName(full));
            File.WriteAllText(full, m.ToJson());
            return projectPath;
        }

        /// <summary>
        /// Liệt kê đường dẫn dưới <paramref name="auditRoot"/> không thuộc module nào. Thư mục là tổ tiên của path module
        /// thì đi vào trong; thư mục không liên quan thì báo nguyên thư mục.
        /// </summary>
        public static List<string> FindUncovered(string projectRoot, string auditRoot, IEnumerable<InstalledModule> modules)
        {
            var owned = modules.SelectMany(m => m.Paths.Append(m.ManifestPath)).ToList();
            var result = new List<string>();
            auditRoot = ModuleFiles.Norm(auditRoot).TrimEnd('/');
            if (!Directory.Exists(Path.Combine(projectRoot, auditRoot)) || IsCovered(auditRoot, owned)) return result;

            void Visit(string dir)
            {
                var full = Path.Combine(projectRoot, dir);
                var children = Directory.GetFileSystemEntries(full)
                    .Select(e => (name: Path.GetFileName(e), isDir: Directory.Exists(e)))
                    .Where(c => !ModuleFiles.IsIgnored(c.name) && !c.name.EndsWith(".meta", StringComparison.OrdinalIgnoreCase) &&
                                !c.name.EndsWith(ModuleManifest.FileSuffix, StringComparison.OrdinalIgnoreCase))
                    .OrderBy(c => c.name, StringComparer.Ordinal);
                foreach (var (name, isDir) in children)
                {
                    var p = dir + "/" + name;
                    if (IsCovered(p, owned)) continue;
                    if (isDir && owned.Any(o => o.StartsWith(p + "/", StringComparison.Ordinal))) Visit(p);
                    else result.Add(p);
                }
            }

            Visit(auditRoot);
            return result;
        }

        private static bool IsCovered(string p, List<string> owned) =>
            owned.Any(o => p == o || p.StartsWith(o + "/", StringComparison.Ordinal));

        /// <summary>Thêm <paramref name="projectPath"/> vào paths của module rồi ghi lại manifest. Trùng hoặc chồng path đã có thì ném lỗi.</summary>
        public static void AddPath(string projectRoot, InstalledModule m, string projectPath)
        {
            projectPath = ModuleFiles.Norm(projectPath).TrimEnd('/');
            var hit = m.Paths.FirstOrDefault(p => ModuleFiles.Overlaps(p, projectPath));
            if (hit != null) throw new InvalidOperationException($"'{projectPath}' trùng hoặc chồng lên '{hit}' đã có trong {m.Id}");

            var rel = RelativePath(ModuleFiles.DirOf(m.ManifestPath), projectPath);
            ModuleFiles.ResolveRelative(ModuleFiles.DirOf(m.ManifestPath), rel); // phải nằm trong Assets/
            m.Manifest.paths = m.Manifest.paths.Append(rel).ToArray();
            File.WriteAllText(Path.Combine(projectRoot, m.ManifestPath), m.Manifest.ToJson());
            m.Paths.Add(projectPath);
        }
    }
}
