using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using NovelGm.Desktop.Api;
using NovelGm.Desktop.Models;
using Xunit;

namespace NovelGm.Desktop.Tests;

/// <summary>
/// 人物卡窗口的行为契约：默认单列、项目展开/收起、草稿保留、仅姓名必填、
/// 保存失败可重试、图片清除、本章行动绑定。窗口不必 ShowDialog：保存结果看
/// <see cref="CharacterEditorWindow.SavedSuccessfully"/>，避免非模态窗口设置 DialogResult。
/// </summary>
public class CharacterCardTests
{
    private static readonly string[] DoubleColumnButtons =
    {
        "PersonalityButton", "AppearanceButton", "OutfitButton",
        "HobbiesButton", "VoiceButton", "SecretButton",
    };

    private static readonly string[] FullRowButtons = { "RoleButton", "GoalsButton", "MiscButton", "ActionButton" };

    [Fact]
    public void Default_layout_shows_only_first_column() => Sta(() =>
    {
        var window = NewWindow();

        Assert.Equal(Visibility.Visible, State(window, "FirstPanel"));
        Assert.Equal(Visibility.Collapsed, State(window, "SecondPanel"));
        Assert.Equal(Visibility.Collapsed, State(window, "ThirdPanel"));
        Assert.Equal(Visibility.Collapsed, State(window, "FieldEditorPanel"));
        Assert.IsType<TextBox>(window.FindName("NameBox"));
        Assert.IsType<TextBox>(window.FindName("AgeBox"));
        Assert.IsType<TextBox>(window.FindName("GenderBox"));

        foreach (var name in DoubleColumnButtons)
            Assert.IsType<Button>(window.FindName(name));
        foreach (var name in FullRowButtons)
            Assert.IsType<Button>(window.FindName(name));

        // 6 个项目按钮排在两列，4 个整行按钮排在同一个纵向容器里
        var personality = (Button)window.FindName("PersonalityButton")!;
        var doubleGrid = Assert.IsType<UniformGrid>(personality.Parent);
        Assert.Equal(2, doubleGrid.Columns);
        var roleParent = ((Button)window.FindName("RoleButton")!).Parent;
        Assert.All(FullRowButtons, name => Assert.Same(roleParent, ((Button)window.FindName(name)!).Parent));
    });

    [Fact]
    public void Clicking_a_field_opens_second_column_and_clicking_it_again_collapses() => Sta(() =>
    {
        var window = NewWindow();
        Click(window, "PersonalityButton");

        Assert.Equal(Visibility.Visible, State(window, "SecondPanel"));
        Assert.Equal(Visibility.Visible, State(window, "FieldEditorPanel"));
        Assert.Equal(Visibility.Collapsed, State(window, "MiscMenuPanel"));
        Assert.Equal(Visibility.Collapsed, State(window, "ThirdPanel"));
        var title = Assert.IsType<TextBlock>(window.FindName("FieldTitle"));
        Assert.Equal(L.T("Field.Personality"), title.Text);
        var editor = Assert.IsType<TextBox>(window.FindName("FieldEditor"));
        Assert.False(editor.IsReadOnly);

        Click(window, "PersonalityButton");
        Assert.Equal(Visibility.Collapsed, State(window, "SecondPanel"));
    });

    [Fact]
    public void Column_count_drives_width_and_the_page_can_scroll_horizontally() => Sta(() =>
    {
        var window = NewWindow();
        var grid = Assert.IsType<Grid>(window.FindName("ColumnsGrid"));
        var scroll = Assert.IsType<ScrollViewer>(window.FindName("RootScroll"));
        Assert.Equal(ScrollBarVisibility.Auto, scroll.HorizontalScrollBarVisibility);
        var one = window.Width;
        Assert.Equal(new GridLength(0), grid.ColumnDefinitions[1].Width);

        Click(window, "PersonalityButton");
        var two = window.Width;
        Assert.True(grid.ColumnDefinitions[1].Width.Value > 0);
        Assert.True(two >= one);

        Click(window, "MiscButton");
        Click(window, "OrganizationButton");
        Assert.True(grid.ColumnDefinitions[2].Width.Value > 0);
        Assert.True(window.Width >= two);

        Click(window, "MiscButton");
        Assert.Equal(new GridLength(0), grid.ColumnDefinitions[1].Width);
        Assert.Equal(new GridLength(0), grid.ColumnDefinitions[2].Width);
    });

    [Fact]
    public void Misc_opens_five_item_menu_and_items_open_third_column() => Sta(() =>
    {
        var window = NewWindow();
        Click(window, "MiscButton");

        Assert.Equal(Visibility.Visible, State(window, "SecondPanel"));
        Assert.Equal(Visibility.Visible, State(window, "MiscMenuPanel"));
        Assert.Equal(Visibility.Collapsed, State(window, "FieldEditorPanel"));
        Assert.Equal(Visibility.Collapsed, State(window, "ThirdPanel"));
        foreach (var name in new[]
                 {
                     "JoinReasonButton", "RelationsButton", "OrganizationButton",
                     "ThinkingHabitsButton", "OtherNotesButton",
                 })
            Assert.IsType<Button>(window.FindName(name));

        Click(window, "OrganizationButton");
        Assert.Equal(Visibility.Visible, State(window, "ThirdPanel"));
        Assert.Equal(L.T("Field.Organization"), Assert.IsType<TextBlock>(window.FindName("MiscFieldTitle")).Text);

        // 切到普通项目：第三列收起，第二列回到普通输入区
        Click(window, "PersonalityButton");
        Assert.Equal(Visibility.Collapsed, State(window, "ThirdPanel"));
        Assert.Equal(Visibility.Visible, State(window, "FieldEditorPanel"));
        Assert.Equal(Visibility.Collapsed, State(window, "MiscMenuPanel"));

        // 再点当前杂项项：第三列收起，菜单还在
        Click(window, "MiscButton");
        Click(window, "OrganizationButton");
        Assert.Equal(Visibility.Visible, State(window, "ThirdPanel"));
        Click(window, "OrganizationButton");
        Assert.Equal(Visibility.Collapsed, State(window, "ThirdPanel"));
        Assert.Equal(Visibility.Visible, State(window, "MiscMenuPanel"));
    });

    [Fact]
    public void Switching_items_keeps_each_draft() => Sta(() =>
    {
        var window = NewWindow();
        var editor = Assert.IsType<TextBox>(window.FindName("FieldEditor"));

        Click(window, "PersonalityButton");
        editor.Text = "甲";
        Click(window, "AppearanceButton");
        Assert.Equal("", editor.Text);
        editor.Text = "乙";
        Click(window, "PersonalityButton");
        Assert.Equal("甲", editor.Text);
        Click(window, "AppearanceButton");
        Assert.Equal("乙", editor.Text);
    });

    [Fact]
    public void Only_the_name_is_required() => Sta(() =>
    {
        var window = NewWindow();
        var requests = new List<CharacterSaveRequestDto>();
        window.SaveHandler = request =>
        {
            requests.Add(request);
            return Task.FromResult(CharacterSaveResult.Success());
        };

        ((TextBox)window.FindName("NameBox")!).Text = "  ";
        Click(window, "SaveButton");
        Assert.Empty(requests);
        Assert.Equal(Visibility.Visible, State(window, "ErrorText"));
        Assert.False(string.IsNullOrWhiteSpace(((TextBlock)window.FindName("ErrorText")!).Text));
        Assert.False(window.SavedSuccessfully);

        ((TextBox)window.FindName("NameBox")!).Text = "只有名字";
        Click(window, "SaveButton");
        Assert.Single(requests);
        Assert.Equal("只有名字", requests[0].Name);
        Assert.True(window.SavedSuccessfully);
    });

    [Fact]
    public void Failed_save_keeps_the_window_draft_and_can_retry() => Sta(() =>
    {
        var window = NewWindow();
        var calls = 0;
        var requests = new List<CharacterSaveRequestDto>();
        window.SaveHandler = request =>
        {
            requests.Add(request);
            calls++;
            return Task.FromResult(calls == 1
                ? CharacterSaveResult.Fail("boom")
                : CharacterSaveResult.Success());
        };

        ((TextBox)window.FindName("NameBox")!).Text = "甲";
        Click(window, "PersonalityButton");
        var editor = (TextBox)window.FindName("FieldEditor")!;
        editor.Text = "不能丢的草稿";

        Click(window, "SaveButton");
        Assert.Equal(1, calls);
        Assert.False(window.SavedSuccessfully);
        Assert.Equal("甲", ((TextBox)window.FindName("NameBox")!).Text);
        Assert.Equal("不能丢的草稿", editor.Text);
        Assert.True(((Button)window.FindName("SaveButton")!).IsEnabled);
        var error = Assert.IsType<TextBlock>(window.FindName("ErrorText"));
        Assert.Equal(Visibility.Visible, error.Visibility);
        Assert.Contains("Char.SaveFailed", error.Text);

        Click(window, "SaveButton");
        Assert.Equal(2, calls);
        Assert.True(window.SavedSuccessfully);
        Assert.Equal("不能丢的草稿", requests[1].Personality);
    });

    [Fact]
    public void Clearing_the_portrait_clears_preview_and_result() => Sta(() =>
    {
        var root = Path.Combine(Path.GetTempPath(), "novel-gm-char-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "人物画像"));
        try
        {
            WritePng(Path.Combine(root, "人物画像", "a.png"));
            var existing = new CharacterDto { Id = "c1", Name = "甲", Portrait = "人物画像/a.png" };
            var window = new CharacterEditorWindow(existing, Array.Empty<CharacterDto>(), root);
            var preview = Assert.IsType<Border>(window.FindName("PortraitPreview"));
            Assert.NotNull(preview.Background);

            Click(window, "ClearPortraitButton");
            Assert.Null(preview.Background);
            Assert.Null(window.PendingPortraitSource);
            Assert.Equal(Visibility.Visible, State(window, "PortraitEmpty"));

            var requests = new List<CharacterSaveRequestDto>();
            window.SaveHandler = request =>
            {
                requests.Add(request);
                return Task.FromResult(CharacterSaveResult.Success());
            };
            Click(window, "SaveButton");
            Assert.Single(requests);
            Assert.Equal("", requests[0].Portrait);
            Assert.Equal("", window.Result.Portrait);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    });

    [Fact]
    public void Chapter_action_is_read_only_without_a_chapter_and_saved_with_one() => Sta(() =>
    {
        // 没有选中章节：行动输入只读，提示「先选择章节」，保存请求不携带章节字段。
        var noChapter = NewWindow(existing: new CharacterDto { Id = "c1", Name = "甲" });
        Click(noChapter, "ActionButton");
        var actionEditor = Assert.IsType<TextBox>(noChapter.FindName("FieldEditor"));
        Assert.True(actionEditor.IsReadOnly);
        var hint = Assert.IsType<TextBlock>(noChapter.FindName("FieldHint"));
        Assert.Equal(Visibility.Visible, hint.Visibility);
        Assert.Equal(L.T("Char.ActionNeedsChapter"), hint.Text);

        CharacterSaveRequestDto? saved = null;
        noChapter.SaveHandler = request =>
        {
            saved = request;
            return Task.FromResult(CharacterSaveResult.Success());
        };
        Click(noChapter, "SaveButton");
        Assert.NotNull(saved);
        Assert.Null(saved!.Chapter_No);
        Assert.Null(saved.Chapter_Action);

        // 有章节：可编辑，请求带 chapter_no / chapter_action。
        var withChapter = new CharacterEditorWindow(
            new CharacterDto { Id = "c1", Name = "甲" },
            Array.Empty<CharacterDto>(), "", 3, "探查灯塔");
        Click(withChapter, "ActionButton");
        var editor = Assert.IsType<TextBox>(withChapter.FindName("FieldEditor"));
        Assert.False(editor.IsReadOnly);
        Assert.Equal("探查灯塔", editor.Text);
        Assert.Equal(L.T("Char.ActionHint"), Assert.IsType<TextBlock>(withChapter.FindName("FieldHint")).Text);
        editor.Text = "去码头";

        saved = null;
        withChapter.SaveHandler = request =>
        {
            saved = request;
            return Task.FromResult(CharacterSaveResult.Success());
        };
        Click(withChapter, "SaveButton");
        Assert.NotNull(saved);
        Assert.Equal(3, saved!.Chapter_No);
        Assert.Equal("去码头", saved.Chapter_Action);
    });

    [Fact]
    public void Hidden_fields_and_relation_metadata_survive_a_save() => Sta(() =>
    {
        var other = new CharacterDto { Id = "c2", Name = "乙" };
        var existing = new CharacterDto
        {
            Id = "c1",
            Name = "甲",
            Age = "27",
            Gender = "女",
            Personality = "谨慎",
            Hobbies = "钓鱼",
            Organization = "灯塔会",
            Thinking_Habits = "先怀疑再求证",
            Goals = new List<string> { "找到旧灯塔", "守住秘密" },
            Recent_Thoughts = "最近的念头",
            Public_Face = "公开身份",
            Known_Facts = new List<string> { "事实一", "事实二" },
            Status = new Dictionary<string, string> { ["dead"] = "false", ["位置"] = "旧灯塔" },
            Relations = new List<RelationDto>
            {
                new() { Target_Id = "c2", Label = "同门", Type = "rival", Intensity = 0.8 },
            },
        };
        var window = new CharacterEditorWindow(existing, new[] { other }, "");

        // 人际关系沿用它原来的单行文本格式，切换项目/保存都不丢类型与强度。
        Click(window, "MiscButton");
        Click(window, "RelationsButton");
        Assert.Equal("乙：同门 | rival | 0.8",
            Assert.IsType<TextBox>(window.FindName("MiscFieldEditor")).Text);

        CharacterSaveRequestDto? saved = null;
        window.SaveHandler = request =>
        {
            saved = request;
            return Task.FromResult(CharacterSaveResult.Success());
        };
        Click(window, "SaveButton");

        Assert.NotNull(saved);
        Assert.Equal("27", saved!.Age);
        Assert.Equal("女", saved.Gender);
        Assert.Equal("谨慎", saved.Personality);
        Assert.Equal("钓鱼", saved.Hobbies);
        Assert.Equal("灯塔会", saved.Organization);
        Assert.Equal("先怀疑再求证", saved.Thinking_Habits);
        Assert.Equal(new[] { "找到旧灯塔", "守住秘密" }, saved.Goals);
        Assert.Equal("最近的念头", saved.Recent_Thoughts);
        Assert.Equal("公开身份", saved.Public_Face);
        Assert.Equal(new[] { "事实一", "事实二" }, saved.Known_Facts);
        Assert.Equal("false", saved.Status["dead"]);
        Assert.Equal("旧灯塔", saved.Status["位置"]);
        var relation = Assert.Single(saved.Relations);
        Assert.Equal("c2", relation.Target_Id);
        Assert.Equal("rival", relation.Type);
        Assert.Equal(0.8, relation.Intensity);
        Assert.Equal("同门", relation.Label);
    });

    [Fact]
    public void Identity_is_beside_the_portrait_and_sections_fill_the_card_height() => Sta(() =>
    {
        var window = NewWindow(); window.ShowInTaskbar = false;
        try
        {
            window.Show(); window.UpdateLayout();
            var first = (FrameworkElement)window.FindName("FirstPanel");
            var header = (FrameworkElement)window.FindName("IdentityHeader");
            var portrait = Bounds(window, "PortraitFrame", first);
            var name = Bounds(window, "NameBox", first);
            var gender = Bounds(window, "GenderBox", first);
            Assert.True(name.Left >= portrait.Right + 12, "basic details should sit to the right of the portrait");
            Assert.True(name.Top >= portrait.Top && gender.Bottom <= portrait.Bottom);
            Assert.InRange((header.ActualHeight + header.Margin.Top + header.Margin.Bottom) / first.ActualHeight, .40, .44);
            Assert.True(Bounds(window, "ActionButton", first).Bottom <= first.ActualHeight + 1);

            Click(window, "PersonalityButton"); window.UpdateLayout();
            Assert.Equal("active", ((Button)window.FindName("PersonalityButton")).Tag);
            Assert.True(((TextBox)window.FindName("FieldEditor")).ActualHeight >= first.ActualHeight - 90);
            Click(window, "MiscButton"); Click(window, "OrganizationButton"); window.UpdateLayout();
            Assert.Null(((Button)window.FindName("PersonalityButton")).Tag);
            Assert.Equal("active", ((Button)window.FindName("MiscButton")).Tag);
            Assert.Equal("active", ((Button)window.FindName("OrganizationButton")).Tag);
            var other = (Button)window.FindName("OtherNotesButton");
            var thinking = (Button)window.FindName("ThinkingHabitsButton");
            Assert.True(other.ActualHeight > thinking.ActualHeight * 1.5);
            Assert.True(((TextBox)window.FindName("MiscFieldEditor")).ActualHeight >= first.ActualHeight - 90);
        }
        finally { window.Close(); }
    });

    private static Rect Bounds(Window window, string name, FrameworkElement relativeTo)
    {
        var element = (FrameworkElement)window.FindName(name);
        return element.TransformToAncestor(relativeTo).TransformBounds(new Rect(element.RenderSize));
    }

    private static CharacterEditorWindow NewWindow(CharacterDto? existing = null, int? chapterNo = null) =>
        new(existing, Array.Empty<CharacterDto>(), "", chapterNo, "");

    private static Visibility State(Window window, string name) =>
        ((FrameworkElement)window.FindName(name)!).Visibility;

    private static void Click(Window window, string name)
    {
        var button = Assert.IsType<Button>(window.FindName(name));
        button.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent, button));
    }

    private static void WritePng(string path)
    {
        var bitmap = new WriteableBitmap(24, 32, 96, 96, PixelFormats.Bgr32, null);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var output = File.Create(path);
        encoder.Save(output);
    }

    private static void Sta(Action action) =>
        StaRunner.Run(action, TimeSpan.FromSeconds(40), "WPF character card operation timed out");
}
