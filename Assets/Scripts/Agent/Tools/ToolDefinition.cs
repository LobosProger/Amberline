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
    /// The GRAMMAR always writes every parameter. An optional key would force it into an
    /// alternation over subsets, multiplying the rules and handing the model back exactly the
    /// choice constrained decoding exists to take away.
    /// </para>
    /// <para>
    /// The RUNNER is more forgiving, and it has to be. A call the model wrote inside its own plan
    /// never went through the grammar, and there it writes what a person would - read_file with a
    /// path and no line numbers. Refusing that costs a whole round trip to be told something the
    /// tool could have assumed: <see cref="ParameterNamesWithASafeDefault"/> is the short list of
    /// arguments where the assumption is unambiguous, and it never includes one whose absence
    /// could be read as "empty" - a write_file with no content must always be refused.
    /// </para>
    /// </summary>
    public class ToolDefinition
    {
        public string Name { get; }
        public IReadOnlyList<string> ParameterNames { get; }

        /// <summary>Parameters the executor fills in for itself when they are missing, so the
        /// runner lets a call through without them. Empty for every mutating tool.</summary>
        public IReadOnlyList<string> ParameterNamesWithASafeDefault { get; }

        /// <summary>True when the tool changes something outside the process - a file or the OS.</summary>
        public bool IsMutating { get; }

        /// <summary>True for `run_command`, which is approval-gated in every permission mode.</summary>
        public bool IsCommand { get; }

        /// <summary>How many tokens the model may spend producing a call to this tool.</summary>
        public int MaximumResponseTokens { get; }

        /// <summary>
        /// True when this tool's output may be replaced by a one-line stand-in once the context
        /// window fills up, because calling the tool again would produce it afresh.
        /// </summary>
        /// <remarks>
        /// Stated per tool rather than derived from <see cref="IsMutating"/>. `finish` and
        /// `ask_user` are not mutating either, and neither one may be trimmed: a user's answer
        /// cannot be fetched again by calling anything.
        /// </remarks>
        public bool CanItsOutputBeDroppedFromHistory { get; }

        public ToolDefinition(string name, IReadOnlyList<string> parameterNames, bool isMutating, bool isCommand,
            int maximumResponseTokens, IReadOnlyList<string> parameterNamesWithASafeDefault = null,
            bool canItsOutputBeDroppedFromHistory = false)
        {
            Name = name;
            ParameterNames = parameterNames ?? new List<string>();
            ParameterNamesWithASafeDefault = parameterNamesWithASafeDefault ?? new List<string>();
            IsMutating = isMutating;
            IsCommand = isCommand;
            MaximumResponseTokens = maximumResponseTokens;
            CanItsOutputBeDroppedFromHistory = canItsOutputBeDroppedFromHistory;
        }

        /// <summary>True when the executor can run without <paramref name="parameterName"/>.</summary>
        public bool CanRunWithout(string parameterName)
        {
            foreach (string nameWithADefault in ParameterNamesWithASafeDefault)
            {
                if (nameWithADefault == parameterName)
                {
                    return true;
                }
            }

            return false;
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
