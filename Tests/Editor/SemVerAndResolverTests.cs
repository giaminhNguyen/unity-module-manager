using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using static ModuleManager.Tests.TestUtil;

namespace ModuleManager.Tests
{
    public class SemVerTests
    {
        [TestCase("1.2.10", "1.2.9", 1)]
        [TestCase("2.0.0", "10.0.0", -1)]
        [TestCase("1.0.0-beta", "1.0.0", -1)]
        [TestCase("v1.0.0", "1.0.0", 0)]
        public void Compare(string a, string b, int sign) => Assert.AreEqual(sign, System.Math.Sign(SemVer.Compare(a, b)));

        [TestCase("1.2")]
        [TestCase("1.2.x")]
        [TestCase("+1.2.3")]
        [TestCase("")]
        public void RejectsInvalid(string s) => Assert.IsFalse(SemVer.TryParse(s, out _));

        [Test]
        public void Bump()
        {
            Assert.AreEqual("1.2.4", SemVer.Parse("1.2.3").Bump(BumpKind.Patch).ToString());
            Assert.AreEqual("1.3.0", SemVer.Parse("1.2.3").Bump(BumpKind.Minor).ToString());
            Assert.AreEqual("2.0.0", SemVer.Parse("1.2.3").Bump(BumpKind.Major).ToString());
            Assert.AreEqual("1.2.3", SemVer.Parse("1.2.3-rc").Bump(BumpKind.Patch).ToString());
        }

        [Test]
        public void NextVersion()
        {
            Assert.AreEqual("1.0.0", ModulePublisher.NextVersion("1.0.0", null, BumpKind.Patch));
            Assert.AreEqual("1.0.1", ModulePublisher.NextVersion("1.0.0", "1.0.0", BumpKind.Patch));
            Assert.AreEqual("2.0.0", ModulePublisher.NextVersion("2.0.0", "1.4.0", BumpKind.Patch), "version nâng tay cao hơn thì giữ");
        }
    }

    public class ResolverTests
    {
        [Test]
        public void InstallsDependenciesFirst()
        {
            var releases = new[] { Release("core", "1.0.0"), Release("eco", "1.0.0", ("core", "1.0.0")), Release("iap", "1.0.0", ("eco", "1.0.0")) };
            var errors = new List<string>();
            var plan = ModuleResolver.Plan(new InstalledModule[0], releases, new[] { releases[2] }, errors);
            CollectionAssert.IsEmpty(errors);
            CollectionAssert.AreEqual(new[] { "core", "eco", "iap" }, plan.Select(s => s.Id));
        }

        [Test]
        public void SkipsSatisfiedDependency()
        {
            var releases = new[] { Release("core", "1.1.0"), Release("eco", "1.0.0", ("core", "1.0.0")) };
            var errors = new List<string>();
            var plan = ModuleResolver.Plan(new[] { Installed("core", "1.0.0") }, releases, new[] { releases[1] }, errors);
            CollectionAssert.IsEmpty(errors);
            CollectionAssert.AreEqual(new[] { "eco" }, plan.Select(s => s.Id));
        }

        [Test]
        public void UpgradesDependencyBelowMinimum()
        {
            var releases = new[] { Release("core", "1.0.0"), Release("core", "1.2.0"), Release("eco", "1.0.0", ("core", "1.2.0")) };
            var errors = new List<string>();
            var plan = ModuleResolver.Plan(new[] { Installed("core", "1.0.0") }, releases, new[] { releases[2] }, errors);
            CollectionAssert.IsEmpty(errors);
            Assert.AreEqual("1.2.0", plan.Single(s => s.Id == "core").Target.Version);
        }

        [Test]
        public void ReportsMissingDependency()
        {
            var errors = new List<string>();
            ModuleResolver.Plan(new InstalledModule[0], new[] { Release("eco", "1.0.0", ("core", "1.0.0")) },
                new[] { Release("eco", "1.0.0", ("core", "1.0.0")) }, errors);
            Assert.IsTrue(errors.Any(e => e.Contains("core")), string.Join("\n", errors));
        }

        [Test]
        public void BlocksDowngradeThatBreaksDependent()
        {
            var installed = new[] { Installed("core", "1.2.0"), Installed("eco", "1.0.0", ("core", "1.2.0")) };
            var errors = new List<string>();
            ModuleResolver.Plan(installed, new[] { Release("core", "1.0.0") }, new[] { Release("core", "1.0.0") }, errors);
            Assert.AreEqual(1, errors.Count);
        }

        [Test]
        public void RemovalBlockedByDependent()
        {
            var installed = new[] { Installed("core", "1.0.0"), Installed("eco", "1.0.0", ("core", "1.0.0")) };
            Assert.AreEqual(1, ModuleResolver.RemovalErrors(installed, "core").Count);
            CollectionAssert.IsEmpty(ModuleResolver.RemovalErrors(installed, "eco"));
        }

        [Test]
        public void AlreadyBrokenProjectDoesNotBlockUnrelatedInstall()
        {
            var installed = new[] { Installed("eco", "1.0.0", ("core", "1.0.0")) };
            var errors = new List<string>();
            var plan = ModuleResolver.Plan(installed, new[] { Release("grid", "1.0.0") }, new[] { Release("grid", "1.0.0") }, errors);
            CollectionAssert.IsEmpty(errors);
            Assert.AreEqual(1, plan.Count);
        }

        [Test]
        public void TopoOrderAndCycleDetection()
        {
            var root = NewRoot();
            try
            {
                Dir(root, "Assets/M");
                foreach (var id in new[] { "a", "b" }) Dir(root, "Assets/" + id);
                WriteManifest(root, "Assets/M/a.module.json", Manifest("a", "1.0.0", new[] { "../a" }, ("b", "1.0.0")));
                WriteManifest(root, "Assets/M/b.module.json", Manifest("b", "1.0.0", new[] { "../b" }, ("a", "1.0.0")));
                var issues = ModulePublisher.Validate(root, ModuleFiles.Scan(root));
                Assert.IsTrue(issues.Any(i => i.Message.Contains("vòng")), string.Join("\n", issues));
            }
            finally { Cleanup(root); }
        }
    }
}
