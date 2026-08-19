namespace Amberline.Ui
{
    /// <summary>
    /// Semantic type of a single terminal line. The controller picks the kind per line and
    /// <see cref="TerminalView"/> turns it into the matching UXML template and USS shape.
    /// </summary>
    public enum TerminalLineKind
    {
        /// <summary>Start-up text printed while the terminal is coming online.</summary>
        Boot,

        /// <summary>Boxed header block - the product name and version at the top of a session.</summary>
        Banner,

        /// <summary>Echo of the line the user just submitted, prefixed with "&gt; ".</summary>
        UserCommand,

        /// <summary>Prose answer from the agent.</summary>
        AgentMessage,

        /// <summary>A tool call and its short result, rendered dimmer than an agent message.</summary>
        ToolActivity,

        /// <summary>System note that is neither an answer nor a failure - /help output, workspace changes.</summary>
        Notice,

        /// <summary>A failure the user has to see: red, and shaped like a tool card.</summary>
        Error
    }
}
