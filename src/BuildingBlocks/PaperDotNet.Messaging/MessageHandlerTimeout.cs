using JasperFx;
using JasperFx.CodeGeneration;
using Wolverine.Configuration;
using Wolverine.Runtime.Handlers;

namespace PaperDotNet.Messaging;

/// <summary>
/// Overrides the execution timeout of a message handler (the default is 60 seconds). Put it on the handler class or
/// its <c>Handle</c> method; modules use it instead of Wolverine's attribute so that only this building block references Wolverine.
/// </summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method)]
public sealed class MessageHandlerTimeoutAttribute(int seconds) : Attribute
{
    public int Seconds { get; } = seconds;
}

internal sealed class MessageHandlerTimeoutPolicy : IHandlerPolicy
{
    public void Apply(IReadOnlyList<HandlerChain> chains, GenerationRules rules, IServiceContainer container)
    {
        foreach (var chain in chains)
        {
            var timeout = chain.Handlers
                .Select(h => h.Method.GetCustomAttributes(typeof(MessageHandlerTimeoutAttribute), true).Concat(h.HandlerType.GetCustomAttributes(typeof(MessageHandlerTimeoutAttribute), true)))
                .SelectMany(a => a.Cast<MessageHandlerTimeoutAttribute>())
                .Select(a => (int?)a.Seconds)
                .Max();
            if (timeout is not null) chain.ExecutionTimeoutInSeconds = timeout.Value;
        }
    }
}
