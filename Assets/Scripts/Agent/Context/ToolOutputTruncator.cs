using System.Collections.Generic;
using System.Text;

namespace Amberline.Agent
{
    // Caps tool output BEFORE it enters the transcript. This is a context-window guard, not a
    // display concern: a 4000 line file pasted into history is spent tokens on every later turn of
    // the same session, and it pushes the system prompt towards the edge of the window where small
    // models start forgetting their output format.
    //
    // The second rule here matters as much as the first: truncation is never silent. A model that
    // is handed the first 200 lines with no note believes it has seen the whole file and answers
    // confidently about code it never read. Every cap therefore states what was dropped AND the
    // exact next call that fetches the rest.
    //
    // The budgets come from the plan: read 200 lines, grep 30 hits, list 200 entries, command last
    // 60 lines. Commands keep the TAIL because a build prints its errors at the end; everything
    // else keeps the head.
    public static class ToolOutputTruncator
    {
        /// <summary>Lines of a file one read_file call may return.</summary>
        public const int k_maximumReadLineCount = 200;

        /// <summary>read_file's 64 KB budget, and the second cap on every other tool output, so a
        /// file of very long lines cannot pass a line-count budget and still flood the window.</summary>
        public const int k_maximumOutputCharacterCount = 64 * 1024;

        /// <summary>Matching lines one grep call may return.</summary>
        public const int k_maximumGrepHitCount = 30;

        /// <summary>Entries one list_dir call may return.</summary>
        public const int k_maximumListEntryCount = 200;

        /// <summary>Trailing lines of command output that reach the transcript.</summary>
        public const int k_maximumCommandLineCount = 60;

        /// <summary>How wide a single line may be before it is shortened in the middle of a listing.</summary>
        public const int k_maximumSingleLineCharacterCount = 220;

        const string k_truncationMarker = "...";

        /// <summary>
        /// Joins the first lines that fit inside both budgets and reports how many were kept, so
        /// the caller can name the exact place to continue from. Pass int.MaxValue for a budget
        /// that does not apply.
        /// </summary>
        public static string JoinFirstLinesWithinBudget(IReadOnlyList<string> outputLines, int maximumLineCount,
            int maximumCharacterCount, out int keptLineCount)
        {
            keptLineCount = 0;

            if (outputLines == null || outputLines.Count == 0)
            {
                return string.Empty;
            }

            var joinedOutputBuilder = new StringBuilder();

            for (int lineIndex = 0; lineIndex < outputLines.Count; lineIndex++)
            {
                if (keptLineCount >= maximumLineCount)
                {
                    break;
                }

                if (!TryAppendLineWithinCharacterBudget(joinedOutputBuilder, outputLines[lineIndex], maximumCharacterCount))
                {
                    break;
                }

                keptLineCount++;
            }

            return joinedOutputBuilder.ToString();
        }

        static bool TryAppendLineWithinCharacterBudget(StringBuilder joinedOutputBuilder, string lineText, int maximumCharacterCount)
        {
            string safeLineText = lineText ?? string.Empty;
            int lengthAfterAppend = joinedOutputBuilder.Length + safeLineText.Length + 1;

            // The very first line is always kept, even when it alone blows the budget - returning
            // nothing at all would read to the model as an empty file.
            if (joinedOutputBuilder.Length > 0 && lengthAfterAppend > maximumCharacterCount)
            {
                return false;
            }

            if (joinedOutputBuilder.Length > 0)
            {
                joinedOutputBuilder.Append('\n');
            }

            joinedOutputBuilder.Append(safeLineText);
            return true;
        }

        /// <summary>
        /// Joins the LAST lines that fit inside both budgets, for command output where the useful
        /// part - the error, the exit summary - is at the end.
        /// </summary>
        public static string JoinLastLinesWithinBudget(IReadOnlyList<string> outputLines, int maximumLineCount,
            int maximumCharacterCount, out int keptLineCount)
        {
            keptLineCount = 0;

            if (outputLines == null || outputLines.Count == 0)
            {
                return string.Empty;
            }

            int firstLineIndexToKeep = outputLines.Count - maximumLineCount;

            if (firstLineIndexToKeep < 0)
            {
                firstLineIndexToKeep = 0;
            }

            var keptLines = new List<string>();

            for (int lineIndex = firstLineIndexToKeep; lineIndex < outputLines.Count; lineIndex++)
            {
                keptLines.Add(outputLines[lineIndex]);
            }

            // Trimming from the front again keeps the tail intact when the character budget bites.
            while (keptLines.Count > 1 && MeasureJoinedLength(keptLines) > maximumCharacterCount)
            {
                keptLines.RemoveAt(0);
            }

            keptLineCount = keptLines.Count;
            return string.Join("\n", keptLines);
        }

        static int MeasureJoinedLength(List<string> lines)
        {
            int joinedLength = 0;

            foreach (string lineText in lines)
            {
                joinedLength += (lineText?.Length ?? 0) + 1;
            }

            return joinedLength;
        }

        /// <summary>
        /// The note that turns a silent cut into an honest one. Returns an empty string when
        /// nothing was dropped, so callers can append it unconditionally. The hint must name the
        /// exact next call, not just say that more exists.
        /// </summary>
        public static string BuildTruncationNote(int shownItemCount, int totalItemCount, string itemNamePlural,
            string howToSeeTheRestHint)
        {
            if (totalItemCount <= shownItemCount)
            {
                return string.Empty;
            }

            int droppedItemCount = totalItemCount - shownItemCount;
            return $"[truncated] showing {shownItemCount} of {totalItemCount} {itemNamePlural}, {droppedItemCount} not shown. {howToSeeTheRestHint}";
        }

        /// <summary>
        /// Shortens one line so a minified file or a base64 blob cannot spend the whole budget on a
        /// single entry. The cut is marked, for the same reason every other cut here is.
        /// </summary>
        public static string ShortenSingleLine(string lineText, int maximumCharacterCount)
        {
            if (string.IsNullOrEmpty(lineText) || lineText.Length <= maximumCharacterCount)
            {
                return lineText ?? string.Empty;
            }

            if (maximumCharacterCount <= k_truncationMarker.Length)
            {
                return k_truncationMarker;
            }

            return lineText.Substring(0, maximumCharacterCount - k_truncationMarker.Length) + k_truncationMarker;
        }
    }
}
