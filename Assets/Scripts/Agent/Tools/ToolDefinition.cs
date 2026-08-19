using System.Collections.Generic;
using System.Threading;
using Cysharp.Threading.Tasks;

namespace Amberline.Agent
{
    /// <summary>
    /// Everything the rest of the system needs to know about one tool without executing it: the
    /// grammar builder reads the name and parameters to emit a branch, the prompt reads them to
    /// describe it, and the runner reads <see cref="IsMutating"/> to decide whether the call has
    /// to pass the approval gate.
    /// <para>
    /// Every parameter is required. An optional one would force the grammar into an alternation
    /// over subsets of keys, multiplying the rules and handing the model back exactly the choice
    /// that constrained decoding exists to take away.
    /// </para>
    /// </summary>
    public class ToolDefinition
    {
        public string Name { get; }
        public IReadOnlyList<string> ParameterNames { get; }

        /// <summary>True when the tool changes something outside the process - a file or the OS.</summary>
        public bool IsMutating { get; }

        /// <summary>True for `run_command`, which is approval-gated in every permission mode.</summary>
        public bool IsCommand { get; }

        /// <summary>How many tokens the model may spend producing a call to this tool.</summary>
        public int MaximumResponseTokens { get; }

        public ToolDefinition(string name, IReadOnlyList<string> parameterNames, bool isMutating, bool isCommand, int maximumResponseTokens)
        {
            Name = name;
            ParameterNames = parameterNames ?? new List<string>();
            IsMutating = isMutating;
            IsCommand = isCommand;
            MaximumResponseTokens = maximumResponseTokens;
        }
    }

    /// <summary>
    /// Runs one tool. Implementations never throw for an ordinary failure - a missing file or a
    /// bad argument comes back as <see cref="ToolResult.Failure"/> so the model can read it and
    /// correct itself, which it cannot do if the loop dies instead.
    /// </summary>
    public interface IToolExecutor
    {
        ToolDefinition Definition { get; }

        UniTask<ToolResult> ExecuteAsync(ToolCall toolCall, CancellationToken cancellationToken);
    }
}
