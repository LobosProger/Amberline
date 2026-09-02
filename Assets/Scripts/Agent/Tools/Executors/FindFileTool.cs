using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using Cysharp.Threading.Tasks;

namespace Amberline.Agent
{
    /// <summary>
    /// Finds files by name anywhere under the workspace. The primitive that was missing: list_dir
    /// only walks two levels and grep only looks inside files, so "where is Player.cs" had no
    /// answer except guessing at a piece of its contents and grepping for that.
    /// </summary>
    /// <remarks>
    /// Literal and case-insensitive, exactly like grep and for the same reason - a regular
    /// expression has to be escaped twice on the way here, once for JSON and once for the engine,
    /// and that is where a small model comes apart. A bare substring also means "Player" finds
    /// PlayerController.cs without the model having to know it might.
    /// <para>
    /// There is deliberately no path parameter. The search always starts at the workspace root,
    /// which is one argument fewer for the grammar to fill in and one fewer for the model to get
    /// wrong; a search that returns too much is narrowed by asking for a longer name, not by
    /// picking a folder the model has not looked in yet.
    /// </para>
    /// </remarks>
    public class FindFileTool : IToolExecutor
    {
        readonly PathSandbox _pathSandbox;

        static readonly ToolDefinition k_toolDefinition = new ToolDefinition(
            name: "find_file",
            parameterNames: new[] { "name" },
            isMutating: false,
            isCommand: false,
            maximumResponseTokens: 512,
            parameterNamesWithASafeDefault: null,
            // A listing of paths is worth nothing once it has been acted on, and it can always be
            // produced again by running the same search.
            canItsOutputBeDroppedFromHistory: true);

        // The same ceiling grep uses. A project large enough to hit it needs a longer name, not a
        // longer wait.
        const int k_maximumFilesToScan = 20000;

        public FindFileTool(PathSandbox pathSandbox)
        {
            _pathSandbox = pathSandbox;
        }

        public ToolDefinition Definition => k_toolDefinition;

        public UniTask<ToolResult> ExecuteAsync(ToolCall toolCall, CancellationToken cancellationToken)
        {
            // On the thread pool, with the token checked inside every loop, so Escape stops a walk
            // over a large tree instead of waiting it out.
            return UniTask.RunOnThreadPool(() => FindFilesWhoseNameContains(toolCall, cancellationToken), true, cancellationToken);
        }

        ToolResult FindFilesWhoseNameContains(ToolCall toolCall, CancellationToken cancellationToken)
        {
            string namePattern = toolCall.GetArgument("name");

            if (string.IsNullOrWhiteSpace(namePattern))
            {
                return ToolResult.Failure(
                    "find_file needs a name. Call it again with part of a file name, for example name Player.cs");
            }

            if (!_pathSandbox.IsWorkspaceFolderAvailable)
            {
                return ToolResult.Failure("find_file: there is no workspace folder to search.");
            }

            var searchState = new FileSearchState();
            WalkAndCollectMatchingFiles(_pathSandbox.WorkspaceRootPath, namePattern.Trim(), searchState, cancellationToken);

            return BuildSearchOutput(namePattern.Trim(), searchState);
        }

        // The same hand-written stack as grep, for the same reason: it is the only shape that can
        // refuse to descend into Library/ before paying for its contents.
        void WalkAndCollectMatchingFiles(string rootAbsolutePath, string namePattern, FileSearchState searchState,
            CancellationToken cancellationToken)
        {
            var directoriesToVisit = new Stack<string>();
            directoriesToVisit.Push(rootAbsolutePath);

            while (directoriesToVisit.Count > 0)
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (searchState.ScannedFileCount >= k_maximumFilesToScan)
                {
                    searchState.DidStopAtFileLimit = true;
                    return;
                }

                string currentDirectoryPath = directoriesToVisit.Pop();
                SearchOneDirectory(currentDirectoryPath, namePattern, searchState, directoriesToVisit, cancellationToken);
            }
        }

        void SearchOneDirectory(string currentDirectoryPath, string namePattern, FileSearchState searchState,
            Stack<string> directoriesToVisit, CancellationToken cancellationToken)
        {
            string[] subdirectoryPaths;
            string[] filePaths;

            try
            {
                subdirectoryPaths = Directory.GetDirectories(currentDirectoryPath);
                filePaths = Directory.GetFiles(currentDirectoryPath);
            }
            catch (Exception)
            {
                // A folder we may not enumerate is skipped, never fatal for the search.
                return;
            }

            foreach (string subdirectoryPath in subdirectoryPaths)
            {
                string subdirectoryName = Path.GetFileName(subdirectoryPath);

                if (_pathSandbox.IsDeniedFileOrDirectoryName(subdirectoryName) || _pathSandbox.IsReparsePoint(subdirectoryPath))
                {
                    continue;
                }

                directoriesToVisit.Push(subdirectoryPath);
            }

            foreach (string filePath in filePaths)
            {
                cancellationToken.ThrowIfCancellationRequested();
                ConsiderOneFile(filePath, namePattern, searchState);
            }
        }

        void ConsiderOneFile(string filePath, string namePattern, FileSearchState searchState)
        {
            searchState.ScannedFileCount++;

            string fileName = Path.GetFileName(filePath);

            if (_pathSandbox.IsDeniedFileOrDirectoryName(fileName))
            {
                return;
            }

            if (fileName.IndexOf(namePattern, StringComparison.OrdinalIgnoreCase) < 0)
            {
                return;
            }

            searchState.MatchingDisplayPaths.Add(_pathSandbox.GetDisplayPath(filePath));
        }

        static ToolResult BuildSearchOutput(string namePattern, FileSearchState searchState)
        {
            if (searchState.MatchingDisplayPaths.Count == 0)
            {
                return ToolResult.Success(BuildNoMatchesText(namePattern, searchState));
            }

            // Sorted so the same search twice reads the same way, and so files that live together
            // are listed together. Directory enumeration order guarantees neither.
            searchState.MatchingDisplayPaths.Sort(StringComparer.OrdinalIgnoreCase);

            string listedPathsText = ToolOutputTruncator.JoinFirstLinesWithinBudget(
                searchState.MatchingDisplayPaths,
                ToolOutputTruncator.k_maximumListEntryCount,
                ToolOutputTruncator.k_maximumOutputCharacterCount,
                out int keptPathCount);

            var outputBuilder = new StringBuilder();
            outputBuilder.Append(DescribeFileCount(searchState.MatchingDisplayPaths.Count));
            outputBuilder.Append($" with {namePattern} in the name:\n");
            outputBuilder.Append(listedPathsText);

            string truncationNote = ToolOutputTruncator.BuildTruncationNote(keptPathCount,
                searchState.MatchingDisplayPaths.Count, "files",
                "Search for a longer part of the name to narrow it down.");

            if (truncationNote.Length > 0)
            {
                outputBuilder.Append('\n');
                outputBuilder.Append(truncationNote);
            }

            AppendNoteWhenTheWalkStoppedEarly(outputBuilder, searchState);

            return ToolResult.Success(outputBuilder.ToString());
        }

        // Written for the model to act on: it says what was compared, so the next call is a
        // shorter name rather than the same one again.
        static string BuildNoMatchesText(string namePattern, FileSearchState searchState)
        {
            var noMatchesBuilder = new StringBuilder();
            noMatchesBuilder.Append(
                $"No file found. find_file compared {namePattern} against the name of every file in the project, case-insensitively and as a plain piece of text. Try a shorter part of the name, or list_dir with path . to see what is there.");

            AppendNoteWhenTheWalkStoppedEarly(noMatchesBuilder, searchState);

            return noMatchesBuilder.ToString();
        }

        static void AppendNoteWhenTheWalkStoppedEarly(StringBuilder outputBuilder, FileSearchState searchState)
        {
            if (!searchState.DidStopAtFileLimit) return;

            outputBuilder.Append($"\n[stopped] the search stopped after {k_maximumFilesToScan} files, so this may not be all of them.");
        }

        static string DescribeFileCount(int fileCount)
        {
            return fileCount == 1 ? "1 file" : $"{fileCount} files";
        }

        class FileSearchState
        {
            public List<string> MatchingDisplayPaths { get; } = new List<string>();
            public int ScannedFileCount { get; set; }
            public bool DidStopAtFileLimit { get; set; }
        }
    }
}
