namespace DeskBox.Domain;

/// <summary>领域规则校验失败。</summary>
public class DomainValidationException(string field, string message) : Exception($"{field}: {message}")
{
    public string Field { get; } = field;
}

/// <summary>状态机收到非法流转请求。</summary>
public sealed class InvalidTransitionException(string machine, string from, string trigger)
    : InvalidOperationException($"{machine}: 状态 {from} 不允许触发 {trigger}")
{
    public string Machine { get; } = machine;
    public string From { get; } = from;
    public string Trigger { get; } = trigger;
}
