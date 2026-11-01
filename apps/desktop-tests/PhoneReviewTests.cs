using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using NovelGm.Desktop.Api;
using NovelGm.Desktop.Controls;
using NovelGm.Desktop.Models;
using Xunit;
namespace NovelGm.Desktop.Tests;

[Collection("serial-body-editing")]
public class PhoneReviewTests
{
    [Fact]
    public void Live_snapshot_preserves_image_selection_format_and_undo() => Sta(() =>
    {
        var editor = new RichTextBox { Document = RichBody.FromPlain("保留选区和撤销记录") };
        var run = (Run)((Paragraph)editor.Document.Blocks.FirstBlock).Inlines.FirstInline;
        run.Foreground = Brushes.Red;
        var image = new Image { Source = new WriteableBitmap(24, 24, 96, 96, PixelFormats.Bgr32, null), Tag = BodyFigures.Marker("image.png") };
        var block = new BlockUIContainer(image); editor.Document.Blocks.Add(block);
        using var host = new Host(editor);
        editor.Selection.Select(run.ContentStart.GetPositionAtOffset(1)!, run.ContentStart.GetPositionAtOffset(4)!);
        var before = editor.Selection.Text; var undo = editor.CanUndo; var changes = 0;
        editor.TextChanged += (_, _) => changes++;
        var snapshot = ReadingSnapshot.Clone(editor.Document);
        Assert.Equal(0, changes); Assert.Equal(before, editor.Selection.Text); Assert.Equal(undo, editor.CanUndo);
        Assert.Same(block, image.Parent); Assert.Same(image, block.Child);
        var copy = Assert.Single(snapshot.Blocks.OfType<BlockUIContainer>());
        Assert.NotSame(image, copy.Child); Assert.Same(image.Source, ((Image)copy.Child).Source);
        Assert.Equal(Colors.Red, ((SolidColorBrush)((Run)((Paragraph)snapshot.Blocks.FirstBlock).Inlines.FirstInline).Foreground).Color);
    });

    [Fact]
    public void Ordinary_paragraphs_align_left_but_heading_quote_and_caption_survive() => Sta(() =>
    {
        var body = new Paragraph(new Bold(new Run("普通正文"))) { TextAlignment = TextAlignment.Right, TextIndent = 12 };
        var heading = new Paragraph(new Run("标题") { FontSize = 28, FontWeight = FontWeights.Bold }) { TextAlignment = TextAlignment.Center };
        var caption = new Paragraph(new Run("题注") { FontSize = 15, FontStyle = FontStyles.Italic }) { TextAlignment = TextAlignment.Center, TextIndent = 0, Margin = new Thickness(0,0,0,16) };
        var quote = new Paragraph(new Run("引用")) { Margin = new Thickness(24,4,0,8) };
        var section = new Section(); foreach (var block in new[] {body,heading,caption,quote}) section.Blocks.Add(block);
        var doc = new FlowDocument(section);
        BodyParagraphs.Normalize(doc,17);
        Assert.Equal(TextAlignment.Left,body.TextAlignment); Assert.Equal(12,body.TextIndent);
        Assert.Equal(TextAlignment.Center,heading.TextAlignment); Assert.Equal(TextAlignment.Center,caption.TextAlignment); Assert.Equal(24,quote.Margin.Left);
    });

    [Theory]
    [InlineData("none")] [InlineData("cover")] [InlineData("scroll")]
    public void Phone_pages_are_real_and_sidebar_resize_does_not_repaginate(string effect) => Sta(() =>
    {
        var store = Store(); store.Data.Preferences.TurnEffect = effect;
        var reader = new ReadingView(store);
        using var host = new Host(reader,420,850);
        var text = string.Join("\n", Enumerable.Repeat("雨落在山门前。他收起没有寄出的信，沿着灯光往前走。",1800));
        var chapter = new ChapterDto { No=1,Title="没有寄出的信",Body_Text=text };
        reader.ShowChapter(new ProjectDto {Id="test",Title="山雨"},chapter,0,2);
        var ticks=0; var timer=new DispatcherTimer {Interval=TimeSpan.FromMilliseconds(10)};
        timer.Tick+=(_,_)=>ticks++;timer.Start();
        WpfPump.Until(()=>reader.PageCount>2);timer.Stop();
        Assert.True(ticks>0); Assert.Equal(320,reader.Snapshot.PageWidth); Assert.Equal(560,reader.Snapshot.PageHeight);
        reader.TurnPage(1);WpfPump.Until(()=>reader.PageNumber==2);
        var anchor=reader.CurrentAnchor(); Assert.True(anchor.Offset>0);
        host.Window.Width=280;host.Window.UpdateLayout();
        Assert.Equal(320,reader.Snapshot.PageWidth);Assert.Equal(2,reader.PageNumber);
        reader.ShowChapter(new ProjectDto {Id="test",Title="山雨"},chapter,0,2,anchor);
        WpfPump.Until(()=>reader.PageNumber==2);
        reader.DisposeReader();
    });

    [Fact]
    public void Toolbar_keeps_writing_controls_and_moves_expert_controls_into_more() => Sta(() =>
    {
        var editor = new RichTextBox {Document=RichBody.FromPlain("甲乙丙")};var bar=new FormatToolbar {Target=editor};
        BodyEditing.Attach(editor,()=>true,()=>null,()=>1,()=>bar,()=>{});
        var panel=new DockPanel();DockPanel.SetDock(bar,Dock.Top);panel.Children.Add(bar);panel.Children.Add(editor);
        using var host=new Host(panel,850,500);
        Assert.False(bar.FontCombo.IsVisible);Assert.False(bar.BoldButton.IsVisible);Assert.False(bar.AlignCenterButton.IsVisible);
        Assert.False(bar.SpacingCombo.IsVisible);
        Assert.True(bar.SizeCombo.IsVisible);Assert.True(bar.StyleCombo.IsVisible);Assert.True(bar.ImageButton.IsVisible);
        Assert.True(bar.MoreButton.IsVisible);
        var run=(Run)((Paragraph)editor.Document.Blocks.FirstBlock).Inlines.FirstInline;
        editor.Selection.Select(run.ContentStart.GetPositionAtOffset(1)!,run.ContentStart.GetPositionAtOffset(2)!);
        var menu=BodyEditing.BuildTextMenu(editor);var row=(StackPanel)((MenuItem)menu.Items[0]).Header;
        var button=(ToggleButton)row.Children[0];button.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
        Assert.Equal("乙",editor.Selection.Text);Assert.Equal(FontWeights.Bold,editor.Selection.GetPropertyValue(TextElement.FontWeightProperty));
        editor.IsReadOnly=true;var disabled=(StackPanel)((MenuItem)BodyEditing.BuildTextMenu(editor).Items[0]).Header;
        Assert.All(disabled.Children.OfType<Control>(),c=>Assert.False(c.IsEnabled));
    });

    [Fact]
    public void Enter_indents_new_body_paragraph_using_current_character_size() => Sta(() =>
    {
        var editor = new RichTextBox { Document = RichBody.FromPlain("正文") };
        BodyEditing.Attach(editor, () => true, () => null, () => 1, () => null, () => { });
        using var host = new Host(editor); editor.Focus(); editor.CaretPosition = editor.Document.ContentEnd;
        editor.Selection.ApplyPropertyValue(TextElement.FontSizeProperty, 24d);
        EditingCommands.EnterParagraphBreak.Execute(null, editor);
        editor.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(editor), 0, Key.Enter) { RoutedEvent = Keyboard.PreviewKeyUpEvent });
        WpfPump.Until(() => editor.CaretPosition.Paragraph?.TextIndent == 48);
        Assert.Equal(TextAlignment.Left, editor.CaretPosition.Paragraph!.TextAlignment);
        Assert.Equal(2, editor.Document.Blocks.OfType<Paragraph>().Count());
    });

    [Fact]
    public void Old_reading_preferences_migrate_and_new_settings_are_bounded()
    {
        var file=Path.Combine(Path.GetTempPath(),Guid.NewGuid()+".json");
        try
        {
            File.WriteAllText(file,"{\"Preferences\":{\"FontSize\":24,\"LineSpacing\":1.95,\"Characters\":36},\"Positions\":{}}");
            var prefs=new ReadingPositionStore(file).Data.Preferences;
            Assert.Equal(24,prefs.FontSize);Assert.Equal(1.5,prefs.LineSpacing,5);Assert.Equal(360,prefs.Width);Assert.Equal(648,prefs.Height);
            prefs.Width=double.NaN;prefs.Height=-3;prefs.PageMargin=999;prefs.SpeechRate=90;prefs.TurnEffect="bad";prefs.Normalize();
            Assert.Equal(360,prefs.Width);Assert.Equal(360,prefs.Height);Assert.Equal(48,prefs.PageMargin);Assert.Equal(10,prefs.SpeechRate);Assert.Equal("scroll",prefs.TurnEffect);
        }
        finally { File.Delete(file); }
    }

    [Fact]
    public void Speech_calls_stay_on_background_STA_and_support_pause_resume_and_missing_voices() => Sta(() =>
    {
        var fake=new FakeVoice();using var service=new ReadingSpeech(()=>fake);
        var voices=WpfPump.Wait(service.VoicesAsync());Assert.Single(voices);
        var task=service.SpeakAsync("正文«插图:secret.png»末尾",0,new ReadingPreferences());WpfPump.Until(()=>task.IsCompleted);task.GetAwaiter().GetResult();
        Assert.DoesNotContain("secret",fake.LastText);Assert.True(fake.Background);Assert.Equal(ApartmentState.STA,fake.Apartment);
        service.Pause();WpfPump.Until(()=>fake.PauseCount==1);service.Resume();WpfPump.Until(()=>fake.ResumeCount==1);
        service.Stop();WpfPump.Until(()=>fake.Stops>=2);
        using var absent=new ReadingSpeech(()=>new FakeVoice {HasVoices=false});Assert.Empty(WpfPump.Wait(absent.VoicesAsync()));
    });
    public class FakeVoice
    {
        public bool HasVoices=true;public int PauseCount,ResumeCount,Stops;public string LastText="";public bool Background;public ApartmentState Apartment;
        public int Rate {get;set;} public int Volume {get;set;} public object? Voice {get;set;} public FakeStatus Status {get;}=new();
        public FakeTokens GetVoices()=>new(){Count=HasVoices?1:0};
        public int Speak(string text,int flags){Background=Thread.CurrentThread.IsBackground;Apartment=Thread.CurrentThread.GetApartmentState();if(text.Length==0)Interlocked.Increment(ref Stops);else LastText=text;return 1;}
        public void Pause()=>Interlocked.Increment(ref PauseCount);public void Resume()=>Interlocked.Increment(ref ResumeCount);
    }
    public class FakeTokens {public int Count {get;set;} public FakeToken Item(int i)=>new();}
    public class FakeToken {public string Id=>"TTS_MS_ZH-CN_TEST";public string GetDescription()=>"测试声音";}
    public class FakeStatus {public int RunningState=>2;public int InputWordPosition=>0;}
    private static ReadingPositionStore Store()=>new(Path.Combine(Path.GetTempPath(),Guid.NewGuid()+".json"));
    private sealed class Host : IDisposable
    {
        public Window Window {get;}
        public Host(UIElement content,double width=600,double height=400)
        {Window=new Window {Content=content,Width=width,Height=height,ShowInTaskbar=false};Window.Show();Window.UpdateLayout();}
        public void Dispose(){Window.Close();Window.Dispatcher.Invoke(()=>{},DispatcherPriority.Background);}
    }
    private static void Sta(Action action)=>StaRunner.Run(action,TimeSpan.FromSeconds(35),"WPF review operation timed out");
}
