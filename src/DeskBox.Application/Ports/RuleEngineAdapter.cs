using DeskBox.Domain.Rules;

namespace DeskBox.Application.Ports;

/// <summary>把领域 <see cref="RuleEngine"/> 暴露为 <see cref="IRuleEngine"/>。</summary>
public sealed class RuleEngineAdapter(RuleEngine engine) : IRuleEngine
{
    public Classification Classify(DesktopEntry entry) => engine.Classify(entry);
}
