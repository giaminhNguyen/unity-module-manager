using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using UnityEditor;
using UnityEngine;

namespace ModuleManager
{
    /// <summary>
    /// Cửa sổ phát hành, chỉ dùng ở project nguồn (Dev mode). So nội dung module với bản đã publish,
    /// tăng version, đóng gói và đẩy lên nguồn.
    /// </summary>
    public class ModulePublisherWindow : EditorWindow
    {
        private static string ProjectRoot => ModuleManagerWindow.ProjectRoot;
        private static string OutDir => Path.Combine(ProjectRoot, "Library", "ModuleManager", "Publish");

        private ManagerSettings _settings;
        private int _sourceIndex;
        private List<InstalledModule> _locals = new List<InstalledModule>();
        private List<ModulePublisher.Issue> _issues = new List<ModulePublisher.Issue>();
        private Dictionary<string, string> _hashes = new Dictionary<string, string>();
        private Dictionary<string, ModuleManifest> _published = new Dictionary<string, ModuleManifest>();
        private readonly Dictionary<string, BumpKind> _bump = new Dictionary<string, BumpKind>();
        private bool _fetched;
        private bool _busy;
        private string _changelog = "";
        private string _status = "";
        private Vector2 _scroll;
        private bool _showDev;
        private bool _showUncovered = true;
        private List<string> _uncovered = new List<string>();
        private readonly Dictionary<string, int> _addTarget = new Dictionary<string, int>();

        [MenuItem("Tools/Module Manager/Publisher", priority = 1)]
        public static void Open()
        {
            var w = GetWindow<ModulePublisherWindow>("Module Publisher");
            w.minSize = new Vector2(760, 420);
        }

        private void OnEnable()
        {
            _settings = ModuleManagerWindow.LoadSettings();
            Rescan();
        }

        private SourceConfig Source =>
            _settings.sources.Count == 0 ? null : _settings.sources[Mathf.Clamp(_sourceIndex, 0, _settings.sources.Count - 1)];

        private void Rescan()
        {
            _locals = ModuleFiles.Scan(ProjectRoot);
            _issues = ModulePublisher.Validate(ProjectRoot, _locals);
            _hashes = new Dictionary<string, string>();
            foreach (var m in _locals.Where(m => m.IsValid))
            {
                try { _hashes[m.Id] = ModulePublisher.ContentHash(ProjectRoot, m); }
                catch (Exception e) { _issues.Add(new ModulePublisher.Issue { ModuleId = m.Id, Message = e.Message }); }
            }

            var audit = AuditRoot();
            try { _uncovered = string.IsNullOrEmpty(audit) ? new List<string>() : ModuleAuthoring.FindUncovered(ProjectRoot, audit, _locals); }
            catch (Exception e)
            {
                _uncovered = new List<string>();
                _issues.Add(new ModulePublisher.Issue { Message = "Soát file chưa thuộc module lỗi: " + e.Message });
            }
        }

        /// <summary>Thư mục soát: auditRoot, trống thì thư mục cha của manifestFolder; trống nốt thì không soát.</summary>
        private string AuditRoot()
        {
            if (!string.IsNullOrWhiteSpace(_settings.auditRoot)) return ModuleFiles.Norm(_settings.auditRoot.Trim()).TrimEnd('/');
            return string.IsNullOrWhiteSpace(_settings.manifestFolder) ? null : ModuleFiles.DirOf(ModuleFiles.Norm(_settings.manifestFolder.Trim()).TrimEnd('/'));
        }

        // ───────────────────────── GUI ─────────────────────────

        private void OnGUI()
        {
            if (!_settings.devMode)
            {
                EditorGUILayout.HelpBox("Publisher chỉ chạy ở project nguồn (nơi viết code gốc của module). " +
                                        "Bật Dev mode cho project này?", MessageType.Info);
                if (GUILayout.Button("Bật Dev mode", GUILayout.Width(140)))
                {
                    _settings.devMode = true;
                    _settings.Save(ProjectRoot);
                }
                return;
            }

            using (new EditorGUILayout.HorizontalScope(EditorStyles.toolbar))
            {
                if (_settings.sources.Count == 0) GUILayout.Label("Chưa có nguồn — thêm ở Module Manager > Cấu hình", EditorStyles.miniLabel);
                else
                {
                    EditorGUI.BeginChangeCheck();
                    _sourceIndex = EditorGUILayout.Popup(_sourceIndex, _settings.sources.Select(s => s.name).ToArray(),
                        EditorStyles.toolbarPopup, GUILayout.Width(170));
                    if (EditorGUI.EndChangeCheck()) _fetched = false;
                }
                using (new EditorGUI.DisabledScope(_busy || Source == null))
                    if (GUILayout.Button(new GUIContent("Fetch", "Lấy các bản đã publish để so sánh"), EditorStyles.toolbarButton, GUILayout.Width(60)))
                        Run(FetchAsync);
                if (GUILayout.Button(new GUIContent("Quét lại", "Đọc lại manifest và tính lại hash nội dung"), EditorStyles.toolbarButton, GUILayout.Width(60)))
                    Rescan();
                GUILayout.FlexibleSpace();
            }

            if (Source?.type == SourceConfig.GitHub)
            {
                EditorGUI.BeginChangeCheck();
                var token = EditorGUILayout.PasswordField(new GUIContent("Token GitHub",
                    "Token cần quyền Contents: Read and write trên repo để tạo release."), Tokens.Get(Source));
                if (EditorGUI.EndChangeCheck()) Tokens.Set(Source, token);
            }

            DrawDevSettings();

            foreach (var issue in _issues)
                EditorGUILayout.HelpBox(issue.ToString(), MessageType.Error);

            EditorGUILayout.LabelField(_status, EditorStyles.miniLabel);
            EditorGUILayout.LabelField(new GUIContent("Changelog", "Ghi chú cho lần publish này, hiện trong Manager và trang release"));
            _changelog = EditorGUILayout.TextArea(_changelog, GUILayout.MinHeight(48));

            _scroll = EditorGUILayout.BeginScrollView(_scroll);
            foreach (var group in _locals.Where(m => m.IsValid).GroupBy(m => m.Manifest.category).OrderBy(g => g.Key, StringComparer.Ordinal))
            {
                EditorGUILayout.LabelField(group.Key, EditorStyles.boldLabel);
                foreach (var m in group.OrderBy(m => m.Manifest.Title, StringComparer.OrdinalIgnoreCase)) DrawRow(m);
            }
            DrawUncovered();
            EditorGUILayout.EndScrollView();

            var changed = Changed();
            using (new EditorGUILayout.HorizontalScope(EditorStyles.toolbar))
            {
                GUILayout.FlexibleSpace();
                using (new EditorGUI.DisabledScope(_busy || !_fetched || changed.Count == 0))
                    if (GUILayout.Button($"Publish tất cả module đã đổi ({changed.Count})", EditorStyles.toolbarButton, GUILayout.Width(230)))
                        Run(() => PublishManyAsync(changed));
            }
        }

        private void DrawDevSettings()
        {
            _showDev = EditorGUILayout.Foldout(_showDev, "Cấu hình dev", true);
            if (!_showDev) return;
            using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
            {
                EditorGUI.BeginChangeCheck();
                _settings.manifestFolder = EditorGUILayout.DelayedTextField(new GUIContent("Thư mục manifest",
                    "Project path (vd Assets/Modules) nơi tạo file manifest mới."), _settings.manifestFolder);
                _settings.idPrefix = EditorGUILayout.DelayedTextField(new GUIContent("Tiền tố id",
                    "Tiền tố id của module mới (vd myframework.)."), _settings.idPrefix);
                _settings.auditRoot = EditorGUILayout.DelayedTextField(new GUIContent("Thư mục soát",
                    "Project path để soát file chưa thuộc module. Trống = thư mục cha của thư mục manifest."), _settings.auditRoot);
                if (EditorGUI.EndChangeCheck())
                {
                    _settings.Save(ProjectRoot);
                    Rescan();
                }
                using (new EditorGUI.DisabledScope(_busy))
                    if (GUILayout.Button(new GUIContent("Tạo module từ mục đang chọn",
                            "Tạo file manifest mới cho thư mục/file đang chọn trong cửa sổ Project"), GUILayout.Width(220)))
                    {
                        var paths = Selection.assetGUIDs.Select(AssetDatabase.GUIDToAssetPath)
                            .Where(p => p.StartsWith("Assets/", StringComparison.Ordinal)).ToList();
                        CreateModule(paths);
                    }
            }
        }

        private void DrawUncovered()
        {
            _showUncovered = EditorGUILayout.Foldout(_showUncovered, $"Chưa thuộc module nào ({_uncovered.Count})", true);
            if (!_showUncovered) return;
            if (string.IsNullOrEmpty(AuditRoot()))
            {
                EditorGUILayout.HelpBox("Chưa biết soát ở đâu. Điền Thư mục manifest hoặc Thư mục soát ở mục Cấu hình dev.", MessageType.Info);
                return;
            }

            var valid = _locals.Where(m => m.IsValid).OrderBy(m => m.Manifest.Title, StringComparer.OrdinalIgnoreCase).ToList();
            var titles = valid.Select(m => m.Manifest.Title).ToArray();
            foreach (var path in _uncovered.ToList())
            {
                using (new EditorGUILayout.HorizontalScope())
                {
                    GUILayout.Label(new GUIContent(path, path), GUILayout.MinWidth(200));
                    GUILayout.FlexibleSpace();
                    using (new EditorGUI.DisabledScope(_busy))
                        if (GUILayout.Button("Tạo module", GUILayout.Width(90)))
                            CreateModule(new List<string> { path });

                    _addTarget.TryGetValue(path, out var idx);
                    idx = Mathf.Clamp(idx, 0, Mathf.Max(0, valid.Count - 1));
                    idx = EditorGUILayout.Popup(idx, titles, GUILayout.Width(160));
                    _addTarget[path] = idx;
                    using (new EditorGUI.DisabledScope(_busy || valid.Count == 0))
                        if (GUILayout.Button("Thêm vào", GUILayout.Width(75)))
                        {
                            var target = valid[idx];
                            Defer(() =>
                            {
                                ModuleAuthoring.AddPath(ProjectRoot, target, path);
                                AssetDatabase.Refresh();
                                _status = $"Đã thêm {path} vào {target.Manifest.Title}";
                            });
                        }
                }
            }
        }

        private void CreateModule(List<string> paths) => Defer(() =>
        {
            if (string.IsNullOrWhiteSpace(_settings.manifestFolder))
                throw new InvalidOperationException("Chưa khai Thư mục manifest ở mục Cấu hình dev");
            if (paths.Count == 0)
                throw new InvalidOperationException("Chọn thư mục/file trong cửa sổ Project (nằm trong Assets/) trước");

            var folder = _settings.manifestFolder.Trim();
            var m = ModuleAuthoring.CreateManifest(folder, paths, _settings.idPrefix);
            var path = ModuleAuthoring.WriteNewManifest(ProjectRoot, folder, m);
            AssetDatabase.Refresh();
            _status = "Đã tạo " + path;
            Rescan();
            var asset = AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(path);
            if (asset == null) return;
            Selection.activeObject = asset;
            EditorGUIUtility.PingObject(asset);
        });

        // Dời ra ngoài OnGUI vì có thể mở hộp thoại; lỗi thì báo bằng hộp thoại.
        private void Defer(Action action) => EditorApplication.delayCall += () =>
        {
            try
            {
                action();
            }
            catch (Exception e)
            {
                Debug.LogException(e);
                _status = "Lỗi: " + e.Message;
                EditorUtility.DisplayDialog("Module Publisher", e.Message, "OK");
            }
            Rescan();
            Repaint();
        };

        private void DrawRow(InstalledModule m)
        {
            _published.TryGetValue(m.Id, out var pub);
            using (new EditorGUILayout.HorizontalScope())
            {
                GUILayout.Label(new GUIContent(m.Manifest.Title, m.Id), GUILayout.Width(230));
                GUILayout.Label(new GUIContent(m.Version, "Version trong manifest nguồn"), GUILayout.Width(70));
                GUILayout.Label(new GUIContent(_fetched ? pub?.version ?? "—" : "?", "Bản mới nhất trên nguồn"), GUILayout.Width(70));
                GUILayout.Label(Status(m), GUILayout.Width(110));
                _bump.TryGetValue(m.Id, out var kind);
                _bump[m.Id] = (BumpKind)EditorGUILayout.EnumPopup(kind, GUILayout.Width(70));
                var next = _fetched ? ModulePublisher.NextVersion(m.Version, pub?.version, _bump[m.Id]) : "?";
                using (new EditorGUI.DisabledScope(_busy || !_fetched || Blocked(m) != null))
                    if (GUILayout.Button(new GUIContent($"Publish {next}", Blocked(m) ?? ""), GUILayout.Width(120)))
                        Run(() => PublishManyAsync(new List<InstalledModule> { m }));
            }
        }

        private string Status(InstalledModule m)
        {
            if (!_fetched) return "";
            if (!_published.TryGetValue(m.Id, out var pub)) return "Chưa publish";
            _hashes.TryGetValue(m.Id, out var hash);
            return pub.contentHash == hash ? "Khớp" : "Đã đổi";
        }

        /// <summary>Lý do không publish được, null nếu được.</summary>
        private string Blocked(InstalledModule m)
        {
            var issue = _issues.FirstOrDefault(i => i.ModuleId == null || i.ModuleId == m.Id);
            if (issue != null) return issue.ToString();
            foreach (var d in m.Manifest.dependencies)
                if (!_published.TryGetValue(d.id, out var dep) || SemVer.Compare(dep.version, d.version) < 0)
                    return $"Cần publish {d.id} >= {d.version} trước";
            return null;
        }

        private List<InstalledModule> Changed() =>
            _locals.Where(m => m.IsValid && _fetched && Status(m) != "Khớp").ToList();

        // ───────────────────────── Hành động ─────────────────────────

        private async Task FetchAsync()
        {
            _status = "Đang tải danh sách…";
            Repaint();
            var releases = await SourceFactory.Create(Source, ProjectRoot).FetchAsync();
            _published = releases.GroupBy(r => r.Id)
                .ToDictionary(g => g.Key, g => g.OrderByDescending(r => SemVer.Parse(r.Version)).First().Manifest);
            _fetched = true;
            _status = $"{Source.name}: {_published.Count} module đã publish — {DateTime.Now:HH:mm}";
        }

        private async Task PublishManyAsync(List<InstalledModule> modules)
        {
            Rescan();
            var ids = new HashSet<string>(modules.Select(m => m.Id));
            var order = ModulePublisher.TopoOrder(_locals.Where(m => m.IsValid && ids.Contains(m.Id)));
            var plan = string.Join("\n", order.Select(m =>
                $"• {m.Manifest.Title} {ModulePublisher.NextVersion(m.Version, _published.TryGetValue(m.Id, out var p) ? p.version : null, Kind(m))}"));
            if (!EditorUtility.DisplayDialog("Publish", $"Publish lên {Source.name}:\n{plan}", "Publish", "Huỷ")) return;

            var source = SourceFactory.Create(Source, ProjectRoot);
            var done = new List<string>();
            try
            {
                for (var i = 0; i < order.Count; i++)
                {
                    var m = order[i];
                    // Module xếp sau có thể phụ thuộc module vừa publish ở bước trước, nên kiểm tra theo _published mới nhất.
                    var blocked = Blocked(m);
                    if (blocked != null) throw new InvalidOperationException($"{m.Manifest.Title}: {blocked}");
                    EditorUtility.DisplayProgressBar("Module Publisher", $"Publish {m.Manifest.Title}", (float)i / order.Count);
                    _published.TryGetValue(m.Id, out var pub);
                    var published = await ModulePublisher.PublishAsync(source, ProjectRoot, m, pub?.version, Kind(m), _changelog, OutDir);
                    _published[m.Id] = published;
                    done.Add($"{m.Manifest.Title} {published.version}");
                }
            }
            finally
            {
                if (done.Count > 0)
                {
                    _status = "Đã publish: " + string.Join(", ", done);
                    AssetDatabase.Refresh();
                }
            }
            _changelog = "";
        }

        private BumpKind Kind(InstalledModule m) => _bump.TryGetValue(m.Id, out var k) ? k : BumpKind.Patch;

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
                EditorUtility.DisplayDialog("Module Publisher", e.Message, "OK");
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
