using System;
using System.Linq;
using NUnit.Framework;

namespace ModuleManager.Tests
{
    public class ScopedRegistryTests
    {
        private const string Npm = "https://registry.npmjs.org";

        private const string Plain = "{\n  \"dependencies\": {\n    \"com.unity.ugui\": \"2.0.0\",\n    \"com.x.git\": \"https://h/r.git#v1\"\n  }\n}\n";

        private static string Manifest(string packages) =>
            "{\"id\":\"plugin.x\",\"version\":\"1.0.0\",\"paths\":[],\"packages\":[" + packages + "]}";

        [Test]
        public void Ensure_AddsRegistryAndKeepsDependencies()
        {
            var json = ScopedRegistries.Ensure(Plain, Npm, new[] { "com.kyrylokuzyk" });
            var reg = ScopedRegistries.Read(json).Single();
            Assert.AreEqual(Npm, reg.url);
            Assert.AreEqual("registry.npmjs.org", reg.name);
            CollectionAssert.AreEqual(new[] { "com.kyrylokuzyk" }, reg.scopes);
            StringAssert.Contains("\"com.x.git\": \"https://h/r.git#v1\"", json);
            StringAssert.Contains("\"com.unity.ugui\": \"2.0.0\"", json);
        }

        [Test]
        public void Ensure_SameRegistryAgain_ReturnsSameString()
        {
            var once = ScopedRegistries.Ensure(Plain, Npm, new[] { "com.kyrylokuzyk" });
            Assert.AreSame(once, ScopedRegistries.Ensure(once, Npm + "/", new[] { "com.kyrylokuzyk" }), "url khác dấu / cuối vẫn là một registry");
        }

        [Test]
        public void Ensure_MergesScopeIntoExistingRegistryAndKeepsOthers()
        {
            var json = "{\"scopedRegistries\":[{\"name\":\"npm\",\"url\":\"https://registry.npmjs.org/\",\"scopes\":[\"com.a\"]}," +
                       "{\"name\":\"openupm\",\"url\":\"https://package.openupm.com\",\"scopes\":[\"com.b\"]}]," +
                       "\"dependencies\":{\"com.a.x\":\"1.0.0\"}}";
            var result = ScopedRegistries.Ensure(json, Npm, new[] { "com.kyrylokuzyk" });
            var regs = ScopedRegistries.Read(result);
            Assert.AreEqual(2, regs.Length);
            CollectionAssert.AreEqual(new[] { "com.a", "com.kyrylokuzyk" }, regs[0].scopes);
            Assert.AreEqual("npm", regs[0].name, "giữ tên registry có sẵn");
            CollectionAssert.AreEqual(new[] { "com.b" }, regs[1].scopes);
            StringAssert.Contains("\"com.a.x\":\"1.0.0\"", result);
        }

        [Test]
        public void Ensure_ScopeOwnedByOtherRegistry_IsLeftAlone()
        {
            var json = "{\"scopedRegistries\":[{\"name\":\"openupm\",\"url\":\"https://package.openupm.com\",\"scopes\":[\"com.kyrylokuzyk\"]}],\"dependencies\":{}}";
            Assert.AreSame(json, ScopedRegistries.Ensure(json, Npm, new[] { "com.kyrylokuzyk" }));
        }

        [Test]
        public void Ensure_IgnoresKeyInsideStringsAndNestedObjects()
        {
            var json = "{\"dependencies\":{\"scopedRegistries\":\"1.0.0\",\"com.q\":\"a \\\"scopedRegistries\\\": [\"}}";
            var result = ScopedRegistries.Ensure(json, Npm, new[] { "com.kyrylokuzyk" });
            Assert.AreEqual(1, ScopedRegistries.Read(result).Length);
            StringAssert.Contains("\"scopedRegistries\":\"1.0.0\"", result);
            StringAssert.Contains("a \\\"scopedRegistries\\\": [", result);
        }

        [Test]
        public void Ensure_EmptyObject()
        {
            var result = ScopedRegistries.Ensure("{}", Npm, new[] { "com.kyrylokuzyk" });
            Assert.AreEqual(1, ScopedRegistries.Read(result).Length);
        }

        [Test]
        public void Identifier_GitUrlVersionOrName()
        {
            Assert.AreEqual("https://h/r.git#v1", new PackageDependency { name = "com.x", source = "https://h/r.git#v1" }.Identifier);
            Assert.AreEqual("com.x@1.5.3", new PackageDependency { name = "com.x", source = " 1.5.3 " }.Identifier);
            Assert.AreEqual("com.x", new PackageDependency { name = "com.x", source = "" }.Identifier);
        }

        [Test]
        public void Manifest_RegistryValidation()
        {
            var ok = ModuleManifest.Parse(Manifest("{\"name\":\"com.kyrylokuzyk.primetween\",\"source\":\"1.5.3\",\"registry\":\"https://registry.npmjs.org\",\"scopes\":[\"com.kyrylokuzyk\"]}"));
            Assert.IsTrue(ok.packages[0].HasRegistry);

            var noRegistry = ModuleManifest.Parse(Manifest("{\"name\":\"com.x\",\"source\":\"https://h/r.git\"}"));
            Assert.IsFalse(noRegistry.packages[0].HasRegistry);
            Assert.IsNotNull(noRegistry.packages[0].scopes);

            Assert.Throws<FormatException>(() => ModuleManifest.Parse(Manifest("{\"name\":\"com.k.p\",\"registry\":\"http://r.org\",\"scopes\":[\"com.k\"]}")), "chỉ https");
            Assert.Throws<FormatException>(() => ModuleManifest.Parse(Manifest("{\"name\":\"com.k.p\",\"registry\":\"https://r.org\",\"scopes\":[]}")), "thiếu scope");
            Assert.Throws<FormatException>(() => ModuleManifest.Parse(Manifest("{\"name\":\"com.other.p\",\"registry\":\"https://r.org\",\"scopes\":[\"com.k\"]}")), "package ngoài scope");
        }
    }
}
