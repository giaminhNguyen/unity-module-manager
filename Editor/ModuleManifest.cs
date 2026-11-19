using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace ModuleManager
{
    /// <summary>
    /// Mô tả một module, lưu ở file <c>*.module.json</c>. File này đi kèm module khi cài,
    /// nên Manager đọc nó để biết module nào đang có, version nào, gồm những đường dẫn nào.
    /// </summary>
    [Serializable]
    public class ModuleManifest
    {
        public const string FileSuffix = ".module.json";

        [Tooltip("Id duy nhất, dạng nhóm.tên (vd myframework.core). Không đổi sau khi đã publish.")]
        public string id;

        [Tooltip("Tên hiển thị trong cửa sổ Module Manager.")]
        public string displayName;

        [Tooltip("Nhóm hiển thị (Core, UI, Tools...). Các nhóm xếp theo tên.")]
        public string category;

        [Tooltip("Mô tả ngắn module làm gì, cần define/thư viện gì.")]
        public string description;

        [Tooltip("Version MAJOR.MINOR.PATCH. Publisher tự tăng khi phát hành.")]
        public string version;

        [Tooltip("Thư mục/file thuộc module, tương đối từ thư mục chứa file manifest. Phải nằm trong Assets/.")]
        public string[] paths;

        [Tooltip("Module phải có trước, kèm version tối thiểu. Manager tự cài kèm.")]
        public ModuleDependency[] dependencies;

        [Tooltip("Package UPM cần có trong Packages/manifest.json. Thiếu thì Manager tự thêm sau khi cài, gỡ module thì gỡ theo.")]
        public PackageDependency[] packages;

        [Tooltip("Tên file .unitypackage (chỉ tên file) trong thư mục .unitypackage của Publisher. Cài = Unity tự import gói này.")]
        public string unityPackage;

        [Tooltip("Project path mà .unitypackage import vào (vd Assets/Plugins/Demigiant). Gỡ module sẽ xoá các path này.")]
        public string[] importedPaths;

        [Tooltip("Hash nội dung lúc publish (Publisher tự ghi) để biết module đã đổi chưa. File nguồn để trống.")]
        public string contentHash;

        public string Title => string.IsNullOrWhiteSpace(displayName) ? id : displayName;

        /// <summary>Các cấp thư mục hiển thị, tách từ category dạng "A/B/C". Trống = "Khác".</summary>
        public string[] CategoryPath
        {
            get
            {
                var parts = (category ?? "").Split('/').Select(s => s.Trim()).Where(s => s.Length > 0).ToArray();
                return parts.Length > 0 ? parts : new[] { "Khác" };
            }
        }

        /// <summary>Parse và kiểm tra tối thiểu. Ném FormatException nếu thiếu id / version / paths.</summary>
        public static ModuleManifest Parse(string json)
        {
            ModuleManifest m;
            try
            {
                m = JsonUtility.FromJson<ModuleManifest>(json);
            }
            catch (Exception e)
            {
                throw new FormatException("JSON manifest hỏng: " + e.Message);
            }

            if (m == null) throw new FormatException("JSON manifest rỗng");
            m.Normalize();
            if (string.IsNullOrWhiteSpace(m.id)) throw new FormatException("Manifest thiếu id");
            if (!SemVer.TryParse(m.version, out _)) throw new FormatException($"{m.id}: version '{m.version}' không hợp lệ");
            if (m.paths.Length == 0 && m.packages.Length == 0 && m.unityPackage.Length == 0)
                throw new FormatException($"{m.id}: cần ít nhất một trong paths, packages, unityPackage");
            if (m.unityPackage.Length > 0 && (m.unityPackage.IndexOfAny(new[] { '/', '\\', ':' }) >= 0 ||
                                              !m.unityPackage.EndsWith(".unitypackage", StringComparison.OrdinalIgnoreCase)))
                throw new FormatException($"{m.id}: unityPackage '{m.unityPackage}' phải là tên file .unitypackage, không kèm thư mục");
            foreach (var p in m.importedPaths)
                if (!ModuleFiles.IsSafeEntry(p))
                    throw new FormatException($"{m.id}: importedPaths '{p}' phải là project path trong Assets/ (vd Assets/Plugins/X)");
            foreach (var d in m.dependencies)
                if (string.IsNullOrWhiteSpace(d.id) || !SemVer.TryParse(d.version, out _))
                    throw new FormatException($"{m.id}: dependency '{d.id}' thiếu id hoặc version hợp lệ");
            foreach (var p in m.packages)
                if (string.IsNullOrWhiteSpace(p.name))
                    throw new FormatException($"{m.id}: package thiếu name");
            return m;
        }

        public string ToJson()
        {
            Normalize();
            return JsonUtility.ToJson(this, true);
        }

        public ModuleManifest Clone() => Parse(ToJson());

        private void Normalize()
        {
            paths ??= Array.Empty<string>();
            dependencies ??= Array.Empty<ModuleDependency>();
            packages ??= Array.Empty<PackageDependency>();
            unityPackage = unityPackage?.Trim() ?? "";
            importedPaths = (importedPaths ?? Array.Empty<string>()).Select(p => ModuleFiles.Norm(p ?? "").Trim().TrimEnd('/')).ToArray();
            category = string.IsNullOrWhiteSpace(category) ? "Khác" : category;
            description ??= "";
            contentHash ??= "";
        }
    }

    [Serializable]
    public class ModuleDependency
    {
        [Tooltip("Id module phụ thuộc.")]
        public string id;

        [Tooltip("Version tối thiểu chấp nhận được.")]
        public string version;
    }

    [Serializable]
    public class PackageDependency
    {
        [Tooltip("Tên package UPM (vd com.cysharp.unitask). Dùng để kiểm tra đã có trong manifest.json chưa.")]
        public string name;

        [Tooltip("Nguồn để thêm: git URL, hoặc để trống để thêm theo tên từ registry.")]
        public string source;
    }

    /// <summary>Module đang có trong project, đọc từ một file *.module.json.</summary>
    public sealed class InstalledModule
    {
        /// <summary>Null nếu file manifest hỏng (xem <see cref="Error"/>).</summary>
        public ModuleManifest Manifest;

        /// <summary>Đường dẫn file manifest, tính từ root project, dạng "Assets/...".</summary>
        public string ManifestPath;

        /// <summary>Đường dẫn các thư mục/file của module, tính từ root project.</summary>
        public List<string> Paths = new List<string>();

        public string Error;

        public bool IsValid => Manifest != null && Error == null;
        public string Id => Manifest?.id;
        public string Version => Manifest?.version;
    }
}
