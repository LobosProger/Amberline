using System;

namespace Amberline.Agent
{
    /// <summary>
    /// The single seam between the agent loop and whatever is watching it. The loop raises; the
    /// terminal subscribes. Nothing in here knows a view exists, which is what keeps the dependency
    /// running one way: the UI references the core and the core never references the UI.
    /// <para>
    /// Every event carries only <c>Amberline.Agent</c> types, so a second front end - an editor
    /// window, a log file, a test - can listen to exactly the same run without the loop changing.
    /// </para>
    /// <para>
    /// The Raise methods are public because <see cref="AgentLoop"/> is a separate class, but they
    /// are its alone to call. They are deliberately not guarded: a subscriber that throws would
    /// surface as a run failure, and the terminal's own try/finally is what guarantees the input
    /// row unlocks either way.
    /// </para>
    /// </summary>
    public class AgentEvents
    {
        /// <summary>A run has begun. Carries the task text the user typed.</summary>
        public event Action<string> OnRunStarted;

        /// <summary>
        /// The plain-text pass is streaming. Carries the WHOLE text produced so far, never a delta -
        /// assign it to the label, never append it. Fires on the main thread.
        /// </summary>
        public event Action<string> OnAnswerTextStreaming;

        /// <summary>The model's plan for this iteration, final and trimmed of any tool call that ran
        /// on after it. Fires once per iteration, before the tool call.</summary>
        public event Action<string> OnThoughtProduced;

        /// <summary>A tool call is about to run. The stage says how far down the parse ladder the
        /// terminal had to go to get it, so a call that only survived repair is visible.</summary>
        public event Action<ToolCall, ToolCallParseStage> OnToolCallStarting;

        /// <summary>What that tool produced. A failed result is an ordinary outcome the model is
        /// expected to recover from, not the end of the run.</summary>
        public event Action<ToolCall, ToolResult> OnToolResultProduced;

        /// <summary>
        /// One line a running command just printed, stdout and stderr merged in arrival order.
        /// Raised WHILE the command is still running, which is the whole point of it: a build is
        /// ten to thirty seconds of otherwise blank screen. Already on the main thread.
        /// </summary>
        public event Action<string> OnCommandOutputLineProduced;

        /// <summary>
        /// Something the loop did on its own behalf that the user should see - today, compacting
        /// the transcript because it was about to outgrow the model's window. Written for the USER,
        /// not for the model.
        /// </summary>
        public event Action<string> OnNoticeProduced;

        /// <summary>
        /// The run is over, whatever happened - finished, failed, cancelled or out of iterations.
        /// The loop guarantees this fires exactly once per run, from a finally block. A run that
        /// ended without it would leave the terminal locked for the rest of the session.
        /// </summary>
        public event Action<RunReport> OnRunFinished;

        public void RaiseRunStarted(string userTaskText)
        {
            OnRunStarted?.Invoke(userTaskText);
        }

        public void RaiseAnswerTextStreaming(string cumulativeText)
        {
            OnAnswerTextStreaming?.Invoke(cumulativeText);
        }

        public void RaiseThoughtProduced(string thoughtText)
        {
            OnThoughtProduced?.Invoke(thoughtText);
        }

        public void RaiseToolCallStarting(ToolCall toolCall, ToolCallParseStage parseStage)
        {
            OnToolCallStarting?.Invoke(toolCall, parseStage);
        }

        public void RaiseToolResultProduced(ToolCall toolCall, ToolResult toolResult)
        {
            OnToolResultProduced?.Invoke(toolCall, toolResult);
        }

        public void RaiseCommandOutputLineProduced(string outputLine)
        {
            OnCommandOutputLineProduced?.Invoke(outputLine);
        }

        /// <summary>
        /// How fast the model is answering right now. Raised about four times a second while a pass
        /// is generating, always on the main thread. Every figure in it is measured by the gateway
        /// rather than reported by the backend - see <see cref="LlmGenerationStats"/>.
        /// </summary>
        public event Action<LlmGenerationStats> OnGenerationStatsProduced;

        public void RaiseGenerationStatsProduced(LlmGenerationStats generationStats)
        {
            OnGenerationStatsProduced?.Invoke(generationStats);
        }

        public void RaiseNoticeProduced(string noticeText)
        {
            OnNoticeProduced?.Invoke(noticeText);
        }

        public void RaiseRunFinished(RunReport runReport)
        {
            OnRunFinished?.Invoke(runReport);
        }
    }
}
