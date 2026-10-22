using System.Globalization;

namespace DeskBox.Application.Startup;

/// <summary>
/// 写日志用的异常描述：只写异常类型名和 HResult（SqliteException 另加 SqliteErrorCode），不写 Message——
/// Message 里常带完整路径（含用户名、文件名），不能进日志。Application 不引用 SQLite，按属性名反射读取错误码。
/// </summary>
public static class FailureText
{
    public static string Describe(Exception ex)
    {
        ArgumentNullException.ThrowIfNull(ex);
        var text = $"{ex.GetType().Name} hresult=0x{ex.HResult.ToString("X8", CultureInfo.InvariantCulture)}";
        if (ex.GetType().Name == "SqliteException" && ex.GetType().GetProperty("SqliteErrorCode")?.GetValue(ex) is int code)
            text += $" sqliteErrorCode={code.ToString(CultureInfo.InvariantCulture)}";
        return text;
    }
}
