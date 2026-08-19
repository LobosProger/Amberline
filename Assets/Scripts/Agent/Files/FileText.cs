using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace Amberline.Agent
{
    /// <summary>
    /// What one text file looked like on disk. <see cref="Text"/> always uses line feeds only, so
    /// every matcher and every differ in the agent works in one line-ending world; the original
    /// ending, the byte order mark and the trailing newline travel alongside it so a write can put
    /// all three back exactly as they were.
    /// </summary>
    public class FileTextInfo
    {
        public string Text { get; }

        /// <summary>Either "\r\n" or "\n" - whichever the file used for MOST of its lines.</summary>
        public string DominantLineEnding { get; }

        public bool HasByteOrderMark { get; }

        /// <summary>True when the file ended with a line break, which most source files do.</summary>
        public bool EndsWithLineBreak { get; }

        public FileTextInfo(string text, string dominantLineEnding, bool hasByteOrderMark, bool endsWithLineBreak)
        {
            Text = text ?? string.Empty;
            DominantLineEnding = dominantLineEnding == FileText.k_windowsLineEnding
                ? FileText.k_windowsLineEnding
                : FileText.k_unixLineEnding;
            HasByteOrderMark = hasByteOrderMark;
            EndsWithLineBreak = endsWithLineBreak;
        }

        /// <summary>
        /// The shape a file that does not exist yet is written with: the platform's own line
        /// ending, no byte order mark, and a trailing newline like every well-formed source file.
        /// </summary>
        public static FileTextInfo CreateForFileThatDoesNotExistYet()
        {
            return new FileTextInfo(string.Empty, FileText.DefaultLineEndingForNewFile, false, true);
        }
    }

    // Reads and writes text WITHOUT churning the file. Three separate facts about a file are read
    // and preserved here, and every one of them exists because losing it turns a one-line change
    // into a whole-file diff:
    //
    // 1. LINE ENDING. The model writes "\n" because that is what JSON escaping teaches it. Writing
    //    that straight into a CRLF file rewrites every single line, so the approval card shows a
    //    2000-line diff for a one-word edit and source control records the same. The DOMINANT
    //    ending is used, not the first one found: real files are mixed, and a stray lone "\n" at
    //    the top of a CRLF file must not decide the whole file's fate.
    // 2. BYTE ORDER MARK. Visual Studio writes UTF-8 with a BOM. Dropping it changes the first
    //    three bytes of the file, which some tools read as a different encoding entirely.
    // 3. TRAILING NEWLINE. Whether the last line ends with a break is invisible in an editor and
    //    very visible in a diff.
    //
    // Binary files are refused by sniffing the CONTENT, not the extension, for the same reason
    // read_file does: a deliberate single-file operation deserves an answer based on what the file
    // actually holds.
    public static class FileText
    {
        /// <summary>The two endings this agent recognises. Old Mac lone-CR files are read as line
        /// feeds and written back as the platform default, because nothing writes them today.</summary>
        public const string k_windowsLineEnding = "\r\n";

        public const string k_unixLineEnding = "\n";

        /// <summary>Files larger than this are refused rather than pulled into memory whole.</summary>
        public const long k_maximumTextFileSizeInBytes = 16L * 1024L * 1024L;

        const int k_binarySniffByteCount = 4096;
        const byte k_firstByteOfUtf8ByteOrderMark = 0xEF;
        const byte k_secondByteOfUtf8ByteOrderMark = 0xBB;
        const byte k_thirdByteOfUtf8ByteOrderMark = 0xBF;

        /// <summary>What a brand new file gets: the platform's own ending, so a file created on
        /// Windows looks like every other file the user's editor writes there.</summary>
        public static string DefaultLineEndingForNewFile =>
            Environment.NewLine == k_windowsLineEnding ? k_windowsLineEnding : k_unixLineEnding;

        /// <summary>
        /// Reads a text file and reports how it was shaped. Returns false with a reason written
        /// for the MODEL to read whenever the file cannot be treated as text. Never throws.
        /// </summary>
        public static bool TryReadTextFile(string absoluteFilePath, out FileTextInfo fileTextInfo, out string failureReason)
        {
            fileTextInfo = null;
            failureReason = null;

            byte[] fileBytes;

            try
            {
                var fileInformation = new FileInfo(absoluteFilePath);

                if (fileInformation.Length > k_maximumTextFileSizeInBytes)
                {
                    failureReason = $"that file is {fileInformation.Length} bytes, which is too large to open. Use grep to find the part you need.";
                    return false;
                }

                fileBytes = File.ReadAllBytes(absoluteFilePath);
            }
            catch (Exception exception)
            {
                failureReason = $"that file could not be opened: {exception.Message}";
                return false;
            }

            if (LooksLikeBinaryContent(fileBytes, fileBytes.Length))
            {
                failureReason = "that file is binary, not UTF-8 text, so it cannot be changed as text. Pick a source file instead.";
                return false;
            }

            fileTextInfo = BuildTextInfoFromBytes(fileBytes);
            return true;
        }

        /// <summary>
        /// True when the bytes are not UTF-8 text. A NUL byte settles it on its own; otherwise a
        /// heavy share of control characters does. Only the first few KB are examined, which is
        /// where any real binary format puts its header anyway.
        /// </summary>
        public static bool LooksLikeBinaryContent(byte[] fileBytes, int byteCount)
        {
            if (fileBytes == null || byteCount <= 0)
            {
                return false;
            }

            int bytesToInspect = Math.Min(byteCount, k_binarySniffByteCount);
            int controlCharacterCount = 0;

            for (int byteIndex = 0; byteIndex < bytesToInspect; byteIndex++)
            {
                byte currentByte = fileBytes[byteIndex];

                // A NUL never appears in UTF-8 text. It does appear in UTF-16 text, which this
                // agent also cannot edit safely, so refusing both is the honest answer.
                if (currentByte == 0)
                {
                    return true;
                }

                bool isControlCharacter = currentByte < 0x09 || (currentByte > 0x0D && currentByte < 0x20);

                if (isControlCharacter)
                {
                    controlCharacterCount++;
                }
            }

            return controlCharacterCount * 10 > bytesToInspect * 3;
        }

        static FileTextInfo BuildTextInfoFromBytes(byte[] fileBytes)
        {
            bool hasByteOrderMark = StartsWithUtf8ByteOrderMark(fileBytes);
            int firstContentByteIndex = hasByteOrderMark ? 3 : 0;

            string rawText = new UTF8Encoding(false)
                .GetString(fileBytes, firstContentByteIndex, fileBytes.Length - firstContentByteIndex);

            string dominantLineEnding = DetectDominantLineEnding(rawText);
            bool endsWithLineBreak = rawText.EndsWith("\n", StringComparison.Ordinal)
                || rawText.EndsWith("\r", StringComparison.Ordinal);

            return new FileTextInfo(NormalizeLineEndingsToLineFeed(rawText), dominantLineEnding, hasByteOrderMark, endsWithLineBreak);
        }

        static bool StartsWithUtf8ByteOrderMark(byte[] fileBytes)
        {
            return fileBytes.Length >= 3
                && fileBytes[0] == k_firstByteOfUtf8ByteOrderMark
                && fileBytes[1] == k_secondByteOfUtf8ByteOrderMark
                && fileBytes[2] == k_thirdByteOfUtf8ByteOrderMark;
        }

        /// <summary>
        /// Which ending the file mostly uses. A file is rarely uniform - one pasted block is enough
        /// to mix them - so the majority decides, and a tie goes to CRLF because a mixed file on
        /// Windows is nearly always a CRLF file with a few stray line feeds in it.
        /// </summary>
        public static string DetectDominantLineEnding(string rawText)
        {
            if (string.IsNullOrEmpty(rawText))
            {
                return DefaultLineEndingForNewFile;
            }

            int carriageReturnLineFeedCount = 0;
            int loneLineFeedCount = 0;

            for (int characterIndex = 0; characterIndex < rawText.Length; characterIndex++)
            {
                if (rawText[characterIndex] != '\n')
                {
                    continue;
                }

                bool isPrecededByCarriageReturn = characterIndex > 0 && rawText[characterIndex - 1] == '\r';

                if (isPrecededByCarriageReturn)
                {
                    carriageReturnLineFeedCount++;
                }
                else
                {
                    loneLineFeedCount++;
                }
            }

            if (carriageReturnLineFeedCount == 0 && loneLineFeedCount == 0)
            {
                return DefaultLineEndingForNewFile;
            }

            return carriageReturnLineFeedCount >= loneLineFeedCount ? k_windowsLineEnding : k_unixLineEnding;
        }

        /// <summary>
        /// Brings any text into the one-line-ending world the rest of the agent works in. Applied
        /// to model-supplied strings too, so a model that wrote \r\n still matches a file we hold
        /// as line feeds.
        /// </summary>
        public static string NormalizeLineEndingsToLineFeed(string text)
        {
            if (string.IsNullOrEmpty(text))
            {
                return string.Empty;
            }

            if (text.IndexOf('\r') < 0)
            {
                return text;
            }

            return text.Replace(k_windowsLineEnding, k_unixLineEnding).Replace('\r', '\n');
        }

        /// <summary>
        /// Splits normalised text into lines WITHOUT the empty element a trailing newline would
        /// otherwise produce - that phantom line shows up in every diff as a blank change. Whether
        /// the text ended with a break is carried by <see cref="FileTextInfo.EndsWithLineBreak"/>.
        /// </summary>
        public static string[] SplitIntoLines(string textWithLineFeeds)
        {
            if (string.IsNullOrEmpty(textWithLineFeeds))
            {
                return Array.Empty<string>();
            }

            string[] splitLines = textWithLineFeeds.Split('\n');

            if (splitLines.Length > 0 && splitLines[splitLines.Length - 1].Length == 0)
            {
                var linesWithoutPhantomLast = new string[splitLines.Length - 1];
                Array.Copy(splitLines, linesWithoutPhantomLast, splitLines.Length - 1);
                return linesWithoutPhantomLast;
            }

            return splitLines;
        }

        /// <summary>Rebuilds normalised text from lines produced by <see cref="SplitIntoLines"/>.</summary>
        public static string JoinLines(IReadOnlyList<string> lines, bool endWithLineBreak)
        {
            if (lines == null || lines.Count == 0)
            {
                return string.Empty;
            }

            var joinedTextBuilder = new StringBuilder();

            for (int lineIndex = 0; lineIndex < lines.Count; lineIndex++)
            {
                if (lineIndex > 0)
                {
                    joinedTextBuilder.Append('\n');
                }

                joinedTextBuilder.Append(lines[lineIndex] ?? string.Empty);
            }

            if (endWithLineBreak)
            {
                joinedTextBuilder.Append('\n');
            }

            return joinedTextBuilder.ToString();
        }

        /// <summary>
        /// Turns normalised text back into the exact bytes to put on disk: the file's own line
        /// ending restored and its byte order mark put back if it had one. This is the step that
        /// keeps a one-line edit a one-line diff.
        /// </summary>
        public static byte[] EncodeTextForFile(string textWithLineFeeds, string dominantLineEnding, bool writeByteOrderMark)
        {
            string normalizedText = NormalizeLineEndingsToLineFeed(textWithLineFeeds);

            string textWithFileLineEndings = dominantLineEnding == k_windowsLineEnding
                ? normalizedText.Replace(k_unixLineEnding, k_windowsLineEnding)
                : normalizedText;

            byte[] contentBytes = new UTF8Encoding(false).GetBytes(textWithFileLineEndings);

            if (!writeByteOrderMark)
            {
                return contentBytes;
            }

            var bytesWithByteOrderMark = new byte[contentBytes.Length + 3];
            bytesWithByteOrderMark[0] = k_firstByteOfUtf8ByteOrderMark;
            bytesWithByteOrderMark[1] = k_secondByteOfUtf8ByteOrderMark;
            bytesWithByteOrderMark[2] = k_thirdByteOfUtf8ByteOrderMark;
            Array.Copy(contentBytes, 0, bytesWithByteOrderMark, 3, contentBytes.Length);

            return bytesWithByteOrderMark;
        }

        /// <summary>How many lines a piece of normalised text holds. Used for short tool output.</summary>
        public static int CountLines(string textWithLineFeeds)
        {
            return SplitIntoLines(textWithLineFeeds).Length;
        }
    }
}
