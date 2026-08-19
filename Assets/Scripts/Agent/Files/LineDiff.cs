using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Threading;

namespace Amberline.Agent
{
    /// <summary>What one rendered diff line is. The view picks its colour from this and nothing else.</summary>
    public enum DiffLineKind
    {
        /// <summary>Present unchanged in both texts.</summary>
        Context,

        /// <summary>Only in the new text - phosphor green.</summary>
        Added,

        /// <summary>Only in the old text - red.</summary>
        Removed,

        /// <summary>A run of unchanged lines that was folded away. <see cref="DiffLine.Text"/>
        /// already reads as a note, so it can be printed as-is in a dim style.</summary>
        Skipped
    }

    /// <summary>
    /// One line of a rendered diff. Both line numbers are carried because a view shows the old and
    /// the new gutter side by side; the number is 0 when the line does not exist on that side.
    /// </summary>
    public class DiffLine
    {
        public DiffLineKind Kind { get; }
        public string Text { get; }

        /// <summary>1-based line number in the OLD text, or 0 when the line is only in the new one.</summary>
        public int OldLineNumber { get; }

        /// <summary>1-based line number in the NEW text, or 0 when the line is only in the old one.</summary>
        public int NewLineNumber { get; }

        public DiffLine(DiffLineKind kind, string text, int oldLineNumber, int newLineNumber)
        {
            Kind = kind;
            Text = text ?? string.Empty;
            OldLineNumber = oldLineNumber;
            NewLineNumber = newLineNumber;
        }
    }

    /// <summary>
    /// A whole diff, ready to render. The counts are taken while building, so the approval card can
    /// show "+3 -1" without walking the list.
    /// </summary>
    public class FileDiff
    {
        public IReadOnlyList<DiffLine> Lines { get; }
        public int AddedLineCount { get; }
        public int RemovedLineCount { get; }

        /// <summary>
        /// True when the changed region was too large to diff line by line and was rendered as one
        /// block removed and one block added. Worth showing in the card: the change is real, but
        /// the pairing of lines inside it is not.
        /// </summary>
        public bool WasRenderedAsWholeBlockReplace { get; }

        public FileDiff(IReadOnlyList<DiffLine> lines, int addedLineCount, int removedLineCount, bool wasRenderedAsWholeBlockReplace)
        {
            Lines = lines ?? new List<DiffLine>();
            AddedLineCount = addedLineCount;
            RemovedLineCount = removedLineCount;
            WasRenderedAsWholeBlockReplace = wasRenderedAsWholeBlockReplace;
        }

        public bool HasChanges => AddedLineCount > 0 || RemovedLineCount > 0;

        /// <summary>"+3 -1", for a card headline or a one-line tool result.</summary>
        public string DescribeChangeCounts()
        {
            return $"+{AddedLineCount.ToString(CultureInfo.InvariantCulture)} -{RemovedLineCount.ToString(CultureInfo.InvariantCulture)}";
        }

        /// <summary>
        /// The diff as plain text with the usual +/-/space gutter, capped at
        /// <paramref name="maximumLineCount"/> so a large change cannot flood a text field. Meant
        /// for the approval request's preview string and for logs, not for the coloured view.
        /// </summary>
        public string ToUnifiedText(int maximumLineCount)
        {
            var unifiedTextBuilder = new StringBuilder();
            int writtenLineCount = 0;

            foreach (var diffLine in Lines)
            {
                if (writtenLineCount >= maximumLineCount)
                {
                    unifiedTextBuilder.Append($"... {Lines.Count - writtenLineCount} more diff lines ...");
                    break;
                }

                if (writtenLineCount > 0)
                {
                    unifiedTextBuilder.Append('\n');
                }

                unifiedTextBuilder.Append(GetGutterCharacterFor(diffLine.Kind)).Append(diffLine.Text);
                writtenLineCount++;
            }

            return unifiedTextBuilder.ToString();
        }

        static string GetGutterCharacterFor(DiffLineKind diffLineKind)
        {
            switch (diffLineKind)
            {
                case DiffLineKind.Added:
                    return "+";
                case DiffLineKind.Removed:
                    return "-";
                case DiffLineKind.Skipped:
                    return "";
                default:
                    return " ";
            }
        }
    }

    // Line diff between two texts, shaped for the approval card.
    //
    // The order of the three steps is the whole design, and it exists because an LCS is quadratic:
    //
    // 1. TRIM the common prefix and suffix. A one-line edit in a 2000-line file leaves a window of
    //    one line against one line, and everything expensive below never runs at all. This is not
    //    an optimisation for the rare case - it is what happens on nearly every real edit.
    // 2. LCS over ONLY the changed window, so the matrix is sized by the change and not by the file.
    // 3. DEGRADE to a whole-block replace when either side of that window is still larger than the
    //    guard. Two thousand-line windows would be a million-cell matrix built on the main thread
    //    while the user waits, and the result would not even be worth reading: when a file is
    //    rewritten wholesale, "everything was removed and everything was added" is the honest
    //    rendering. This is the trap that freezes the UI, so it is closed by construction.
    //
    // Long runs of unchanged lines are then folded into a single Skipped note, because a card
    // showing 1900 identical lines around one change is a card nobody reads.
    public static class LineDiff
    {
        /// <summary>
        /// The largest changed window either side may have before the diff degrades to a whole
        /// block replace. 500x500 is a quarter of a million cells - a few milliseconds - and past
        /// that the pairing stops being informative anyway.
        /// </summary>
        public const int k_maximumChangedWindowLineCount = 500;

        /// <summary>Unchanged lines kept on each side of a change before the rest is folded away.</summary>
        public const int k_defaultContextLineCount = 3;

        /// <summary>Diffs two normalised texts with the default amount of context.</summary>
        public static FileDiff Compare(string oldTextWithLineFeeds, string newTextWithLineFeeds)
        {
            return Compare(oldTextWithLineFeeds, newTextWithLineFeeds, k_defaultContextLineCount, CancellationToken.None);
        }

        /// <summary>
        /// Diffs two normalised texts. Both are expected to use line feeds only - pass them through
        /// <see cref="FileText.NormalizeLineEndingsToLineFeed"/> first, or the ending itself shows
        /// up as a change on every line.
        /// </summary>
        public static FileDiff Compare(string oldTextWithLineFeeds, string newTextWithLineFeeds,
            int contextLineCount, CancellationToken cancellationToken)
        {
            string[] oldLines = FileText.SplitIntoLines(oldTextWithLineFeeds);
            string[] newLines = FileText.SplitIntoLines(newTextWithLineFeeds);

            int commonPrefixLineCount = CountCommonPrefixLines(oldLines, newLines, cancellationToken);
            int commonSuffixLineCount = CountCommonSuffixLines(oldLines, newLines, commonPrefixLineCount, cancellationToken);

            int changedOldLineCount = oldLines.Length - commonPrefixLineCount - commonSuffixLineCount;
            int changedNewLineCount = newLines.Length - commonPrefixLineCount - commonSuffixLineCount;

            var diffLines = new List<DiffLine>();
            AppendUnchangedPrefixLines(diffLines, oldLines, commonPrefixLineCount);

            bool wasRenderedAsWholeBlockReplace = IsChangedWindowTooLargeToDiff(changedOldLineCount, changedNewLineCount);

            if (wasRenderedAsWholeBlockReplace)
            {
                AppendWholeBlockReplace(diffLines, oldLines, newLines, commonPrefixLineCount, changedOldLineCount, changedNewLineCount, cancellationToken);
            }
            else
            {
                AppendLongestCommonSubsequenceDiff(diffLines, oldLines, newLines, commonPrefixLineCount,
                    changedOldLineCount, changedNewLineCount, cancellationToken);
            }

            AppendUnchangedSuffixLines(diffLines, oldLines, newLines, commonSuffixLineCount);

            var foldedDiffLines = FoldLongRunsOfUnchangedLines(diffLines, contextLineCount);

            CountAddedAndRemovedLines(diffLines, out int addedLineCount, out int removedLineCount);

            return new FileDiff(foldedDiffLines, addedLineCount, removedLineCount, wasRenderedAsWholeBlockReplace);
        }

        static int CountCommonPrefixLines(string[] oldLines, string[] newLines, CancellationToken cancellationToken)
        {
            int shortestLineCount = Math.Min(oldLines.Length, newLines.Length);
            int commonPrefixLineCount = 0;

            while (commonPrefixLineCount < shortestLineCount)
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (!string.Equals(oldLines[commonPrefixLineCount], newLines[commonPrefixLineCount], StringComparison.Ordinal))
                {
                    break;
                }

                commonPrefixLineCount++;
            }

            return commonPrefixLineCount;
        }

        static int CountCommonSuffixLines(string[] oldLines, string[] newLines, int commonPrefixLineCount, CancellationToken cancellationToken)
        {
            int remainingOldLineCount = oldLines.Length - commonPrefixLineCount;
            int remainingNewLineCount = newLines.Length - commonPrefixLineCount;
            int shortestRemainingLineCount = Math.Min(remainingOldLineCount, remainingNewLineCount);
            int commonSuffixLineCount = 0;

            while (commonSuffixLineCount < shortestRemainingLineCount)
            {
                cancellationToken.ThrowIfCancellationRequested();

                string oldLine = oldLines[oldLines.Length - 1 - commonSuffixLineCount];
                string newLine = newLines[newLines.Length - 1 - commonSuffixLineCount];

                if (!string.Equals(oldLine, newLine, StringComparison.Ordinal))
                {
                    break;
                }

                commonSuffixLineCount++;
            }

            return commonSuffixLineCount;
        }

        static void AppendUnchangedPrefixLines(List<DiffLine> diffLines, string[] oldLines, int commonPrefixLineCount)
        {
            for (int lineIndex = 0; lineIndex < commonPrefixLineCount; lineIndex++)
            {
                diffLines.Add(new DiffLine(DiffLineKind.Context, oldLines[lineIndex], lineIndex + 1, lineIndex + 1));
            }
        }

        static bool IsChangedWindowTooLargeToDiff(int changedOldLineCount, int changedNewLineCount)
        {
            return changedOldLineCount > k_maximumChangedWindowLineCount
                || changedNewLineCount > k_maximumChangedWindowLineCount;
        }

        static void AppendWholeBlockReplace(List<DiffLine> diffLines, string[] oldLines, string[] newLines,
            int commonPrefixLineCount, int changedOldLineCount, int changedNewLineCount, CancellationToken cancellationToken)
        {
            for (int windowIndex = 0; windowIndex < changedOldLineCount; windowIndex++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                int oldLineIndex = commonPrefixLineCount + windowIndex;
                diffLines.Add(new DiffLine(DiffLineKind.Removed, oldLines[oldLineIndex], oldLineIndex + 1, 0));
            }

            for (int windowIndex = 0; windowIndex < changedNewLineCount; windowIndex++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                int newLineIndex = commonPrefixLineCount + windowIndex;
                diffLines.Add(new DiffLine(DiffLineKind.Added, newLines[newLineIndex], 0, newLineIndex + 1));
            }
        }

        // The matrix holds the LCS length for every pair of suffixes of the two windows, so the
        // walk below can decide at each step whether dropping an old line or an new one keeps more
        // of the sequence. Built back to front, which is what lets the walk run front to back and
        // emit the lines in reading order.
        static void AppendLongestCommonSubsequenceDiff(List<DiffLine> diffLines, string[] oldLines, string[] newLines,
            int commonPrefixLineCount, int changedOldLineCount, int changedNewLineCount, CancellationToken cancellationToken)
        {
            if (changedOldLineCount == 0 && changedNewLineCount == 0)
            {
                return;
            }

            var commonSubsequenceLengths = BuildCommonSubsequenceLengths(oldLines, newLines, commonPrefixLineCount,
                changedOldLineCount, changedNewLineCount, cancellationToken);

            int oldWindowIndex = 0;
            int newWindowIndex = 0;

            while (oldWindowIndex < changedOldLineCount && newWindowIndex < changedNewLineCount)
            {
                cancellationToken.ThrowIfCancellationRequested();

                string oldLine = oldLines[commonPrefixLineCount + oldWindowIndex];
                string newLine = newLines[commonPrefixLineCount + newWindowIndex];

                if (string.Equals(oldLine, newLine, StringComparison.Ordinal))
                {
                    diffLines.Add(new DiffLine(DiffLineKind.Context, oldLine,
                        commonPrefixLineCount + oldWindowIndex + 1, commonPrefixLineCount + newWindowIndex + 1));
                    oldWindowIndex++;
                    newWindowIndex++;
                    continue;
                }

                bool droppingOldLineKeepsMore = commonSubsequenceLengths[oldWindowIndex + 1, newWindowIndex]
                    >= commonSubsequenceLengths[oldWindowIndex, newWindowIndex + 1];

                if (droppingOldLineKeepsMore)
                {
                    diffLines.Add(new DiffLine(DiffLineKind.Removed, oldLine, commonPrefixLineCount + oldWindowIndex + 1, 0));
                    oldWindowIndex++;
                }
                else
                {
                    diffLines.Add(new DiffLine(DiffLineKind.Added, newLine, 0, commonPrefixLineCount + newWindowIndex + 1));
                    newWindowIndex++;
                }
            }

            while (oldWindowIndex < changedOldLineCount)
            {
                cancellationToken.ThrowIfCancellationRequested();
                int oldLineIndex = commonPrefixLineCount + oldWindowIndex;
                diffLines.Add(new DiffLine(DiffLineKind.Removed, oldLines[oldLineIndex], oldLineIndex + 1, 0));
                oldWindowIndex++;
            }

            while (newWindowIndex < changedNewLineCount)
            {
                cancellationToken.ThrowIfCancellationRequested();
                int newLineIndex = commonPrefixLineCount + newWindowIndex;
                diffLines.Add(new DiffLine(DiffLineKind.Added, newLines[newLineIndex], 0, newLineIndex + 1));
                newWindowIndex++;
            }
        }

        static int[,] BuildCommonSubsequenceLengths(string[] oldLines, string[] newLines, int commonPrefixLineCount,
            int changedOldLineCount, int changedNewLineCount, CancellationToken cancellationToken)
        {
            var commonSubsequenceLengths = new int[changedOldLineCount + 1, changedNewLineCount + 1];

            for (int oldWindowIndex = changedOldLineCount - 1; oldWindowIndex >= 0; oldWindowIndex--)
            {
                cancellationToken.ThrowIfCancellationRequested();

                for (int newWindowIndex = changedNewLineCount - 1; newWindowIndex >= 0; newWindowIndex--)
                {
                    string oldLine = oldLines[commonPrefixLineCount + oldWindowIndex];
                    string newLine = newLines[commonPrefixLineCount + newWindowIndex];

                    if (string.Equals(oldLine, newLine, StringComparison.Ordinal))
                    {
                        commonSubsequenceLengths[oldWindowIndex, newWindowIndex] =
                            commonSubsequenceLengths[oldWindowIndex + 1, newWindowIndex + 1] + 1;
                    }
                    else
                    {
                        commonSubsequenceLengths[oldWindowIndex, newWindowIndex] = Math.Max(
                            commonSubsequenceLengths[oldWindowIndex + 1, newWindowIndex],
                            commonSubsequenceLengths[oldWindowIndex, newWindowIndex + 1]);
                    }
                }
            }

            return commonSubsequenceLengths;
        }

        static void AppendUnchangedSuffixLines(List<DiffLine> diffLines, string[] oldLines, string[] newLines, int commonSuffixLineCount)
        {
            for (int suffixIndex = commonSuffixLineCount - 1; suffixIndex >= 0; suffixIndex--)
            {
                int oldLineIndex = oldLines.Length - 1 - suffixIndex;
                int newLineIndex = newLines.Length - 1 - suffixIndex;
                diffLines.Add(new DiffLine(DiffLineKind.Context, oldLines[oldLineIndex], oldLineIndex + 1, newLineIndex + 1));
            }
        }

        // A run of unchanged lines only earns its place next to a change. A run at the very start
        // keeps only its tail and a run at the very end keeps only its head, because the lines
        // furthest from a change are the ones nobody reads.
        static List<DiffLine> FoldLongRunsOfUnchangedLines(List<DiffLine> diffLines, int contextLineCount)
        {
            var foldedDiffLines = new List<DiffLine>();
            int lineIndex = 0;

            while (lineIndex < diffLines.Count)
            {
                if (diffLines[lineIndex].Kind != DiffLineKind.Context)
                {
                    foldedDiffLines.Add(diffLines[lineIndex]);
                    lineIndex++;
                    continue;
                }

                int runStartIndex = lineIndex;

                while (lineIndex < diffLines.Count && diffLines[lineIndex].Kind == DiffLineKind.Context)
                {
                    lineIndex++;
                }

                AppendFoldedContextRun(foldedDiffLines, diffLines, runStartIndex, lineIndex, contextLineCount);
            }

            return foldedDiffLines;
        }

        static void AppendFoldedContextRun(List<DiffLine> foldedDiffLines, List<DiffLine> diffLines,
            int runStartIndex, int runEndIndex, int contextLineCount)
        {
            int runLineCount = runEndIndex - runStartIndex;
            int keptHeadLineCount = runStartIndex == 0 ? 0 : contextLineCount;
            int keptTailLineCount = runEndIndex == diffLines.Count ? 0 : contextLineCount;

            if (runLineCount <= keptHeadLineCount + keptTailLineCount)
            {
                for (int lineIndex = runStartIndex; lineIndex < runEndIndex; lineIndex++)
                {
                    foldedDiffLines.Add(diffLines[lineIndex]);
                }

                return;
            }

            for (int lineIndex = runStartIndex; lineIndex < runStartIndex + keptHeadLineCount; lineIndex++)
            {
                foldedDiffLines.Add(diffLines[lineIndex]);
            }

            int foldedLineCount = runLineCount - keptHeadLineCount - keptTailLineCount;
            foldedDiffLines.Add(new DiffLine(DiffLineKind.Skipped, $"... {foldedLineCount} unchanged lines ...", 0, 0));

            for (int lineIndex = runEndIndex - keptTailLineCount; lineIndex < runEndIndex; lineIndex++)
            {
                foldedDiffLines.Add(diffLines[lineIndex]);
            }
        }

        static void CountAddedAndRemovedLines(List<DiffLine> diffLines, out int addedLineCount, out int removedLineCount)
        {
            addedLineCount = 0;
            removedLineCount = 0;

            foreach (var diffLine in diffLines)
            {
                if (diffLine.Kind == DiffLineKind.Added)
                {
                    addedLineCount++;
                }
                else if (diffLine.Kind == DiffLineKind.Removed)
                {
                    removedLineCount++;
                }
            }
        }
    }
}
