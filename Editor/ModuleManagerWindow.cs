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
        private string _expanded;
        private string _search = "";
        private string _status = "Bấm Fetch để tải danh sách module từ nguồn.";
        private bool _busy;
        private bool _showSettings;
        private Vector2 _scroll;

        [MenuItem("Tools/Module Manager/Manager", priority = 0)]
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
        }

        private void Rescan() => _installed = ModuleFiles.Scan(ProjectRoot);

        private SourceConfig Source =>
            _settings.sources.Count == 0 ? null : _settings.sources[Mathf.Clamp(_sourceIndex, 0, _settings.sources.Count - 1)];

        private InstalledModule Installed(string id) => _installed.FirstOrDefault(m => m.IsValid && m.Id == id);

        // ───────────────────────── GUI ─────────────────────────

        private void OnGUI()
        {
            DrawToolbar();
            if (_showSettings) DrawSettings();
            if (_settings.devMode)
                EditorGUILayout.HelpBox("Dev mode: project này là nguồn module nên Cài / Gỡ bị khoá để không ghi đè source. " +
                                        "Phát hành bằng Tools/Module Manager/Publisher.", MessageType.Info);
            foreach (var bad in _installed.Where(m => !m.IsValid))
                EditorGUILayout.HelpBox(bad.Error, MessageType.Error);
            if (_settings.sources.Count == 0)
                EditorGUILayout.HelpBox("Chưa có nguồn module. Mở Cấu hình → Thêm nguồn, nhập owner/repo (GitHub) hoặc đường dẫn thư mục, rồi bấm Fetch.", MessageType.Info);
            EditorGUILayout.LabelField(_status, EditorStyles.miniLabel);

            _scroll = EditorGUILayout.BeginScrollView(_scroll);
            var ids = _byId.Keys.Union(_installed.Where(m => m.IsValid).Select(m => m.Id)).Distinct();
            var rows = ids.Select(id => (id, manifest: Latest(id)?.Manifest ?? Installed(id).Manifest))
                .Where(r => string.IsNullOrEmpty(_search) ||
                            r.manifest.Title.IndexOf(_search, StringComparison.OrdinalIgnoreCase) >= 0 ||
                            r.id.IndexOf(_search, StringComparison.OrdinalIgnoreCase) >= 0);

            foreach (var group in rows.GroupBy(r => r.manifest.category).OrderBy(g => g.Key, StringComparer.Ordinal))
            {
                var open = !_collapsed.Contains(group.Key);
                var now = EditorGUILayout.Foldout(open, $"{group.Key} ({group.Count()})", true, EditorStyles.foldoutHeader);
                if (now != open)
                {
                    if (now) _collapsed.Remove(group.Key);
                    else _collapsed.Add(group.Key);
                }
                if (!now) continue;
                foreach (var r in group.OrderBy(r => r.manifest.Title, StringComparer.OrdinalIgnoreCase)) DrawRow(r.id);
                EditorGUILayout.Space(4);
            }
            EditorGUILayout.EndScrollView();

            DrawFooter();
        }

        private void DrawToolbar()
        {
            using (new EditorGUILayout.HorizontalScope(EditorStyles.toolbar))
            {
                if (_settings.sources.Count == 0) GUILayout.Label("Chưa có nguồn — mở Cấu hình để thêm", EditorStyles.miniLabel);
                else
                    _sourceIndex = EditorGUILayout.Popup(_sourceIndex, _settings.sources.Select(s => s.name).ToArray(),
                        EditorStyles.toolbarPopup, GUILayout.Width(170));

                using (new EditorGUI.DisabledScope(_busy || Source == null))
                    if (GUILayout.Button(new GUIContent("Fetch", "Tải danh sách module và version từ nguồn"), EditorStyles.toolbarButton, GUILayout.Width(60)))
                        Run(FetchAsync);
                if (GUILayout.Button(new GUIContent("Quét lại", "Đọc lại các *.module.json đang có trong project"), EditorStyles.toolbarButton, GUILayout.Width(60)))
                    Rescan();

                GUILayout.FlexibleSpace();
                _search = GUILayout.TextField(_search, EditorStyles.toolbarSearchField, GUILayout.Width(200));
                _showSettings = GUILayout.Toggle(_showSettings, "Cấu hình", EditorStyles.toolbarButton, GUILayout.Width(70));
            }

            if (Source?.type == SourceConfig.GitHub)
            {
                EditorGUI.BeginChangeCheck();
                var token = EditorGUILayout.PasswordField(new GUIContent("Token GitHub",
                    "Fine-grained token của repo. Repo public có thể để trống. Lưu theo máy (EditorPrefs), không vào git."), Tokens.Get(Source));
                if (EditorGUI.EndChangeCheck()) Tokens.Set(Source, token);
            }
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

            using (new EditorGUILayout.HorizontalScope())
            {
                var expanded = _expanded == id;
                if (GUILayout.Button(expanded ? "▼" : "▶", EditorStyles.label, GUILayout.Width(16))) _expanded = expanded ? null : id;
                GUILayout.Label(new GUIContent(manifest.Title, id + "\n" + manifest.description), GUILayout.Width(230));

                var style = new GUIStyle(EditorStyles.label);
                style.normal.textColor = inst != null ? new Color(0.35f, 0.8f, 0.35f) : Color.gray;
                GUILayout.Label(inst?.Version ?? "Chưa cài", style, GUILayout.Width(90));

                if (versions != null)
                {
                    _picked.TryGetValue(id, out var idx);
                    idx = Mathf.Clamp(EditorGUILayout.Popup(idx, versions.Select(v => v.Version).ToArray(), GUILayout.Width(90)), 0, versions.Count - 1);
                    _picked[id] = idx;
                    pick = versions[idx];
                }
                else GUILayout.Label(new GUIContent("—", "Nguồn không có module này (chưa Fetch hoặc cài từ nguồn khác)"), GUILayout.Width(90));

                var locked = _busy || _settings.devMode;
                using (new EditorGUI.DisabledScope(locked || pick == null))
                    if (GUILayout.Button(ActionLabel(inst, pick), GUILayout.Width(80)))
                    {
                        var target = pick;
                        Run(() => InstallAsync(new[] { target }));
                    }
                using (new EditorGUI.DisabledScope(locked || inst == null))
                    if (GUILayout.Button("Gỡ", GUILayout.Width(40)))
                        EditorApplication.delayCall += () => Remove(inst);
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
                if (inst != null)
                    EditorGUILayout.LabelField("Đang ở", string.Join("\n", inst.Paths), EditorStyles.wordWrappedLabel);
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
            using (new EditorGUILayout.HorizontalScope(EditorStyles.toolbar))
            {
                GUILayout.Label($"Đã cài {_installed.Count(m => m.IsValid)} module", EditorStyles.miniLabel);
                GUILayout.FlexibleSpace();
                using (new EditorGUI.DisabledScope(_busy || _settings.devMode || updates.Count == 0))
                    if (GUILayout.Button($"Cập nhật tất cả ({updates.Count})", EditorStyles.toolbarButton, GUILayout.Width(150)))
                        Run(() => InstallAsync(updates));
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

        private List<ModuleRelease> Updatable() =>
            _installed.Where(m => m.IsValid)
                .Select(m => (m, latest: Latest(m.Id)))
                .Where(t => t.latest != null && SemVer.Compare(t.latest.Version, t.m.Version) > 0)
                .Select(t => t.latest).ToList();

        private async Task FetchAsync()
        {
            _status = "Đang tải danh sách…";
            Repaint();
            _releases = await SourceFactory.Create(Source, ProjectRoot).FetchAsync();
            _byId = _releases.GroupBy(r => r.Id)
                .ToDictionary(g => g.Key, g => g.OrderByDescending(r => SemVer.Parse(r.Version)).ToList());
            _picked.Clear();
            Rescan();
            _status = $"{Source.name}: {_byId.Count} module, {_releases.Count} bản phát hành — {DateTime.Now:HH:mm}";
        }

        private async Task InstallAsync(IReadOnlyList<ModuleRelease> requested)
        {
            Rescan();
            var errors = new List<string>();
            var plan = ModuleResolver.Plan(_installed, _releases, requested, errors);
            if (errors.Count > 0)
            {
                EditorUtility.DisplayDialog("Không cài được", string.Join("\n", errors.Distinct()), "OK");
                return;
            }
            if (plan.Count == 0)
            {
                _status = "Đã đúng version, không có gì để làm.";
                return;
            }

            var missing = ModuleInstaller.MissingPackages(ProjectRoot, plan.Select(s => s.Target.Manifest));
            var message = string.Join("\n", plan.Select(s => "• " + s.Describe()));
            if (!string.IsNullOrWhiteSpace(Source.installRoot)) message += "\n\nCài vào: " + Source.installRoot.Trim();
            if (plan.Any(s => s.Installed != null)) message += "\n\nThư mục cũ của module được cập nhật sẽ bị xoá rồi chép bản mới.";
            if (missing.Count > 0) message += "\n\nThêm package: " + string.Join(", ", missing.Select(p => p.name));
            if (!EditorUtility.DisplayDialog("Module Manager", message, "Thực hiện", "Huỷ")) return;

            var source = SourceFactory.Create(Source, ProjectRoot);
            var zips = await ModuleInstaller.DownloadAsync(source, plan, DownloadDir,
                (info, p) => EditorUtility.DisplayProgressBar("Module Manager", info, p));
            EditorUtility.DisplayProgressBar("Module Manager", "Chép file…", 1f);
            ModuleInstaller.Apply(ProjectRoot, plan, zips, _installed, Source.installRoot);
            AssetDatabase.Refresh();
            if (missing.Count > 0) AddPackages(missing);
            _status = "Xong: " + string.Join(", ", plan.Select(s => $"{s.Target.Manifest.Title} {s.Target.Version}"));
        }

        private void Remove(InstalledModule m)
        {
            var errors = ModuleResolver.RemovalErrors(_installed, m.Id);
            if (errors.Count > 0)
            {
                EditorUtility.DisplayDialog("Không gỡ được", string.Join("\n", errors), "OK");
                return;
            }
            if (!EditorUtility.DisplayDialog("Gỡ module", $"Gỡ {m.Manifest.Title} {m.Version}? Sẽ xoá:\n" +
                                                          string.Join("\n", m.Paths.Append(m.ManifestPath)), "Gỡ", "Huỷ")) return;
            ModuleFiles.Delete(ProjectRoot, m);
            AssetDatabase.Refresh();
            Rescan();
            _status = $"Đã gỡ {m.Manifest.Title}.";
        }

        private static void AddPackages(List<PackageDependency> packages)
        {
            var ids = packages.Select(p => string.IsNullOrWhiteSpace(p.source) ? p.name : p.source).ToArray();
            var request = Client.AddAndRemove(ids, null);
            void Poll()
            {
                if (!request.IsCompleted) return;
                EditorApplication.update -= Poll;
                if (request.Status == StatusCode.Failure) Debug.LogError("[ModuleManager] Thêm package lỗi: " + request.Error?.message);
                else Debug.Log("[ModuleManager] Đã thêm package: " + string.Join(", ", ids));
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
