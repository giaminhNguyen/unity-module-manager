using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using NUnit.Framework;
using UnityEngine;
using static ModuleManager.Tests.TestUtil;

namespace ModuleManager.Tests
{
    /// <summary>Toàn bộ luồng publish → cài → cập nhật → gỡ qua FolderSource, trên project giả.</summary>
    public class PublishInstallTests
    {
        private string _src, _dst, _feed, _installRoot;

        [SetUp]
        public void SetUp()
        {
            _src = NewRoot();
            _dst = NewRoot();
            _feed = Path.Combine(_src, "Feed");
            _installRoot = null;

            Dir(_src, "Assets/Fw");
            Dir(_src, "Assets/Fw/Modules");
            Dir(_src, "Assets/Fw/A");
            Dir(_src, "Assets/Fw/A/Sub");
            FileWithMeta(_src, "Assets/Fw/A/a1.cs", "v1");
            FileWithMeta(_src, "Assets/Fw/A/old.cs");
            FileWithMeta(_src, "Assets/Fw/A/Sub/s.cs");
            File.WriteAllText(Path.Combine(_src, "Assets/Fw/A/.hidden"), "bỏ qua");
            Dir(_src, "Assets/Fw/B");
            FileWithMeta(_src, "Assets/Fw/B/b.cs");
            WriteManifest(_src, "Assets/Fw/Modules/a.module.json", Manifest("t.a", "1.0.0", new[] { "../A" }));
            WriteManifest(_src, "Assets/Fw/Modules/b.module.json", Manifest("t.b", "1.0.0", new[] { "../B" }, ("t.a", "1.0.0")));
        }

        [TearDown]
        public void TearDown()
        {
            Cleanup(_src);
            Cleanup(_dst);
        }

        private IModuleSource Feed => new FolderSource(_feed);

        private void PublishAll(string latestA = null, string latestB = null)
        {
            var locals = ModuleFiles.Scan(_src);
            CollectionAssert.IsEmpty(ModulePublisher.Validate(_src, locals).Select(i => i.ToString()));
            foreach (var m in ModulePublisher.TopoOrder(locals))
                ModulePublisher.PublishAsync(Feed, _src, m, m.Id == "t.a" ? latestA : latestB, BumpKind.Patch, "ghi chú",
                    Path.Combine(_src, "Out")).GetAwaiter().GetResult();
        }

        private void Install(params string[] ids)
        {
            var releases = Feed.FetchAsync().Result;
            var latest = releases.GroupBy(r => r.Id).Select(g => g.OrderByDescending(r => SemVer.Parse(r.Version)).First());
            var installed = ModuleFiles.Scan(_dst);
            var errors = new List<string>();
            var plan = ModuleResolver.Plan(installed, releases, latest.Where(r => ids.Contains(r.Id)), errors);
            CollectionAssert.IsEmpty(errors);
            var zips = ModuleInstaller.DownloadAsync(Feed, plan, Path.Combine(_dst, "Dl")).Result;
            ModuleInstaller.Apply(_dst, plan, zips, installed, _installRoot);
        }

        [Test]
        public void InstallIntoCustomRoot()
        {
            PublishAll();
            _installRoot = "Assets/Game/Working/";
            Install("t.b");
            Assert.AreEqual("v1", Read(_dst, "Assets/Game/Working/A/a1.cs"));
            Assert.IsTrue(Exists(_dst, "Assets/Game/Working/B/b.cs.meta"));
            Assert.IsTrue(Exists(_dst, "Assets/Game/Working/Modules/b.module.json"));
            Assert.IsTrue(Exists(_dst, "Assets/Game/Working.meta"), "meta thư mục gốc đổi theo thư mục cài");
            Assert.IsFalse(Exists(_dst, "Assets/Fw"));
            var a = ModuleFiles.Scan(_dst).Single(m => m.Id == "t.a");
            CollectionAssert.AreEqual(new[] { "Assets/Game/Working/A" }, a.Paths);

            // Cập nhật vẫn ghi vào thư mục cài
            File.WriteAllText(Path.Combine(_src, "Assets/Fw/A/a1.cs"), "v2");
            ModulePublisher.PublishAsync(Feed, _src, ModuleFiles.Scan(_src).Single(m => m.Id == "t.a"), "1.0.0", BumpKind.Patch, "",
                Path.Combine(_src, "Out")).GetAwaiter().GetResult();
            Install("t.a");
            Assert.AreEqual("v2", Read(_dst, "Assets/Game/Working/A/a1.cs"));
            Assert.IsFalse(Exists(_dst, "Assets/Fw"));
        }

        [Test]
        public void MapRootRejectsBadInput()
        {
            Assert.AreEqual("Assets/Fw/A/a.cs", ModuleFiles.MapRoot("Assets/Fw/A/a.cs", " "));
            Assert.AreEqual("Assets/W/A/a.cs", ModuleFiles.MapRoot("Assets/Fw/A/a.cs", "Assets\\W"));
            Assert.Throws<FormatException>(() => ModuleFiles.MapRoot("Assets/Fw/a.cs", "Assets"));
            Assert.Throws<FormatException>(() => ModuleFiles.MapRoot("Assets/Fw/a.cs", "Assets/../x"));
            Assert.Throws<FormatException>(() => ModuleFiles.MapRoot("Assets/Fw/a.cs", "Other/W"));
            Assert.Throws<InvalidDataException>(() => ModuleFiles.MapRoot("Assets/a.cs", "Assets/W"));
        }

        [Test]
        public void PublishInstallUpdateRemove()
        {
            PublishAll();
            var releases = Feed.FetchAsync().Result;
            Assert.AreEqual(2, releases.Count);
            Assert.IsFalse(string.IsNullOrEmpty(releases[0].Manifest.contentHash));

            Install("t.b"); // kéo theo t.a
            Assert.AreEqual("v1", Read(_dst, "Assets/Fw/A/a1.cs"));
            Assert.IsTrue(Exists(_dst, "Assets/Fw/A/Sub/s.cs.meta"));
            Assert.IsTrue(Exists(_dst, "Assets/Fw/B/b.cs"));
            Assert.IsTrue(Exists(_dst, "Assets/Fw.meta"), "meta thư mục cha phải đi kèm");
            Assert.IsFalse(Exists(_dst, "Assets/Fw/A/.hidden"), "file ẩn không được đóng gói");
            var installed = ModuleFiles.Scan(_dst);
            CollectionAssert.AreEquivalent(new[] { "t.a", "t.b" }, installed.Select(m => m.Id));

            // Sửa A ở nguồn: đổi nội dung, xoá một file → hash đổi → publish 1.0.1
            var a = ModuleFiles.Scan(_src).Single(m => m.Id == "t.a");
            var oldHash = ModulePublisher.ContentHash(_src, a);
            File.WriteAllText(Path.Combine(_src, "Assets/Fw/A/a1.cs"), "v2");
            File.Delete(Path.Combine(_src, "Assets/Fw/A/old.cs"));
            File.Delete(Path.Combine(_src, "Assets/Fw/A/old.cs.meta"));
            Assert.AreNotEqual(oldHash, ModulePublisher.ContentHash(_src, ModuleFiles.Scan(_src).Single(m => m.Id == "t.a")));
            ModulePublisher.PublishAsync(Feed, _src, ModuleFiles.Scan(_src).Single(m => m.Id == "t.a"), "1.0.0", BumpKind.Patch, "",
                Path.Combine(_src, "Out")).GetAwaiter().GetResult();
            StringAssert.Contains("\"1.0.1\"", Read(_src, "Assets/Fw/Modules/a.module.json"), "version nguồn phải được ghi lại");

            File.WriteAllText(Path.Combine(_dst, "Assets/Fw.meta"), "meta riêng của project");
            Install("t.a");
            Assert.AreEqual("v2", Read(_dst, "Assets/Fw/A/a1.cs"));
            Assert.IsFalse(Exists(_dst, "Assets/Fw/A/old.cs"), "file bị xoá ở bản mới phải biến mất");
            Assert.AreEqual("meta riêng của project", Read(_dst, "Assets/Fw.meta"), "không ghi đè meta thư mục cha đã có");
            Assert.IsTrue(Exists(_dst, "Assets/Fw/B/b.cs"), "module khác giữ nguyên");
            Assert.AreEqual("1.0.1", ModuleFiles.Scan(_dst).Single(m => m.Id == "t.a").Version);

            // Gỡ: t.a đang bị t.b cần → chặn; gỡ t.b trước thì được
            installed = ModuleFiles.Scan(_dst);
            Assert.IsNotEmpty(ModuleResolver.RemovalErrors(installed, "t.a"));
            ModuleFiles.Delete(_dst, installed.Single(m => m.Id == "t.b"));
            Assert.IsFalse(Exists(_dst, "Assets/Fw/B"));
            Assert.IsFalse(Exists(_dst, "Assets/Fw/B.meta"));
            Assert.IsFalse(Exists(_dst, "Assets/Fw/Modules/b.module.json"));
            Assert.IsTrue(Exists(_dst, "Assets/Fw/A/a1.cs"));
        }

        [Test]
        public void PublishSameVersionTwiceFails()
        {
            PublishAll();
            var a = ModuleFiles.Scan(_src).Single(m => m.Id == "t.a");
            Assert.Throws<InvalidOperationException>(() =>
                ModulePublisher.PublishAsync(Feed, _src, a, null, BumpKind.Patch, "", Path.Combine(_src, "Out")).GetAwaiter().GetResult());
        }

        [Test]
        public void ValidateFindsMissingMetaAndOverlap()
        {
            File.Delete(Path.Combine(_src, "Assets/Fw/A/a1.cs.meta"));
            WriteManifest(_src, "Assets/Fw/Modules/c.module.json", Manifest("t.c", "1.0.0", new[] { "../A/Sub" }));
            var issues = ModulePublisher.Validate(_src, ModuleFiles.Scan(_src)).Select(i => i.ToString()).ToList();
            Assert.IsTrue(issues.Any(i => i.Contains("a1.cs.meta")), string.Join("\n", issues));
            Assert.IsTrue(issues.Any(i => i.Contains("chồng")), string.Join("\n", issues));
        }

        [Test]
        public void RejectsPackageWritingOutsideItsPaths()
        {
            var manifest = Manifest("evil", "1.0.0", new[] { "../Evil" });
            foreach (var bad in new[] { "Assets/../outside.txt", "Assets/Other/x.cs", "ProjectSettings/x.asset" })
            {
                var zip = Path.Combine(_dst, "evil.zip");
                if (File.Exists(zip)) File.Delete(zip);
                using (var z = new ZipArchive(File.Create(zip), ZipArchiveMode.Create))
                {
                    Write(z, "Assets/M/evil.module.json", manifest.ToJson());
                    Write(z, bad, "x");
                }
                Assert.Throws<InvalidDataException>(() => ModuleFiles.Extract(zip, _dst), bad);
                Assert.IsFalse(Exists(_dst, "Assets/M/evil.module.json"), "gói hỏng thì không được ghi file nào");
            }
        }

        [Test]
        public void ApplyRejectsPathClashWithOtherModule()
        {
            PublishAll();
            Install("t.a");
            // module khác trong nguồn chiếm cùng thư mục với t.a
            WriteManifest(_src, "Assets/Fw/Modules/a.module.json", Manifest("t.a", "1.0.0", new[] { "../B" }));
            WriteManifest(_src, "Assets/Fw/Modules/b.module.json", Manifest("t.x", "1.0.0", new[] { "../A" }));
            var x = ModuleFiles.Scan(_src).Single(m => m.Id == "t.x");
            ModulePublisher.PublishAsync(Feed, _src, x, null, BumpKind.Patch, "", Path.Combine(_src, "Out")).GetAwaiter().GetResult();
            var rel = Feed.FetchAsync().Result.Single(r => r.Id == "t.x");
            var plan = new List<PlanStep> { new PlanStep { Target = rel } };
            var zips = ModuleInstaller.DownloadAsync(Feed, plan, Path.Combine(_dst, "Dl")).Result;
            Assert.Throws<InvalidOperationException>(() => ModuleInstaller.Apply(_dst, plan, zips, ModuleFiles.Scan(_dst)));
            Assert.AreEqual("v1", Read(_dst, "Assets/Fw/A/a1.cs"), "lỗi phải dừng trước khi ghi");
        }

        private static void Write(ZipArchive z, string name, string text)
        {
            var bytes = Encoding.UTF8.GetBytes(text);
            using (var s = z.CreateEntry(name).Open()) s.Write(bytes, 0, bytes.Length);
        }
    }

    public class GitHubParseTests
    {
        [Serializable]
        private class FakeAsset
        {
            public string name;
            public string url;
            public long size;
        }

        [Serializable]
        private class FakeRelease
        {
            public string tag_name;
            public string body;
            public bool draft;
            public string published_at = "2026-11-19T00:00:00Z";
            public FakeAsset[] assets;
        }

        [Test]
        public void ParsesModuleReleasesAndSkipsOthers()
        {
            var m = TestUtil.Manifest("inworking.toppic.core", "1.2.0", new[] { "../Toppic/Base" }, ("x.y", "1.0.0"));
            m.description = "mô tả có --> và \"nháy\"";
            var good = new FakeRelease
            {
                tag_name = "inworking.toppic.core/1.2.0",
                body = GitHubSource.BuildBody(m, "Sửa lỗi popup"),
                assets = new[] { new FakeAsset { name = "inworking.toppic.core-1.2.0.zip", url = "https://api/1", size = 10 } }
            };
            var plain = new FakeRelease { tag_name = "v1", body = "release thường", assets = new FakeAsset[0] };
            var draft = new FakeRelease { tag_name = "d", body = good.body, draft = true, assets = good.assets };
            var json = "[" + string.Join(",", new[] { good, plain, draft }.Select(r => JsonUtility.ToJson(r))) + "]";

            var list = GitHubSource.ParseReleases(json, out var raw);
            Assert.AreEqual(3, raw);
            Assert.AreEqual(1, list.Count);
            Assert.AreEqual("1.2.0", list[0].Version);
            Assert.AreEqual("Sửa lỗi popup", list[0].Changelog);
            Assert.AreEqual(m.description, list[0].Manifest.description);
            Assert.AreEqual("x.y", list[0].Manifest.dependencies[0].id);
            Assert.AreEqual("https://api/1", list[0].AssetUrl);
        }
    }
}
