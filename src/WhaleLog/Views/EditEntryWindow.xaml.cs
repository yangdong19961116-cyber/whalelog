using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using WhaleLog.Models;
using WhaleLog.Services;

namespace WhaleLog.Views;

/// <summary>
/// 编辑一条已经写下的记录。
///
/// 之前写错了只能删掉重打一条 —— 可删掉的是"这条记录存在过"这个事实本身，
/// 时间、序号、关联的归档文件全都跟着没了。这个窗改的是那条记录本身：
/// Id / CreatedAt / 进度 / 勾选状态 / 顺延标记一个都不动，只换正文、分类、标签。
///
/// 刻意**不**做"失焦自动关闭"：改记录是会停下来想一想的事，
/// 跟「记一笔」那种"呼出即写"的场景不一样，鼠标点到别处就消失会很难用。
/// </summary>
public sealed partial class EditEntryWindow : Window
{
    private readonly NoteEntry _entry;
    private readonly List<ToggleButton> _chips = new();
    private string _selectedCategory;
    private bool _closing;

    public EditEntryWindow(NoteEntry entry, DateTime dayOf)
    {
        _entry = entry;
        _selectedCategory = entry.Category;

        InitializeComponent();
        BuildCategoryChips();

        BodyInput.Text = entry.Text ?? "";
        TagInput.Text = entry.Tag ?? "";

        // 日期写在标题上：从「往期」翻回几天前改东西时，没有这一行很容易
        // 记不清正在改的是哪天的记录。
        DateHint.Text = $"· {dayOf:yyyy年M月d日} {ReportService.WeekdayText(dayOf)} · {entry.TimeText}";

        // 输入过空内容之后又接着打字，把红字提示收回去
        BodyInput.TextChanged += OnBodyTextChanged;
    }

    /// <summary>点过保存、内容已经写回模型。false = 取消，调用方什么都别做。</summary>
    public bool Saved { get; private set; }

    private void BuildCategoryChips()
    {
        var categories = NoteCategories.All.ToList();

        // 分类理论上只会是 NoteCategories.All 里的那几个。但 index.json 是可以手改的，
        // 真出现别的值时直接丢掉它，界面上就一个都不高亮、保存又原样写回去 ——
        // 看着就像坏了。所以给它补一个格子。
        if (!string.IsNullOrWhiteSpace(_selectedCategory) && !categories.Contains(_selectedCategory))
            categories.Add(_selectedCategory);

        foreach (var category in categories)
        {
            var chip = new ToggleButton
            {
                Content = category,
                IsChecked = category == _selectedCategory,
                Margin = new Thickness(0, 0, 6, 0),
                Padding = new Thickness(11, 4, 11, 4),
            };
            chip.SetResourceReference(StyleProperty, "ChipToggle");
            chip.Click += OnChipClick;
            CategoryChips.Children.Add(chip);
            _chips.Add(chip);
        }
    }

    /// <summary>
    /// 分类单选。
    ///
    /// ToggleButton 之间**不互斥**，所以互斥必须显式做，而且只能有一个真源（_selectedCategory）：
    /// 勾选状态全部从它推导出来。
    ///
    /// 目标值从**被点的那个** sender 推导，绝不读别的控件的 IsChecked。末尾那支
    /// `: _selectedCategory` 是必须的 —— ToggleButton 的真实行为是"先翻转 IsChecked 再抛 Click"，
    /// 再点一次当前项时 clicked.IsChecked 已经是 false 了，不兜住就变成"一个都不选"。
    /// </summary>
    private void OnChipClick(object sender, RoutedEventArgs e)
    {
        if (sender is not ToggleButton clicked) return;

        var target = clicked.IsChecked == true
            ? clicked.Content as string ?? NoteCategories.Work
            : _selectedCategory;

        _selectedCategory = target;

        foreach (var chip in _chips)
            chip.IsChecked = (chip.Content as string) == target;
    }

    private void OnBodyTextChanged(object sender, TextChangedEventArgs e)
    {
        if (HintText.Text == DefaultHint) return;
        HintText.Text = DefaultHint;
        HintText.SetResourceReference(ForegroundProperty, "TextMutedBrush");
    }

    private const string DefaultHint = "Enter 保存 · Shift+Enter 换行 · Esc 取消";

    private void OnInputKeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Escape:
                e.Handled = true;
                RequestClose();
                break;

            case Key.Enter:
                // Enter 保存、Shift+Enter 换行 —— 和「记一笔」保持同一套手感。
                // 标签框里没有换行可言，但共用一个处理器最省事，Shift+Enter 在那边也无害。
                if ((Keyboard.Modifiers & ModifierKeys.Shift) == 0)
                {
                    e.Handled = true;
                    Save();
                }
                break;
        }
    }

    private void OnSaveClick(object sender, RoutedEventArgs e) => Save();

    private void OnCancelClick(object sender, RoutedEventArgs e) => RequestClose();

    /// <summary>
    /// 把改动写回那条记录。
    ///
    /// 写的是模型里的同一个对象，所以界面靠绑定自动更新，不需要重建列表 ——
    /// 也不会多出一条新记录、不会换掉原来的时间。
    /// </summary>
    private void Save()
    {
        var text = BodyInput.Text.Trim();
        if (text.Length == 0)
        {
            // 空正文等于抹掉这条记录，可那正是「删除」按钮干的活。
            // 保存偷偷干删除的事太意外了，这里拦住并说清楚。
            HintText.Text = "内容不能为空。想删掉这条，请用列表上的「删除」。";
            HintText.SetResourceReference(ForegroundProperty, "DangerBrush");
            BodyInput.Focus();
            return;
        }

        _entry.Text = text;
        _entry.Category = _selectedCategory;
        _entry.Tag = TagInput.Text.Trim();

        Saved = true;
        RequestClose();
    }

    private void OnHeaderDrag(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState != MouseButtonState.Pressed) return;
        try { DragMove(); }
        catch { /* 鼠标状态异常时忽略 */ }
    }

    /// <summary>
    /// 统一的关闭入口。
    ///
    /// 带 _closing 守卫的原因见 QuickCaptureWindow：关闭过程中窗口会失活，
    /// 再回调进来 Close() 一次就会抛"窗口关闭期间无法调用 Close"。
    /// </summary>
    private void RequestClose()
    {
        if (_closing) return;
        _closing = true;
        try
        {
            Close();
        }
        catch (InvalidOperationException)
        {
            // 已经进入关闭流程，忽略
        }
    }

    // ---------------------------------------------------------------
    // 自检出口
    //
    // 刻意留成"能摸到真控件、能点真保存"这一组，而不是开一条直接改模型的近路 ——
    // 近路是模型对了就通过，窗体整个坏掉照样绿，等于没测。
    // 「改完之后模型真的变了吗」才是这一节要验的东西。
    // ---------------------------------------------------------------

    public TextBox BodyBoxForProbe => BodyInput;

    public TextBox TagBoxForProbe => TagInput;

    /// <summary>自检用：模拟用户点某个分类格子（还原 ToggleButton「先翻转再抛 Click」的真实行为）。</summary>
    public bool ClickCategoryForProbe(string category)
    {
        foreach (var chip in _chips)
        {
            if ((chip.Content as string) != category) continue;

            chip.IsChecked = chip.IsChecked != true;
            chip.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            return true;
        }
        return false;
    }

    /// <summary>自检用：窗口内部记住的分类（唯一真源）。</summary>
    public string SelectedCategoryForProbe => _selectedCategory;

    /// <summary>自检用：当前高亮的格子有几个 —— 大于 1 就是"单选没做互斥"。</summary>
    public int CheckedChipCountForProbe => _chips.Count(c => c.IsChecked == true);

    /// <summary>自检用：高亮的格子写的是哪个分类。</summary>
    public string CheckedChipTextForProbe =>
        string.Join("、", _chips.Where(c => c.IsChecked == true).Select(c => c.Content as string));

    public void SaveForProbe() => Save();

    public void CloseForProbe() => RequestClose();
}
