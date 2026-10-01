using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using TechSupportReply.Core.Diagnostics;
using TechSupportReply.Core.Llm;
using TechSupportReply.Core.Settings;

namespace TechSupportReply.App.SettingsUi
{
    /// <summary>설정 대화상자. 편집은 SettingsEditor의 사본에만 반영하고 [저장]에서 한 번에 적용한다.</summary>
    public sealed class SettingsForm : Form
    {
        private readonly ISettingsHost _host;
        private readonly FileLog _log;
        // 창을 닫으면 취소한다(진행 중인 연결 테스트가 닫힌 창에 결과를 쓰지 않게).
        private readonly CancellationTokenSource _closing = new CancellationTokenSource();
        private bool _loadingProfile;

        // 사용자
        private readonly TextBox _userName = new TextBox { Dock = DockStyle.Fill };
        private readonly TextBox _userTitle = new TextBox { Dock = DockStyle.Fill };
        private readonly TextBox _userCompany = new TextBox { Dock = DockStyle.Fill };
        private readonly TextBox _userTone = new TextBox { Dock = DockStyle.Fill };

        // 프로필
        private readonly TextBox _pName = new TextBox { Dock = DockStyle.Fill };
        private readonly ComboBox _pProvider = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Dock = DockStyle.Fill };
        private readonly ComboBox _pModel = new ComboBox { DropDownStyle = ComboBoxStyle.DropDown, Dock = DockStyle.Fill };
        private readonly Button _pLoadModels = new Button { Text = "모델 목록 불러오기", AutoSize = true };
        private readonly Label _pModelsResult = new Label { AutoSize = true, MaximumSize = new Size(420, 0) };
        private const string NoKeyMessage = "API 키가 없습니다. 키를 입력하거나 환경 변수를 확인하세요.";
        private readonly TextBox _pBaseUrl = new TextBox { Dock = DockStyle.Fill };
        private readonly NumericUpDown _pMaxTokens = new NumericUpDown { Minimum = 256, Maximum = 128000, Increment = 1000, Dock = DockStyle.Left, Width = 100 };
        private readonly ComboBox _pEffort = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Dock = DockStyle.Left, Width = 100 };
        private readonly RadioButton _pKeyDirect = new RadioButton { Text = "직접 입력(DPAPI 암호화 저장)", AutoSize = true };
        private readonly RadioButton _pKeyEnv = new RadioButton { Text = "환경 변수에서 읽기", AutoSize = true };
        private readonly TextBox _pKey = new TextBox { UseSystemPasswordChar = true, Dock = DockStyle.Fill };
        private readonly Label _pKeyState = new Label { AutoSize = true, ForeColor = SystemColors.GrayText };
        private readonly TextBox _pEnvVar = new TextBox { Dock = DockStyle.Fill };
        private readonly TextBox _pWorkspace = new TextBox { Dock = DockStyle.Fill };
        private readonly Button _pTest = new Button { Text = "연결 테스트", AutoSize = true };
        private readonly Label _pTestResult = new Label { AutoSize = true, MaximumSize = new Size(420, 0) };
        private readonly ComboBox _defaultProfile = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Dock = DockStyle.Fill };
        private readonly ComboBox _classifierProfile = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Dock = DockStyle.Fill };
        private readonly Panel _profileEditor = new Panel { Dock = DockStyle.Fill };

        // 지식 폴더
        private readonly TextBox _ragRoot = new TextBox { Dock = DockStyle.Fill };
        private readonly NumericUpDown _refTopK = new NumericUpDown { Minimum = 1, Maximum = 30, Dock = DockStyle.Left, Width = 80 };
        private readonly NumericUpDown _styleTopK = new NumericUpDown { Minimum = 0, Maximum = 10, Dock = DockStyle.Left, Width = 80 };
        private readonly Button _syncNow = new Button { Text = "지금 동기화", AutoSize = true };
        private readonly Label _syncResult = new Label { AutoSize = true, MaximumSize = new Size(560, 0) };
        private readonly ListView _indexStatus = new ListView { View = View.Details, Dock = DockStyle.Fill, FullRowSelect = true, HeaderStyle = ColumnHeaderStyle.Nonclickable };

        internal readonly TabControl Tabs = new TabControl { Dock = DockStyle.Fill };
        internal readonly ListBox ProfileList = new ListBox { Dock = DockStyle.Fill, IntegralHeight = false };

        public SettingsForm(ISettingsHost host)
        {
            _host = host ?? throw new ArgumentNullException(nameof(host));
            _log = host.Log ?? throw new ArgumentException("ISettingsHost.Log가 없습니다.", nameof(host));
            Editor = new SettingsEditor(host.Settings, host.Secrets);
            Text = "기술지원 답변 — 설정";
            Font = new Font("맑은 고딕", 9f);
            StartPosition = FormStartPosition.CenterScreen;
            Size = new Size(780, 600);
            MinimumSize = new Size(700, 520);

            Tabs.TabPages.Add(BuildUserTab());
            Tabs.TabPages.Add(BuildProfilesTab());
            Tabs.TabPages.Add(BuildKnowledgeTab());

            var save = new Button { Text = "저장", AutoSize = true };
            var cancel = new Button { Text = "취소", AutoSize = true, DialogResult = DialogResult.Cancel };
            var bottom = new FlowLayoutPanel { Dock = DockStyle.Bottom, FlowDirection = FlowDirection.RightToLeft, AutoSize = true, Padding = new Padding(6) };
            bottom.Controls.Add(cancel);
            bottom.Controls.Add(save);
            Controls.Add(Tabs);
            Controls.Add(bottom);
            CancelButton = cancel;
            save.Click += (s, e) =>
            {
                try
                {
                    if (TrySave(out var errors)) { DialogResult = DialogResult.OK; Close(); }
                    else MessageBox.Show(this, string.Join("\n", errors), "설정을 확인하세요", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                }
                catch (Exception ex)
                {
                    _log.Error("설정 저장 실패", ex);
                    MessageBox.Show(this, "설정을 저장하지 못했습니다: " + ex.Message, "저장 실패", MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
            };

            LoadUser();
            RefreshProfileList(Editor.Working.Profiles.FirstOrDefault()?.Id);
            LoadKnowledge();
        }

        internal SettingsEditor Editor { get; }
        internal ComboBox ModelCombo => _pModel;
        internal TextBox BaseUrlBox => _pBaseUrl;
        internal Label ModelsResultLabel => _pModelsResult;
        internal Label TestResultLabel => _pTestResult;
        internal TimeSpan ModelListTimeout { get; set; } = TimeSpan.FromSeconds(30);

        /// <summary>
        /// [모델 목록 불러오기]. 입력 중인 값을 반영한 프로필 사본과 키로 호스트에 목록을 요청하고(백그라운드), 결과를 모델 드롭다운에 채운다.
        /// 모델 칸의 현재 값은 지우지 않는다(목록에 없는 모델도 직접 입력해 쓸 수 있다). 예외를 던지지 않는다.
        /// </summary>
        internal async Task LoadModelsAsync()
        {
            string requestedId = null;
            // 그사이 창을 닫았거나 다른 프로필을 골랐으면 결과를 버린다.
            bool Stale() => IsDisposed || (requestedId != null && _currentProfileId != requestedId);
            _pLoadModels.Enabled = false;
            try
            {
                var p = CurrentProfileWithKey(out var key);
                if (p == null) return;
                requestedId = p.Id;
                if (string.IsNullOrWhiteSpace(key)) { ShowResult(_pModelsResult, false, NoKeyMessage); return; }
                var snapshot = new LlmProfile
                {
                    Id = p.Id, DisplayName = p.DisplayName, Provider = p.Provider, Model = p.Model, BaseUrl = p.BaseUrl, WorkspaceId = p.WorkspaceId,
                };
                ShowResult(_pModelsResult, true, "모델 목록을 불러오는 중…");
                IReadOnlyList<ModelListing> models;
                using (var cts = new CancellationTokenSource(ModelListTimeout))
                    models = await Task.Run(() => _host.ListModelsAsync(snapshot, key, cts.Token), cts.Token);
                if (Stale()) return;
                models = models ?? new ModelListing[0];
                _loadedModels[snapshot.Id] = new LoadedModels(snapshot.Provider, snapshot.BaseUrl, models);
                FillModels(models);
            }
            catch (OperationCanceledException)
            {
                if (!Stale()) ShowResult(_pModelsResult, false, "응답 시간이 초과되었습니다. 네트워크 또는 Base URL을 확인하세요.");
            }
            catch (LlmException ex)
            {
                _log.Error($"모델 목록 불러오기 실패({ex.Kind})", ex);
                if (!Stale()) ShowResult(_pModelsResult, false, ex.UserMessage);
            }
            catch (Exception ex)
            {
                _log.Error("모델 목록 불러오기 실패", ex);
                if (!Stale()) ShowResult(_pModelsResult, false, "모델 목록을 불러오지 못했습니다: " + ex.Message);
            }
            finally
            {
                if (!IsDisposed) _pLoadModels.Enabled = true;
            }
        }

        /// <summary>
        /// [연결 테스트]. 결과가 오기 전에 다른 프로필을 골랐거나 창을 닫았으면 결과를 버린다(창을 닫으면 요청도 취소). 예외를 던지지 않는다.
        /// </summary>
        internal async Task TestConnectionAsync()
        {
            string requestedId = null;
            bool Stale() => IsDisposed || _closing.IsCancellationRequested || (requestedId != null && _currentProfileId != requestedId);
            var started = false;
            try
            {
                var p = CurrentProfileWithKey(out var key);
                if (p == null) return;
                requestedId = p.Id;
                if (string.IsNullOrWhiteSpace(key)) { ShowTest(false, NoKeyMessage); return; }
                _pTest.Enabled = false;
                started = true;
                ShowTest(true, "확인 중…");
                var llm = _host.CreateLlmForTest(p, key);
                var r = await LlmConnectionTester.TestAsync(llm, _closing.Token);
                if (!Stale()) ShowTest(r.Success, r.Message);
            }
            catch (OperationCanceledException) when (Stale())
            {
                // 창을 닫아 취소했다.
            }
            catch (Exception ex)
            {
                _log.Error("연결 테스트 실패", ex);
                if (!Stale()) ShowTest(false, ex is LlmException le ? le.UserMessage : ex.Message);
            }
            finally
            {
                if (started && !IsDisposed) _pTest.Enabled = true;
            }
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            _closing.Cancel();
            base.OnFormClosed(e);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing && !IsDisposed)   // Dispose를 두 번 불러도 폐기된 CTS를 다시 취소하지 않는다
            {
                _closing.Cancel();
                _closing.Dispose();
            }
            base.Dispose(disposing);
        }

        /// <summary>이벤트 처리기 본문을 감싸 예외를 로그에 남기고 한국어 메시지로 알린다.</summary>
        private void Guard(Action action, string what)
        {
            try { action(); }
            catch (Exception ex)
            {
                _log.Error(what + " 실패", ex);
                MessageBox.Show(this, what + " 중 오류가 발생했습니다: " + ex.Message, "오류", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        internal bool TrySave(out IReadOnlyList<string> errors)
        {
            StoreUser();
            StoreProfile();
            StoreKnowledge();
            errors = Editor.Validate();
            if (errors.Count > 0) return false;
            _host.ApplySettings(Editor.Commit());
            return true;
        }

        internal void AddPreset(ProfilePreset preset)
        {
            StoreProfile();
            var p = Editor.AddProfile(preset);
            RefreshProfileList(p.Id);
        }

        // ---------- 사용자 ----------
        private TabPage BuildUserTab()
        {
            var t = Grid();
            AddRow(t, "이름", _userName);
            AddRow(t, "직함", _userTitle);
            AddRow(t, "회사", _userCompany);
            AddRow(t, "기본 어조", _userTone);
            AddRow(t, "", new Label { Text = "답변 서명과 어조에 사용합니다.", AutoSize = true, ForeColor = SystemColors.GrayText });
            return Page("사용자", t);
        }

        private void LoadUser()
        {
            var u = Editor.Working.User;
            _userName.Text = u.Name;
            _userTitle.Text = u.Title;
            _userCompany.Text = u.Company;
            _userTone.Text = u.Tone;
        }

        private void StoreUser()
        {
            var u = Editor.Working.User;
            u.Name = _userName.Text.Trim();
            u.Title = _userTitle.Text.Trim();
            u.Company = _userCompany.Text.Trim();
            u.Tone = _userTone.Text.Trim();
        }

        // ---------- 프로필 ----------
        private TabPage BuildProfilesTab()
        {
            _pProvider.Items.AddRange(new object[] { "Anthropic (Claude)", "OpenAI 호환 (OpenAI·xAI 등)" });
            _pEffort.Items.AddRange(new object[] { "low", "medium", "high", "max" });

            var left = new TableLayoutPanel { Dock = DockStyle.Left, Width = 210, RowCount = 2 };
            left.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            left.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            var addButtons = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill };
            var addClaude = new Button { Text = "Claude 추가", AutoSize = true };
            var addOpenAi = new Button { Text = "OpenAI 추가", AutoSize = true };
            var addXai = new Button { Text = "xAI 추가", AutoSize = true };
            var remove = new Button { Text = "삭제", AutoSize = true };
            addButtons.Controls.AddRange(new Control[] { addClaude, addOpenAi, addXai, remove });
            left.Controls.Add(ProfileList, 0, 0);
            left.Controls.Add(addButtons, 0, 1);

            var editor = Grid();
            AddRow(editor, "표시명", _pName);
            AddRow(editor, "공급자", _pProvider);
            AddRow(editor, "모델", InlineRow(_pModel, _pLoadModels));
            AddRow(editor, "", _pModelsResult);
            AddRow(editor, "Base URL", _pBaseUrl);
            AddRow(editor, "", new Label { Text = "비워 두면 공급자 기본값. xAI는 https://api.x.ai/v1", AutoSize = true, ForeColor = SystemColors.GrayText });
            AddRow(editor, "최대 토큰", _pMaxTokens);
            AddRow(editor, "Effort", _pEffort);
            AddRow(editor, "API 키", _pKeyDirect);
            AddRow(editor, "", _pKey);
            AddRow(editor, "", _pKeyState);
            AddRow(editor, "", _pKeyEnv);
            AddRow(editor, "변수 이름", _pEnvVar);
            AddRow(editor, "Workspace ID", _pWorkspace);
            AddRow(editor, "", new Label { Text = "워크스페이스에 속하지 않은 Anthropic 키만 필요합니다(wrkspc_…).", AutoSize = true, ForeColor = SystemColors.GrayText });
            AddRow(editor, "", _pTest);
            AddRow(editor, "", _pTestResult);
            _profileEditor.Controls.Add(editor);

            var defaults = Grid();
            defaults.Dock = DockStyle.Bottom;
            defaults.AutoSize = true;
            AddRow(defaults, "기본 답변 프로필", _defaultProfile);
            AddRow(defaults, "제품 분류 프로필", _classifierProfile);

            var page = new TabPage("LLM 프로필") { Padding = new Padding(6) };
            page.Controls.Add(_profileEditor);
            page.Controls.Add(left);
            page.Controls.Add(defaults);

            ProfileList.SelectedIndexChanged += (s, e) => Guard(() =>
            {
                if (_loadingProfile) return;
                StoreProfileById(_currentProfileId);
                LoadProfile(SelectedProfile());
            }, "프로필 선택");
            addClaude.Click += (s, e) => Guard(() => AddPreset(ProfilePreset.Claude), "프로필 추가");
            addOpenAi.Click += (s, e) => Guard(() => AddPreset(ProfilePreset.OpenAI), "프로필 추가");
            addXai.Click += (s, e) => Guard(() => AddPreset(ProfilePreset.Xai), "프로필 추가");
            remove.Click += (s, e) => Guard(() =>
            {
                var p = SelectedProfile();
                if (p == null) return;
                if (MessageBox.Show(this, $"'{p.DisplayName}' 프로필을 삭제할까요? 저장된 키도 삭제됩니다.", "프로필 삭제", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
                _currentProfileId = null;
                Editor.RemoveProfile(p.Id);
                RefreshProfileList(Editor.Working.Profiles.FirstOrDefault()?.Id);
            }, "프로필 삭제");
            _pProvider.SelectedIndexChanged += (s, e) => Guard(UpdateProviderFields, "공급자 변경");
            _pKeyDirect.CheckedChanged += (s, e) => Guard(UpdateKeyFields, "키 방식 변경");
            _pName.Leave += (s, e) => Guard(() => { StoreProfile(); RefreshProfileList(_currentProfileId); }, "표시명 변경");
            _pTest.Click += async (s, e) => await TestConnectionAsync();
            _pLoadModels.Click += async (s, e) => await LoadModelsAsync();
            return page;
        }

        private string _currentProfileId;

        /// <summary>창이 열려 있는 동안 프로필별로 불러온 모델 목록. 다른 프로필을 골랐다가 돌아와도 다시 불러올 필요가 없게 한다.</summary>
        private readonly Dictionary<string, LoadedModels> _loadedModels = new Dictionary<string, LoadedModels>();

        /// <summary>목록을 받은 공급자·Base URL을 함께 기억해, 그 뒤 바뀌었으면 다른 엔드포인트의 목록을 보여 주지 않는다.</summary>
        private sealed class LoadedModels
        {
            public LoadedModels(LlmProviderKind provider, string baseUrl, IReadOnlyList<ModelListing> models)
            {
                Provider = provider;
                BaseUrl = baseUrl ?? "";
                Models = models;
            }
            public LlmProviderKind Provider { get; }
            public string BaseUrl { get; }
            public IReadOnlyList<ModelListing> Models { get; }
            public bool Matches(LlmProfile p) => p.Provider == Provider && string.Equals(p.BaseUrl ?? "", BaseUrl, StringComparison.OrdinalIgnoreCase);
        }

        private LlmProfile SelectedProfile() => (ProfileList.SelectedItem as ProfileItem)?.Profile;

        private sealed class ProfileItem
        {
            public ProfileItem(LlmProfile p) { Profile = p; }
            public LlmProfile Profile { get; }
            public override string ToString() => Profile.DisplayName;
        }

        private void RefreshProfileList(string selectId)
        {
            _loadingProfile = true;
            ProfileList.BeginUpdate();
            ProfileList.Items.Clear();
            foreach (var p in Editor.Working.Profiles) ProfileList.Items.Add(new ProfileItem(p));
            ProfileList.EndUpdate();
            var item = ProfileList.Items.Cast<ProfileItem>().FirstOrDefault(i => i.Profile.Id == selectId);
            ProfileList.SelectedItem = item;
            _loadingProfile = false;
            LoadProfile(item?.Profile);
            RefreshDefaultCombos();
        }

        private void RefreshDefaultCombos()
        {
            foreach (var (combo, id) in new[] { (_defaultProfile, Editor.Working.DefaultProfileId), (_classifierProfile, Editor.Working.ClassifierProfileId) })
            {
                combo.Items.Clear();
                foreach (var p in Editor.Working.Profiles) combo.Items.Add(new ProfileItem(p));
                combo.SelectedItem = combo.Items.Cast<ProfileItem>().FirstOrDefault(i => i.Profile.Id == id);
            }
        }

        private void LoadProfile(LlmProfile p)
        {
            _currentProfileId = p?.Id;
            _profileEditor.Enabled = p != null;
            _pTestResult.Text = "";
            _pModel.Items.Clear();
            _pModelsResult.Text = "";
            if (p == null) return;
            _pName.Text = p.DisplayName;
            _pProvider.SelectedIndex = p.Provider == LlmProviderKind.Anthropic ? 0 : 1;
            if (_loadedModels.TryGetValue(p.Id, out var loaded) && loaded.Matches(p))
            {
                _pModel.BeginUpdate();
                foreach (var m in loaded.Models) _pModel.Items.Add(m.Id);
                _pModel.EndUpdate();
            }
            _pModel.Text = p.Model;
            _pBaseUrl.Text = p.BaseUrl;
            _pMaxTokens.Value = Math.Max(_pMaxTokens.Minimum, Math.Min(_pMaxTokens.Maximum, p.MaxTokens));
            _pEffort.SelectedItem = string.IsNullOrEmpty(p.Effort) ? "medium" : p.Effort;
            _pKey.Text = "";
            _pEnvVar.Text = p.ApiKeyEnvVar;
            _pWorkspace.Text = p.WorkspaceId;
            _pKeyEnv.Checked = !string.IsNullOrWhiteSpace(p.ApiKeyEnvVar);
            _pKeyDirect.Checked = !_pKeyEnv.Checked;
            _pKeyState.Text = Editor.HasPendingKey(p.Id) ? "새 키 입력됨(저장 시 적용)" : Editor.HasStoredKey(p) ? "저장된 키가 있습니다. 바꾸려면 새 키를 입력하세요." : "저장된 키가 없습니다.";
            UpdateProviderFields();
            UpdateKeyFields();
        }

        private void StoreProfile() => StoreProfileById(_currentProfileId);

        private void StoreProfileById(string id)
        {
            var p = id == null ? null : Editor.Working.FindProfile(id);
            if (p != null)
            {
                p.DisplayName = _pName.Text.Trim();
                p.Provider = _pProvider.SelectedIndex == 0 ? LlmProviderKind.Anthropic : LlmProviderKind.OpenAI;
                p.Model = _pModel.Text.Trim();
                p.BaseUrl = _pBaseUrl.Text.Trim();
                p.MaxTokens = (int)_pMaxTokens.Value;
                p.Effort = (_pEffort.SelectedItem as string) ?? "medium";
                p.ApiKeyEnvVar = _pKeyEnv.Checked ? _pEnvVar.Text.Trim() : "";
                p.WorkspaceId = _pWorkspace.Text.Trim();
                if (_pKeyDirect.Checked && _pKey.Text.Trim().Length > 0) Editor.SetPendingKey(p.Id, _pKey.Text);
            }
            if (_defaultProfile.SelectedItem is ProfileItem d) Editor.Working.DefaultProfileId = d.Profile.Id;
            if (_classifierProfile.SelectedItem is ProfileItem c) Editor.Working.ClassifierProfileId = c.Profile.Id;
        }

        private void UpdateProviderFields()
        {
            var anthropic = _pProvider.SelectedIndex == 0;
            _pEffort.Enabled = anthropic;
            _pWorkspace.Enabled = anthropic;
        }

        private void UpdateKeyFields()
        {
            _pKey.Enabled = _pKeyDirect.Checked;
            _pEnvVar.Enabled = _pKeyEnv.Checked;
        }

        private void ShowTest(bool ok, string message) => ShowResult(_pTestResult, ok, message);

        private static void ShowResult(Label label, bool ok, string message)
        {
            label.ForeColor = ok ? Color.SeaGreen : Color.Firebrick;
            label.Text = message;
        }

        /// <summary>편집 중인 값을 반영한 뒤 선택한 프로필과 [연결 테스트]·[모델 목록 불러오기]에 쓸 키(환경 변수 → 입력 중 → 저장됨)를 돌려준다.</summary>
        private LlmProfile CurrentProfileWithKey(out string key)
        {
            StoreProfile();
            var p = SelectedProfile();
            key = p == null ? null : Editor.ResolveKey(p, _host.GetEnv);
            return p;
        }

        private void FillModels(IReadOnlyList<ModelListing> models)
        {
            var current = _pModel.Text.Trim();
            _pModel.BeginUpdate();
            _pModel.Items.Clear();
            foreach (var m in models) _pModel.Items.Add(m.Id);
            _pModel.EndUpdate();
            _pModel.Text = current;
            if (models.Count == 0)
            {
                ShowResult(_pModelsResult, false, "이 키로 쓸 수 있는 모델이 없습니다. 키 권한과 Base URL을 확인하세요.");
                return;
            }
            var note = models.Any(m => string.Equals(m.Id, current, StringComparison.OrdinalIgnoreCase))
                ? ""
                : " 현재 모델은 목록에 없습니다(직접 입력한 값도 그대로 쓸 수 있습니다).";
            ShowResult(_pModelsResult, true, $"모델 {models.Count}개를 불러왔습니다. 목록에서 고르거나 직접 입력하세요.{note}");
        }

        // ---------- 지식 폴더 ----------
        private TabPage BuildKnowledgeTab()
        {
            var browse = new Button { Text = "찾아보기…", AutoSize = true };

            var t = Grid();
            AddRow(t, "RAG 루트", InlineRow(_ragRoot, browse));
            AddRow(t, "", new Label { Text = @"예: \\server\KB — 관리자가 Indexer로 색인을 만든 공유 폴더", AutoSize = true, ForeColor = SystemColors.GrayText });
            AddRow(t, "근거 문서 수", _refTopK);
            AddRow(t, "문체 예시 수", _styleTopK);
            AddRow(t, "", _syncNow);
            AddRow(t, "", _syncResult);
            t.Dock = DockStyle.Top;
            t.AutoSize = true;

            foreach (var (text, width) in new[] { ("제품", 160), ("버전", 60), ("빌드 시각", 150), ("파일", 60), ("청크", 70) })
                _indexStatus.Columns.Add(text, width);

            var page = new TabPage("지식 폴더") { Padding = new Padding(6) };
            page.Controls.Add(_indexStatus);
            page.Controls.Add(t);

            browse.Click += (s, e) => Guard(() =>
            {
                using (var dlg = new FolderBrowserDialog { Description = "공유 지식 폴더(RAG 루트)를 선택하세요.", SelectedPath = _ragRoot.Text })
                    if (dlg.ShowDialog(this) == DialogResult.OK) _ragRoot.Text = dlg.SelectedPath;
            }, "폴더 선택");
            _syncNow.Click += async (s, e) =>
            {
                var started = false;
                try
                {
                    if (!string.Equals((_host.Settings.RagRoot ?? "").Trim(), _ragRoot.Text.Trim(), StringComparison.OrdinalIgnoreCase))
                    {
                        _syncResult.Text = "RAG 루트를 바꿨습니다. 먼저 [저장]한 뒤 다시 여세요.";
                        return;
                    }
                    _syncNow.Enabled = false;
                    started = true;
                    _syncResult.Text = "동기화 중…";
                    _syncResult.Text = await _host.SyncNowAsync(CancellationToken.None);
                    LoadIndexStatus();
                }
                catch (Exception ex)
                {
                    _log.Error("동기화 실패", ex);
                    _syncResult.Text = "동기화 실패: " + ex.Message;
                }
                finally
                {
                    if (started) _syncNow.Enabled = true;
                }
            };
            return page;
        }

        private void LoadKnowledge()
        {
            _ragRoot.Text = Editor.Working.RagRoot;
            _refTopK.Value = Math.Max(_refTopK.Minimum, Math.Min(_refTopK.Maximum, Editor.Working.ReferenceTopK));
            _styleTopK.Value = Math.Max(_styleTopK.Minimum, Math.Min(_styleTopK.Maximum, Editor.Working.StyleExampleTopK));
            LoadIndexStatus();
        }

        private void LoadIndexStatus()
        {
            _indexStatus.Items.Clear();
            try
            {
                var manifest = _host.LoadLocalManifest();
                if (manifest == null) return;
                foreach (var p in manifest.Products)
                    _indexStatus.Items.Add(new ListViewItem(new[]
                    {
                        p.ProductId, p.Version.ToString(CultureInfo.InvariantCulture), p.BuiltAtUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm"),
                        p.FileCount.ToString(CultureInfo.InvariantCulture), p.ChunkCount.ToString(CultureInfo.InvariantCulture),
                    }));
            }
            catch (Exception ex)
            {
                _syncResult.Text = "색인 상태를 읽지 못했습니다: " + ex.Message;
            }
        }

        private void StoreKnowledge()
        {
            Editor.Working.RagRoot = _ragRoot.Text.Trim();
            Editor.Working.ReferenceTopK = (int)_refTopK.Value;
            Editor.Working.StyleExampleTopK = (int)_styleTopK.Value;
        }

        // ---------- 배치 도우미 ----------
        private static TableLayoutPanel Grid()
        {
            var t = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, AutoScroll = true, Padding = new Padding(4) };
            t.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 120));
            t.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            return t;
        }

        private static void AddRow(TableLayoutPanel t, string caption, Control control)
        {
            t.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            var row = t.RowCount++;
            t.Controls.Add(new Label { Text = caption, AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(0, 6, 6, 0) }, 0, row);
            t.Controls.Add(control, 1, row);
        }

        /// <summary>입력 칸(남는 폭 전부)과 옆 버튼을 한 줄에 놓는다.</summary>
        private static TableLayoutPanel InlineRow(Control main, Control side)
        {
            var row = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, AutoSize = true, Margin = Padding.Empty };
            row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            row.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            row.Controls.Add(main, 0, 0);
            row.Controls.Add(side, 1, 0);
            return row;
        }

        private static TabPage Page(string title, Control content)
        {
            var page = new TabPage(title) { Padding = new Padding(6) };
            page.Controls.Add(content);
            return page;
        }
    }
}
