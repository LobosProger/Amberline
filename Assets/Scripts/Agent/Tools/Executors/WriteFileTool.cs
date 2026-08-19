using System;
using System.IO;
using System.Threading;
using Cysharp.Threading.Tasks;

namespace Amberline.Agent
{
    // write_file(path, content) - the whole file, replaced.
    //
    // The tool itself is short. What it has to get right is the two things around it:
    //
    // 1. IT DOES NOT CHURN THE FILE. The model writes "\n" because JSON escaping teaches it to, and
    //    writing that straight over a CRLF file rewrites every line - so a one-word change arrives
    //    at the approval card as a two-thousand-line diff and the user cannot see what actually
    //    changed. The file's own line ending and byte order mark are read first and put back.
    // 2. IT CAN BE PREVIEWED WITHOUT BEING RUN. The approval gate asks for the change before the
    //    user has said yes, so working out the new text and doing it are two separate steps here.
    //    Both go through the SAME method, which is why the diff on the card is provably the change
    //    that lands rather than a second guess at it.
    //
    // The response token budget is 2048 rather than the 512 a read gets, because the model has to
    // fit a whole source file inside one JSON string.
    public class WriteFileTool : IToolExecutor, IFileChangePreviewProvider
    {
        readonly PathSandbox _pathSandbox;
        readonly FileWriteService _fileWriteService;

        static readonly ToolDefinition k_toolDefinition = new ToolDefinition(
            name: "write_file",
            parameterNames: new[] { "path", "content" },
            isMutating: true,
            isCommand: false,
            maximumResponseTokens: 2048);

        const int k_diffContextLineCount = 3;

        public WriteFileTool(PathSandbox pathSandbox, FileWriteService fileWriteService)
        {
            _pathSandbox = pathSandbox;
            _fileWriteService = fileWriteService;
        }

        public ToolDefinition Definition => k_toolDefinition;

        public UniTask<FileChangePreview> BuildFileChangePreviewAsync(ToolCall toolCall, CancellationToken cancellationToken)
        {
            return UniTask.RunOnThreadPool(() => PlanWholeFileWrite(toolCall, cancellationToken), true, cancellationToken);
        }

        public UniTask<ToolResult> ExecuteAsync(ToolCall toolCall, CancellationToken cancellationToken)
        {
            // File IO runs on the thread pool so writing a large file cannot stall the player loop
            // and freeze the terminal mid-turn.
            return UniTask.RunOnThreadPool(() => WriteWholeFileFromCall(toolCall, cancellationToken), true, cancellationToken);
        }

        ToolResult WriteWholeFileFromCall(ToolCall toolCall, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();

            // Planned again here, on purpose: the file may have changed between the approval card
            // and this moment, and the write must follow what is on disk now.
            var plannedChange = PlanWholeFileWrite(toolCall, cancellationToken);

            if (!plannedChange.IsAvailable)
            {
                return ToolResult.Failure(plannedChange.FailureMessage);
            }

            var writeOutcome = _fileWriteService.WriteWholeFile(toolCall.GetArgument("path"), plannedChange.NewText,
                plannedChange.LineEnding, plannedChange.HasByteOrderMark, cancellationToken);

            if (!writeOutcome.IsSuccess)
            {
                return ToolResult.Failure($"write_file: {writeOutcome.FailureMessage}");
            }

            return ToolResult.Success(BuildSuccessOutputText(plannedChange, writeOutcome));
        }

        // Works out exactly what would be written, and why it could not be. Called once to build
        // the approval card and once again to do the write.
        FileChangePreview PlanWholeFileWrite(ToolCall toolCall, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();

            string suppliedPath = toolCall.GetArgument("path");

            if (string.IsNullOrWhiteSpace(suppliedPath))
            {
                return FileChangePreview.Unavailable(k_toolDefinition.Name,
                    "write_file needs a path. Call it again as write_file with arguments path and content, for example path src/Player.cs.");
            }

            if (!_pathSandbox.TryResolvePath(suppliedPath, out string absoluteFilePath, out string rejectionReason))
            {
                return FileChangePreview.Unavailable(k_toolDefinition.Name, $"write_file: {rejectionReason}");
            }

            string displayPath = _pathSandbox.GetDisplayPath(absoluteFilePath);

            if (Directory.Exists(absoluteFilePath))
            {
                return FileChangePreview.Unavailable(k_toolDefinition.Name,
                    $"write_file: {displayPath} is a folder, not a file. Give the path of a file, like {displayPath}/New.cs.");
            }

            string newText = FileText.NormalizeLineEndingsToLineFeed(toolCall.GetArgument("content"));

            if (!TryReadFileBeingReplaced(absoluteFilePath, displayPath, out FileTextInfo existingFileText, out string readFailure))
            {
                return FileChangePreview.Unavailable(k_toolDefinition.Name, readFailure);
            }

            bool isNewFile = !File.Exists(absoluteFilePath);
            var diff = LineDiff.Compare(existingFileText.Text, newText, k_diffContextLineCount, cancellationToken);

            if (!diff.HasChanges)
            {
                // Silently writing identical bytes teaches the model nothing and invites it to try
                // again; saying so plainly is what breaks the loop.
                return FileChangePreview.Unavailable(k_toolDefinition.Name,
                    $"write_file: {displayPath} already contains exactly that content, so nothing was written. Move on to the next step, or call finish.");
            }

            return FileChangePreview.ForChange(k_toolDefinition.Name, displayPath, isNewFile, diff, newText,
                existingFileText.DominantLineEnding, existingFileText.HasByteOrderMark);
        }

        // A file that does not exist yet is not an error here - it is the create case, and it comes
        // back as empty text with the platform's own line ending.
        static bool TryReadFileBeingReplaced(string absoluteFilePath, string displayPath,
            out FileTextInfo existingFileText, out string failureReason)
        {
            failureReason = null;

            if (!File.Exists(absoluteFilePath))
            {
                existingFileText = FileTextInfo.CreateForFileThatDoesNotExistYet();
                return true;
            }

            if (!FileText.TryReadTextFile(absoluteFilePath, out existingFileText, out string readRejectionReason))
            {
                failureReason = $"write_file: {displayPath} cannot be replaced because {readRejectionReason}";
                return false;
            }

            return true;
        }

        static string BuildSuccessOutputText(FileChangePreview plannedChange, FileWriteOutcome writeOutcome)
        {
            int writtenLineCount = FileText.CountLines(plannedChange.NewText);

            if (writeOutcome.DidCreateNewFile)
            {
                return $"created {writeOutcome.DisplayPath} with {writtenLineCount} lines.";
            }

            return $"wrote {writeOutcome.DisplayPath}, now {writtenLineCount} lines ({plannedChange.Diff.DescribeChangeCounts()}).";
        }
    }
}
