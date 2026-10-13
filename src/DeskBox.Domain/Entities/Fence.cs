using DeskBox.Domain.StateMachines;

namespace DeskBox.Domain.Entities;

/// <summary>
/// 桌面分区方框。X/Y/Width/Height 为 DIP（1/96 英寸），相对所属显示器（MonitorId）工作区左上角，
/// 与显示器位置和缩放无关（TDD §4.5.2）。Dpi 仅记录最后一次保存时所在显示器的 DPI，换算不依赖它。
/// </summary>
public sealed class Fence
{
    public const int TitleMaxLength = 32;
    public const int MinSize = 120;
    public const int MinOpacity = 20;
    public const int MaxOpacity = 100;

    private readonly FenceStateMachine _state;

    public Guid Id { get; }
    public string Title { get; private set; }
    public string MonitorId { get; private set; }
    public int X { get; private set; }
    public int Y { get; private set; }
    public int Width { get; private set; }
    public int Height { get; private set; }
    public int Dpi { get; private set; }
    public int Opacity { get; private set; }
    public bool IsStaging { get; }
    public int SortOrder { get; set; }
    public FenceState State => _state.State;

    public Fence(Guid id, string title, string monitorId, int x, int y, int width, int height,
        int dpi = 96, int opacity = 85, bool isStaging = false)
    {
        if (id == Guid.Empty) throw new DomainValidationException(nameof(Id), "不能为空");
        Id = id;
        Title = ValidateTitle(title);
        MonitorId = ValidateMonitor(monitorId);
        (X, Y) = (x, y);
        (Width, Height) = ValidateSize(width, height);
        Dpi = ValidateDpi(dpi);
        Opacity = ValidateOpacity(opacity);
        IsStaging = isStaging;
        _state = new FenceStateMachine();
    }

    public void Rename(string title) => Title = ValidateTitle(title);

    public void MoveTo(string monitorId, int x, int y, int dpi)
    {
        MonitorId = ValidateMonitor(monitorId);
        Dpi = ValidateDpi(dpi);
        (X, Y) = (x, y);
    }

    public void Resize(int width, int height) => (Width, Height) = ValidateSize(width, height);

    public void SetOpacity(int opacity) => Opacity = ValidateOpacity(opacity);

    public void Collapse() => _state.Fire(FenceTrigger.Collapse);
    public void Expand() => _state.Fire(FenceTrigger.Expand);
    public void Hide() => _state.Fire(FenceTrigger.Hide);
    public void Show() => _state.Fire(FenceTrigger.Show);

    public void Delete()
    {
        if (IsStaging) throw new DomainValidationException(nameof(IsStaging), "暂存方框不能删除");
        _state.Fire(FenceTrigger.Delete);
    }

    private static string ValidateTitle(string title)
    {
        var t = title?.Trim() ?? string.Empty;
        if (t.Length is < 1 or > TitleMaxLength)
            throw new DomainValidationException(nameof(Title), $"长度须为 1-{TitleMaxLength}");
        return t;
    }

    private static string ValidateMonitor(string monitorId) =>
        string.IsNullOrWhiteSpace(monitorId)
            ? throw new DomainValidationException(nameof(MonitorId), "不能为空")
            : monitorId;

    private static (int, int) ValidateSize(int w, int h) =>
        w < MinSize || h < MinSize
            ? throw new DomainValidationException("Size", $"宽高须 >= {MinSize}")
            : (w, h);

    private static int ValidateDpi(int dpi) =>
        dpi is < 48 or > 960 ? throw new DomainValidationException(nameof(Dpi), "须在 48-960 之间") : dpi;

    private static int ValidateOpacity(int o) =>
        o is < MinOpacity or > MaxOpacity
            ? throw new DomainValidationException(nameof(Opacity), $"须在 {MinOpacity}-{MaxOpacity} 之间")
            : o;
}
