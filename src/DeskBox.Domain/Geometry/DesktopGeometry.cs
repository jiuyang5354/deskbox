namespace DeskBox.Domain.Geometry;

/// <summary>整数矩形（左上角 + 宽高）。物理像素或 DIP 由使用方约定。</summary>
public readonly record struct ScreenRect(int X, int Y, int Width, int Height)
{
    public int Right => X + Width;
    public int Bottom => Y + Height;

    public bool Contains(ScreenRect r) => r.X >= X && r.Y >= Y && r.Right <= Right && r.Bottom <= Bottom;

    public bool ContainsPoint(int x, int y) => x >= X && x < Right && y >= Y && y < Bottom;
}

/// <summary>
/// 方框坐标换算（TDD §4.5.2）。数据库里存的是 <b>DIP（1/96 英寸）、相对所属显示器工作区左上角</b> 的位置，
/// 与显示器物理位置和缩放比例无关；显示时按当前显示器 DPI 换算成物理像素。
/// 换算用"四舍五入、远离零"：DPI ≥ 96 时 DIP → 物理 → DIP 精确还原（误差 ≤ 0.5·96/dpi &lt; 0.5）。
/// </summary>
public static class DesktopGeometry
{
    public const int BaseDpi = 96;

    public static int ToPhysical(int dip, int dpi) => (int)Math.Round(dip * (double)dpi / BaseDpi, MidpointRounding.AwayFromZero);

    public static int ToDip(int physical, int dpi) => (int)Math.Round(physical * (double)BaseDpi / dpi, MidpointRounding.AwayFromZero);

    /// <summary>存储位置（DIP，相对工作区）→ 屏幕物理像素。</summary>
    public static ScreenRect ToScreen(ScreenRect dip, ScreenRect workArea, int dpi) =>
        new(workArea.X + ToPhysical(dip.X, dpi), workArea.Y + ToPhysical(dip.Y, dpi), ToPhysical(dip.Width, dpi), ToPhysical(dip.Height, dpi));

    /// <summary>屏幕物理像素 → 存储位置（DIP，相对工作区）。</summary>
    public static ScreenRect ToStored(ScreenRect screen, ScreenRect workArea, int dpi) =>
        new(ToDip(screen.X - workArea.X, dpi), ToDip(screen.Y - workArea.Y, dpi), ToDip(screen.Width, dpi), ToDip(screen.Height, dpi));

    /// <summary>把矩形整体挪进工作区（先缩小到不超过工作区，再平移），结果一定完全在工作区内。</summary>
    public static ScreenRect ClampInto(ScreenRect r, ScreenRect workArea)
    {
        var w = Math.Min(r.Width, workArea.Width);
        var h = Math.Min(r.Height, workArea.Height);
        var x = Math.Clamp(r.X, workArea.X, workArea.Right - w);
        var y = Math.Clamp(r.Y, workArea.Y, workArea.Bottom - h);
        return new ScreenRect(x, y, w, h);
    }
}
