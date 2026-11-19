using System;
using System.IO;
using System.Linq;

namespace ModuleManager.Tests
{
    /// <summary>Dựng project giả trong thư mục tạm để test không đụng Assets thật.</summary>
    internal static class TestUtil
    {
        public static string NewRoot()
        {
            var d = Path.Combine(Path.GetTempPath(), "mm-test-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path.Combine(d, "Assets"));
            return d;
        }

        public static void Cleanup(string root)
        {
            if (root != null && Directory.Exists(root)) Directory.Delete(root, true);
        }

        public static void Dir(string root, string rel)
        {
            Directory.CreateDirectory(Path.Combine(root, rel));
            File.WriteAllText(Path.Combine(root, rel) + ".meta", "folder " + rel);
        }

        public static void FileWithMeta(string root, string rel, string content = "x")
        {
            var p = Path.Combine(root, rel);
            Directory.CreateDirectory(Path.GetDirectoryName(p));
            File.WriteAllText(p, content);
            File.WriteAllText(p + ".meta", "meta " + rel);
        }

        public static ModuleManifest Manifest(string id, string version, string[] paths, params (string id, string version)[] deps) =>
            new ModuleManifest
            {
                id = id,
                displayName = id,
                category = "Test",
                version = version,
                paths = paths,
                dependencies = deps.Select(d => new ModuleDependency { id = d.id, version = d.version }).ToArray(),
                packages = Array.Empty<PackageDependency>()
            };

        public static void WriteManifest(string root, string rel, ModuleManifest m) => FileWithMeta(root, rel, m.ToJson());

        public static ModuleRelease Release(string id, string version, params (string id, string version)[] deps) =>
            new ModuleRelease { Manifest = Manifest(id, version, new[] { "../" + id }, deps), AssetName = $"{id}-{version}.zip" };

        public static InstalledModule Installed(string id, string version, params (string id, string version)[] deps) =>
            new InstalledModule { Manifest = Manifest(id, version, new[] { "../" + id }, deps), ManifestPath = $"Assets/M/{id}.module.json" };

        public static string Read(string root, string rel) => File.ReadAllText(Path.Combine(root, rel));
        public static bool Exists(string root, string rel) => File.Exists(Path.Combine(root, rel)) || Directory.Exists(Path.Combine(root, rel));
    }
}
