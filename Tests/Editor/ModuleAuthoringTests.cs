using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using NUnit.Framework;
using static ModuleManager.Tests.TestUtil;

namespace ModuleManager.Tests
{
    public class ModuleAuthoringTests
    {
        private string _root;

        [SetUp]
        public void SetUp() => _root = NewRoot();

        [TearDown]
        public void TearDown() => Cleanup(_root);

        private static InstalledModule Module(string id, string manifestPath, params string[] paths) =>
            new InstalledModule { Manifest = Manifest(id, "1.0.0", new[] { "x" }), ManifestPath = manifestPath, Paths = paths.ToList() };

        // ───────────────────────── RelativePath ─────────────────────────

        [TestCase("Assets/Modules", "Assets/Toppic/Popup", "../Toppic/Popup")]
        [TestCase("Assets/Modules", "Assets/Modules/Sub", "Sub")]
        [TestCase("Assets/A/B", "Assets/C", "../../C")]
        [TestCase("Assets", "Assets/X/Y.cs", "X/Y.cs")]
        [TestCase("Assets/A", "Assets/A", ".")]
        public void RelativePath_RoundTripsWithResolveRelative(string from, string to, string expected)
        {
            var rel = ModuleAuthoring.RelativePath(from, to);
            Assert.AreEqual(expected, rel);
            Assert.AreEqual(to, ModuleFiles.ResolveRelative(from, rel));
        }

        [Test]
        public void RelativePath_AcceptsBackslashes() =>
            Assert.AreEqual("../B", ModuleAuthoring.RelativePath("Assets\\A", "Assets\\B"));

        // ───────────────────────── CreateManifest ─────────────────────────

        [Test]
        public void CreateManifest_SanitizesIdAndBuildsRelativePaths()
        {
            var m = ModuleAuthoring.CreateManifest("Assets/Modules",
                new[] { "Assets/Toppic/Pop_Up V2", "Assets/Other/File.cs" }, "my.");

            Assert.AreEqual("my.pop-up-v2", m.id);
            Assert.AreEqual("Pop_Up V2", m.displayName);
            Assert.AreEqual("Toppic", m.category);
            Assert.AreEqual("1.0.0", m.version);
            CollectionAssert.AreEqual(new[] { "../Toppic/Pop_Up V2", "../Other/File.cs" }, m.paths);
            Assert.IsEmpty(m.dependencies);
            Assert.IsEmpty(m.packages);
        }

        [Test]
        public void CreateManifest_NoPathsThrows() =>
            Assert.Throws<ArgumentException>(() => ModuleAuthoring.CreateManifest("Assets/Modules", new string[0], "my."));

        // ───────────────────────── WriteNewManifest ─────────────────────────

        [Test]
        public void WriteNewManifest_WritesFileAndRejectsDuplicate()
        {
            var m = ModuleAuthoring.CreateManifest("Assets/Modules", new[] { "Assets/Lib/A" }, "my.");

            var path = ModuleAuthoring.WriteNewManifest(_root, "Assets/Modules", m);

            Assert.AreEqual("Assets/Modules/my.a.module.json", path);
            Assert.AreEqual("my.a", ModuleManifest.Parse(Read(_root, path)).id);
            Assert.Throws<InvalidOperationException>(() => ModuleAuthoring.WriteNewManifest(_root, "Assets/Modules", m));
        }

        // ───────────────────────── FindUncovered ─────────────────────────

        [Test]
        public void FindUncovered_ReportsWholeUnrelatedFolderAndSkipsIgnored()
        {
            FileWithMeta(_root, "Assets/Lib/A/a.cs");
            FileWithMeta(_root, "Assets/Lib/B/b1.cs");
            FileWithMeta(_root, "Assets/Lib/B/b2.cs");
            FileWithMeta(_root, "Assets/Lib/.hidden/h.cs");
            FileWithMeta(_root, "Assets/Lib/skip~/s.cs");
            var a = Module("my.a", "Assets/Modules/my.a.module.json", "Assets/Lib/A");
            FileWithMeta(_root, a.ManifestPath);

            var result = ModuleAuthoring.FindUncovered(_root, "Assets", new[] { a });

            CollectionAssert.AreEqual(new[] { "Assets/Lib/B" }, result);
        }

        [Test]
        public void FindUncovered_AncestorFolderListsLooseFiles()
        {
            FileWithMeta(_root, "Assets/Lib/A/a.cs");
            FileWithMeta(_root, "Assets/Lib/loose.cs");
            FileWithMeta(_root, "Assets/Lib/Sub/Deep/d.cs");
            var a = Module("my.a", "Assets/Modules/my.a.module.json", "Assets/Lib/A");
            FileWithMeta(_root, a.ManifestPath);

            var result = ModuleAuthoring.FindUncovered(_root, "Assets", new[] { a });

            CollectionAssert.AreEqual(new[] { "Assets/Lib/Sub", "Assets/Lib/loose.cs" }, result);
        }

        [Test]
        public void FindUncovered_FolderAlreadyInModuleIsSkipped()
        {
            FileWithMeta(_root, "Assets/Lib/A/deep/a.cs");
            FileWithMeta(_root, "Assets/Lib/b.cs");
            var m = Module("my.lib", "Assets/Modules/my.lib.module.json", "Assets/Lib");
            FileWithMeta(_root, m.ManifestPath);

            Assert.IsEmpty(ModuleAuthoring.FindUncovered(_root, "Assets", new[] { m }));
            Assert.IsEmpty(ModuleAuthoring.FindUncovered(_root, "Assets/Lib/A", new[] { m }));
        }

        // ───────────────────────── AddPath ─────────────────────────

        [Test]
        public void AddPath_AppendsRelativePathAndRejectsOverlap()
        {
            const string manifestPath = "Assets/Modules/my.a.module.json";
            WriteManifest(_root, manifestPath, Manifest("my.a", "1.0.0", new[] { "../Lib/A" }));
            FileWithMeta(_root, "Assets/Lib/A/a.cs");
            FileWithMeta(_root, "Assets/Lib/B/b.cs");
            var m = ModuleFiles.Load(_root, manifestPath);

            ModuleAuthoring.AddPath(_root, m, "Assets/Lib/B");

            var reloaded = ModuleFiles.Load(_root, manifestPath);
            CollectionAssert.AreEqual(new[] { "../Lib/A", "../Lib/B" }, reloaded.Manifest.paths);
            CollectionAssert.AreEqual(new[] { "Assets/Lib/A", "Assets/Lib/B" }, reloaded.Paths);
            CollectionAssert.AreEqual(reloaded.Paths, m.Paths);

            var before = Read(_root, manifestPath);
            Assert.Throws<InvalidOperationException>(() => ModuleAuthoring.AddPath(_root, m, "Assets/Lib/B"));
            Assert.Throws<InvalidOperationException>(() => ModuleAuthoring.AddPath(_root, m, "Assets/Lib/A/sub"));
            Assert.Throws<InvalidOperationException>(() => ModuleAuthoring.AddPath(_root, m, "Assets/Lib"));
            Assert.AreEqual(before, Read(_root, manifestPath));
        }
    }
}
