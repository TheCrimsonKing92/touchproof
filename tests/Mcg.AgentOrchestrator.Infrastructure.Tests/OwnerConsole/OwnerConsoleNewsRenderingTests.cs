using System.Collections.Concurrent;
using System.Drawing;
using Mcg.AgentOrchestrator.App.OwnerConsole;
using Terminal.Gui.App;
using Terminal.Gui.Drawing;
using Terminal.Gui.Drivers;
using Terminal.Gui.Input;
using Terminal.Gui.ViewBase;

// Parallel-safe: this fixture owns its application and terminal buffer.
public sealed class OwnerConsoleNewsRenderingTests
{
    [Fact]
    public void NewsDialogDrawsBoldKeyCellsAcrossWrappedRowsAndPages()
    {
        const string fingerprint = "sha256:0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";
        var entries = OwnerConsoleChangelog.Parse(
            $"## 1 - Short\nSurface fingerprint: {fingerprint}\n" +
            "- Before `ABCDEFGHIJKLMNOPQRSTUVWX` after\n- Another line\n- Last line");
        Assert.NotNull(entries);

        using var app = Application.Create();
        OwnerConsoleFrameOutputInstaller.RequireFactoryMember(app.GetType()).SetValue(app, new ScreenFactory());
        app.Init();
        var driver = app.Driver;
        Assert.NotNull(driver);
        if (driver.AnsiStartupGate is { } startup)
            foreach (var query in startup.PendingQueries.ToArray()) startup.MarkComplete(query);

        using var dialog = new OwnerConsoleTextDialog("Console news", OwnerConsoleChangelog.FormatMarkedDetails(entries));
        dialog.Body.Width = 18;
        dialog.Body.Height = 3;
        var session = app.Begin(dialog);
        try
        {
            app.LayoutAndDraw(forceRedraw: true);
            Assert.Equal(new Size(18, 3), dialog.Body.Viewport.Size);
            var bounds = dialog.Body.FrameToScreen();
            Assert.Equal("1 - Short", ReadRow(0).TrimEnd());
            Assert.Equal("- Before", ReadRow(1).TrimEnd());
            Assert.Equal("ABCDEFGHIJKLMNOPQR", ReadRow(2));
            AssertBold(1, 0, 18, bold: false);
            AssertBold(2, 0, 18, bold: true);
            AssertCleanPage();

            Assert.True(dialog.HandleKey(Key.PageDown));
            app.LayoutAndDraw(forceRedraw: true);
            Assert.Equal(1, dialog.Page.Offset);
            Assert.Equal("- Before", ReadRow(0).TrimEnd());
            Assert.Equal("ABCDEFGHIJKLMNOPQR", ReadRow(1));
            Assert.Equal("STUVWX after", ReadRow(2).TrimEnd());
            AssertBold(0, 0, 18, bold: false);
            AssertBold(1, 0, 18, bold: true);
            AssertBold(2, 0, 6, bold: true);
            AssertBold(2, 6, 18, bold: false);
            AssertCleanPage();

            Assert.True(dialog.HandleKey(Key.PageDown));
            app.LayoutAndDraw(forceRedraw: true);
            Assert.Equal("STUVWX after", ReadRow(0).TrimEnd());
            Assert.Equal("- Another line", ReadRow(1).TrimEnd());
            Assert.Equal("- Last line", ReadRow(2).TrimEnd());
            AssertBold(0, 0, 6, bold: true);
            AssertBold(0, 6, 18, bold: false);
            AssertCleanPage();

            string ReadRow(int row) => string.Concat(Enumerable.Range(0, 18).Select(column =>
                driver.Contents![bounds.Y + row, bounds.X + column].Grapheme));

            void AssertBold(int row, int start, int end, bool bold)
            {
                for (var column = start; column < end; column++)
                {
                    var attribute = driver.Contents![bounds.Y + row, bounds.X + column].Attribute;
                    Assert.NotNull(attribute);
                    Assert.Equal(bold, attribute.Value.Style.HasFlag(TextStyle.Bold));
                }
            }

            void AssertCleanPage()
            {
                var rendered = string.Concat(Enumerable.Range(0, 3).Select(ReadRow));
                Assert.DoesNotContain("`", rendered);
                Assert.DoesNotContain("Surface fingerprint", rendered);
                Assert.DoesNotContain(fingerprint, rendered);
            }
        }
        finally { app.End(session); }
    }

    private sealed class ScreenFactory() : AnsiComponentFactory(
        output: new OwnerConsoleFrameReproductionTests.RecordingOutput(80, 20) { RecordOnly = true },
        sizeMonitor: new FixedSizeMonitor())
    {
        public override IInput<char> CreateInput() => new NoInput();
    }

    private sealed class NoInput : IInput<char>
    {
        public CancellationTokenSource? ExternalCancellationTokenSource { get; set; }
        public void Initialize(ConcurrentQueue<char> inputBuffer) { }
        public void Run(CancellationToken cancellationToken) => cancellationToken.WaitHandle.WaitOne();
        public void Dispose() { }
    }

    private sealed class FixedSizeMonitor : ISizeMonitor
    {
        public bool InitialSizeReceived => true;
        public int InitialCursorRow => 0;
        public event EventHandler<SizeChangedEventArgs>? SizeChanged { add { } remove { } }
        public void Initialize(IDriver driver) => driver.SetScreenSize(80, 20);
        public bool Poll() => false;
    }
}
