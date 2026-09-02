using System;
using System.Collections.Generic;
using System.Text;

namespace Amberline.Agent
{
    // Catches the model looping, in the two shapes it actually loops in.
    //
    // 1. THE SAME CALL TWICE IN A ROW. Back-to-back only, deliberately: the obvious widening -
    //    remembering the last few calls and firing on any A-B-A-B pattern - was tried and removed,
    //    because read_file, write_file, read_file is not a loop, it is the read-after-write
    //    verification the system prompt itself teaches. Blocking it makes the agent look broken
    //    exactly while it is doing the right thing.
    //
    // 2. A CALL THAT ALREADY FAILED, SENT AGAIN UNCHANGED - however many other calls came in
    //    between. Measured on mistral-7b, which spent every remaining round trip of a run
    //    alternating read_file with the same wrong edit_file anchor, apologising each time; rule 1
    //    never fired, because the read in between was a different call each time round.
    //
    //    THE MEMORY IS DROPPED THE MOMENT ANYTHING ON DISK CHANGES, and without that this rule is
    //    worse than no rule. "A failed call will fail again" is only true while the world stands
    //    still, and the whole point of this agent is that it does not: measured on Qwen3-4B,
    //    `python hello.py` failed on a missing import, the agent added the import, re-ran the very
    //    same command to check its own fix - and was refused, so it reported success without ever
    //    having seen the script work. A write, an edit or a command that succeeds makes every
    //    earlier failure worth trying again, so ForgetCallsThatFailed is called for each one.
    //
    // Deliberately not a MonoBehaviour and holds no scene state, so it stays testable.
    // Collaborator: AgentLoop asks IsRepeatOfPreviousCall before executing and calls RememberCall
    // afterwards; Reset is wired to /clear and to the start of a new run.
    public class RepeatedCallDetector
    {
        readonly HashSet<string> _signaturesOfCallsThatFailed = new HashSet<string>(StringComparer.Ordinal);

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

        /// <summary>
        /// True when this exact call has already been run in this run and failed. A failed call
        /// repeated unchanged can only fail again, so there is never a reason to spend a round trip
        /// on it, however many other calls came in between.
        /// </summary>
        public bool HasThisExactCallAlreadyFailed(ToolCall toolCall)
        {
            return toolCall != null && _signaturesOfCallsThatFailed.Contains(BuildSignatureOfCall(toolCall));
        }

        /// <summary>Records a call that failed, so it is never run a second time in this run.</summary>
        public void RememberCallThatFailed(ToolCall toolCall)
        {
            if (toolCall == null)
            {
                return;
            }

            _signaturesOfCallsThatFailed.Add(BuildSignatureOfCall(toolCall));
        }

        /// <summary>
        /// Forgets every failure, because something on disk has just changed and the calls that
        /// failed against the old state may well succeed against the new one. Called after every
        /// successful write, edit or command - never after a read, which changes nothing.
        /// </summary>
        public void ForgetCallsThatFailed()
        {
            _signaturesOfCallsThatFailed.Clear();
        }

        /// <summary>Forgets everything, so the next call can never be reported as a repeat.</summary>
        public void Reset()
        {
            _signatureOfPreviousCall = null;
            _signaturesOfCallsThatFailed.Clear();
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
