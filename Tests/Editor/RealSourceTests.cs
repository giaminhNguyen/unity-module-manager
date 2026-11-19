using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using NUnit.Framework;
using static ModuleManager.Tests.TestUtil;

namespace ModuleManager.Tests
{
    /// <summary>
    /// Kiểm tra manifest của một project nguồn thật (chạy tay / CI). Bỏ qua nếu không đặt biến môi trường:
    ///   MM_SOURCE_ROOT   — root project nguồn (bắt buộc)
    ///   MM_AUDIT_DIR     — "Assets/..." : liệt kê file trong thư mục này chưa thuộc module nào
    ///   MM_INSTALL_IDS   — id module cần cài thử, cách nhau dấu phẩy (mặc định module cuối trong thứ tự phụ thuộc)
    ///   MM_INSTALL_INTO  — root một project Unity để cài thật vào (dùng cho bước compile thử); bỏ trống = thư mục tạm
    ///   MM_FEED_OUT      — giữ lại thư mục chứa zip mọi module (FolderSource) để dùng tiếp; bỏ trống = xoá sau test
    /// </summary>
    public class RealSourceTests
    {
        private static string Env(string key) => Environment.GetEnvironmentVariable(key);

        [Test]
        public void ManifestsValidAndModulesInstallWithOnlyTheirDependencies()
        {
            var root = Env("MM_SOURCE_ROOT");
            if (string.IsNullOrEmpty(root)) Assert.Ignore("Đặt MM_SOURCE_ROOT để kiểm tra một project nguồn thật");

            var locals = ModuleFiles.Scan(root);
            var issues = ModulePublisher.Validate(root, locals).Select(i => i.ToString()).ToList();
            Assert.IsEmpty(issues, string.Join("\n", issues));
            TestContext.WriteLine($"{locals.Count} module hợp lệ");

            Audit(root, locals);

            var temp = NewRoot();
            try
            {
                var feed = new FolderSource(string.IsNullOrEmpty(Env("MM_FEED_OUT")) ? Path.Combine(temp, "Feed") : Env("MM_FEED_OUT"));
                foreach (var m in ModulePublisher.TopoOrder(locals))
                {
                    var zip = ModulePublisher.Pack(root, m, Path.Combine(temp, "Out"), out var published);
                    feed.PublishAsync(published, "", zip).GetAwaiter().GetResult();
                    TestContext.WriteLine($"{m.Id,-45} {new FileInfo(zip).Length / 1024,6} KB");
                }

                var into = string.IsNullOrEmpty(Env("MM_INSTALL_INTO")) ? Path.Combine(temp, "Consumer") : Env("MM_INSTALL_INTO");
                Directory.CreateDirectory(Path.Combine(into, "Assets"));
                var ids = (Env("MM_INSTALL_IDS") ?? ModulePublisher.TopoOrder(locals).Last().Id).Split(',').Select(s => s.Trim()).ToList();
                var releases = feed.FetchAsync().Result;
                var installed = ModuleFiles.Scan(into);
                var errors = new List<string>();
                var plan = ModuleResolver.Plan(installed, releases, releases.Where(r => ids.Contains(r.Id)), errors);
                Assert.IsEmpty(errors, string.Join("\n", errors));
                var zips = ModuleInstaller.DownloadAsync(feed, plan, Path.Combine(temp, "Dl")).Result;
                ModuleInstaller.Apply(into, plan, zips, installed);

                var after = ModuleFiles.Scan(into).Select(m => m.Id).ToList();
                TestContext.WriteLine("Đã cài vào " + into + ": " + string.Join(", ", after));
                CollectionAssert.IsSubsetOf(ids, after);
            }
            finally
            {
                Cleanup(temp);
            }
        }

        /// <summary>In ra file chưa thuộc module nào — dấu hiệu quên thêm path khi tạo thư mục mới.</summary>
        private static void Audit(string root, List<InstalledModule> locals)
        {
            var dir = Env("MM_AUDIT_DIR");
            if (string.IsNullOrEmpty(dir)) return;
            var owned = locals.SelectMany(m => m.Paths.Append(m.ManifestPath)).ToList();
            var orphans = new List<string>();
            var baseFull = Path.GetFullPath(root).Replace('\\', '/').TrimEnd('/') + "/";
            foreach (var f in Directory.GetFiles(Path.Combine(root, dir), "*", SearchOption.AllDirectories))
            {
                var rel = Path.GetFullPath(f).Replace('\\', '/').Substring(baseFull.Length);
                if (rel.Split('/').Any(ModuleFiles.IsIgnored)) continue;
                var asset = rel.EndsWith(".meta") ? rel.Substring(0, rel.Length - 5) : rel;
                if (owned.Any(p => asset == p || asset.StartsWith(p + "/"))) continue;
                if (rel.EndsWith(".meta") && Directory.Exists(Path.Combine(root, asset))) continue; // meta thư mục cha
                orphans.Add(rel);
            }
            TestContext.WriteLine(orphans.Count == 0 ? "Mọi file đều thuộc module" : "Chưa thuộc module nào:\n" + string.Join("\n", orphans));
        }
    }
}
