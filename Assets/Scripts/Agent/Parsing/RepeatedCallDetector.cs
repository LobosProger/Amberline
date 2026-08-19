using System;
using System.Collections.Generic;
using System.Text;

namespace Amberline.Agent
{
    // Catches the model looping: the same tool called with the same arguments twice in a row.
    //
    // Back-to-back only, deliberately. The obvious widening - remembering the last few calls and
    // firing on an A-B-A-B pattern - was tried and removed, because read_file, write_file, read_file
    // is not a loop: it is the read-after-write verification the system prompt itself teaches.
    // Blocking it makes the agent look broken exactly while it is doing the right thing.
    //
    // Deliberately not a MonoBehaviour and holds no scene state, so it stays testable.
    // Collaborator: AgentLoop asks IsRepeatOfPreviousCall before executing and calls RememberCall
    // afterwards; Reset is wired to /clear and to the start of a new run.
    public class RepeatedCallDetector
    {
        string _signatureOfPreviousCall;

        // Separators no tool name or argument value can contain, so two different calls can never
        // flatten into the same signature. Written as codes because they have no printable form.
        const char k_separatorBeforeArgumentName = (char)1;
        const char k_separatorBeforeArgumentValue = (char)2;

        /// <summary>
        /// True when <paramref name="toolCall"/> is identical to the call that ran immediately
        /// before it. A pure check - <see cref="RememberCall"/> records the call separately, so the
        /// loop can decide what to do before the history moves on.
        /// </summary>
        public bool IsRepeatOfPreviousCall(ToolCall toolCall)
        {
            if (toolCall == null || _signatureOfPreviousCall == null)
            {
                return false;
            }

            return string.Equals(_signatureOfPreviousCall, BuildSignatureOfCall(toolCall), StringComparison.Ordinal);
        }

        /// <summary>Records the call this turn made, replacing whatever came before it.</summary>
        public void RememberCall(ToolCall toolCall)
        {
            _signatureOfPreviousCall = toolCall == null ? null : BuildSignatureOfCall(toolCall);
        }

        /// <summary>Forgets the previous call, so the next one can never be reported as a repeat.</summary>
        public void Reset()
        {
            _signatureOfPreviousCall = null;
        }

        // Arguments are sorted by name so the same call written with its keys in a different order
        // still counts as the same call - key order in a JSON object carries no meaning.
        static string BuildSignatureOfCall(ToolCall toolCall)
        {
            var argumentNames = new List<string>(toolCall.Arguments.Keys);
            argumentNames.Sort(StringComparer.Ordinal);

            var builder = new StringBuilder(toolCall.ToolName);
            foreach (string argumentName in argumentNames)
            {
                builder.Append(k_separatorBeforeArgumentName);
                builder.Append(argumentName);
                builder.Append(k_separatorBeforeArgumentValue);
                builder.Append(toolCall.GetArgument(argumentName));
            }

            return builder.ToString();
        }
    }
}
