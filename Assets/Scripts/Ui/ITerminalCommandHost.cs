using System.Threading;

namespace Amberline.Ui
{
    /// <summary>
    /// The narrow view of <see cref="TerminalCliController"/> that the pieces split out of it are
    /// allowed to reach back through. Implemented by the controller itself.
    /// </summary>
    /// <remarks>
    /// It exists because three fields genuinely cannot be cut apart. The cancellation source is
    /// replaced at the start of every turn and read by the slash commands, by the approval preview
    /// and by Escape; the resolved workspace path is written by /cd and read by the boot lines and
    /// the status bar; and the thought block and the running tool card are owned by the event
    /// handlers, which /clear has to close before it takes their labels out of the log.
    /// <para>
    /// An interface rather than a bag of delegates: the alternative was a constructor taking five
    /// <c>Func</c>s, where nothing on the call site says what any of them is for.
    /// </para>
    /// </remarks>
    public interface ITerminalCommandHost
    {
        /// <summary>The folder the agent is sandboxed to right now, as the controller resolved it.</summary>
        string WorkspaceFolderPath { get; }

        /// <summary>
        /// The token of the turn that is running. Falls back to the destroy token when no turn is
        /// in flight, so a caller never has to reason about which of the two it is holding.
        /// </summary>
        CancellationToken GetCancellationTokenOfCurrentTurn();

        /// <summary>
        /// Moves the sandbox, rebuilds the tool stack around it, clears the conversation and
        /// updates both bars. The path must already be verified to exist - see
        /// <see cref="SlashCommandHandler"/>, which does the parsing half.
        /// </summary>
        void ApplyWorkspaceFolderPath(string fullFolderPath);

        /// <summary>
        /// Closes the thought block and the running tool card. Called before the log is cut, so
        /// neither one is left holding a label that is no longer in the panel.
        /// </summary>
        void PrepareScreenForClearing();

        /// <summary>Writes the state word and the context budget into the status bar.</summary>
        void ShowStateWithContextUsage(string stateText);
    }
}
