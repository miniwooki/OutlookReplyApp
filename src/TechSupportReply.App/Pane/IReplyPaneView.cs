using System;
using System.Collections.Generic;
using TechSupportReply.Core.Products;
using TechSupportReply.Core.Settings;

namespace TechSupportReply.App.Pane
{
    public enum PaneState
    {
        Idle,
        Classifying,
        Generating,
    }

    /// <summary>작업창 화면 계약. 프레젠터는 UI 스레드에서 호출하고, 백그라운드 콜백은 Post로 넘긴다.</summary>
    public interface IReplyPaneView
    {
        event EventHandler GenerateRequested;
        event EventHandler StopRequested;
        event EventHandler DraftRequested;
        /// <summary>사용자가 드롭다운에서 제품을 직접 바꿨을 때만 발생한다(코드로 선택할 때는 발생하지 않음).</summary>
        event EventHandler ProductChangedByUser;
        /// <summary>사용자가 LLM 프로필 드롭다운을 직접 바꿨을 때만 발생한다(코드로 선택할 때는 발생하지 않음).</summary>
        event EventHandler ProfileChangedByUser;

        void ShowMail(string subject, string sender);
        void SetProducts(IReadOnlyList<ProductDefinition> products, string selectedId);
        void SelectProduct(string productId);
        void SetClassification(string text);
        /// <summary>키가 있는 프로필만 받는다. 화면에는 "표시명 · 모델"로 보인다.</summary>
        void SetProfiles(IReadOnlyList<LlmProfile> profiles, string selectedId);
        /// <summary>false면 상태와 관계없이 [답변 생성]을 끈다(키가 있는 프로필이 없을 때).</summary>
        void SetGenerateAvailable(bool available);
        void SetState(PaneState state);
        void ClearReply();
        void AppendReply(string delta);
        /// <summary>줄바꿈은 \n으로 주고받는다.</summary>
        string ReplyText { get; set; }
        void SetReferences(IReadOnlyList<string> citations);
        void SetWarnings(IReadOnlyList<string> warnings);
        void SetStatus(string message, bool isError);

        string SelectedProductId { get; }
        string SelectedProfileId { get; }
        string ExtraInstruction { get; }
        bool UseRag { get; }

        void Post(Action action);
    }
}
