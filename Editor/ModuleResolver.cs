using System.Collections.Generic;
using System.Linq;

namespace ModuleManager
{
    /// <summary>Một bước trong kế hoạch cài: cài mới, cập nhật, hạ cấp hoặc cài lại một module.</summary>
    public sealed class PlanStep
    {
        /// <summary>Bản đang cài, null nếu cài mới.</summary>
        public InstalledModule Installed;

        public ModuleRelease Target;

        public string Id => Target.Id;

        public string Describe()
        {
            var name = Target.Manifest.Title;
            if (Installed == null) return $"Cài {name} {Target.Version}";
            var c = SemVer.Compare(Target.Version, Installed.Version);
            if (c > 0) return $"Cập nhật {name} {Installed.Version} → {Target.Version}";
            if (c < 0) return $"Hạ cấp {name} {Installed.Version} → {Target.Version}";
            return $"Cài lại {name} {Target.Version}";
        }
    }

    /// <summary>Tính thứ tự cài (phụ thuộc trước) và kiểm tra trạng thái cuối không làm hỏng module khác.</summary>
    public static class ModuleResolver
    {
        /// <param name="requested">Bản người dùng chọn — được ưu tiên hơn bản tự kéo theo.</param>
        /// <param name="errors">Nhận lỗi; có lỗi thì không được thực hiện kế hoạch.</param>
        public static List<PlanStep> Plan(IEnumerable<InstalledModule> installed, IEnumerable<ModuleRelease> available,
            IEnumerable<ModuleRelease> requested, List<string> errors)
        {
            var inst = ValidById(installed);
            var latest = available.GroupBy(r => r.Id)
                .ToDictionary(g => g.Key, g => g.OrderByDescending(r => SemVer.Parse(r.Version)).First());

            var req = requested.ToList();
            var reqIds = new HashSet<string>(req.Select(r => r.Id));
            var chosen = req.GroupBy(r => r.Id).ToDictionary(g => g.Key, g => g.First());
            var visited = new HashSet<string>();
            var steps = new List<PlanStep>();

            void Visit(ModuleRelease r)
            {
                if (!visited.Add(r.Id)) return;
                foreach (var d in r.Manifest.dependencies)
                {
                    if (chosen.TryGetValue(d.id, out var picked))
                    {
                        if (SemVer.Compare(picked.Version, d.version) < 0)
                            errors.Add($"{r.Manifest.Title} cần {d.id} >= {d.version}, nhưng đang chọn {picked.Version}");
                        else Visit(picked);
                        continue;
                    }

                    if (inst.TryGetValue(d.id, out var have) && SemVer.Compare(have.Version, d.version) >= 0) continue;

                    if (!latest.TryGetValue(d.id, out var best) || SemVer.Compare(best.Version, d.version) < 0)
                    {
                        errors.Add($"{r.Manifest.Title} cần {d.id} >= {d.version}, nguồn không có bản phù hợp");
                        continue;
                    }

                    chosen[d.id] = best;
                    Visit(best);
                }

                inst.TryGetValue(r.Id, out var cur);
                if (cur == null || reqIds.Contains(r.Id) || cur.Version != r.Version)
                    steps.Add(new PlanStep { Installed = cur, Target = r });
            }

            foreach (var r in req) Visit(r);

            var final = inst.ToDictionary(k => k.Key, v => v.Value.Manifest);
            foreach (var s in steps) final[s.Id] = s.Target.Manifest;
            errors.AddRange(NewErrors(inst.Values.Select(m => m.Manifest), final.Values));
            return steps;
        }

        /// <summary>Lỗi phát sinh nếu gỡ module <paramref name="id"/> (module khác đang cần nó).</summary>
        public static List<string> RemovalErrors(IEnumerable<InstalledModule> installed, string id) =>
            RemovalErrors(installed, new[] { id });

        /// <summary>Lỗi phụ thuộc mới phát sinh nếu gỡ cùng lúc các module <paramref name="ids"/>.</summary>
        public static List<string> RemovalErrors(IEnumerable<InstalledModule> installed, ICollection<string> ids)
        {
            var inst = ValidById(installed);
            return NewErrors(inst.Values.Select(m => m.Manifest), inst.Values.Where(m => !ids.Contains(m.Id)).Select(m => m.Manifest));
        }

        /// <summary>Mọi phụ thuộc chưa thoả trong một tập module.</summary>
        public static List<string> CheckState(IEnumerable<ModuleManifest> modules)
        {
            var list = modules.ToList();
            var map = list.GroupBy(m => m.id).ToDictionary(g => g.Key, g => g.First());
            var errors = new List<string>();
            foreach (var m in list)
            foreach (var d in m.dependencies)
            {
                if (!map.TryGetValue(d.id, out var x))
                    errors.Add($"{m.Title} cần {d.id} >= {d.version} nhưng {d.id} sẽ không còn trong project");
                else if (SemVer.Compare(x.version, d.version) < 0)
                    errors.Add($"{m.Title} cần {d.id} >= {d.version}, nhưng sẽ là {x.version}");
            }
            return errors;
        }

        /// <summary>Chỉ báo lỗi mới do thay đổi gây ra, không báo lỗi project đã có từ trước.</summary>
        private static List<string> NewErrors(IEnumerable<ModuleManifest> before, IEnumerable<ModuleManifest> after) =>
            CheckState(after).Except(CheckState(before)).ToList();

        private static Dictionary<string, InstalledModule> ValidById(IEnumerable<InstalledModule> installed) =>
            installed.Where(m => m.IsValid).GroupBy(m => m.Id).ToDictionary(g => g.Key, g => g.First());
    }
}
