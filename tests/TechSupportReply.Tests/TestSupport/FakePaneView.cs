using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using TechSupportReply.App.Pane;
using TechSupportReply.Core.Products;
using TechSupportReply.Core.Settings;

namespace TechSupportReply.Tests.TestSupport
{
    /// <summary>호출을 기록하는 뷰. Post는 즉시 실행하고, 어느 스레드에서 호출되어도 되도록 잠근다.</summary>
    internal sealed class FakePaneView : IReplyPaneView
    {
        private readonly object _lock = new object();
        private readonly StringBuilder _reply = new StringBuilder();

        public event EventHandler GenerateRequested;
        public event EventHandler StopRequested;
        public event EventHandler DraftRequested;
        public event EventHandler ProductChangedByUser;

        public string Subject { get; private set; }
        public List<string> ProductIds { get; private set; } = new List<string>();
        public string Classification { get; private set; }
        public List<string> ProfileIds { get; private set; } = new List<string>();
        public PaneState State { get; private set; }
        public List<PaneState> States { get; } = new List<PaneState>();
        public List<string> References { get; private set; } = new List<string>();
        public List<string> Warnings { get; private set; } = new List<string>();
        public string Status { get; private set; }
        public bool StatusIsError { get; private set; }
        public int ShowMailThreadId { get; private set; }

        public string SelectedProductId { get; set; }
        public string SelectedProfileId { get; set; }
        public string ExtraInstruction { get; set; } = "";
        public bool UseRag { get; set; } = true;

        public void ShowMail(string subject, string sender)
        {
            lock (_lock) { Subject = subject; ShowMailThreadId = Environment.CurrentManagedThreadId; }
        }

        public void SetProducts(IReadOnlyList<ProductDefinition> products, string selectedId)
        {
            lock (_lock) { ProductIds = products.Select(p => p.Id).ToList(); SelectedProductId = selectedId; }
        }

        public void SelectProduct(string productId)
        {
            lock (_lock) SelectedProductId = productId;
        }

        public void SetClassification(string text)
        {
            lock (_lock) Classification = text;
        }

        public void SetProfiles(IReadOnlyList<LlmProfile> profiles, string selectedId)
        {
            lock (_lock) { ProfileIds = profiles.Select(p => p.Id).ToList(); SelectedProfileId = selectedId; }
        }

        public void SetState(PaneState state)
        {
            lock (_lock) { State = state; States.Add(state); }
        }

        public void ClearReply()
        {
            lock (_lock) _reply.Clear();
        }

        public void AppendReply(string delta)
        {
            lock (_lock) _reply.Append(delta);
        }

        public string ReplyText
        {
            get { lock (_lock) return _reply.ToString(); }
            set { lock (_lock) { _reply.Clear(); _reply.Append(value); } }
        }

        public void SetReferences(IReadOnlyList<string> citations)
        {
            lock (_lock) References = citations.ToList();
        }

        public void SetWarnings(IReadOnlyList<string> warnings)
        {
            lock (_lock) Warnings = warnings.ToList();
        }

        public void SetStatus(string message, bool isError)
        {
            lock (_lock) { Status = message; StatusIsError = isError; }
        }

        public void Post(Action action) => action();

        public void UserChangesProduct(string id)
        {
            SelectedProductId = id;
            ProductChangedByUser?.Invoke(this, EventArgs.Empty);
        }

        public void ClickGenerate() => GenerateRequested?.Invoke(this, EventArgs.Empty);
        public void ClickStop() => StopRequested?.Invoke(this, EventArgs.Empty);
        public void ClickDraft() => DraftRequested?.Invoke(this, EventArgs.Empty);
    }
}
