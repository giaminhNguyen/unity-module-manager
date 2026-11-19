using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace ModuleManager
{
    /// <summary>
    /// Thao tác file của module trên đĩa: quét manifest, gom file, đóng zip, giải nén, xoá.
    /// Chỉ dùng System.IO (không AssetDatabase) nên test được bằng thư mục tạm.
    /// Mọi đường dẫn "project path" đều dạng "Assets/..." với dấu '/'.
    /// </summary>
    public static class ModuleFiles
    {
        // ───────────────────────── Đường dẫn ─────────────────────────

        public static string Norm(string p) => p.Replace('\\', '/');

        /// <summary>Unity bỏ qua file/thư mục ẩn, đuôi ~, tên cvs, đuôi .tmp — Manager cũng bỏ qua.</summary>
        public static bool IsIgnored(string name) =>
            name.StartsWith(".", StringComparison.Ordinal) || name.EndsWith("~", StringComparison.Ordinal) ||
            name.Equals("cvs", StringComparison.OrdinalIgnoreCase) || name.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase);

        /// <summary>Hai đường dẫn trùng nhau hoặc cái này nằm trong cái kia.</summary>
        public static bool Overlaps(string a, string b) =>
            a == b || a.StartsWith(b + "/", StringComparison.Ordinal) || b.StartsWith(a + "/", StringComparison.Ordinal);

        /// <summary>Ghép đường dẫn tương đối vào thư mục gốc, xử lý "." và "..". Kết quả phải nằm trong Assets/.</summary>
        public static string ResolveRelative(string baseDir, string rel)
        {
            if (string.IsNullOrWhiteSpace(rel)) throw new FormatException("path rỗng");
            rel = Norm(rel.Trim());
            if (rel.StartsWith("/", StringComparison.Ordinal) || rel.Contains(":"))
                throw new FormatException($"path '{rel}' phải là đường dẫn tương đối");

            var parts = new List<string>(Norm(baseDir).Split(new[] { '/' }, StringSplitOptions.RemoveEmptyEntries));
            foreach (var seg in rel.Split('/'))
            {
                if (seg.Length == 0 || seg == ".") continue;
                if (seg == "..")
                {
                    if (parts.Count == 0) throw new FormatException($"path '{rel}' vượt ra ngoài project");
                    parts.RemoveAt(parts.Count - 1);
                }
                else parts.Add(seg);
            }

            var result = string.Join("/", parts);
            if (!result.StartsWith("Assets/", StringComparison.Ordinal))
                throw new FormatException($"path '{rel}' phải nằm trong Assets/");
            return result;
        }

        /// <summary>Đổi <c>paths</c> của manifest (tương đối thư mục manifest) thành project path.</summary>
        public static List<string> ResolvePaths(string manifestPath, ModuleManifest m) =>
            m.paths.Select(p => ResolveRelative(DirOf(manifestPath), p)).ToList();

        internal static string DirOf(string p)
        {
            var i = p.LastIndexOf('/');
            return i < 0 ? "" : p.Substring(0, i);
        }

        private static string Full(string root, string projectPath) => Path.GetFullPath(Path.Combine(root, projectPath));

        private static string Rel(string root, string full)
        {
            var r = Norm(Path.GetFullPath(root)).TrimEnd('/') + "/";
            return Norm(Path.GetFullPath(full)).Substring(r.Length).TrimEnd('/');
        }

        // ───────────────────────── Quét ─────────────────────────

        /// <summary>Tìm mọi *.module.json trong Assets/ (bỏ qua thư mục ẩn như .git).</summary>
        public static List<InstalledModule> Scan(string projectRoot)
        {
            var list = new List<InstalledModule>();
            var assets = Path.Combine(projectRoot, "Assets");
            if (!Directory.Exists(assets)) return list;
            Walk(assets, f =>
            {
                if (f.EndsWith(ModuleManifest.FileSuffix, StringComparison.OrdinalIgnoreCase))
                    list.Add(Load(projectRoot, Rel(projectRoot, f)));
            }, null);
            return list;
        }

        public static InstalledModule Load(string projectRoot, string manifestPath)
        {
            var m = new InstalledModule { ManifestPath = manifestPath };
            try
            {
                m.Manifest = ModuleManifest.Parse(File.ReadAllText(Full(projectRoot, manifestPath)));
                m.Paths = ResolvePaths(manifestPath, m.Manifest);
            }
            catch (Exception e)
            {
                m.Error = $"{manifestPath}: {e.Message}";
            }
            return m;
        }

        private static void Walk(string dir, Action<string> onFile, Action<string> onDir)
        {
            foreach (var f in Directory.GetFiles(dir))
                if (!IsIgnored(Path.GetFileName(f))) onFile?.Invoke(f);
            foreach (var d in Directory.GetDirectories(dir))
            {
                if (IsIgnored(Path.GetFileName(d))) continue;
                onDir?.Invoke(d);
                Walk(d, onFile, onDir);
            }
        }

        // ───────────────────────── Gom file để publish ─────────────────────────

        public sealed class FileSet
        {
            /// <summary>File của module kể cả .meta, không gồm file manifest.</summary>
            public readonly List<string> Owned = new List<string>();

            /// <summary>.meta của thư mục cha (dùng chung giữa các module) — chỉ ghi khi máy đích chưa có.</summary>
            public readonly List<string> Shared = new List<string>();

            public readonly List<string> Errors = new List<string>();
        }

        public static FileSet Collect(string projectRoot, InstalledModule m)
        {
            var set = new FileSet();
            var owned = new SortedSet<string>(StringComparer.Ordinal);

            void AddMeta(string path)
            {
                var meta = path + ".meta";
                if (File.Exists(Full(projectRoot, meta))) owned.Add(meta);
                else set.Errors.Add($"{m.Id}: thiếu {meta} — mở project bằng Unity để sinh .meta rồi publish lại");
            }

            void AddFile(string path)
            {
                if (path.EndsWith(".meta", StringComparison.OrdinalIgnoreCase))
                {
                    // .meta mồ côi (asset đã xoá) thì bỏ, Unity cũng tự xoá nó
                    var asset = path.Substring(0, path.Length - 5);
                    if (File.Exists(Full(projectRoot, asset)) || Directory.Exists(Full(projectRoot, asset))) owned.Add(path);
                    return;
                }
                owned.Add(path);
                AddMeta(path);
            }

            foreach (var p in m.Paths)
            {
                var full = Full(projectRoot, p);
                if (Directory.Exists(full))
                {
                    AddMeta(p);
                    Walk(full, f => AddFile(Rel(projectRoot, f)), d => AddMeta(Rel(projectRoot, d)));
                }
                else if (File.Exists(full)) AddFile(p);
                else set.Errors.Add($"{m.Id}: không tìm thấy '{p}'");
            }

            if (!File.Exists(Full(projectRoot, m.ManifestPath + ".meta")))
                set.Errors.Add($"{m.Id}: thiếu {m.ManifestPath}.meta — mở project bằng Unity để sinh .meta");

            owned.Remove(m.ManifestPath);
            owned.Remove(m.ManifestPath + ".meta");
            set.Owned.AddRange(owned);

            var shared = new SortedSet<string>(StringComparer.Ordinal);
            foreach (var p in m.Paths.Append(m.ManifestPath))
            {
                for (var dir = DirOf(p); dir.StartsWith("Assets/", StringComparison.Ordinal); dir = DirOf(dir))
                    if (File.Exists(Full(projectRoot, dir + ".meta")) && !owned.Contains(dir + ".meta")) shared.Add(dir + ".meta");
            }
            set.Shared.AddRange(shared);
            return set;
        }

        /// <summary>
        /// Hash nội dung module: các file Owned + manifest (bỏ version, contentHash).
        /// Đổi file, .meta hay khai báo phụ thuộc đều làm hash đổi; chỉ tăng version thì không.
        /// </summary>
        public static string ContentHash(string projectRoot, InstalledModule m, FileSet set)
        {
            var clone = m.Manifest.Clone();
            clone.version = "0.0.0";
            clone.contentHash = "";

            using (var sha = SHA1.Create())
            {
                void Feed(byte[] b) => sha.TransformBlock(b, 0, b.Length, null, 0);
                Feed(Encoding.UTF8.GetBytes(clone.ToJson()));
                foreach (var f in set.Owned)
                {
                    Feed(Encoding.UTF8.GetBytes("\0" + f + "\0"));
                    Feed(File.ReadAllBytes(Full(projectRoot, f)));
                }
                sha.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
                return string.Concat(sha.Hash.Select(b => b.ToString("x2")));
            }
        }

        /// <summary>Đóng zip: file của module + .meta thư mục cha + manifest (nội dung lấy từ <paramref name="manifest"/>).</summary>
        public static void BuildZip(string projectRoot, InstalledModule m, FileSet set, ModuleManifest manifest, string zipPath)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(zipPath));
            if (File.Exists(zipPath)) File.Delete(zipPath);

            using (var fs = File.Create(zipPath))
            using (var zip = new ZipArchive(fs, ZipArchiveMode.Create))
            {
                foreach (var f in set.Owned.Concat(set.Shared).Append(m.ManifestPath + ".meta"))
                {
                    var src = Full(projectRoot, f);
                    if (!File.Exists(src)) continue;
                    using (var es = zip.CreateEntry(f, CompressionLevel.Optimal).Open())
                    using (var input = File.OpenRead(src))
                        input.CopyTo(es);
                }

                var bytes = new UTF8Encoding(false).GetBytes(manifest.ToJson());
                using (var es = zip.CreateEntry(m.ManifestPath, CompressionLevel.Optimal).Open())
                    es.Write(bytes, 0, bytes.Length);
            }
        }

        // ───────────────────────── Cài / gỡ ─────────────────────────

        public sealed class PackageInfo
        {
            public ModuleManifest Manifest;
            public string ManifestPath;
            public List<string> Paths;
            public readonly List<string> Entries = new List<string>();
        }

        /// <summary>
        /// Đọc và kiểm tra gói zip. Gói là dữ liệu tải từ ngoài nên mọi entry phải nằm trong Assets/,
        /// thuộc paths của chính module, hoặc là .meta của thư mục cha — không thì ném lỗi.
        /// </summary>
        public static PackageInfo ReadPackage(string zipPath, string installRoot = null)
        {
            var info = new PackageInfo();
            using (var zip = new ZipArchive(File.OpenRead(zipPath), ZipArchiveMode.Read))
            {
                foreach (var e in zip.Entries)
                {
                    if (e.FullName.EndsWith("/", StringComparison.Ordinal)) continue;
                    if (!IsSafeEntry(e.FullName)) throw new InvalidDataException($"Gói chứa đường dẫn không hợp lệ: '{e.FullName}'");
                    var name = MapRoot(e.FullName, installRoot);
                    info.Entries.Add(name);
                    if (!name.EndsWith(ModuleManifest.FileSuffix, StringComparison.OrdinalIgnoreCase)) continue;
                    if (info.Manifest != null) throw new InvalidDataException("Gói chứa nhiều hơn một *.module.json");
                    using (var r = new StreamReader(e.Open(), Encoding.UTF8))
                        info.Manifest = ModuleManifest.Parse(r.ReadToEnd());
                    info.ManifestPath = name;
                }
            }

            if (info.Manifest == null) throw new InvalidDataException($"Gói {Path.GetFileName(zipPath)} không có *.module.json");
            info.Paths = ResolvePaths(info.ManifestPath, info.Manifest);
            foreach (var n in info.Entries)
                if (!IsOwned(n, info) && !IsParentMeta(n, info))
                    throw new InvalidDataException($"Gói {info.Manifest.id} chứa '{n}' nằm ngoài paths của module");
            return info;
        }

        /// <summary>Giải nén gói vào project. File không thuộc module (meta thư mục cha) chỉ ghi nếu chưa có.</summary>
        public static int Extract(string zipPath, string projectRoot, string installRoot = null)
        {
            var info = ReadPackage(zipPath, installRoot);
            var written = 0;
            using (var zip = new ZipArchive(File.OpenRead(zipPath), ZipArchiveMode.Read))
            {
                foreach (var e in zip.Entries)
                {
                    if (e.FullName.EndsWith("/", StringComparison.Ordinal)) continue;
                    var name = MapRoot(e.FullName, installRoot);
                    var target = Full(projectRoot, name);
                    if (File.Exists(target))
                    {
                        if (!IsOwned(name, info)) continue;
                        File.SetAttributes(target, FileAttributes.Normal);
                    }
                    Directory.CreateDirectory(Path.GetDirectoryName(target));
                    using (var input = e.Open())
                    using (var output = File.Create(target))
                        input.CopyTo(output);
                    written++;
                }
            }
            return written;
        }

        /// <summary>Xoá mọi path của module (kèm .meta) và file manifest.</summary>
        public static void Delete(string projectRoot, InstalledModule m)
        {
            foreach (var p in m.Paths.Append(m.ManifestPath))
            {
                DeletePath(Full(projectRoot, p));
                DeletePath(Full(projectRoot, p + ".meta"));
            }
        }

        private static void DeletePath(string full)
        {
            if (Directory.Exists(full))
            {
                foreach (var f in Directory.GetFiles(full, "*", SearchOption.AllDirectories))
                    File.SetAttributes(f, FileAttributes.Normal);
                Directory.Delete(full, true);
            }
            else if (File.Exists(full))
            {
                File.SetAttributes(full, FileAttributes.Normal);
                File.Delete(full);
            }
        }

        /// <summary>
        /// Đổi thư mục cấp đầu dưới Assets/ của một entry sang installRoot
        /// (Assets/InWorking/X → Assets/Working/X, Assets/InWorking.meta → Assets/Working.meta). installRoot trống = giữ nguyên.
        /// </summary>
        public static string MapRoot(string entry, string installRoot)
        {
            if (string.IsNullOrWhiteSpace(installRoot)) return entry;
            var root = Norm(installRoot.Trim()).TrimEnd('/');
            if (!IsSafeEntry(root)) throw new FormatException($"Thư mục cài '{installRoot}' phải là thư mục con của Assets/ (vd Assets/Working)");
            var slash = entry.IndexOf('/', "Assets/".Length);
            if (slash >= 0) return root + entry.Substring(slash);
            if (entry.EndsWith(".meta", StringComparison.Ordinal)) return root + ".meta";
            throw new InvalidDataException($"'{entry}' nằm thẳng trong Assets/, không đổi được thư mục cài");
        }

        private static bool IsSafeEntry(string n) =>
            n.StartsWith("Assets/", StringComparison.Ordinal) && n.IndexOf('\\') < 0 && n.IndexOf(':') < 0 &&
            n.Split('/').All(s => s.Length > 0 && s != "." && s != "..");

        private static bool IsOwned(string entry, PackageInfo info) =>
            entry == info.ManifestPath || entry == info.ManifestPath + ".meta" ||
            info.Paths.Any(p => entry == p || entry == p + ".meta" || entry.StartsWith(p + "/", StringComparison.Ordinal));

        private static bool IsParentMeta(string entry, PackageInfo info)
        {
            if (!entry.EndsWith(".meta", StringComparison.Ordinal)) return false;
            var dir = entry.Substring(0, entry.Length - 5) + "/";
            return info.Paths.Append(info.ManifestPath).Any(p => p.StartsWith(dir, StringComparison.Ordinal));
        }
    }
}
