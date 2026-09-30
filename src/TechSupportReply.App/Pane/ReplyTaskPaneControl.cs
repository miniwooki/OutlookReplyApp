using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;
using TechSupportReply.Core.Products;
using TechSupportReply.Core.Settings;

namespace TechSupportReply.App.Pane
{
    /// <summary>Outlook 오른쪽 작업창. 디자이너 없이 코드로 배치한다.</summary>
    public sealed class ReplyTaskPaneControl : UserControl, IReplyPaneView
    {
        private sealed class Item
        {
            public Item(string id, string text) { Id = id; Text = text; }
            public string Id { get; }
            public string Text { get; }
            public override string ToString() => Text;
        }

        private readonly Label _subject = new Label { AutoSize = false, Dock = DockStyle.Fill, AutoEllipsis = true, Font = new Font("맑은 고딕", 9.5f, FontStyle.Bold) };
        private readonly Label _sender = new Label { AutoSize = false, Dock = DockStyle.Fill, AutoEllipsis = true, ForeColor = SystemColors.GrayText };
        private readonly Label _classification = new Label { AutoSize = true, Dock = DockStyle.Fill, ForeColor = SystemColors.GrayText, MaximumSize = new Size(1000, 0) };
        private readonly TextBox _instruction = new TextBox { Multiline = true, Height = 44, Dock = DockStyle.Fill, ScrollBars = ScrollBars.Vertical };
        private readonly CheckBox _useRag = new CheckBox { Text = "지식 폴더(RAG) 검색 사용", Checked = true, AutoSize = true };
        private readonly Label _warnings = new Label { AutoSize = true, Dock = DockStyle.Fill, ForeColor = Color.DarkOrange, MaximumSize = new Size(1000, 0) };
        private readonly LinkLabel _settings = new LinkLabel { Text = "설정…", AutoSize = true, Anchor = AnchorStyles.Right };

        private PaneState _state = PaneState.Idle;
        private bool _generateAvailable = true;

        internal readonly ComboBox ProductCombo = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Dock = DockStyle.Fill };
        internal readonly ComboBox ProfileCombo = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Dock = DockStyle.Fill };
        internal readonly Button GenerateButton = new Button { Text = "답변 생성", AutoSize = true };
        internal readonly Button StopButton = new Button { Text = "중지", AutoSize = true, Enabled = false };
        internal readonly Button DraftButton = new Button { Text = "회신 초안 만들기", AutoSize = true };
        internal readonly TextBox ReplyBox = new TextBox { Multiline = true, ScrollBars = ScrollBars.Vertical, Dock = DockStyle.Fill, AcceptsReturn = true, WordWrap = true };
        internal readonly ListBox ReferenceList = new ListBox { Dock = DockStyle.Fill, IntegralHeight = false, Height = 80 };
        internal readonly Label StatusLabel = new Label { AutoSize = true, Dock = DockStyle.Fill, MaximumSize = new Size(1000, 0) };

        public ReplyTaskPaneControl()
        {
            Font = new Font("맑은 고딕", 9f);
            Padding = new Padding(6);
            var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, AutoScroll = true };
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

            void Row(Control left, Control right, SizeType sizeType = SizeType.AutoSize, float height = 0)
            {
                layout.RowStyles.Add(new RowStyle(sizeType, height));
                var row = layout.RowCount++;
                if (right == null)
                {
                    layout.Controls.Add(left, 0, row);
                    layout.SetColumnSpan(left, 2);
                }
                else
                {
                    layout.Controls.Add(left, 0, row);
                    layout.Controls.Add(right, 1, row);
                }
            }
            Label Caption(string text) => new Label { Text = text, AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(0, 6, 6, 0) };

            var header = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, Height = 22, Margin = Padding.Empty };
            header.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            header.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            header.Controls.Add(_subject, 0, 0);
            header.Controls.Add(_settings, 1, 0);
            Row(header, null, SizeType.Absolute, 24);
            Row(_sender, null, SizeType.Absolute, 20);
            Row(Caption("제품군"), ProductCombo);
            Row(_classification, null);
            Row(Caption("LLM 프로필"), ProfileCombo);
            Row(_useRag, null);
            Row(Caption("추가 지시"), _instruction);
            var buttons = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, WrapContents = true, Margin = Padding.Empty };
            buttons.Controls.AddRange(new Control[] { GenerateButton, StopButton, DraftButton });
            Row(buttons, null);
            Row(ReplyBox, null, SizeType.Percent, 100);
            Row(Caption("참고 문서"), null);
            Row(ReferenceList, null, SizeType.Absolute, 84);
            Row(_warnings, null);
            Row(StatusLabel, null);
            Controls.Add(layout);

            GenerateButton.Click += (s, e) => GenerateRequested?.Invoke(this, EventArgs.Empty);
            StopButton.Click += (s, e) => StopRequested?.Invoke(this, EventArgs.Empty);
            DraftButton.Click += (s, e) => DraftRequested?.Invoke(this, EventArgs.Empty);
            ProductCombo.SelectionChangeCommitted += (s, e) => ProductChangedByUser?.Invoke(this, EventArgs.Empty);
            ProfileCombo.SelectionChangeCommitted += (s, e) => ProfileChangedByUser?.Invoke(this, EventArgs.Empty);
            _settings.LinkClicked += (s, e) => SettingsRequested?.Invoke(this, EventArgs.Empty);
            ShowMail("메일을 선택한 뒤 리본의 [기술지원 답변]을 누르세요.", "");
            SetState(PaneState.Idle);
        }

        public event EventHandler GenerateRequested;
        public event EventHandler StopRequested;
        public event EventHandler DraftRequested;
        public event EventHandler ProductChangedByUser;
        public event EventHandler ProfileChangedByUser;
        public event EventHandler SettingsRequested;

        public string SelectedProductId => (ProductCombo.SelectedItem as Item)?.Id;
        public string SelectedProfileId => (ProfileCombo.SelectedItem as Item)?.Id;
        public string ExtraInstruction => _instruction.Text.Trim();
        public bool UseRag => _useRag.Checked;

        public string ReplyText
        {
            get => ReplyBox.Text.Replace("\r\n", "\n");
            set => ReplyBox.Text = ToWindowsNewlines(value);
        }

        public void ShowMail(string subject, string sender)
        {
            _subject.Text = subject ?? "";
            _sender.Text = sender ?? "";
        }

        public void SetProducts(IReadOnlyList<ProductDefinition> products, string selectedId)
        {
            ProductCombo.BeginUpdate();
            ProductCombo.Items.Clear();
            foreach (var p in products) ProductCombo.Items.Add(new Item(p.Id, p.DisplayName));
            ProductCombo.EndUpdate();
            SelectProduct(selectedId);
        }

        public void SelectProduct(string productId) => Select(ProductCombo, productId);

        public void SetClassification(string text) => _classification.Text = text ?? "";

        public void SetProfiles(IReadOnlyList<LlmProfile> profiles, string selectedId)
        {
            ProfileCombo.BeginUpdate();
            ProfileCombo.Items.Clear();
            foreach (var p in profiles) ProfileCombo.Items.Add(new Item(p.Id, ProfileLabel(p)));
            ProfileCombo.EndUpdate();
            Select(ProfileCombo, selectedId);
        }

        public void SetState(PaneState state)
        {
            _state = state;
            var idle = state == PaneState.Idle;
            GenerateButton.Enabled = idle && _generateAvailable;
            DraftButton.Enabled = idle;
            StopButton.Enabled = state == PaneState.Generating;
            ProductCombo.Enabled = state != PaneState.Generating;
            ProfileCombo.Enabled = idle;
            ReplyBox.ReadOnly = state == PaneState.Generating;
            UseWaitCursor = state != PaneState.Idle;
        }

        public void SetGenerateAvailable(bool available)
        {
            _generateAvailable = available;
            GenerateButton.Enabled = _state == PaneState.Idle && available;
        }

        /// <summary>드롭다운 표시: "표시명 · 모델"(모델이 비어 있으면 표시명만).</summary>
        internal static string ProfileLabel(LlmProfile p) =>
            string.IsNullOrWhiteSpace(p.Model) ? p.DisplayName : p.DisplayName + " · " + p.Model.Trim();

        public void ClearReply() => ReplyBox.Clear();

        public void AppendReply(string delta) => ReplyBox.AppendText(ToWindowsNewlines(delta));

        public void SetReferences(IReadOnlyList<string> citations)
        {
            ReferenceList.BeginUpdate();
            ReferenceList.Items.Clear();
            foreach (var c in citations) ReferenceList.Items.Add(c);
            ReferenceList.EndUpdate();
        }

        public void SetWarnings(IReadOnlyList<string> warnings) =>
            _warnings.Text = warnings.Count == 0 ? "" : string.Join("\n", warnings.Select(w => "⚠ " + w));

        public void SetStatus(string message, bool isError)
        {
            StatusLabel.Text = message ?? "";
            StatusLabel.ForeColor = isError ? Color.Firebrick : SystemColors.ControlText;
        }

        public void Post(Action action)
        {
            // WinForms/COM은 UI 스레드에서만 접근한다. 핸들이 없거나 폐기됐으면 호출 스레드에서 실행하지 않고 버린다.
            if (IsDisposed || !IsHandleCreated) return;
            try { BeginInvoke(action); }
            catch (InvalidOperationException) { /* 핸들이 그 사이 사라짐: 버린다 */ }
        }

        private static void Select(ComboBox combo, string id)
        {
            var item = combo.Items.Cast<Item>().FirstOrDefault(i => i.Id == id);
            combo.SelectedItem = item ?? (combo.Items.Count > 0 ? combo.Items[0] : null);
        }

        private static string ToWindowsNewlines(string text) => (text ?? "").Replace("\r\n", "\n").Replace("\n", "\r\n");
    }
}
