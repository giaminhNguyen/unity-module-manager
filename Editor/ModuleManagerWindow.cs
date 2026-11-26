using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using UnityEditor;
using UnityEditor.PackageManager;
using UnityEngine;

namespace ModuleManager
{
    /// <summary>
    /// Cửa sổ cài / cập nhật / gỡ module. Danh mục module lấy từ nguồn (GitHub release hoặc thư mục),
    /// module đang có lấy từ các file *.module.json trong Assets/.
    /// </summary>
    public class ModuleManagerWindow : EditorWindow
    {
        internal static string ProjectRoot => Directory.GetParent(Application.dataPath).FullName;
        private static string DownloadDir => Path.Combine(ProjectRoot, "Library", "ModuleManager", "Downloads");
        private static readonly string[] SourceTypes = { SourceConfig.GitHub, SourceConfig.Folder };

        private ManagerSettings _settings;
        private int _sourceIndex;
        private List<ModuleRelease> _releases = new List<ModuleRelease>();
        private Dictionary<string, List<ModuleRelease>> _byId = new Dictionary<string, List<ModuleRelease>>();
        private List<InstalledModule> _installed = new List<InstalledModule>();
        private readonly Dictionary<string, int> _picked = new Dictionary<string, int>();
        private readonly HashSet<string> _collapsed = new HashSet<string>();
        private readonly HashSet<string> _selected = new HashSet<string>();
        private string _expanded;
        private string _search = "";
        private string _status = "Bấm Fetch để tải danh sách module từ nguồn.";
        private bool _busy;
        private bool _showSettings;
        private Vector2 _scroll;

        private static readonly string[] Tabs = { "Manager", "Publisher" };
        [SerializeField] private int _tab;
        private PublisherTab _publisher;

        [MenuItem("Window/Module Manager")]
        public static void Open()
        {
            var w = GetWindow<ModuleManagerWindow>("Module Manager");
            w.minSize = new Vector2(760, 420);
        }

        internal static ManagerSettings LoadSettings() => ManagerSettings.Load(ProjectRoot);

        private void OnEnable()
        {
            _settings = LoadSettings();
            Rescan();
            LoadCache();
        }

        /// <summary>Lấy danh sách lần Fetch trước của nguồn đang chọn (giữ được qua domain reload sau khi cài).</summary>
        private void LoadCache()
        {
            if (Source == null) return;
            var cached = ReleaseCache.Load(ProjectRoot, Source, out var at);
            if (cached == null) return;
            SetReleases(cached);
            _status = $"{Source.name}: {_byId.Count} module · dữ liệu lúc {at.ToLocalTime():HH:mm dd/MM} (cache) — Fetch để cập nhật";
        }

        private void SetReleases(List<ModuleRelease> releases)
        {
            _releases = releases;
            _byId = _releases.GroupBy(r => r.Id)
                .ToDictionary(g => g.Key, g => g.OrderByDescending(r => SemVer.Parse(r.Version)).ToList());
            _picked.Clear();
        }

        private void Rescan() => _installed = ModuleFiles.Scan(ProjectRoot);

        private SourceConfig Source =>
            _settings.sources.Count == 0 ? null : _settings.sources[Mathf.Clamp(_sourceIndex, 0, _settings.sources.Count - 1)];

        private InstalledModule Installed(string id) => _installed.FirstOrDefault(m => m.IsValid && m.Id == id);

        // ───────────────────────── GUI ─────────────────────────

        private void OnGUI()
        {
            _tab = ModuleStyles.Tabs(_tab, Tabs);
            if (_tab == 1)
            {
                // Tạo khi mở tab lần đầu: Publisher tính hash mọi module (kể cả .unitypackage lớn) nên không làm sẵn.
                _publisher ??= new PublisherTab(_settings, Repaint);
                _publisher.Draw();
                return;
            }
            DrawManager();
        }

        private void DrawManager()
        {
            DrawToolbar();
            if (_showSettings) DrawSettings();
            if (_settings.devMode)
                EditorGUILayout.HelpBox("Dev mode: project này là nguồn module nên Cài / Gỡ bị khoá để không ghi đè source. " +
                                        "Phát hành ở tab Publisher.", MessageType.Info);
            foreach (var bad in _installed.Where(m => !m.IsValid))
                EditorGUILayout.HelpBox(bad.Error, MessageType.Error);
            if (_settings.sources.Count == 0)
                EditorGUILayout.HelpBox("Chưa có nguồn module. Mở Cấu hình → Thêm nguồn, nhập owner/repo (GitHub) hoặc đường dẫn thư mục, rồi bấm Fetch.", MessageType.Info);
            DrawSummary();

            _scroll = EditorGUILayout.BeginScrollView(_scroll);
            var ids = _byId.Keys.Union(_installed.Where(m => m.IsValid).Select(m => m.Id)).Distinct();
            var rows = ids.Select(id => (id, manifest: Latest(id)?.Manifest ?? Installed(id).Manifest))
                .Where(r => string.IsNullOrEmpty(_search) ||
                            r.manifest.Title.IndexOf(_search, StringComparison.OrdinalIgnoreCase) >= 0 ||
                            r.id.IndexOf(_search, StringComparison.OrdinalIgnoreCase) >= 0).ToList();

            if (rows.Count == 0)
                EditorGUILayout.LabelField(_byId.Count == 0 ? "Chưa có danh sách module. Chọn nguồn rồi bấm Fetch." : "Không có module khớp ô tìm kiếm.",
                    ModuleStyles.Hint);
            else
            {
                DrawColumns(("Đang cài", ModuleStyles.VersionWidth), ("Bản", ModuleStyles.PickWidth),
                    ("Trạng thái", ModuleStyles.StatusWidth), ("", ModuleStyles.ActionWidth + ModuleStyles.IconWidth + 4));
                DrawCategoryTree(rows, r => r.manifest, r => DrawRow(r.id), _collapsed, _selected);
            }
            EditorGUILayout.EndScrollView();

            DrawFooter();
        }

        /// <summary>
        /// Vẽ danh sách thành cây foldout theo category dạng "A/B/C" (dữ liệu từ manifest, tool không biết trước nhóm nào).
        /// Trạng thái gập lưu theo đường dẫn đầy đủ của nhóm. Có <paramref name="selected"/> thì mỗi dòng và mỗi thư mục
        /// có ô chọn (theo id module); ô thư mục chọn / bỏ chọn cả nhóm.
        /// </summary>
        internal static void DrawCategoryTree<T>(IList<T> items, Func<T, ModuleManifest> manifest, Action<T> drawRow,
            HashSet<string> collapsed, HashSet<string> selected = null)
        {
            var row = 0;
            DrawCategoryLevel(items.Select(i => (path: manifest(i).CategoryPath, title: manifest(i).Title, id: manifest(i).id, item: i)).ToList(),
                0, "", drawRow, collapsed, selected, ref row);
        }

        private static void DrawCategoryLevel<T>(List<(string[] path, string title, string id, T item)> items, int depth, string prefix,
            Action<T> drawRow, HashSet<string> collapsed, HashSet<string> selected, ref int row)
        {
            foreach (var r in items.Where(x => x.path.Length == depth).OrderBy(x => x.title, StringComparer.OrdinalIgnoreCase))
            {
                var rect = EditorGUILayout.BeginHorizontal(GUILayout.MinHeight(ModuleStyles.RowHeight));
                ModuleStyles.RowBackground(rect, row++, selected != null && selected.Contains(r.id));
                GUILayout.Space(4 + depth * 14);
                if (selected != null)
                    using (new EditorGUILayout.VerticalScope(GUILayout.Width(ModuleStyles.CheckWidth)))
                    {
                        GUILayout.FlexibleSpace();
                        Check(selected, new[] { r.id });
                        GUILayout.FlexibleSpace();
                    }
                using (new EditorGUILayout.VerticalScope()) drawRow(r.item);
                EditorGUILayout.EndHorizontal();
            }

            foreach (var g in items.Where(x => x.path.Length > depth).GroupBy(x => x.path[depth]).OrderBy(g => g.Key, StringComparer.Ordinal))
            {
                var key = prefix + g.Key;
                var open = !collapsed.Contains(key);
                if (depth == 0) GUILayout.Space(6);
                var rect = EditorGUILayout.BeginHorizontal(GUILayout.Height(depth == 0 ? 24 : 21));
                ModuleStyles.GroupBackground(rect, depth);
                GUILayout.Space(4 + depth * 14);
                if (selected != null) Check(selected, g.Select(x => x.id).ToList());
                var now = EditorGUILayout.Foldout(open, $"{g.Key}  ({g.Count()})", true,
                    depth == 0 ? ModuleStyles.Group : ModuleStyles.SubGroup);
                EditorGUILayout.EndHorizontal();
                if (now != open)
                {
                    if (now) collapsed.Remove(key);
                    else collapsed.Add(key);
                }
                if (!now) continue;
                DrawCategoryLevel(g.ToList(), depth + 1, key + "/", drawRow, collapsed, selected, ref row);
            }
        }

        /// <summary>Hàng tiêu đề cột: cột "Module" giãn, các cột sau cố định (khớp độ rộng với từng dòng).</summary>
        internal static void DrawColumns(params (string title, float width)[] columns)
        {
            using (new EditorGUILayout.HorizontalScope())
            {
                GUILayout.Space(4 + ModuleStyles.CheckWidth + ModuleStyles.ArrowWidth + 4);
                GUILayout.Label("Module", ModuleStyles.Column, GUILayout.MinWidth(0), GUILayout.ExpandWidth(true));
                foreach (var (title, width) in columns) GUILayout.Label(title, ModuleStyles.Column, GUILayout.Width(width));
            }
            ModuleStyles.Separator();
        }

        /// <summary>Ô chọn cho một nhóm id: tick = chọn hết, bỏ tick = bỏ hết, chọn một phần thì hiện dấu "–".</summary>
        private static void Check(HashSet<string> selected, ICollection<string> ids)
        {
            var all = ids.All(selected.Contains);
            EditorGUI.showMixedValue = !all && ids.Any(selected.Contains);
            var now = EditorGUILayout.Toggle(all, GUILayout.Width(16));
            EditorGUI.showMixedValue = false;
            if (now == all) return;
            if (now) selected.UnionWith(ids);
            else selected.ExceptWith(ids);
        }

        /// <summary>Tên + nhãn loại (dòng 1) và id · mô tả (dòng 2), dùng cho cột Module ở cả 2 tab.</summary>
        internal static void DrawNameCell(ModuleManifest m)
        {
            using (new EditorGUILayout.VerticalScope(GUILayout.MinWidth(0), GUILayout.ExpandWidth(true)))
            {
                GUILayout.FlexibleSpace();
                using (new EditorGUILayout.HorizontalScope())
                {
                    GUILayout.Label(new GUIContent(m.Title, m.id), ModuleStyles.Title, GUILayout.ExpandWidth(false));
                    var tag = ModuleStyles.KindTag(m);
                    if (tag != null) GUILayout.Label(tag, ModuleStyles.Tag, GUILayout.ExpandWidth(false));
                    GUILayout.FlexibleSpace();
                }
                var sub = string.IsNullOrWhiteSpace(m.description) ? m.id : m.id + "  ·  " + m.description;
                GUILayout.Label(new GUIContent(sub, m.description), ModuleStyles.Sub, GUILayout.MinWidth(0), GUILayout.ExpandWidth(true));
                GUILayout.FlexibleSpace();
            }
        }

        /// <summary>Gộp danh sách dài cho hộp thoại: quá <paramref name="max"/> dòng thì rút gọn.</summary>
        internal static string Lines(IEnumerable<string> lines, int max = 25)
        {
            var list = lines.ToList();
            return list.Count <= max
                ? string.Join("\n", list)
                : string.Join("\n", list.Take(max)) + $"\n… và {list.Count - max} mục khác";
        }

        private void DrawToolbar()
        {
            using (new EditorGUILayout.HorizontalScope(EditorStyles.toolbar))
            {
                if (_settings.sources.Count == 0) GUILayout.Label("Chưa có nguồn — mở Cấu hình để thêm", EditorStyles.miniLabel);
                else
                {
                    GUILayout.Label("Nguồn", EditorStyles.miniLabel, GUILayout.ExpandWidth(false));
                    EditorGUI.BeginChangeCheck();
                    _sourceIndex = EditorGUILayout.Popup(_sourceIndex, _settings.sources.Select(s => s.name).ToArray(),
                        EditorStyles.toolbarPopup, GUILayout.Width(150));
                    if (EditorGUI.EndChangeCheck())
                    {
                        SetReleases(new List<ModuleRelease>());
                        _status = "";
                        LoadCache();
                    }
                }

                using (new EditorGUI.DisabledScope(_busy || Source == null))
                    if (GUILayout.Button(ModuleStyles.Icon("Refresh", " Fetch", "Tải danh sách module và version từ nguồn (kết quả được lưu cache)"),
                            EditorStyles.toolbarButton, GUILayout.Width(70)))
                        Run(FetchAsync);
                if (GUILayout.Button(new GUIContent("Quét lại", "Đọc lại các *.module.json đang có trong project"), EditorStyles.toolbarButton, GUILayout.Width(60)))
                    Rescan();

                GUILayout.FlexibleSpace();
                _search = GUILayout.TextField(_search, EditorStyles.toolbarSearchField, GUILayout.Width(200));
                _showSettings = GUILayout.Toggle(_showSettings, ModuleStyles.Icon("_Popup", " Cấu hình", "Nguồn, thư mục cài, Dev mode"),
                    EditorStyles.toolbarButton, GUILayout.Width(86));
            }

            TokenField.Draw(Source, "Fine-grained token của repo. Repo public có thể để trống. Lưu theo máy (EditorPrefs), không vào git.");
        }

        private void DrawSettings()
        {
            using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
            {
                EditorGUI.BeginChangeCheck();
                EditorGUILayout.LabelField("Nguồn module (lưu ở " + ManagerSettings.FilePath + ")", EditorStyles.boldLabel);
                for (var i = 0; i < _settings.sources.Count; i++)
                {
                    var s = _settings.sources[i];
                    using (new EditorGUILayout.HorizontalScope())
                    {
                        s.name = EditorGUILayout.TextField(s.name, GUILayout.Width(140));
                        var t = Mathf.Max(0, Array.IndexOf(SourceTypes, s.type));
                        s.type = SourceTypes[EditorGUILayout.Popup(t, SourceTypes, GUILayout.Width(80))];
                        s.location = EditorGUILayout.TextField(s.location);
                        if (GUILayout.Button("Xoá", GUILayout.Width(45)))
                        {
                            _settings.sources.RemoveAt(i);
                            break;
                        }
                    }
                    s.installRoot = EditorGUILayout.TextField(new GUIContent("    Thư mục cài",
                        "Vd Assets/Working: thay thư mục cấp đầu dưới Assets/ của gói. Trống = cài đúng đường dẫn như ở project nguồn."), s.installRoot);
                }
                if (GUILayout.Button("Thêm nguồn", GUILayout.Width(110)))
                    _settings.sources.Add(new SourceConfig { name = "Nguồn mới", type = SourceConfig.GitHub, location = "owner/repo" });

                var presets = SourceConfig.Presets.Where(p => !_settings.sources.Any(p.SameAs)).ToArray();
                if (presets.Length > 0)
                    using (new EditorGUILayout.HorizontalScope())
                    {
                        GUILayout.Label("Preset nguồn", GUILayout.Width(90));
                        foreach (var p in presets)
                            if (GUILayout.Button(new GUIContent("+ " + p.name, p.type + ": " + p.location), GUILayout.ExpandWidth(false)))
                                _settings.sources.Add(p.Clone());
                        GUILayout.FlexibleSpace();
                    }

                _settings.devMode = EditorGUILayout.ToggleLeft(new GUIContent("Dev mode (project nguồn)",
                    "Bật ở project chứa code gốc của module: khoá Cài/Gỡ để không ghi đè source, mở Publisher."), _settings.devMode);
                if (EditorGUI.EndChangeCheck()) _settings.Save(ProjectRoot);
            }
        }

        private void DrawRow(string id)
        {
            _byId.TryGetValue(id, out var versions);
            var inst = Installed(id);
            var manifest = versions?[0].Manifest ?? inst.Manifest;
            ModuleRelease pick = null;

            using (new EditorGUILayout.HorizontalScope(GUILayout.MinHeight(ModuleStyles.RowHeight)))
            {
                var expanded = _expanded == id;
                if (GUILayout.Button(new GUIContent(expanded ? "▾" : "▸", "Chi tiết"), ModuleStyles.Arrow,
                        GUILayout.Width(ModuleStyles.ArrowWidth), GUILayout.ExpandHeight(true)))
                    _expanded = expanded ? null : id;
                DrawNameCell(manifest);

                ModuleStyles.CellLabel(new GUIContent(inst?.Version ?? "—"), ModuleStyles.VersionWidth);

                using (new EditorGUILayout.VerticalScope(GUILayout.Width(ModuleStyles.PickWidth)))
                {
                    GUILayout.FlexibleSpace();
                    if (versions != null)
                    {
                        _picked.TryGetValue(id, out var idx);
                        idx = Mathf.Clamp(EditorGUILayout.Popup(idx, versions.Select(v => v.Version).ToArray(), GUILayout.Width(ModuleStyles.PickWidth - 6)),
                            0, versions.Count - 1);
                        _picked[id] = idx;
                        pick = versions[idx];
                    }
                    else GUILayout.Label(new GUIContent("—", "Nguồn không có module này (chưa Fetch hoặc cài từ nguồn khác)"), ModuleStyles.Cell);
                    GUILayout.FlexibleSpace();
                }

                using (new EditorGUILayout.VerticalScope(GUILayout.Width(ModuleStyles.StatusWidth)))
                {
                    GUILayout.FlexibleSpace();
                    var latest = versions?[0];
                    if (inst == null) ModuleStyles.Status("Chưa cài", ModuleStyles.Muted);
                    else if (latest == null) ModuleStyles.Status("Ngoài nguồn", ModuleStyles.Muted, "Nguồn đang chọn không có module này");
                    else if (SemVer.Compare(latest.Version, inst.Version) > 0) ModuleStyles.Status("Có bản " + latest.Version, ModuleStyles.Amber);
                    else ModuleStyles.Status("Đã cài", ModuleStyles.Green);
                    GUILayout.FlexibleSpace();
                }

                var locked = _busy || _settings.devMode;
                using (new EditorGUILayout.VerticalScope(GUILayout.Width(ModuleStyles.ActionWidth + ModuleStyles.IconWidth + 4)))
                {
                    GUILayout.FlexibleSpace();
                    using (new EditorGUILayout.HorizontalScope())
                    {
                        using (new EditorGUI.DisabledScope(locked || pick == null))
                            if (GUILayout.Button(ActionLabel(inst, pick), GUILayout.Width(ModuleStyles.ActionWidth), GUILayout.Height(22)))
                            {
                                var target = pick;
                                Run(() => InstallAsync(new[] { target }));
                            }
                        using (new EditorGUI.DisabledScope(locked || inst == null))
                            if (GUILayout.Button(ModuleStyles.Icon("TreeEditor.Trash", "", "Gỡ module"),
                                    GUILayout.Width(ModuleStyles.IconWidth), GUILayout.Height(22)))
                                EditorApplication.delayCall += () => Remove(new[] { inst });
                    }
                    GUILayout.FlexibleSpace();
                }
            }

            if (_expanded == id) DrawDetails(manifest, pick, inst);
        }

        private static void DrawDetails(ModuleManifest manifest, ModuleRelease pick, InstalledModule inst)
        {
            using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
            {
                var m = pick?.Manifest ?? manifest;
                EditorGUILayout.LabelField("Id", m.id);
                if (!string.IsNullOrWhiteSpace(m.description)) EditorGUILayout.LabelField(m.description, EditorStyles.wordWrappedLabel);
                if (m.dependencies.Length > 0)
                    EditorGUILayout.LabelField("Cần", string.Join(", ", m.dependencies.Select(d => $"{d.id} >= {d.version}")), EditorStyles.wordWrappedLabel);
                if (m.packages.Length > 0)
                    EditorGUILayout.LabelField("Package UPM", string.Join(", ", m.packages.Select(p => p.name)), EditorStyles.wordWrappedLabel);
                var where = inst == null ? new List<string>() : inst.Paths.Concat(inst.Manifest.importedPaths).ToList();
                if (where.Count > 0)
                    EditorGUILayout.LabelField("Đang ở", string.Join("\n", where), EditorStyles.wordWrappedLabel);
                if (pick != null)
                {
                    EditorGUILayout.LabelField($"Bản {pick.Version}", $"{pick.PublishedAt}  ·  {pick.Size / 1024f:0} KB");
                    if (!string.IsNullOrWhiteSpace(pick.Changelog)) EditorGUILayout.LabelField(pick.Changelog, EditorStyles.wordWrappedMiniLabel);
                }
            }
        }

        private void DrawFooter()
        {
            var updates = Updatable();
            // Bản cài lấy theo version đang chọn ở từng dòng; mục không có trên nguồn thì bỏ qua.
            var toInstall = _selected.Select(Picked).Where(r => r != null).ToList();
            var toRemove = _selected.Select(Installed).Where(m => m != null).ToList();
            var locked = _busy || _settings.devMode;
            ModuleStyles.BeginBar();
            GUILayout.Label($"Đã chọn {_selected.Count}", EditorStyles.boldLabel, GUILayout.Width(90), GUILayout.Height(22));
            using (new EditorGUI.DisabledScope(locked || toInstall.Count == 0))
                if (GUILayout.Button($"Cài / Cập nhật ({toInstall.Count})", GUILayout.Height(22), GUILayout.Width(150)))
                    Run(async () =>
                    {
                        if (await InstallAsync(toInstall)) _selected.Clear();
                    });
            using (new EditorGUI.DisabledScope(locked || toRemove.Count == 0))
                if (GUILayout.Button($"Gỡ ({toRemove.Count})", GUILayout.Height(22), GUILayout.Width(80)))
                    EditorApplication.delayCall += () =>
                    {
                        if (Remove(toRemove)) _selected.Clear();
                    };
            using (new EditorGUI.DisabledScope(_selected.Count == 0))
                if (GUILayout.Button("Bỏ chọn", GUILayout.Height(22), GUILayout.Width(70)))
                    _selected.Clear();
            GUILayout.FlexibleSpace();
            using (new EditorGUI.DisabledScope(locked || updates.Count == 0))
                if (GUILayout.Button($"Cập nhật tất cả ({updates.Count})", GUILayout.Height(22), GUILayout.Width(150)))
                    Run(() => InstallAsync(updates));
            ModuleStyles.EndBar();
        }

        /// <summary>Dải tóm tắt: đếm theo trạng thái + thời điểm dữ liệu / thông báo gần nhất.</summary>
        private void DrawSummary()
        {
            var installed = _installed.Count(m => m.IsValid);
            var updates = Updatable().Count;
            var notInstalled = _byId.Keys.Count(id => Installed(id) == null);
            using (new EditorGUILayout.HorizontalScope(ModuleStyles.Bar))
            {
                ModuleStyles.Status($"{installed} đã cài", ModuleStyles.Green, null, GUILayout.ExpandWidth(false));
                GUILayout.Space(10);
                ModuleStyles.Status($"{updates} có bản mới", updates > 0 ? ModuleStyles.Amber : ModuleStyles.Muted, null, GUILayout.ExpandWidth(false));
                GUILayout.Space(10);
                ModuleStyles.Status($"{notInstalled} chưa cài", ModuleStyles.Muted, null, GUILayout.ExpandWidth(false));
                GUILayout.Space(16);
                GUILayout.Label(new GUIContent(_status, _status), ModuleStyles.Sub, GUILayout.MinWidth(0), GUILayout.ExpandWidth(true));
            }
        }

        private static string ActionLabel(InstalledModule inst, ModuleRelease pick)
        {
            if (inst == null || pick == null) return "Cài";
            var c = SemVer.Compare(pick.Version, inst.Version);
            return c > 0 ? "Cập nhật" : c < 0 ? "Hạ cấp" : "Cài lại";
        }

        // ───────────────────────── Hành động ─────────────────────────

        private ModuleRelease Latest(string id) => _byId.TryGetValue(id, out var v) ? v[0] : null;

        /// <summary>Bản đang chọn ở ô version của dòng (mặc định bản mới nhất), null nếu nguồn không có module này.</summary>
        private ModuleRelease Picked(string id)
        {
            if (!_byId.TryGetValue(id, out var v)) return null;
            _picked.TryGetValue(id, out var idx);
            return v[Mathf.Clamp(idx, 0, v.Count - 1)];
        }

        private List<ModuleRelease> Updatable() =>
            _installed.Where(m => m.IsValid)
                .Select(m => (m, latest: Latest(m.Id)))
                .Where(t => t.latest != null && SemVer.Compare(t.latest.Version, t.m.Version) > 0)
                .Select(t => t.latest).ToList();

        private async Task FetchAsync()
        {
            _status = "Đang tải danh sách…";
            Repaint();
            var releases = await SourceFactory.Create(Source, ProjectRoot).FetchAsync();
            SetReleases(releases);
            ReleaseCache.Save(ProjectRoot, Source, releases, DateTime.UtcNow);
            Rescan();
            _status = $"{Source.name}: {_byId.Count} module, {_releases.Count} bản phát hành — Fetch lúc {DateTime.Now:HH:mm}";
        }

        /// <returns>true nếu đã cài xong (không lỗi, không huỷ).</returns>
        private async Task<bool> InstallAsync(IReadOnlyList<ModuleRelease> requested)
        {
            Rescan();
            var errors = new List<string>();
            var plan = ModuleResolver.Plan(_installed, _releases, requested, errors);
            if (errors.Count > 0)
            {
                EditorUtility.DisplayDialog("Không cài được", string.Join("\n", errors.Distinct()), "OK");
                return false;
            }
            if (plan.Count == 0)
            {
                _status = "Đã đúng version, không có gì để làm.";
                return true;
            }

            var missing = ModuleInstaller.MissingPackages(ProjectRoot, plan.Select(s => s.Target.Manifest));
            var message = Lines(plan.Select(s => "• " + s.Describe()));
            if (!string.IsNullOrWhiteSpace(Source.installRoot)) message += "\n\nCài vào: " + Source.installRoot.Trim();
            if (plan.Any(s => s.Installed != null)) message += "\n\nThư mục cũ của module được cập nhật sẽ bị xoá rồi chép bản mới.";
            if (missing.Count > 0) message += "\n\nThêm package: " + string.Join(", ", missing.Select(p => p.name));
            var unityPackages = plan.Select(s => s.Target.Manifest.unityPackage).Where(u => !string.IsNullOrEmpty(u)).ToList();
            if (unityPackages.Count > 0) message += "\n\nImport: " + string.Join(", ", unityPackages);
            if (!EditorUtility.DisplayDialog("Module Manager", message, "Thực hiện", "Huỷ")) return false;

            var source = SourceFactory.Create(Source, ProjectRoot);
            var zips = await ModuleInstaller.DownloadAsync(source, plan, DownloadDir,
                (info, p) => EditorUtility.DisplayProgressBar("Module Manager", info, p));
            EditorUtility.DisplayProgressBar("Module Manager", "Chép file…", 1f);
            var imports = ModuleInstaller.Apply(ProjectRoot, plan, zips, _installed, Source.installRoot);
            AssetDatabase.Refresh();
            // File .unitypackage nằm trong Library/ nên không bao giờ vào Assets/ hay git.
            foreach (var p in imports) AssetDatabase.ImportPackage(p, false);
            if (missing.Count > 0) AddPackages(missing);
            _status = "Xong: " + string.Join(", ", plan.Select(s => $"{s.Target.Manifest.Title} {s.Target.Version}"));
            return true;
        }

        /// <summary>Gỡ một hoặc nhiều module: một hộp xác nhận, xoá hết, Refresh một lần, gỡ package một lần.</summary>
        /// <returns>true nếu đã gỡ.</returns>
        private bool Remove(IReadOnlyList<InstalledModule> modules)
        {
            var ids = new HashSet<string>(modules.Select(m => m.Id));
            var errors = ModuleResolver.RemovalErrors(_installed, ids);
            if (errors.Count > 0)
            {
                EditorUtility.DisplayDialog("Không gỡ được", string.Join("\n", errors), "OK");
                return false;
            }
            var packages = ModuleInstaller.PackagesToRemove(_installed, modules.ToList());
            var what = modules.Count == 1 ? $"{modules[0].Manifest.Title} {modules[0].Version}" : $"{modules.Count} module";
            var message = $"Gỡ {what}? Sẽ xoá:\n" +
                          Lines(modules.SelectMany(m => m.Paths.Concat(m.Manifest.importedPaths).Append(m.ManifestPath)));
            if (packages.Count > 0) message += "\n\nGỡ package: " + string.Join(", ", packages);
            if (!EditorUtility.DisplayDialog("Gỡ module", message, "Gỡ", "Huỷ")) return false;
            foreach (var m in modules) ModuleFiles.Delete(ProjectRoot, m);
            AssetDatabase.Refresh();
            if (packages.Count > 0) RemovePackages(packages);
            Rescan();
            _status = "Đã gỡ " + string.Join(", ", modules.Select(m => m.Manifest.Title)) + ".";
            return true;
        }

        private static void AddPackages(List<PackageDependency> packages) =>
            ChangePackages(packages.Select(p => string.IsNullOrWhiteSpace(p.source) ? p.name : p.source).ToArray(), null);

        private static void RemovePackages(List<string> names) => ChangePackages(null, names.ToArray());

        private static void ChangePackages(string[] add, string[] remove)
        {
            var ids = (add ?? Array.Empty<string>()).Concat(remove ?? Array.Empty<string>()).ToArray();
            var request = Client.AddAndRemove(add, remove);
            void Poll()
            {
                if (!request.IsCompleted) return;
                EditorApplication.update -= Poll;
                if (request.Status == StatusCode.Failure) Debug.LogError("[ModuleManager] Đổi package lỗi: " + request.Error?.message);
                else Debug.Log("[ModuleManager] Đã " + (add != null ? "thêm" : "gỡ") + " package: " + string.Join(", ", ids));
            }
            EditorApplication.update += Poll;
        }

        // Dời ra ngoài OnGUI: hộp thoại mở giữa lúc vẽ làm hỏng layout của frame đó.
        private void Run(Func<Task> action) => EditorApplication.delayCall += () => RunNow(action);

        private async void RunNow(Func<Task> action)
        {
            if (_busy) return;
            _busy = true;
            try
            {
                await action();
            }
            catch (Exception e)
            {
                Debug.LogException(e);
                _status = "Lỗi: " + e.Message;
                EditorUtility.DisplayDialog("Module Manager", e.Message, "OK");
            }
            finally
            {
                _busy = false;
                EditorUtility.ClearProgressBar();
                Rescan();
                Repaint();
            }
        }
    }
}
