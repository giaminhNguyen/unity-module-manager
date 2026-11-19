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
    /// Tab Publisher trong cửa sổ Module Manager, chỉ dùng ở project nguồn (Dev mode). So nội dung module với bản
    /// đã publish, tăng version, đóng gói và đẩy lên nguồn.
    /// </summary>
    internal sealed class PublisherTab
    {
        private static string ProjectRoot => ModuleManagerWindow.ProjectRoot;
        private static string OutDir => Path.Combine(ProjectRoot, "Library", "ModuleManager", "Publish");
        private string UnityPackageDir => _settings.UnityPackageDir(ProjectRoot);

        private ManagerSettings _settings;
        private int _sourceIndex;
        private List<InstalledModule> _locals = new List<InstalledModule>();
        private List<ModulePublisher.Issue> _issues = new List<ModulePublisher.Issue>();
        private Dictionary<string, string> _hashes = new Dictionary<string, string>();
        private Dictionary<string, ModuleManifest> _published = new Dictionary<string, ModuleManifest>();
        private readonly Dictionary<string, BumpKind> _bump = new Dictionary<string, BumpKind>();
        private readonly HashSet<string> _collapsed = new HashSet<string>();
        private readonly HashSet<string> _selected = new HashSet<string>();
        private BumpKind _batchBump;
        private bool _fetched;
        private bool _busy;
        private string _changelog = "";
        private string _status = "";
        private Vector2 _scroll;
        private bool _showDev;
        private bool _showIssues;
        private Vector2 _issuesScroll;
        private bool _showUncovered = true;
        private List<string> _uncovered = new List<string>();
        private readonly Dictionary<string, int> _addTarget = new Dictionary<string, int>();
        private Action _repaint;

        /// <param name="settings">Dùng chung với tab Manager nên đổi ở tab nào cũng thấy ở tab kia.</param>
        public PublisherTab(ManagerSettings settings, Action repaint)
        {
            _settings = settings;
            _repaint = repaint;
            Rescan();
            LoadCache();
        }

        /// <summary>Lấy danh sách đã publish từ lần Fetch trước (cache dùng chung với tab Manager).</summary>
        private void LoadCache()
        {
            _fetched = false;
            _published = new Dictionary<string, ModuleManifest>();
            if (Source == null) return;
            var cached = ReleaseCache.Load(ProjectRoot, Source, out var at);
            if (cached == null) return;
            SetPublished(cached);
            _status = $"{Source.name}: {_published.Count} module đã publish · dữ liệu lúc {at.ToLocalTime():HH:mm dd/MM} (cache) — Fetch để cập nhật";
        }

        private void SetPublished(List<ModuleRelease> releases)
        {
            _published = releases.GroupBy(r => r.Id)
                .ToDictionary(g => g.Key, g => g.OrderByDescending(r => SemVer.Parse(r.Version)).First().Manifest);
            _fetched = true;
        }

        private void Repaint() => _repaint?.Invoke();

        private SourceConfig Source =>
            _settings.sources.Count == 0 ? null : _settings.sources[Mathf.Clamp(_sourceIndex, 0, _settings.sources.Count - 1)];

        private void Rescan()
        {
            _locals = ModuleFiles.Scan(ProjectRoot);
            _issues = ModulePublisher.Validate(ProjectRoot, _locals, UnityPackageDir);
            _hashes = new Dictionary<string, string>();
            foreach (var m in _locals.Where(m => m.IsValid))
            {
                try { _hashes[m.Id] = ModulePublisher.ContentHash(ProjectRoot, m, UnityPackageDir); }
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

        public void Draw()
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
                if (_settings.sources.Count == 0) GUILayout.Label("Chưa có nguồn — thêm ở tab Manager > Cấu hình", EditorStyles.miniLabel);
                else
                {
                    GUILayout.Label("Nguồn", EditorStyles.miniLabel, GUILayout.ExpandWidth(false));
                    EditorGUI.BeginChangeCheck();
                    _sourceIndex = EditorGUILayout.Popup(_sourceIndex, _settings.sources.Select(s => s.name).ToArray(),
                        EditorStyles.toolbarPopup, GUILayout.Width(150));
                    if (EditorGUI.EndChangeCheck())
                    {
                        _status = "";
                        LoadCache();
                    }
                }
                using (new EditorGUI.DisabledScope(_busy || Source == null))
                    if (GUILayout.Button(ModuleStyles.Icon("Refresh", " Fetch", "Lấy các bản đã publish để so sánh (kết quả được lưu cache)"),
                            EditorStyles.toolbarButton, GUILayout.Width(70)))
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

            if (_issues.Count > 0) DrawIssues();

            DrawSummary();
            using (new EditorGUILayout.VerticalScope(ModuleStyles.Bar))
            {
                EditorGUILayout.LabelField(new GUIContent("Changelog", "Ghi chú cho lần publish này, hiện trong Manager và trang release"),
                    EditorStyles.boldLabel);
                _changelog = EditorGUILayout.TextArea(_changelog, GUILayout.MinHeight(44));
            }

            _scroll = EditorGUILayout.BeginScrollView(_scroll);
            ModuleManagerWindow.DrawColumns(("Nguồn", ModuleStyles.VersionWidth), ("Đã publish", ModuleStyles.VersionWidth + 10),
                ("Trạng thái", ModuleStyles.StatusWidth), ("Mức tăng", 76), ("", 126));
            ModuleManagerWindow.DrawCategoryTree(_locals.Where(m => m.IsValid).ToList(), m => m.Manifest, DrawRow, _collapsed, _selected);
            GUILayout.Space(8);
            DrawUncovered();
            EditorGUILayout.EndScrollView();

            var changed = Changed();
            var selected = _locals.Where(m => m.IsValid && _selected.Contains(m.Id)).ToList();
            ModuleStyles.BeginBar();
            GUILayout.Label($"Đã chọn {selected.Count}", EditorStyles.boldLabel, GUILayout.Width(90), GUILayout.Height(22));
            using (new EditorGUI.DisabledScope(_busy || selected.Count == 0))
            {
                _batchBump = (BumpKind)EditorGUILayout.EnumPopup(_batchBump, GUILayout.Width(70), GUILayout.Height(22));
                if (GUILayout.Button(new GUIContent("Áp mức tăng", "Đặt mức tăng version này cho mọi mục đang chọn"),
                        GUILayout.Width(95), GUILayout.Height(22)))
                    foreach (var m in selected) _bump[m.Id] = _batchBump;
            }
            using (new EditorGUI.DisabledScope(_busy || !_fetched || selected.Count == 0))
                if (GUILayout.Button($"Publish ({selected.Count})", GUILayout.Width(100), GUILayout.Height(22)))
                    Run(() => PublishManyAsync(selected));
            using (new EditorGUI.DisabledScope(_selected.Count == 0))
                if (GUILayout.Button("Bỏ chọn", GUILayout.Width(70), GUILayout.Height(22)))
                    _selected.Clear();
            GUILayout.FlexibleSpace();
            using (new EditorGUI.DisabledScope(_busy || !_fetched || changed.Count == 0))
                if (GUILayout.Button($"Publish tất cả đã đổi ({changed.Count})", GUILayout.Width(190), GUILayout.Height(22)))
                    Run(() => PublishManyAsync(changed));
            ModuleStyles.EndBar();
        }

        /// <summary>Lỗi kiểm tra gom vào một mục gập được, cuộn khi dài; dòng module bị lỗi hiện "Bị chặn" kèm lý do.</summary>
        private void DrawIssues()
        {
            using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
            {
                var title = new GUIContent($" {_issues.Count} lỗi cần sửa trước khi publish", EditorGUIUtility.IconContent("console.erroricon.sml").image);
                _showIssues = EditorGUILayout.Foldout(_showIssues, title, true);
                if (!_showIssues) return;
                _issuesScroll = EditorGUILayout.BeginScrollView(_issuesScroll, GUILayout.MaxHeight(140));
                foreach (var issue in _issues) EditorGUILayout.LabelField("• " + issue, ModuleStyles.Hint);
                EditorGUILayout.EndScrollView();
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
                _settings.unityPackageFolder = EditorGUILayout.DelayedTextField(new GUIContent("Thư mục .unitypackage",
                    "Nơi để file .unitypackage của module plugin (tương đối root project hoặc tuyệt đối). Phải nằm ngoài Assets/ " +
                    "và ngoài repo git: file chỉ được đưa lên GitHub Release. Trống = UnityPackages."), _settings.unityPackageFolder);
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
            var blocked = Blocked(m);
            using (new EditorGUILayout.HorizontalScope(GUILayout.MinHeight(ModuleStyles.RowHeight)))
            {
                GUILayout.Space(ModuleStyles.ArrowWidth);
                ModuleManagerWindow.DrawNameCell(m.Manifest);
                ModuleStyles.CellLabel(new GUIContent(m.Version, "Version trong manifest nguồn"), ModuleStyles.VersionWidth);
                ModuleStyles.CellLabel(new GUIContent(_fetched ? pub?.version ?? "—" : "?", "Bản mới nhất trên nguồn"), ModuleStyles.VersionWidth + 10);

                using (new EditorGUILayout.VerticalScope(GUILayout.Width(ModuleStyles.StatusWidth)))
                {
                    GUILayout.FlexibleSpace();
                    var status = Status(m);
                    if (blocked != null) ModuleStyles.Status("Bị chặn", ModuleStyles.Red, blocked);
                    else if (status == "Khớp") ModuleStyles.Status(status, ModuleStyles.Green);
                    else if (status == "Đã đổi") ModuleStyles.Status(status, ModuleStyles.Amber);
                    else if (status == "Chưa publish") ModuleStyles.Status(status, ModuleStyles.Blue);
                    else ModuleStyles.Status("Chưa Fetch", ModuleStyles.Muted);
                    GUILayout.FlexibleSpace();
                }

                using (new EditorGUILayout.VerticalScope(GUILayout.Width(76)))
                {
                    GUILayout.FlexibleSpace();
                    _bump.TryGetValue(m.Id, out var kind);
                    _bump[m.Id] = (BumpKind)EditorGUILayout.EnumPopup(kind, GUILayout.Width(70));
                    GUILayout.FlexibleSpace();
                }

                using (new EditorGUILayout.VerticalScope(GUILayout.Width(126)))
                {
                    GUILayout.FlexibleSpace();
                    var next = _fetched ? ModulePublisher.NextVersion(m.Version, pub?.version, _bump[m.Id]) : "?";
                    using (new EditorGUI.DisabledScope(_busy || !_fetched || blocked != null))
                        if (GUILayout.Button(new GUIContent($"Publish {next}", blocked ?? ""), GUILayout.Width(120), GUILayout.Height(22)))
                            Run(() => PublishManyAsync(new List<InstalledModule> { m }));
                    GUILayout.FlexibleSpace();
                }
            }
        }

        /// <summary>Dải tóm tắt: đếm theo trạng thái + thời điểm dữ liệu / thông báo gần nhất.</summary>
        private void DrawSummary()
        {
            var valid = _locals.Where(m => m.IsValid).ToList();
            using (new EditorGUILayout.HorizontalScope(ModuleStyles.Bar))
            {
                if (_fetched)
                {
                    ModuleStyles.Status($"{valid.Count(m => Status(m) == "Khớp")} khớp", ModuleStyles.Green, null, GUILayout.ExpandWidth(false));
                    GUILayout.Space(10);
                    ModuleStyles.Status($"{valid.Count(m => Status(m) == "Đã đổi")} đã đổi", ModuleStyles.Amber, null, GUILayout.ExpandWidth(false));
                    GUILayout.Space(10);
                    ModuleStyles.Status($"{valid.Count(m => Status(m) == "Chưa publish")} chưa publish", ModuleStyles.Blue, null, GUILayout.ExpandWidth(false));
                    GUILayout.Space(16);
                }
                GUILayout.Label(new GUIContent(_status, _status), ModuleStyles.Sub, GUILayout.MinWidth(0), GUILayout.ExpandWidth(true));
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
            SetPublished(releases);
            ReleaseCache.Save(ProjectRoot, Source, releases, DateTime.UtcNow);
            _status = $"{Source.name}: {_published.Count} module đã publish — Fetch lúc {DateTime.Now:HH:mm}";
        }

        private async Task PublishManyAsync(List<InstalledModule> modules)
        {
            Rescan();
            var ids = new HashSet<string>(modules.Select(m => m.Id));
            var order = ModulePublisher.TopoOrder(_locals.Where(m => m.IsValid && ids.Contains(m.Id)));
            var plan = ModuleManagerWindow.Lines(order.Select(m =>
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
                    var published = await ModulePublisher.PublishAsync(source, ProjectRoot, m, pub?.version, Kind(m), _changelog, OutDir, UnityPackageDir);
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
            // Lấy lại danh sách để trạng thái và cache khớp các bản vừa publish.
            var summary = _status;
            await FetchAsync();
            _status = summary;
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
