using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Threading;
using Cysharp.Threading.Tasks;

namespace Amberline.Agent
{
    // edit_file(path, find, replace) - one exact place in one file.
    //
    // The whole tool exists to enforce a single rule: THE FIND TEXT MUST MATCH EXACTLY ONE PLACE.
    // Silently editing the first of several matches is the failure mode that makes an agent
    // untrustworthy, because the user approves a diff of one hunk and the model believes it changed
    // the one it meant. A small model reaches for a one-line anchor - "return;" or a closing brace -
    // by default, so refusing ambiguity is not an edge case, it is the common path.
    //
    // Matching runs in three stages, and the FIRST stage that finds anything decides. Each one
    // forgives a different thing the model gets wrong, in order of how much it forgives:
    //
    //   1. As written. The model copied the text out of a read_file result and it matches.
    //   2. Line endings normalised in the find text. The model wrote \r\n where the file has none,
    //      or the other way round. Costs nothing to forgive and is invisible to the reader.
    //   3. Indentation tolerant. The model dropped or guessed the leading whitespace - by far the
    //      most common near miss, since indentation is exactly what gets lost when a model
    //      reconstructs a line from memory instead of copying it. The replacement is re-indented to
    //      match the file, so forgiving the input does not corrupt the output.
    //
    // When nothing matches, the message names the CLOSEST line in the file with its line number.
    // "Not found" alone gets answered with a slightly different guess; a line number gets answered
    // with a read_file of that line.
    public class EditFileTool : IToolExecutor, IFileChangePreviewProvider
    {
        readonly PathSandbox _pathSandbox;
        readonly FileWriteService _fileWriteService;

        static readonly ToolDefinition k_toolDefinition = new ToolDefinition(
            name: "edit_file",
            parameterNames: new[] { "path", "find", "replace" },
            isMutating: true,
            isCommand: false,
            maximumResponseTokens: 2048);

        const int k_diffContextLineCount = 3;
        const int k_maximumReportedMatchCount = 4;
        const int k_minimumScoreToCallALineANearMiss = 4;
        const int k_maximumQuotedLineLength = 120;

        /// <summary>Which stage of the match ladder produced an answer, if any.</summary>
        enum FindStageOutcome
        {
            /// <summary>This stage found nothing - try the next one.</summary>
            NotFound,

            /// <summary>Exactly one occurrence, and the new text is built.</summary>
            Applied,

            /// <summary>More than one occurrence, so the edit is refused with an explanation.</summary>
            Ambiguous
        }

        public EditFileTool(PathSandbox pathSandbox, FileWriteService fileWriteService)
        {
            _pathSandbox = pathSandbox;
            _fileWriteService = fileWriteService;
        }

        public ToolDefinition Definition => k_toolDefinition;

        public UniTask<FileChangePreview> BuildFileChangePreviewAsync(ToolCall toolCall, CancellationToken cancellationToken)
        {
            return UniTask.RunOnThreadPool(() => PlanSingleEdit(toolCall, cancellationToken), true, cancellationToken);
        }

        public UniTask<ToolResult> ExecuteAsync(ToolCall toolCall, CancellationToken cancellationToken)
        {
            // File IO runs on the thread pool so reading and matching a large file cannot stall the
            // player loop and freeze the terminal mid-turn.
            return UniTask.RunOnThreadPool(() => EditFileFromCall(toolCall, cancellationToken), true, cancellationToken);
        }

        ToolResult EditFileFromCall(ToolCall toolCall, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();

            // Planned again here, on purpose: the file may have changed between the approval card
            // and this moment, and an anchor that was unique then may not be unique now.
            var plannedChange = PlanSingleEdit(toolCall, cancellationToken);

            if (!plannedChange.IsAvailable)
            {
                return ToolResult.Failure(plannedChange.FailureMessage);
            }

            var writeOutcome = _fileWriteService.WriteWholeFile(toolCall.GetArgument("path"), plannedChange.NewText,
                plannedChange.LineEnding, plannedChange.HasByteOrderMark, cancellationToken);

            if (!writeOutcome.IsSuccess)
            {
                return ToolResult.Failure($"edit_file: {writeOutcome.FailureMessage}");
            }

            return ToolResult.Success($"edited {writeOutcome.DisplayPath} ({plannedChange.Diff.DescribeChangeCounts()} lines).");
        }

        // Works out exactly what would be written, and why it could not be. Called once to build
        // the approval card and once again to do the write.
        FileChangePreview PlanSingleEdit(ToolCall toolCall, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();

            string suppliedPath = toolCall.GetArgument("path");
            string findText = toolCall.GetArgument("find");
            string replaceText = toolCall.GetArgument("replace");

            var argumentFailure = FindFailureInArguments(suppliedPath, findText, replaceText);

            if (argumentFailure != null)
            {
                return argumentFailure;
            }

            if (!_pathSandbox.TryResolvePath(suppliedPath, out string absoluteFilePath, out string rejectionReason))
            {
                return FileChangePreview.Unavailable(k_toolDefinition.Name, $"edit_file: {rejectionReason}");
            }

            string displayPath = _pathSandbox.GetDisplayPath(absoluteFilePath);

            if (!TryReadFileBeingEdited(absoluteFilePath, displayPath, out FileTextInfo fileText, out string readFailure))
            {
                return FileChangePreview.Unavailable(k_toolDefinition.Name, readFailure);
            }

            if (!TryApplyEditToText(fileText, findText, replaceText, displayPath, cancellationToken,
                    out string newText, out string editFailure))
            {
                return FileChangePreview.Unavailable(k_toolDefinition.Name, editFailure);
            }

            var diff = LineDiff.Compare(fileText.Text, newText, k_diffContextLineCount, cancellationToken);

            if (!diff.HasChanges)
            {
                return FileChangePreview.Unavailable(k_toolDefinition.Name,
                    $"edit_file: applying that change to {displayPath} would leave it exactly as it is, so nothing was written. Move on to the next step, or call finish.");
            }

            return FileChangePreview.ForChange(k_toolDefinition.Name, displayPath, false, diff, newText,
                fileText.DominantLineEnding, fileText.HasByteOrderMark);
        }

        // Returns null when the arguments are usable. Every message names the next call to make,
        // because a bare complaint tends to be answered with prose instead of a corrected call.
        static FileChangePreview FindFailureInArguments(string suppliedPath, string findText, string replaceText)
        {
            if (string.IsNullOrWhiteSpace(suppliedPath))
            {
                return FileChangePreview.Unavailable(k_toolDefinition.Name,
                    "edit_file needs a path. Call it again as edit_file with arguments path, find and replace.");
            }

            if (string.IsNullOrEmpty(findText))
            {
                return FileChangePreview.Unavailable(k_toolDefinition.Name,
                    "edit_file needs a find text. Put the exact lines you want to change in find, and what they should become in replace.");
            }

            if (string.IsNullOrWhiteSpace(findText))
            {
                // Whitespace matches thousands of places in any source file, so it can never be the
                // unique anchor this tool requires.
                return FileChangePreview.Unavailable(k_toolDefinition.Name,
                    "edit_file: find is only whitespace, which matches everywhere. Put the exact lines you want to change in find.");
            }

            if (string.Equals(findText, replaceText, StringComparison.Ordinal))
            {
                return FileChangePreview.Unavailable(k_toolDefinition.Name,
                    "edit_file: find and replace are the same text, so there is nothing to change. Put the NEW text in replace.");
            }

            return null;
        }

        static bool TryReadFileBeingEdited(string absoluteFilePath, string displayPath,
            out FileTextInfo fileText, out string failureReason)
        {
            fileText = null;
            failureReason = null;

            if (Directory.Exists(absoluteFilePath))
            {
                failureReason = $"edit_file: {displayPath} is a folder, not a file. Call list_dir on it to see what is inside.";
                return false;
            }

            if (!File.Exists(absoluteFilePath))
            {
                failureReason = $"edit_file: no such file: {displayPath}. Use write_file to create it, or grep for a name you know to find the correct path.";
                return false;
            }

            if (!FileText.TryReadTextFile(absoluteFilePath, out fileText, out string readRejectionReason))
            {
                failureReason = $"edit_file: {displayPath} cannot be edited because {readRejectionReason}";
                return false;
            }

            return true;
        }

        // The three-stage ladder. The first stage that finds anything decides - including when what
        // it finds is several matches, which is refused rather than passed down to a looser stage
        // that would find even more.
        bool TryApplyEditToText(FileTextInfo fileText, string findText, string replaceText, string displayPath,
            CancellationToken cancellationToken, out string newText, out string failureMessage)
        {
            string normalizedFindText = FileText.NormalizeLineEndingsToLineFeed(findText);
            string normalizedReplaceText = FileText.NormalizeLineEndingsToLineFeed(replaceText);

            var stageOutcome = TryApplyLiteralFind(fileText.Text, findText, normalizedReplaceText, displayPath,
                cancellationToken, out newText, out failureMessage);

            if (stageOutcome != FindStageOutcome.NotFound)
            {
                return stageOutcome == FindStageOutcome.Applied;
            }

            if (!string.Equals(normalizedFindText, findText, StringComparison.Ordinal))
            {
                stageOutcome = TryApplyLiteralFind(fileText.Text, normalizedFindText, normalizedReplaceText, displayPath,
                    cancellationToken, out newText, out failureMessage);

                if (stageOutcome != FindStageOutcome.NotFound)
                {
                    return stageOutcome == FindStageOutcome.Applied;
                }
            }

            stageOutcome = TryApplyIndentationTolerantFind(fileText, normalizedFindText, normalizedReplaceText, displayPath,
                cancellationToken, out newText, out failureMessage);

            if (stageOutcome != FindStageOutcome.NotFound)
            {
                return stageOutcome == FindStageOutcome.Applied;
            }

            failureMessage = BuildMessageForFindThatMatchedNothing(fileText, normalizedFindText, displayPath, cancellationToken);
            return false;
        }

        FindStageOutcome TryApplyLiteralFind(string fileTextWithLineFeeds, string findText, string replaceText,
            string displayPath, CancellationToken cancellationToken, out string newText, out string failureMessage)
        {
            newText = null;
            failureMessage = null;

            var occurrenceIndices = FindAllOccurrenceIndices(fileTextWithLineFeeds, findText, cancellationToken);

            if (occurrenceIndices.Count == 0)
            {
                return FindStageOutcome.NotFound;
            }

            if (occurrenceIndices.Count > 1)
            {
                var matchedLineNumbers = new List<int>();

                foreach (int occurrenceIndex in occurrenceIndices)
                {
                    matchedLineNumbers.Add(CountLineNumberAtCharacterIndex(fileTextWithLineFeeds, occurrenceIndex));
                }

                failureMessage = BuildMessageForFindThatMatchedTooOften(occurrenceIndices.Count, matchedLineNumbers, displayPath);
                return FindStageOutcome.Ambiguous;
            }

            int onlyOccurrenceIndex = occurrenceIndices[0];
            newText = fileTextWithLineFeeds.Substring(0, onlyOccurrenceIndex)
                + replaceText
                + fileTextWithLineFeeds.Substring(onlyOccurrenceIndex + findText.Length);

            return FindStageOutcome.Applied;
        }

        static List<int> FindAllOccurrenceIndices(string fileTextWithLineFeeds, string findText, CancellationToken cancellationToken)
        {
            var occurrenceIndices = new List<int>();

            if (string.IsNullOrEmpty(findText))
            {
                return occurrenceIndices;
            }

            int searchStartIndex = 0;

            while (searchStartIndex <= fileTextWithLineFeeds.Length - findText.Length)
            {
                cancellationToken.ThrowIfCancellationRequested();

                int occurrenceIndex = fileTextWithLineFeeds.IndexOf(findText, searchStartIndex, StringComparison.Ordinal);

                if (occurrenceIndex < 0)
                {
                    break;
                }

                occurrenceIndices.Add(occurrenceIndex);
                searchStartIndex = occurrenceIndex + findText.Length;
            }

            return occurrenceIndices;
        }

        static int CountLineNumberAtCharacterIndex(string fileTextWithLineFeeds, int characterIndex)
        {
            int lineNumber = 1;
            int lastIndexToCount = Math.Min(characterIndex, fileTextWithLineFeeds.Length);

            for (int scanIndex = 0; scanIndex < lastIndexToCount; scanIndex++)
            {
                if (fileTextWithLineFeeds[scanIndex] == '\n')
                {
                    lineNumber++;
                }
            }

            return lineNumber;
        }

        // The stage that forgives lost indentation. Lines are compared with both ends trimmed, and
        // the replacement is then re-indented to whatever the file really uses, so a model that
        // wrote its find text flush left does not flatten the block it edits.
        FindStageOutcome TryApplyIndentationTolerantFind(FileTextInfo fileText, string normalizedFindText,
            string normalizedReplaceText, string displayPath, CancellationToken cancellationToken,
            out string newText, out string failureMessage)
        {
            newText = null;
            failureMessage = null;

            string[] fileLines = FileText.SplitIntoLines(fileText.Text);
            string[] findLines = FileText.SplitIntoLines(normalizedFindText);

            if (findLines.Length == 0 || findLines.Length > fileLines.Length)
            {
                return FindStageOutcome.NotFound;
            }

            var matchStartLineIndices = FindIndentationTolerantMatchStartLines(fileLines, findLines, cancellationToken);

            if (matchStartLineIndices.Count == 0)
            {
                return FindStageOutcome.NotFound;
            }

            if (matchStartLineIndices.Count > 1)
            {
                var matchedLineNumbers = new List<int>();

                foreach (int matchStartLineIndex in matchStartLineIndices)
                {
                    matchedLineNumbers.Add(matchStartLineIndex + 1);
                }

                failureMessage = BuildMessageForFindThatMatchedTooOften(matchStartLineIndices.Count, matchedLineNumbers, displayPath);
                return FindStageOutcome.Ambiguous;
            }

            int onlyMatchStartLineIndex = matchStartLineIndices[0];
            string[] replaceLines = FileText.SplitIntoLines(normalizedReplaceText);
            string[] reindentedReplaceLines = ReindentReplacementLines(replaceLines,
                ReadLeadingWhitespace(fileLines[onlyMatchStartLineIndex]), ReadLeadingWhitespace(findLines[0]));

            var newFileLines = new List<string>(fileLines.Length - findLines.Length + reindentedReplaceLines.Length);

            for (int lineIndex = 0; lineIndex < onlyMatchStartLineIndex; lineIndex++)
            {
                newFileLines.Add(fileLines[lineIndex]);
            }

            newFileLines.AddRange(reindentedReplaceLines);

            for (int lineIndex = onlyMatchStartLineIndex + findLines.Length; lineIndex < fileLines.Length; lineIndex++)
            {
                newFileLines.Add(fileLines[lineIndex]);
            }

            newText = FileText.JoinLines(newFileLines, fileText.EndsWithLineBreak);
            return FindStageOutcome.Applied;
        }

        static List<int> FindIndentationTolerantMatchStartLines(string[] fileLines, string[] findLines, CancellationToken cancellationToken)
        {
            var matchStartLineIndices = new List<int>();
            int lastPossibleStartLineIndex = fileLines.Length - findLines.Length;
            int startLineIndex = 0;

            while (startLineIndex <= lastPossibleStartLineIndex)
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (DoLinesMatchIgnoringSurroundingWhitespace(fileLines, findLines, startLineIndex))
                {
                    matchStartLineIndices.Add(startLineIndex);
                    startLineIndex += findLines.Length;
                    continue;
                }

                startLineIndex++;
            }

            return matchStartLineIndices;
        }

        static bool DoLinesMatchIgnoringSurroundingWhitespace(string[] fileLines, string[] findLines, int startLineIndex)
        {
            for (int findLineIndex = 0; findLineIndex < findLines.Length; findLineIndex++)
            {
                string fileLine = fileLines[startLineIndex + findLineIndex].Trim();
                string findLine = findLines[findLineIndex].Trim();

                if (!string.Equals(fileLine, findLine, StringComparison.Ordinal))
                {
                    return false;
                }
            }

            return true;
        }

        static string ReadLeadingWhitespace(string lineText)
        {
            int firstNonWhitespaceIndex = 0;

            while (firstNonWhitespaceIndex < lineText.Length && char.IsWhiteSpace(lineText[firstNonWhitespaceIndex]))
            {
                firstNonWhitespaceIndex++;
            }

            return lineText.Substring(0, firstNonWhitespaceIndex);
        }

        // Shifts the replacement by however much indentation the model left out, or put in too much
        // of. Anything stranger than that is left exactly as written, because guessing further would
        // silently reformat code the user is about to approve.
        static string[] ReindentReplacementLines(string[] replaceLines, string fileIndentation, string findIndentation)
        {
            if (replaceLines.Length == 0)
            {
                return replaceLines;
            }

            if (fileIndentation.StartsWith(findIndentation, StringComparison.Ordinal))
            {
                string missingIndentation = fileIndentation.Substring(findIndentation.Length);
                return missingIndentation.Length == 0
                    ? replaceLines
                    : AddIndentationToEveryNonEmptyLine(replaceLines, missingIndentation);
            }

            if (findIndentation.StartsWith(fileIndentation, StringComparison.Ordinal))
            {
                string surplusIndentation = findIndentation.Substring(fileIndentation.Length);
                return RemoveIndentationFromEveryLineThatHasIt(replaceLines, surplusIndentation);
            }

            return replaceLines;
        }

        static string[] AddIndentationToEveryNonEmptyLine(string[] replaceLines, string missingIndentation)
        {
            var reindentedLines = new string[replaceLines.Length];

            for (int lineIndex = 0; lineIndex < replaceLines.Length; lineIndex++)
            {
                reindentedLines[lineIndex] = replaceLines[lineIndex].Length == 0
                    ? replaceLines[lineIndex]
                    : missingIndentation + replaceLines[lineIndex];
            }

            return reindentedLines;
        }

        static string[] RemoveIndentationFromEveryLineThatHasIt(string[] replaceLines, string surplusIndentation)
        {
            var reindentedLines = new string[replaceLines.Length];

            for (int lineIndex = 0; lineIndex < replaceLines.Length; lineIndex++)
            {
                reindentedLines[lineIndex] = replaceLines[lineIndex].StartsWith(surplusIndentation, StringComparison.Ordinal)
                    ? replaceLines[lineIndex].Substring(surplusIndentation.Length)
                    : replaceLines[lineIndex];
            }

            return reindentedLines;
        }

        static string BuildMessageForFindThatMatchedTooOften(int matchCount, List<int> matchedLineNumbers, string displayPath)
        {
            var reportedLineNumbers = new List<string>();

            for (int lineNumberIndex = 0; lineNumberIndex < matchedLineNumbers.Count && lineNumberIndex < k_maximumReportedMatchCount; lineNumberIndex++)
            {
                reportedLineNumbers.Add(matchedLineNumbers[lineNumberIndex].ToString(CultureInfo.InvariantCulture));
            }

            string lineNumberList = string.Join(", ", reportedLineNumbers);

            if (matchedLineNumbers.Count > k_maximumReportedMatchCount)
            {
                lineNumberList += " and more";
            }

            return $"edit_file: the find text matches {matchCount} places in {displayPath} (lines {lineNumberList}), so it is not clear which one to change and nothing was written. " +
                "Call edit_file again with more of the surrounding lines in find, so it matches exactly one place.";
        }

        // Naming the closest line turns a dead end into a next move: the model reads that line and
        // copies the real text, instead of guessing at a slightly different anchor.
        static string BuildMessageForFindThatMatchedNothing(FileTextInfo fileText, string normalizedFindText,
            string displayPath, CancellationToken cancellationToken)
        {
            string[] fileLines = FileText.SplitIntoLines(fileText.Text);
            string[] findLines = FileText.SplitIntoLines(normalizedFindText);
            string firstFindLine = findLines.Length > 0 ? findLines[0].Trim() : string.Empty;

            if (TryFindClosestLine(fileLines, firstFindLine, cancellationToken, out int closestLineNumber, out string closestLineText))
            {
                string quotedClosestLine = ToolOutputTruncator.ShortenSingleLine(closestLineText.Trim(), k_maximumQuotedLineLength);

                return $"edit_file: the find text does not appear in {displayPath}, so nothing was written. The closest line is line {closestLineNumber}: {quotedClosestLine} " +
                    "Call read_file around that line, copy the exact text including its indentation, and include enough surrounding lines to make it unique.";
            }

            return $"edit_file: the find text does not appear in {displayPath}, so nothing was written. " +
                "Call read_file on it, copy the exact lines you want to change, and call edit_file again with them.";
        }

        static bool TryFindClosestLine(string[] fileLines, string firstFindLine, CancellationToken cancellationToken,
            out int closestLineNumber, out string closestLineText)
        {
            closestLineNumber = 0;
            closestLineText = string.Empty;

            if (firstFindLine.Length == 0)
            {
                return false;
            }

            int bestScore = 0;

            for (int lineIndex = 0; lineIndex < fileLines.Length; lineIndex++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                int score = ScoreHowCloseTwoLinesAre(fileLines[lineIndex].Trim(), firstFindLine);

                if (score <= bestScore)
                {
                    continue;
                }

                bestScore = score;
                closestLineNumber = lineIndex + 1;
                closestLineText = fileLines[lineIndex];
            }

            return bestScore >= k_minimumScoreToCallALineANearMiss;
        }

        // Cheap on purpose. A real edit distance over every line of a large file costs more than the
        // hint is worth, and containment plus a shared opening is what a near miss actually looks
        // like: the model got the statement right and the indentation or the tail wrong.
        static int ScoreHowCloseTwoLinesAre(string fileLine, string findLine)
        {
            if (fileLine.Length == 0 || findLine.Length == 0)
            {
                return 0;
            }

            if (fileLine.Contains(findLine) || findLine.Contains(fileLine))
            {
                return Math.Min(fileLine.Length, findLine.Length) * 2;
            }

            int commonPrefixLength = 0;
            int shortestLength = Math.Min(fileLine.Length, findLine.Length);

            while (commonPrefixLength < shortestLength && fileLine[commonPrefixLength] == findLine[commonPrefixLength])
            {
                commonPrefixLength++;
            }

            return commonPrefixLength;
        }
    }
}
