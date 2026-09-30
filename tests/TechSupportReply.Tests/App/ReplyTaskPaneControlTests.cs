using System;
using System.Linq;
using System.Threading;
using System.Windows.Forms;
using TechSupportReply.App.Pane;
using TechSupportReply.Core.Products;
using TechSupportReply.Core.Settings;
using TechSupportReply.Tests.TestSupport;
using Xunit;

namespace TechSupportReply.Tests.App
{
    public class ReplyTaskPaneControlTests
    {
        [Fact]
        public void ReplyText_RoundTripsNewlines()
        {
            Sta.Run(() =>
            {
                using (var c = new ReplyTaskPaneControl())
                {
                    c.AppendReply("첫 줄\n둘");
                    c.AppendReply("째 줄");
                    Assert.Equal("첫 줄\r\n둘째 줄", c.ReplyBox.Text);
                    Assert.Equal("첫 줄\n둘째 줄", c.ReplyText);
                    c.ReplyText = "a\nb";
                    Assert.Equal("a\r\nb", c.ReplyBox.Text);
                }
            });
        }

        [Fact]
        public void Products_ProgrammaticSelection_DoesNotRaiseUserEvent()
        {
            Sta.Run(() =>
            {
                using (var c = new ReplyTaskPaneControl())
                {
                    int userChanges = 0;
                    c.ProductChangedByUser += (s, e) => userChanges++;
                    c.SetProducts(ProductCatalog.CreateDefault().Products, "ls-dyna");
                    Assert.Equal("ls-dyna", c.SelectedProductId);
                    c.SelectProduct("ansys-fluent");
                    Assert.Equal("ansys-fluent", c.SelectedProductId);
                    Assert.Equal(0, userChanges);
                }
            });
        }

        [Fact]
        public void Profiles_SelectedId()
        {
            Sta.Run(() =>
            {
                using (var c = new ReplyTaskPaneControl())
                {
                    var a = new LlmProfile { Id = "a", DisplayName = "A" };
                    var b = new LlmProfile { Id = "b", DisplayName = "B" };
                    c.SetProfiles(new[] { a, b }, "b");
                    Assert.Equal("b", c.SelectedProfileId);
                    Assert.Equal(new[] { "A", "B" }, c.ProfileCombo.Items.Cast<object>().Select(o => o.ToString()));
                }
            });
        }

        [Theory]
        [InlineData(PaneState.Idle, true, false)]
        [InlineData(PaneState.Classifying, false, false)]
        [InlineData(PaneState.Generating, false, true)]
        public void SetState_TogglesButtons(PaneState state, bool generateEnabled, bool stopEnabled)
        {
            Sta.Run(() =>
            {
                using (var c = new ReplyTaskPaneControl())
                {
                    c.SetState(state);
                    Assert.Equal(generateEnabled, c.GenerateButton.Enabled);
                    Assert.Equal(stopEnabled, c.StopButton.Enabled);
                    Assert.Equal(state == PaneState.Idle, c.DraftButton.Enabled);
                }
            });
        }

        [Fact]
        public void Buttons_RaiseEvents_AndStatusShowsError()
        {
            Sta.Run(() =>
            {
                using (var c = new ReplyTaskPaneControl())
                {
                    int gen = 0, stop = 0, draft = 0;
                    c.GenerateRequested += (s, e) => gen++;
                    c.StopRequested += (s, e) => stop++;
                    c.DraftRequested += (s, e) => draft++;
                    c.SetState(PaneState.Idle);
                    c.GenerateButton.PerformClick();
                    c.DraftButton.PerformClick();
                    c.SetState(PaneState.Generating);
                    c.StopButton.PerformClick();
                    Assert.Equal((1, 1, 1), (gen, stop, draft));

                    c.SetStatus("오류", true);
                    Assert.Equal("오류", c.StatusLabel.Text);
                    c.SetReferences(new[] { "a.pdf p.1" });
                    Assert.Single(c.ReferenceList.Items);
                    Assert.True(c.UseRag);
                }
            });
        }

        [Fact]
        public void Post_FromBackgroundThread_BeforeHandle_DoesNotRunOnCallerThread()
        {
            Sta.Run(() =>
            {
                using (var c = new ReplyTaskPaneControl())
                {
                    bool ran = false;
                    var t = new Thread(() => c.Post(() => ran = true));
                    t.Start();
                    t.Join();
                    Assert.False(ran);
                }
            });
        }

        [Fact]
        public void Post_AfterDispose_DoesNothing()
        {
            Sta.Run(() =>
            {
                var c = new ReplyTaskPaneControl();
                c.Dispose();
                bool ran = false;
                c.Post(() => ran = true);
                Assert.False(ran);
            });
        }

        [Fact]
        public void Post_WithHandle_RunsOnUiThread()
        {
            Sta.Run(() =>
            {
                using (var c = new ReplyTaskPaneControl())
                {
                    var _ = c.Handle;
                    int uiThread = Thread.CurrentThread.ManagedThreadId;
                    int ranOn = -1;
                    var t = new Thread(() => c.Post(() => ranOn = Thread.CurrentThread.ManagedThreadId));
                    t.Start();
                    t.Join();
                    Application.DoEvents();
                    Assert.Equal(uiThread, ranOn);
                }
            });
        }
    }
}
