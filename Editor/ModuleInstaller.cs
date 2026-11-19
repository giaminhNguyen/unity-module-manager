using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace ModuleManager
{
    /// <summary>
    /// Thực hiện kế hoạch cài. Thứ tự: tải hết → kiểm tra hết → mới xoá/chép.
    /// Gói nào hỏng hay đè lên module khác thì dừng trước khi đụng tới file, project giữ nguyên.
    /// Phần chép file chạy đồng bộ nên domain reload không chen vào giữa chừng.
    /// </summary>
    public static class ModuleInstaller
    {
        public static async Task<List<string>> DownloadAsync(IModuleSource source, IList<PlanStep> plan, string dir,
            Action<string, float> progress = null)
        {
            var zips = new List<string>();
            for (var i = 0; i < plan.Count; i++)
            {
                progress?.Invoke($"Tải {plan[i].Target.AssetName}", (float)i / plan.Count);
                zips.Add(await source.DownloadAsync(plan[i].Target, dir));
            }
            return zips;
        }

        /// <param name="installRoot">Thư mục cài thay cho thư mục cấp đầu dưới Assets/ của gói. Null = giữ đường dẫn gốc.
        /// Module đã cài ở chỗ khác được xoá ở chỗ cũ rồi chép vào installRoot.</param>
        /// <returns>Các file .unitypackage đã chép ra Library/ModuleManager/Import, theo thứ tự plan, để gọi import.</returns>
        public static List<string> Apply(string projectRoot, IList<PlanStep> plan, IList<string> zips, IEnumerable<InstalledModule> installed,
            string installRoot = null)
        {
            if (plan.Count != zips.Count) throw new ArgumentException("Số gói không khớp số bước");

            // Đường dẫn của mọi module sau khi cài: module không đụng tới giữ path cũ, module trong plan lấy path mới.
            var planIds = new HashSet<string>(plan.Select(s => s.Id));
            var owners = installed.Where(m => m.IsValid && !planIds.Contains(m.Id))
                .SelectMany(m => m.Paths.Select(p => (m.Id, p))).ToList();

            for (var i = 0; i < plan.Count; i++)
            {
                var info = ModuleFiles.ReadPackage(zips[i], installRoot);
                var t = plan[i].Target.Manifest;
                if (info.Manifest.id != t.id || info.Manifest.version != t.version)
                    throw new InvalidDataException($"Gói {Path.GetFileName(zips[i])} là {info.Manifest.id} {info.Manifest.version}, không khớp {t.id} {t.version}");

                foreach (var p in info.Paths)
                {
                    var clash = owners.FirstOrDefault(o => o.Id != t.id && ModuleFiles.Overlaps(o.p, p));
                    if (clash.Id != null)
                        throw new InvalidOperationException($"{t.Title}: '{p}' trùng với module {clash.Id} ('{clash.p}')");
                }
                owners.AddRange(info.Paths.Select(p => (t.id, p)));
            }

            var imports = new List<string>();
            var importDir = Path.Combine(projectRoot, "Library", "ModuleManager", "Import");
            for (var i = 0; i < plan.Count; i++)
            {
                if (plan[i].Installed != null) ModuleFiles.Delete(projectRoot, plan[i].Installed);
                ModuleFiles.Extract(zips[i], projectRoot, installRoot);
                var unityPackage = ModuleFiles.ExtractUnityPackage(zips[i], importDir);
                if (unityPackage != null) imports.Add(unityPackage);
            }
            return imports;
        }

        /// <summary>Package UPM của module sắp gỡ mà không module đang cài nào khác còn khai.</summary>
        public static List<string> PackagesToRemove(IEnumerable<InstalledModule> installed, InstalledModule removing)
        {
            var kept = new HashSet<string>(installed.Where(m => m.IsValid && m.Id != removing.Id)
                .SelectMany(m => m.Manifest.packages).Select(p => p.name));
            return removing.Manifest.packages.Select(p => p.name).Where(n => !kept.Contains(n)).Distinct().ToList();
        }

        /// <summary>Package UPM mà các module cần nhưng Packages/manifest.json chưa có.</summary>
        public static List<PackageDependency> MissingPackages(string projectRoot, IEnumerable<ModuleManifest> modules)
        {
            var path = Path.Combine(projectRoot, "Packages", "manifest.json");
            var text = File.Exists(path) ? File.ReadAllText(path) : "";
            return modules.SelectMany(m => m.packages)
                .Where(p => !text.Contains("\"" + p.name + "\""))
                .GroupBy(p => p.name).Select(g => g.First()).ToList();
        }
    }
}
