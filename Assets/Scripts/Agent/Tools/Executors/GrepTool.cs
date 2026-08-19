using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using Cysharp.Threading.Tasks;

namespace Amberline.Agent
{
    // grep(pattern, path) - a LITERAL, case-insensitive substring search. Deliberately not a
    // regular expression, for two independent reasons:
    //
    // 1. A regex has to be escaped twice before it reaches us - once for regex syntax and once for
    //    JSON - and that double escaping is precisely where a small model falls apart. A literal
    //    pattern is a piece of text the model already has in front of it.
    // 2. A pattern that comes from a model is untrusted input, and an untrusted regex is a
    //    catastrophic-backtracking hazard: one nested quantifier can hang the search for minutes
    //    inside the Unity player loop.
    //
    // The rest of this class is about not drowning. Hits are grouped per file so twelve matches in
    // one file read as one block instead of twelve paths; the binary-extension skip list and the
    // per-file size cap are carried over from the previous agent because both earned their place;
    // and every file is read inside its own try/catch, so one locked or unreadable file skips
    // itself instead of failing the whole search.
    //
    // Matches keep being COUNTED after the display cap is reached. Telling the model there were 214
    // matches and it is seeing 30 is what makes it narrow the pattern; telling it there were 30
    // makes it believe it has the full picture.
    public class GrepTool : IToolExecutor
    {
        readonly PathSandbox _pathSandbox;

        static readonly ToolDefinition k_toolDefinition = new ToolDefinition(
            name: "grep",
            parameterNames: new[] { "pattern", "path" },
            isMutating: false,
            isCommand: false,
            maximumResponseTokens: 512);

        // Carried over from the previous agent, including the Unity-specific entries: .asset,
        // .unity and .prefab are YAML, so they are technically text, but they are machine-written
        // text full of GUIDs that no coding task ever needs to grep through.
        static readonly HashSet<string> k_binaryFileExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            ".dll", ".exe", ".so", ".dylib", ".pdb", ".png", ".jpg", ".jpeg", ".gif", ".bmp", ".tga", ".psd",
            ".mp3", ".wav", ".ogg", ".mp4", ".mov", ".zip", ".gz", ".rar", ".7z", ".bin", ".pdf",
            ".asset", ".unity", ".prefab", ".meta", ".ttf", ".otf", ".fbx", ".obj", ".blend", ".gguf"
        };

        const long k_maximumFileSizeToSearchInBytes = 1024L * 1024L;
        const int k_maximumFilesToScan = 20000;
        const int k_lineNumberColumnWidth = 6;

        public GrepTool(PathSandbox pathSandbox)
        {
            _pathSandbox = pathSandbox;
        }

        public ToolDefinition Definition => k_toolDefinition;

        public UniTask<ToolResult> ExecuteAsync(ToolCall toolCall, CancellationToken cancellationToken)
        {
            // The search runs on the thread pool, and every loop inside it checks the token, so
            // pressing Escape stops a walk over a large tree instead of waiting it out.
            return UniTask.RunOnThreadPool(() => SearchForLiteralPattern(toolCall, cancellationToken), true, cancellationToken);
        }

        ToolResult SearchForLiteralPattern(ToolCall toolCall, CancellationToken cancellationToken)
        {
            string searchPattern = toolCall.GetArgument("pattern");

            if (string.IsNullOrEmpty(searchPattern))
            {
                return ToolResult.Failure("grep needs a pattern. Call it again with a piece of text to look for, for example pattern class Player and path .");
            }

            // A missing path means the project root, which is the only thing it can mean here.
            string suppliedPath = toolCall.GetArgument("path");

            if (!_pathSandbox.TryResolvePath(suppliedPath, out string absoluteSearchPath, out string rejectionReason))
            {
                return ToolResult.Failure($"grep: {rejectionReason}");
            }

            string displayPath = _pathSandbox.GetDisplayPath(absoluteSearchPath);
            var searchState = new GrepSearchState();

            if (File.Exists(absoluteSearchPath))
            {
                SearchOneFile(absoluteSearchPath, searchPattern, searchState, cancellationToken);
            }
            else if (Directory.Exists(absoluteSearchPath))
            {
                WalkAndSearchEveryFile(absoluteSearchPath, searchPattern, searchState, cancellationToken);
            }
            else
            {
                return ToolResult.Failure($"grep: no such file or folder: {displayPath}. Call list_dir with path . to see the project root.");
            }

            return BuildSearchOutput(searchPattern, displayPath, searchState);
        }

        // The same hand-written stack as list_dir, for the same reason: it is the only shape that
        // can refuse to descend into Library/ before paying for its contents. Unlike list_dir this
        // one has no depth limit - a search is expected to reach the whole subtree.
        void WalkAndSearchEveryFile(string rootAbsolutePath, string searchPattern, GrepSearchState searchState,
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
                SearchOneDirectory(currentDirectoryPath, searchPattern, searchState, directoriesToVisit, cancellationToken);
            }
        }

        void SearchOneDirectory(string currentDirectoryPath, string searchPattern, GrepSearchState searchState,
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
                SearchOneFile(filePath, searchPattern, searchState, cancellationToken);
            }
        }

        void SearchOneFile(string filePath, string searchPattern, GrepSearchState searchState, CancellationToken cancellationToken)
        {
            if (ShouldSkipFileForSearch(filePath))
            {
                return;
            }

            searchState.ScannedFileCount++;
            string displayPath = _pathSandbox.GetDisplayPath(filePath);
            FileHitGroup hitGroupForThisFile = null;
            bool fileHasAnyMatch = false;

            try
            {
                int lineNumber = 0;

                foreach (string currentLine in File.ReadLines(filePath))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    lineNumber++;

                    // A NUL byte means the extension list missed a binary file. Abandon it quietly.
                    if (currentLine.IndexOf('\0') >= 0)
                    {
                        return;
                    }

                    if (currentLine.IndexOf(searchPattern, StringComparison.OrdinalIgnoreCase) < 0)
                    {
                        continue;
                    }

                    fileHasAnyMatch = true;
                    searchState.TotalMatchCount++;

                    if (searchState.ShownHitCount >= ToolOutputTruncator.k_maximumGrepHitCount)
                    {
                        continue;
                    }

                    if (hitGroupForThisFile == null)
                    {
                        hitGroupForThisFile = new FileHitGroup(displayPath);
                        searchState.HitGroups.Add(hitGroupForThisFile);
                    }

                    hitGroupForThisFile.HitLines.Add(FormatHitLine(lineNumber, currentLine));
                    searchState.ShownHitCount++;
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception)
            {
                // Locked, deleted mid-walk or unreadable: skip this file, keep the search alive.
            }

            if (fileHasAnyMatch)
            {
                searchState.MatchedFileCount++;
            }
        }

        bool ShouldSkipFileForSearch(string filePath)
        {
            string fileName = Path.GetFileName(filePath);

            if (_pathSandbox.IsDeniedFileOrDirectoryName(fileName))
            {
                return true;
            }

            if (k_binaryFileExtensions.Contains(Path.GetExtension(filePath)))
            {
                return true;
            }

            try
            {
                var fileInformation = new FileInfo(filePath);

                if ((fileInformation.Attributes & FileAttributes.ReparsePoint) != 0)
                {
                    return true;
                }

                return fileInformation.Length > k_maximumFileSizeToSearchInBytes;
            }
            catch (Exception)
            {
                return true;
            }
        }

        static string FormatHitLine(int lineNumber, string lineText)
        {
            string lineNumberColumn = lineNumber.ToString(CultureInfo.InvariantCulture).PadLeft(k_lineNumberColumnWidth);
            string shortenedLineText = ToolOutputTruncator.ShortenSingleLine(lineText.TrimStart(),
                ToolOutputTruncator.k_maximumSingleLineCharacterCount);

            return $"{lineNumberColumn}: {shortenedLineText}";
        }

        static ToolResult BuildSearchOutput(string searchPattern, string displayPath, GrepSearchState searchState)
        {
            if (searchState.TotalMatchCount == 0)
            {
                return ToolResult.Success(BuildNoMatchesText(searchPattern, displayPath));
            }

            searchState.HitGroups.Sort((firstGroup, secondGroup) =>
                string.Compare(firstGroup.DisplayPath, secondGroup.DisplayPath, StringComparison.OrdinalIgnoreCase));

            var renderedLines = new List<string>();

            foreach (FileHitGroup hitGroup in searchState.HitGroups)
            {
                renderedLines.Add(hitGroup.DisplayPath);
                renderedLines.AddRange(hitGroup.HitLines);
            }

            string groupedHitsText = ToolOutputTruncator.JoinFirstLinesWithinBudget(renderedLines, int.MaxValue,
                ToolOutputTruncator.k_maximumOutputCharacterCount, out int keptLineCount);

            string matchCountText = DescribeCount(searchState.TotalMatchCount, "match", "matches");
            string fileCountText = DescribeCount(searchState.MatchedFileCount, "file", "files");

            var outputBuilder = new StringBuilder();
            outputBuilder.Append($"{matchCountText} for {searchPattern} in {fileCountText} under {displayPath}:\n");
            outputBuilder.Append(groupedHitsText);

            if (keptLineCount < renderedLines.Count)
            {
                outputBuilder.Append("\n[truncated] the output was cut to keep it short.");
            }

            string truncationNote = ToolOutputTruncator.BuildTruncationNote(searchState.ShownHitCount, searchState.TotalMatchCount,
                "matches", "Use a longer pattern, or pass a subfolder as path, to narrow the search.");

            if (truncationNote.Length > 0)
            {
                outputBuilder.Append('\n');
                outputBuilder.Append(truncationNote);
            }

            if (searchState.DidStopAtFileLimit)
            {
                outputBuilder.Append($"\n[stopped] the search stopped after {k_maximumFilesToScan} files. Pass a subfolder as path to search a smaller part of the project.");
            }

            return ToolResult.Success(outputBuilder.ToString());
        }

        static string BuildNoMatchesText(string searchPattern, string displayPath)
        {
            var noMatchesBuilder = new StringBuilder();
            noMatchesBuilder.Append($"No matches. grep searched {displayPath} for the literal text {searchPattern}, case-insensitively and not as a regular expression. Try a shorter piece of the exact text, or another folder.");

            // A multi-line pattern can never match, because the search runs one line at a time.
            if (searchPattern.IndexOf('\n') >= 0)
            {
                noMatchesBuilder.Append(" The pattern spans several lines, and grep compares one line at a time, so search for a single line of it.");
            }

            return noMatchesBuilder.ToString();
        }

        static string DescribeCount(int count, string singularNoun, string pluralNoun)
        {
            return count == 1 ? $"{count} {singularNoun}" : $"{count} {pluralNoun}";
        }

        class GrepSearchState
        {
            public List<FileHitGroup> HitGroups { get; } = new List<FileHitGroup>();
            public int TotalMatchCount { get; set; }
            public int ShownHitCount { get; set; }
            public int MatchedFileCount { get; set; }
            public int ScannedFileCount { get; set; }
            public bool DidStopAtFileLimit { get; set; }
        }

        class FileHitGroup
        {
            public string DisplayPath { get; }
            public List<string> HitLines { get; } = new List<string>();

            public FileHitGroup(string displayPath)
            {
                DisplayPath = displayPath;
            }
        }
    }
}
