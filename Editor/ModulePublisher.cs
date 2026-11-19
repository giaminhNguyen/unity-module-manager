using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace ModuleManager
{
    /// <summary>Logic phát hành ở project nguồn: kiểm tra manifest, đóng gói, đẩy lên nguồn.</summary>
    public static class ModulePublisher
    {
        public sealed class Issue
        {
            /// <summary>Module bị ảnh hưởng; null = lỗi chung chặn mọi module.</summary>
            public string ModuleId;

            public string Message;

            public override string ToString() => ModuleId == null ? Message : $"[{ModuleId}] {Message}";
        }

        /// <summary>Kiểm tra toàn bộ manifest nguồn: trùng id, phụ thuộc thiếu/vòng, path chồng nhau, thiếu file/.meta.</summary>
        public static List<Issue> Validate(string projectRoot, IList<InstalledModule> modules, string unityPackageDir = null)
        {
            var issues = new List<Issue>();
            foreach (var m in modules.Where(m => !m.IsValid))
                issues.Add(new Issue { Message = m.Error });

            var valid = modules.Where(m => m.IsValid).ToList();
            foreach (var g in valid.GroupBy(m => m.Id).Where(g => g.Count() > 1))
                issues.Add(new Issue { ModuleId = g.Key, Message = "trùng id ở: " + string.Join(", ", g.Select(m => m.ManifestPath)) });

            var byId = valid.GroupBy(m => m.Id).ToDictionary(g => g.Key, g => g.First());
            foreach (var m in valid)
            {
                foreach (var d in m.Manifest.dependencies)
                {
                    if (!byId.TryGetValue(d.id, out var dep))
                        issues.Add(new Issue { ModuleId = m.Id, Message = $"phụ thuộc {d.id} không có manifest" });
                    else if (SemVer.Compare(dep.Version, d.version) < 0)
                        issues.Add(new Issue { ModuleId = m.Id, Message = $"cần {d.id} >= {d.version} nhưng nguồn đang là {dep.Version}" });
                }

                foreach (var e in ModuleFiles.Collect(projectRoot, m, unityPackageDir).Errors)
                    issues.Add(new Issue { ModuleId = m.Id, Message = e });
            }

            // Path chồng nhau thì cập nhật module này sẽ xoá mất file của module kia.
            for (var i = 0; i < valid.Count; i++)
            for (var j = i + 1; j < valid.Count; j++)
            {
                var a = valid[i];
                var b = valid[j];
                var hit = a.Paths.Append(a.ManifestPath)
                    .SelectMany(x => b.Paths.Append(b.ManifestPath).Select(y => (x, y)))
                    .FirstOrDefault(t => ModuleFiles.Overlaps(t.x, t.y));
                if (hit.x != null)
                    issues.Add(new Issue { ModuleId = a.Id, Message = $"'{hit.x}' chồng lên '{hit.y}' của {b.Id}" });
            }

            foreach (var cycle in FindCycles(byId))
                issues.Add(new Issue { Message = "phụ thuộc vòng: " + cycle });

            return issues;
        }

        /// <summary>Version sẽ publish: tự tăng từ bản đã publish, trừ khi version nguồn đã được nâng tay cao hơn.</summary>
        public static string NextVersion(string local, string latestPublished, BumpKind kind)
        {
            if (string.IsNullOrEmpty(latestPublished)) return local;
            return SemVer.Compare(local, latestPublished) > 0 ? local : SemVer.Parse(latestPublished).Bump(kind).ToString();
        }

        /// <summary>Hash nội dung hiện tại của module nguồn (để so với bản đã publish).</summary>
        public static string ContentHash(string projectRoot, InstalledModule m, string unityPackageDir = null) =>
            ModuleFiles.ContentHash(projectRoot, m, ModuleFiles.Collect(projectRoot, m, unityPackageDir));

        /// <summary>Đóng gói module thành zip; manifest trong zip mang version + contentHash.</summary>
        public static string Pack(string projectRoot, InstalledModule m, string outDir, out ModuleManifest published,
            string unityPackageDir = null)
        {
            var set = ModuleFiles.Collect(projectRoot, m, unityPackageDir);
            if (set.Errors.Count > 0) throw new InvalidOperationException(string.Join("\n", set.Errors));
            published = m.Manifest.Clone();
            published.contentHash = ModuleFiles.ContentHash(projectRoot, m, set);
            var zip = Path.Combine(outDir, $"{m.Id}-{published.version}.zip");
            ModuleFiles.BuildZip(projectRoot, m, set, published, zip);
            return zip;
        }

        /// <summary>
        /// Ghi version mới vào manifest nguồn, đóng gói rồi đẩy lên nguồn.
        /// Đẩy lỗi thì version nguồn vẫn giữ số mới; lần sau publish lại đúng số đó (không nhảy số).
        /// </summary>
        public static async Task<ModuleManifest> PublishAsync(IModuleSource source, string projectRoot, InstalledModule m,
            string latestPublished, BumpKind kind, string changelog, string outDir, string unityPackageDir = null)
        {
            var next = NextVersion(m.Version, latestPublished, kind);
            if (next != m.Version)
            {
                m.Manifest.version = next;
                m.Manifest.contentHash = "";
                File.WriteAllText(Path.Combine(projectRoot, m.ManifestPath), m.Manifest.ToJson());
            }

            var zip = Pack(projectRoot, m, outDir, out var published, unityPackageDir);
            await source.PublishAsync(published, changelog, zip);
            return published;
        }

        /// <summary>Xếp module theo phụ thuộc (phụ thuộc trước), chỉ xét trong tập đưa vào.</summary>
        public static List<InstalledModule> TopoOrder(IEnumerable<InstalledModule> modules)
        {
            var map = modules.Where(m => m.IsValid).GroupBy(m => m.Id).ToDictionary(g => g.Key, g => g.First());
            var result = new List<InstalledModule>();
            var seen = new HashSet<string>();

            void Visit(InstalledModule m)
            {
                if (!seen.Add(m.Id)) return;
                foreach (var d in m.Manifest.dependencies)
                    if (map.TryGetValue(d.id, out var dep)) Visit(dep);
                result.Add(m);
            }

            foreach (var m in map.Values.OrderBy(m => m.Id, StringComparer.Ordinal)) Visit(m);
            return result;
        }

        private static IEnumerable<string> FindCycles(Dictionary<string, InstalledModule> byId)
        {
            var state = new Dictionary<string, int>(); // 1 = đang thăm, 2 = xong
            var stack = new List<string>();
            var cycles = new List<string>();

            void Visit(string id)
            {
                state[id] = 1;
                stack.Add(id);
                foreach (var d in byId[id].Manifest.dependencies)
                {
                    if (!byId.ContainsKey(d.id)) continue;
                    state.TryGetValue(d.id, out var s);
                    if (s == 1) cycles.Add(string.Join(" → ", stack.Skip(stack.IndexOf(d.id)).Append(d.id)));
                    else if (s == 0) Visit(d.id);
                }
                stack.RemoveAt(stack.Count - 1);
                state[id] = 2;
            }

            foreach (var id in byId.Keys.OrderBy(k => k, StringComparer.Ordinal))
                if (!state.ContainsKey(id)) Visit(id);
            return cycles;
        }
    }
}
