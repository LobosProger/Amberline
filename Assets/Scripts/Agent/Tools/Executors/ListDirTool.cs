using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using Cysharp.Threading.Tasks;

namespace Amberline.Agent
{
    // list_dir(path) - the tool that answers "what is here", and the reason the previous agent used
    // to drown.
    //
    // The walk is written by hand, with an explicit stack, exactly two levels deep. It is NOT
    // SearchOption.AllDirectories and NOT EnumerationOptions.RecurseSubdirectories, because neither
    // of those can PRUNE: they hand back a flat list of everything and the filtering happens after
    // the disk has already been walked. In a Unity project that means descending into Library/,
    // which is tens of thousands of generated files - the old agent did exactly that and never
    // came back with anything useful.
    //
    // Two levels is a deliberate middle: one level makes the model spend a turn per folder, and
    // three floods the transcript. Denied folders are reported by name rather than hidden, so the
    // model learns they exist and is closed, instead of guessing paths inside them turn after turn.
    public class ListDirTool : IToolExecutor
    {
        readonly PathSandbox _pathSandbox;

        static readonly ToolDefinition k_toolDefinition = new ToolDefinition(
            name: "list_dir",
            parameterNames: new[] { "path" },
            isMutating: false,
            isCommand: false,
            maximumResponseTokens: 512);

        const int k_maximumWalkDepth = 2;
        const int k_maximumEntriesToCollect = 20000;
        const int k_maximumSkippedNamesToReport = 8;

        public ListDirTool(PathSandbox pathSandbox)
        {
            _pathSandbox = pathSandbox;
        }

        public ToolDefinition Definition => k_toolDefinition;

        public UniTask<ToolResult> ExecuteAsync(ToolCall toolCall, CancellationToken cancellationToken)
        {
            // The walk runs on the thread pool: a wide folder is thousands of stat calls and the
            // player loop must keep drawing the terminal while they happen.
            return UniTask.RunOnThreadPool(() => ListRequestedDirectory(toolCall, cancellationToken), true, cancellationToken);
        }

        ToolResult ListRequestedDirectory(ToolCall toolCall, CancellationToken cancellationToken)
        {
            // A missing path means the project root here - that is the only thing it can mean, and
            // the sandbox already treats an empty path as the root.
            string suppliedPath = toolCall.GetArgument("path");

            if (!_pathSandbox.TryResolvePath(suppliedPath, out string absoluteDirectoryPath, out string rejectionReason))
            {
                return ToolResult.Failure($"list_dir: {rejectionReason}");
            }

            string displayPath = _pathSandbox.GetDisplayPath(absoluteDirectoryPath);

            if (File.Exists(absoluteDirectoryPath))
            {
                return ToolResult.Failure($"list_dir: {displayPath} is a file, not a folder. Call read_file on it instead.");
            }

            if (!Directory.Exists(absoluteDirectoryPath))
            {
                return ToolResult.Failure($"list_dir: no such folder: {displayPath}. Call list_dir with path . to see the project root.");
            }

            var collectedEntries = new List<DirectoryEntryInfo>();
            var skippedDirectoryNames = new List<string>();

            WalkTwoLevelsDeep(absoluteDirectoryPath, collectedEntries, skippedDirectoryNames, cancellationToken);

            return BuildListOutput(displayPath, collectedEntries, skippedDirectoryNames);
        }

        void WalkTwoLevelsDeep(string rootAbsolutePath, List<DirectoryEntryInfo> collectedEntries,
            List<string> skippedDirectoryNames, CancellationToken cancellationToken)
        {
            var directoriesToVisit = new Stack<PendingDirectory>();
            directoriesToVisit.Push(new PendingDirectory(rootAbsolutePath, 1));

            while (directoriesToVisit.Count > 0)
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (collectedEntries.Count >= k_maximumEntriesToCollect)
                {
                    return;
                }

                PendingDirectory pendingDirectory = directoriesToVisit.Pop();
                CollectEntriesOfOneDirectory(pendingDirectory, directoriesToVisit, collectedEntries, skippedDirectoryNames);
            }
        }

        void CollectEntriesOfOneDirectory(PendingDirectory pendingDirectory, Stack<PendingDirectory> directoriesToVisit,
            List<DirectoryEntryInfo> collectedEntries, List<string> skippedDirectoryNames)
        {
            string[] subdirectoryPaths;
            string[] filePaths;

            try
            {
                subdirectoryPaths = Directory.GetDirectories(pendingDirectory.AbsolutePath);
                filePaths = Directory.GetFiles(pendingDirectory.AbsolutePath);
            }
            catch (Exception)
            {
                // A folder we are not allowed to enumerate is skipped, never fatal for the listing.
                return;
            }

            foreach (string subdirectoryPath in subdirectoryPaths)
            {
                AddSubdirectoryEntry(subdirectoryPath, pendingDirectory.Depth, directoriesToVisit, collectedEntries, skippedDirectoryNames);
            }

            foreach (string filePath in filePaths)
            {
                AddFileEntry(filePath, collectedEntries);
            }
        }

        void AddSubdirectoryEntry(string subdirectoryPath, int currentDepth, Stack<PendingDirectory> directoriesToVisit,
            List<DirectoryEntryInfo> collectedEntries, List<string> skippedDirectoryNames)
        {
            string subdirectoryName = Path.GetFileName(subdirectoryPath);

            if (_pathSandbox.IsDeniedFileOrDirectoryName(subdirectoryName) || _pathSandbox.IsReparsePoint(subdirectoryPath))
            {
                RememberSkippedDirectoryName(skippedDirectoryNames, subdirectoryName);
                return;
            }

            collectedEntries.Add(new DirectoryEntryInfo(_pathSandbox.GetDisplayPath(subdirectoryPath), true, 0));

            if (currentDepth < k_maximumWalkDepth)
            {
                directoriesToVisit.Push(new PendingDirectory(subdirectoryPath, currentDepth + 1));
            }
        }

        static void RememberSkippedDirectoryName(List<string> skippedDirectoryNames, string subdirectoryName)
        {
            if (skippedDirectoryNames.Count >= k_maximumSkippedNamesToReport || skippedDirectoryNames.Contains(subdirectoryName))
            {
                return;
            }

            skippedDirectoryNames.Add(subdirectoryName);
        }

        void AddFileEntry(string filePath, List<DirectoryEntryInfo> collectedEntries)
        {
            string fileName = Path.GetFileName(filePath);

            // Meta files are skipped silently rather than reported: a Unity folder has one per
            // asset, so naming them would be the whole listing.
            if (_pathSandbox.IsDeniedFileOrDirectoryName(fileName))
            {
                return;
            }

            try
            {
                var fileInformation = new FileInfo(filePath);

                if ((fileInformation.Attributes & FileAttributes.ReparsePoint) != 0)
                {
                    return;
                }

                collectedEntries.Add(new DirectoryEntryInfo(_pathSandbox.GetDisplayPath(filePath), false, fileInformation.Length));
            }
            catch (Exception)
            {
                // A file that vanished or cannot be inspected is left out of the listing.
            }
        }

        static ToolResult BuildListOutput(string displayPath, List<DirectoryEntryInfo> collectedEntries, List<string> skippedDirectoryNames)
        {
            if (collectedEntries.Count == 0)
            {
                return ToolResult.Success($"{displayPath} is empty.{BuildSkippedDirectoriesNote(skippedDirectoryNames)}");
            }

            // Sorting by path groups every child under its parent, which is what makes a two-level
            // listing readable at all - the stack itself pops in no useful order.
            collectedEntries.Sort((firstEntry, secondEntry) =>
                string.Compare(firstEntry.DisplayPath, secondEntry.DisplayPath, StringComparison.OrdinalIgnoreCase));

            var renderedEntryLines = new List<string>(collectedEntries.Count);

            foreach (DirectoryEntryInfo directoryEntry in collectedEntries)
            {
                renderedEntryLines.Add(RenderEntryLine(directoryEntry));
            }

            string entryListingText = ToolOutputTruncator.JoinFirstLinesWithinBudget(renderedEntryLines,
                ToolOutputTruncator.k_maximumListEntryCount, ToolOutputTruncator.k_maximumOutputCharacterCount,
                out int keptEntryCount);

            var outputBuilder = new StringBuilder();
            outputBuilder.Append($"{displayPath} contains {collectedEntries.Count} entries, up to {k_maximumWalkDepth} levels deep:\n");
            outputBuilder.Append(entryListingText);
            outputBuilder.Append(BuildSkippedDirectoriesNote(skippedDirectoryNames));

            string truncationNote = ToolOutputTruncator.BuildTruncationNote(keptEntryCount, collectedEntries.Count, "entries",
                "Call list_dir on one of the folders listed above to see what is inside it.");

            if (truncationNote.Length > 0)
            {
                outputBuilder.Append('\n');
                outputBuilder.Append(truncationNote);
            }

            return ToolResult.Success(outputBuilder.ToString());
        }

        static string RenderEntryLine(DirectoryEntryInfo directoryEntry)
        {
            return directoryEntry.IsDirectory
                ? $"{directoryEntry.DisplayPath}/"
                : $"{directoryEntry.DisplayPath} ({directoryEntry.SizeInBytes} bytes)";
        }

        static string BuildSkippedDirectoriesNote(List<string> skippedDirectoryNames)
        {
            if (skippedDirectoryNames.Count == 0)
            {
                return string.Empty;
            }

            return $"\n[skipped] these folders are closed to the agent and were not opened: {string.Join(", ", skippedDirectoryNames)}";
        }

        readonly struct PendingDirectory
        {
            public string AbsolutePath { get; }
            public int Depth { get; }

            public PendingDirectory(string absolutePath, int depth)
            {
                AbsolutePath = absolutePath;
                Depth = depth;
            }
        }

        readonly struct DirectoryEntryInfo
        {
            public string DisplayPath { get; }
            public bool IsDirectory { get; }
            public long SizeInBytes { get; }

            public DirectoryEntryInfo(string displayPath, bool isDirectory, long sizeInBytes)
            {
                DisplayPath = displayPath;
                IsDirectory = isDirectory;
                SizeInBytes = sizeInBytes;
            }
        }
    }
}
