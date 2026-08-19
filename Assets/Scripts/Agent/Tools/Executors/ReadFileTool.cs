using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using Cysharp.Threading.Tasks;

namespace Amberline.Agent
{
    // read_file(path, start_line, end_line) - the tool the agent reaches for most, so its output
    // shape decides how well every later turn goes.
    //
    // Three things it must get right:
    //
    // 1. It NEVER returns a whole large file. The budget is 200 lines or 64 KB, whichever runs out
    //    first, because the text lands in the transcript and stays there for the rest of the run.
    // 2. When it truncates, it names the EXACT start_line to continue from. A model that has to
    //    guess the next window either re-reads what it already has or skips a block silently.
    // 3. Line numbers are printed, because grep and edit_file both talk in line numbers - and the
    //    header says out loud that the numbers are not part of the file, so they do not end up
    //    inside an edit_file find string.
    //
    // Binary files are refused by sniffing the CONTENT rather than the extension: an extension list
    // is the right guard for grep, which walks thousands of files it never chose, but a single
    // deliberate read deserves an answer based on what the file actually holds.
    public class ReadFileTool : IToolExecutor
    {
        readonly PathSandbox _pathSandbox;

        static readonly ToolDefinition k_toolDefinition = new ToolDefinition(
            name: "read_file",
            parameterNames: new[] { "path", "start_line", "end_line" },
            isMutating: false,
            isCommand: false,
            maximumResponseTokens: 512);

        const long k_maximumFileSizeInBytes = 16L * 1024L * 1024L;
        const int k_binarySniffByteCount = 4096;
        const int k_lineNumberColumnWidth = 5;
        const int k_maximumAddressableLineNumber = 100000000;

        public ReadFileTool(PathSandbox pathSandbox)
        {
            _pathSandbox = pathSandbox;
        }

        public ToolDefinition Definition => k_toolDefinition;

        public UniTask<ToolResult> ExecuteAsync(ToolCall toolCall, CancellationToken cancellationToken)
        {
            // File IO runs on the thread pool so reading a large file cannot stall the player loop
            // and freeze the terminal mid-turn.
            return UniTask.RunOnThreadPool(() => ReadRequestedFileWindow(toolCall, cancellationToken), true, cancellationToken);
        }

        ToolResult ReadRequestedFileWindow(ToolCall toolCall, CancellationToken cancellationToken)
        {
            string suppliedPath = toolCall.GetArgument("path");

            if (string.IsNullOrWhiteSpace(suppliedPath))
            {
                return ToolResult.Failure("read_file needs a path. Call it again as read_file with arguments path, start_line and end_line, for example path src/Player.cs, start_line 1, end_line 200.");
            }

            if (!_pathSandbox.TryResolvePath(suppliedPath, out string absoluteFilePath, out string rejectionReason))
            {
                return ToolResult.Failure($"read_file: {rejectionReason}");
            }

            string displayPath = _pathSandbox.GetDisplayPath(absoluteFilePath);

            if (Directory.Exists(absoluteFilePath))
            {
                return ToolResult.Failure($"read_file: {displayPath} is a folder, not a file. Call list_dir on it to see what is inside.");
            }

            if (!File.Exists(absoluteFilePath))
            {
                return ToolResult.Failure($"read_file: no such file: {displayPath}. Call list_dir on the folder above it, or grep for a name you know, to find the correct path.");
            }

            ToolResult fileGuardFailure = CheckFileIsReadableText(absoluteFilePath, displayPath);

            if (fileGuardFailure != null)
            {
                return fileGuardFailure;
            }

            int firstLineToRead = ResolveFirstLineToRead(toolCall);
            int lastLineAllowed = ResolveLastLineAllowed(toolCall, firstLineToRead);

            var collectedNumberedLines = new List<string>();
            int totalLineCount;

            try
            {
                totalLineCount = CollectNumberedLinesInWindow(absoluteFilePath, firstLineToRead, lastLineAllowed,
                    collectedNumberedLines, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception exception)
            {
                return ToolResult.Failure($"read_file: could not read {displayPath}: {exception.Message}");
            }

            return BuildReadOutput(displayPath, collectedNumberedLines, firstLineToRead, totalLineCount);
        }

        // Returns null when the file is fine to read, and a ready-made failure when it is not.
        ToolResult CheckFileIsReadableText(string absoluteFilePath, string displayPath)
        {
            long fileSizeInBytes;

            try
            {
                fileSizeInBytes = new FileInfo(absoluteFilePath).Length;
            }
            catch (Exception exception)
            {
                return ToolResult.Failure($"read_file: could not open {displayPath}: {exception.Message}");
            }

            if (fileSizeInBytes > k_maximumFileSizeInBytes)
            {
                return ToolResult.Failure($"read_file: {displayPath} is {fileSizeInBytes} bytes, which is too large to open. Use grep to find the part you need.");
            }

            if (LooksLikeBinaryFile(absoluteFilePath))
            {
                return ToolResult.Failure($"read_file: {displayPath} is a binary file, not UTF-8 text, so it cannot be read. Pick a source file instead.");
            }

            return null;
        }

        static bool LooksLikeBinaryFile(string absoluteFilePath)
        {
            try
            {
                using (var fileStream = new FileStream(absoluteFilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                {
                    var sniffBuffer = new byte[k_binarySniffByteCount];
                    int readByteCount = fileStream.Read(sniffBuffer, 0, sniffBuffer.Length);
                    return ContainsBinaryBytes(sniffBuffer, readByteCount);
                }
            }
            catch (Exception)
            {
                // If the sniff itself fails, let the real read report the real error.
                return false;
            }
        }

        static bool ContainsBinaryBytes(byte[] sniffBuffer, int readByteCount)
        {
            int controlCharacterCount = 0;

            for (int byteIndex = 0; byteIndex < readByteCount; byteIndex++)
            {
                byte currentByte = sniffBuffer[byteIndex];

                // A NUL byte never appears in UTF-8 text. It does appear in UTF-16 text, which this
                // tool also cannot present sensibly, so refusing both is the honest answer.
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

            return readByteCount > 0 && controlCharacterCount * 10 > readByteCount * 3;
        }

        // A missing or unparsable line number is not worth a failed turn: reading from the top is
        // the only thing it can sensibly mean, and the header states the window that was used.
        int ResolveFirstLineToRead(ToolCall toolCall)
        {
            int firstLineToRead = ParseLineNumberArgument(toolCall.GetArgument("start_line"), 1);

            if (firstLineToRead < 1)
            {
                firstLineToRead = 1;
            }

            if (firstLineToRead > k_maximumAddressableLineNumber)
            {
                firstLineToRead = k_maximumAddressableLineNumber;
            }

            return firstLineToRead;
        }

        static int ParseLineNumberArgument(string argumentText, int fallbackValue)
        {
            if (string.IsNullOrWhiteSpace(argumentText))
            {
                return fallbackValue;
            }

            if (int.TryParse(argumentText.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsedLineNumber))
            {
                return parsedLineNumber;
            }

            return fallbackValue;
        }

        int ResolveLastLineAllowed(ToolCall toolCall, int firstLineToRead)
        {
            int lastLineOfBudget = firstLineToRead + ToolOutputTruncator.k_maximumReadLineCount - 1;
            int lastLineRequested = ParseLineNumberArgument(toolCall.GetArgument("end_line"), lastLineOfBudget);

            if (lastLineRequested < firstLineToRead)
            {
                lastLineRequested = lastLineOfBudget;
            }

            return Math.Min(lastLineRequested, lastLineOfBudget);
        }

        // Streams the file instead of loading it, so a big file costs IO but never memory, and
        // keeps counting after the window so the footer can say how much is left.
        static int CollectNumberedLinesInWindow(string absoluteFilePath, int firstLineToRead, int lastLineAllowed,
            List<string> collectedNumberedLines, CancellationToken cancellationToken)
        {
            int totalLineCount = 0;

            using (var fileStream = new FileStream(absoluteFilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            using (var fileReader = new StreamReader(fileStream, Encoding.UTF8, true))
            {
                string currentLine;

                while ((currentLine = fileReader.ReadLine()) != null)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    totalLineCount++;

                    if (totalLineCount >= firstLineToRead && totalLineCount <= lastLineAllowed)
                    {
                        collectedNumberedLines.Add(FormatNumberedLine(totalLineCount, currentLine));
                    }
                }
            }

            return totalLineCount;
        }

        static string FormatNumberedLine(int lineNumber, string lineText)
        {
            string lineNumberColumn = lineNumber.ToString(CultureInfo.InvariantCulture).PadLeft(k_lineNumberColumnWidth);
            return $"{lineNumberColumn}| {lineText}";
        }

        ToolResult BuildReadOutput(string displayPath, List<string> collectedNumberedLines, int firstLineToRead, int totalLineCount)
        {
            if (totalLineCount == 0)
            {
                return ToolResult.Success($"{displayPath} is empty (0 lines).");
            }

            if (collectedNumberedLines.Count == 0)
            {
                return ToolResult.Failure($"read_file: {displayPath} has only {totalLineCount} lines, so start_line {firstLineToRead} is past the end. Call read_file again with a start_line between 1 and {totalLineCount}.");
            }

            string fileWindowText = ToolOutputTruncator.JoinFirstLinesWithinBudget(collectedNumberedLines,
                ToolOutputTruncator.k_maximumReadLineCount, ToolOutputTruncator.k_maximumOutputCharacterCount,
                out int keptLineCount);

            int lastShownLineNumber = firstLineToRead + keptLineCount - 1;

            var outputBuilder = new StringBuilder();
            outputBuilder.Append($"{displayPath} lines {firstLineToRead}-{lastShownLineNumber} of {totalLineCount} (the line numbers are not part of the file)\n");
            outputBuilder.Append(fileWindowText);

            if (lastShownLineNumber < totalLineCount)
            {
                outputBuilder.Append($"\n[truncated] {totalLineCount - lastShownLineNumber} more lines. Call read_file again on {displayPath} with start_line {lastShownLineNumber + 1} to continue.");
            }

            return ToolResult.Success(outputBuilder.ToString());
        }
    }
}
