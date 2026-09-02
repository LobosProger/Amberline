using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Threading;
using Cysharp.Threading.Tasks;
using UnityEngine;

namespace Amberline.Agent
{
    /// <summary>
    /// Everything the approval card needs to show one proposed file change, and everything the
    /// executor needs to apply it - deliberately the same object, so the diff on screen is built
    /// from the very text that gets written and the two can never drift apart.
    /// <para>
    /// When <see cref="IsAvailable"/> is false the change cannot be made at all and
    /// <see cref="FailureMessage"/> is the sentence the model gets back. The card should not be
    /// shown in that case - let the call run and fail, so the model reads the reason.
    /// </para>
    /// </summary>
    public class FileChangePreview
    {
        public bool IsAvailable { get; }

        /// <summary>Written for the model to read. Empty when <see cref="IsAvailable"/> is true.</summary>
        public string FailureMessage { get; }

        /// <summary>"write_file" or "edit_file".</summary>
        public string ToolName { get; }

        /// <summary>Workspace-relative, forward slashes - the only spelling the user should see.</summary>
        public string DisplayPath { get; }

        /// <summary>True when the file does not exist yet, so the card says "create", not "edit".</summary>
        public bool IsNewFile { get; }

        /// <summary>
        /// The change to render. For a new file this is a diff against nothing, so every line comes
        /// back as <see cref="DiffLineKind.Added"/> and the card can render one control for both cases.
        /// </summary>
        public FileDiff Diff { get; }

        /// <summary>The exact text that will be written, line feeds only. Not for display.</summary>
        public string NewText { get; }

        /// <summary>The line ending the file will keep, chosen from what is already on disk.</summary>
        public string LineEnding { get; }

        public bool HasByteOrderMark { get; }

        FileChangePreview(bool isAvailable, string failureMessage, string toolName, string displayPath, bool isNewFile,
            FileDiff diff, string newText, string lineEnding, bool hasByteOrderMark)
        {
            IsAvailable = isAvailable;
            FailureMessage = failureMessage ?? string.Empty;
            ToolName = toolName ?? string.Empty;
            DisplayPath = displayPath ?? string.Empty;
            IsNewFile = isNewFile;
            Diff = diff;
            NewText = newText ?? string.Empty;
            LineEnding = lineEnding ?? FileText.k_unixLineEnding;
            HasByteOrderMark = hasByteOrderMark;
        }

        public static FileChangePreview ForChange(string toolName, string displayPath, bool isNewFile, FileDiff diff,
            string newText, string lineEnding, bool hasByteOrderMark)
        {
            return new FileChangePreview(true, null, toolName, displayPath, isNewFile, diff, newText, lineEnding, hasByteOrderMark);
        }

        public static FileChangePreview Unavailable(string toolName, string failureMessage)
        {
            return new FileChangePreview(false, failureMessage, toolName, string.Empty, false, null, string.Empty, null, false);
        }

        /// <summary>One line for the top of the card: "create New.cs (+12)".</summary>
        public string BuildHeadline()
        {
            if (!IsAvailable)
            {
                return FailureMessage;
            }

            string actionName = IsNewFile ? "create" : "edit";
            return $"{actionName} {DisplayPath} ({Diff.DescribeChangeCounts()})";
        }
    }

    /// <summary>
    /// Implemented by the two mutating file executors so the approval gate can build a real diff
    /// before the call runs. Kept off <see cref="IToolExecutor"/> on purpose: the gate asks for a
    /// preview only when it finds this interface, and a tool with nothing to preview stays simple.
    /// </summary>
    public interface IFileChangePreviewProvider
    {
        /// <summary>
        /// Works out exactly what the call would do, without doing it. Runs the file read and the
        /// diff on a thread-pool thread, so the caller may await it from the main thread safely.
        /// Never throws for an ordinary failure - it comes back as an unavailable preview.
        /// </summary>
        UniTask<FileChangePreview> BuildFileChangePreviewAsync(ToolCall toolCall, CancellationToken cancellationToken);
    }

    /// <summary>What one write did, or why it did nothing.</summary>
    public class FileWriteOutcome
    {
        public bool IsSuccess { get; }

        /// <summary>Written for the model to read. Empty on success.</summary>
        public string FailureMessage { get; }

        public string DisplayPath { get; }
        public bool DidCreateNewFile { get; }

        FileWriteOutcome(bool isSuccess, string failureMessage, string displayPath, bool didCreateNewFile)
        {
            IsSuccess = isSuccess;
            FailureMessage = failureMessage ?? string.Empty;
            DisplayPath = displayPath ?? string.Empty;
            DidCreateNewFile = didCreateNewFile;
        }

        public static FileWriteOutcome Success(string displayPath, bool didCreateNewFile)
        {
            return new FileWriteOutcome(true, null, displayPath, didCreateNewFile);
        }

        public static FileWriteOutcome Failure(string failureMessage)
        {
            return new FileWriteOutcome(false, failureMessage, string.Empty, false);
        }
    }

    /// <summary>
    /// One recorded change, enough to put the file back the way it was. A creation carries no
    /// backup because undoing it means deleting the file.
    /// </summary>
    public class ChangeJournalEntry
    {
        public int SequenceNumber { get; }
        public DateTime UtcTimeStamp { get; }

        /// <summary>The workspace this change was made in. Checked before undo, so a change made in
        /// another project folder is never replayed into the current one.</summary>
        public string WorkspaceRootPath { get; }

        /// <summary>Workspace-relative path, re-resolved through the sandbox before any undo.</summary>
        public string RelativePath { get; }

        /// <summary>False when the change CREATED the file, so undoing it deletes the file.</summary>
        public bool DidFileExistBefore { get; }

        /// <summary>Name of the file holding the previous bytes. Empty for a creation.</summary>
        public string BackupFileName { get; }

        public ChangeJournalEntry(int sequenceNumber, DateTime utcTimeStamp, string workspaceRootPath,
            string relativePath, bool didFileExistBefore, string backupFileName)
        {
            SequenceNumber = sequenceNumber;
            UtcTimeStamp = utcTimeStamp;
            WorkspaceRootPath = workspaceRootPath ?? string.Empty;
            RelativePath = relativePath ?? string.Empty;
            DidFileExistBefore = didFileExistBefore;
            BackupFileName = backupFileName ?? string.Empty;
        }
    }

    // The only class in the agent that puts bytes on disk. Three rules hold it together.
    //
    // 1. EVERY PATH GOES THROUGH THE SANDBOX HERE, not only in the caller. The executors resolve a
    //    path to read and diff it, and this class resolves it AGAIN before writing. That looks like
    //    duplication and is not: it means no future caller can hand this class an absolute path it
    //    worked out for itself, and there is exactly one place to audit for "can the agent write
    //    outside the workspace".
    //
    // 2. THE WRITE IS ATOMIC AND LEAVES NOTHING BEHIND. Content goes to a temp file OUTSIDE the
    //    workspace and then replaces the target in one step, so a crash mid-write cannot leave a
    //    half-written source file. The temp file is outside the workspace because Unity imports
    //    everything under Assets/ and would generate a .meta for a stray .tmp. File.Replace is
    //    called with a NULL backup argument for exactly the same reason - the alternative drops a
    //    .bak next to the file, inside Assets/, and the Editor imports it as a broken asset.
    //    File.Replace also refuses to run when the destination does not exist, so creating a file
    //    is a separate path that moves the temp file into place instead.
    //
    // 3. THE PREVIOUS CONTENT IS JOURNALLED BEFORE THE WRITE, not after. That ordering is what
    //    makes /undo trustworthy: if the write fails halfway, the bytes needed to put the file back
    //    are already saved. The journal lives under Application.persistentDataPath - outside the
    //    user's project, so undo history is never something the agent can read, edit or commit.
    public class FileWriteService
    {
        readonly PathSandbox _pathSandbox;
        readonly string _undoFolderPath;
        readonly string _backupFolderPath;
        readonly string _journalFilePath;
        readonly string _temporaryWriteFolderPath;

        const string k_journalFileName = "journal.tsv";
        const string k_backupFolderName = "backups";
        const string k_temporaryWriteFolderName = "pending-writes";
        const string k_amberlineFolderName = "amberline";
        const string k_undoFolderName = "undo";
        const char k_journalFieldSeparator = '\t';
        const int k_journalFieldCount = 6;

        /// <summary>
        /// Pass the sandbox the executors use. <paramref name="undoFolderPath"/> is for tests only;
        /// leave it null in the game.
        /// <para>
        /// Construct this on the MAIN THREAD. Application.persistentDataPath throws when it is read
        /// from a thread-pool thread, and every write afterwards runs off the main thread.
        /// </para>
        /// </summary>
        public FileWriteService(PathSandbox pathSandbox, string undoFolderPath = null)
        {
            _pathSandbox = pathSandbox;
            _undoFolderPath = string.IsNullOrEmpty(undoFolderPath) ? BuildDefaultUndoFolderPath() : undoFolderPath;
            _backupFolderPath = Path.Combine(_undoFolderPath, k_backupFolderName);
            _journalFilePath = Path.Combine(_undoFolderPath, k_journalFileName);
            _temporaryWriteFolderPath = Path.Combine(_undoFolderPath, k_temporaryWriteFolderName);
        }

        static string BuildDefaultUndoFolderPath()
        {
            return Path.Combine(Application.persistentDataPath, k_amberlineFolderName, k_undoFolderName);
        }

        /// <summary>Where the undo history lives. Outside the workspace, always.</summary>
        public string UndoFolderPath => _undoFolderPath;

        /// <summary>How many changes /undo can still walk back. Backs the "nothing to undo" notice.</summary>
        public int GetUndoableChangeCount()
        {
            return ReadJournalEntries().Count;
        }

        /// <summary>The recorded changes, oldest first. Backs /diff and the status line.</summary>
        public IReadOnlyList<ChangeJournalEntry> GetUndoableChanges()
        {
            return ReadJournalEntries();
        }

        /// <summary>
        /// Records the current content, then replaces the file with <paramref name="newTextWithLineFeeds"/>.
        /// The path is resolved through the sandbox here, so callers pass the path the MODEL wrote,
        /// never one they resolved themselves. Never throws except on cancellation.
        /// </summary>
        public FileWriteOutcome WriteWholeFile(string modelSuppliedPath, string newTextWithLineFeeds,
            string lineEndingToUse, bool writeByteOrderMark, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (!_pathSandbox.TryResolvePath(modelSuppliedPath, out string absoluteFilePath, out string rejectionReason))
            {
                return FileWriteOutcome.Failure(rejectionReason);
            }

            string displayPath = _pathSandbox.GetDisplayPath(absoluteFilePath);

            if (Directory.Exists(absoluteFilePath))
            {
                return FileWriteOutcome.Failure($"{displayPath} is a folder, not a file. Give the path of a file.");
            }

            if (!TryCreateParentFolder(absoluteFilePath, displayPath, out string parentFolderFailure))
            {
                return FileWriteOutcome.Failure(parentFolderFailure);
            }

            bool didFileExistBefore = File.Exists(absoluteFilePath);

            if (!TryRecordPreviousContentInJournal(absoluteFilePath, displayPath, didFileExistBefore, out string journalFailure))
            {
                return FileWriteOutcome.Failure(journalFailure);
            }

            byte[] contentBytes = FileText.EncodeTextForFile(newTextWithLineFeeds, lineEndingToUse, writeByteOrderMark);

            if (!TryWriteBytesAtomically(absoluteFilePath, contentBytes, out string writeFailure))
            {
                // The journal entry describes a change that never landed, so it is taken back out
                // rather than left for /undo to replay over an untouched file.
                RemoveLastJournalEntry();
                return FileWriteOutcome.Failure($"{displayPath} could not be written: {writeFailure}");
            }

            return FileWriteOutcome.Success(displayPath, !didFileExistBefore);
        }

        bool TryCreateParentFolder(string absoluteFilePath, string displayPath, out string failureReason)
        {
            failureReason = null;

            try
            {
                string parentFolderPath = Path.GetDirectoryName(absoluteFilePath);

                if (!string.IsNullOrEmpty(parentFolderPath) && !Directory.Exists(parentFolderPath))
                {
                    Directory.CreateDirectory(parentFolderPath);
                }

                return true;
            }
            catch (Exception exception)
            {
                failureReason = $"the folder for {displayPath} could not be created: {exception.Message}";
                return false;
            }
        }

        bool TryRecordPreviousContentInJournal(string absoluteFilePath, string displayPath, bool didFileExistBefore, out string failureReason)
        {
            failureReason = null;

            try
            {
                Directory.CreateDirectory(_backupFolderPath);

                int nextSequenceNumber = ReadJournalEntries().Count + 1;
                string backupFileName = string.Empty;

                if (didFileExistBefore)
                {
                    backupFileName = $"{nextSequenceNumber.ToString("D6", CultureInfo.InvariantCulture)}-{Guid.NewGuid():N}.bak";
                    File.Copy(absoluteFilePath, Path.Combine(_backupFolderPath, backupFileName), true);
                }

                var journalEntry = new ChangeJournalEntry(nextSequenceNumber, DateTime.UtcNow,
                    _pathSandbox.WorkspaceRootPath, displayPath, didFileExistBefore, backupFileName);

                File.AppendAllText(_journalFilePath, FormatJournalLine(journalEntry) + "\n");
                return true;
            }
            catch (Exception exception)
            {
                // Refusing to write is the right answer: a change that cannot be undone is worse
                // than a change that did not happen.
                failureReason = $"the previous content of {displayPath} could not be saved for undo, so nothing was written: {exception.Message}";
                return false;
            }
        }

        static string FormatJournalLine(ChangeJournalEntry journalEntry)
        {
            // The relative path goes LAST, because it is the only field that could in theory hold a
            // separator, and parsing splits into a fixed number of fields so the tail stays whole.
            return string.Join(k_journalFieldSeparator.ToString(),
                journalEntry.SequenceNumber.ToString(CultureInfo.InvariantCulture),
                journalEntry.UtcTimeStamp.Ticks.ToString(CultureInfo.InvariantCulture),
                journalEntry.DidFileExistBefore ? "1" : "0",
                journalEntry.BackupFileName,
                journalEntry.WorkspaceRootPath,
                journalEntry.RelativePath);
        }

        // The temp file lives outside the workspace so Unity never sees it, which means it can land
        // on a different volume from the target - and File.Replace and File.Move both refuse to
        // cross volumes. The volume is checked up front rather than discovered through an exception,
        // and the cross-volume case falls back to a plain write, which is still safe because the
        // previous content is already in the journal by the time this runs.
        bool TryWriteBytesAtomically(string absoluteFilePath, byte[] contentBytes, out string failureReason)
        {
            failureReason = null;
            string temporaryFilePath = null;

            try
            {
                Directory.CreateDirectory(_temporaryWriteFolderPath);
                temporaryFilePath = Path.Combine(_temporaryWriteFolderPath, $"write-{Guid.NewGuid():N}.tmp");
                File.WriteAllBytes(temporaryFilePath, contentBytes);

                if (!AreOnTheSameVolume(temporaryFilePath, absoluteFilePath))
                {
                    File.WriteAllBytes(absoluteFilePath, contentBytes);
                    return true;
                }

                MoveTemporaryFileOntoTarget(temporaryFilePath, absoluteFilePath, contentBytes);
                return true;
            }
            catch (Exception exception)
            {
                failureReason = exception.Message;
                return false;
            }
            finally
            {
                DeleteFileIfItStillExists(temporaryFilePath);
            }
        }

        static bool AreOnTheSameVolume(string firstPath, string secondPath)
        {
            try
            {
                return string.Equals(Path.GetPathRoot(Path.GetFullPath(firstPath)),
                    Path.GetPathRoot(Path.GetFullPath(secondPath)), StringComparison.OrdinalIgnoreCase);
            }
            catch (Exception)
            {
                return false;
            }
        }

        static void MoveTemporaryFileOntoTarget(string temporaryFilePath, string absoluteFilePath, byte[] contentBytes)
        {
            // File.Replace throws when the destination is missing, so a brand new file is moved into
            // place instead. Both operations are a single rename on the same volume.
            if (!File.Exists(absoluteFilePath))
            {
                File.Move(temporaryFilePath, absoluteFilePath);
                return;
            }

            try
            {
                // The null backup argument is the point: with a name here, Windows would leave a
                // .bak beside the file, inside Assets/, for Unity to import as a broken asset.
                File.Replace(temporaryFilePath, absoluteFilePath, null);
            }
            catch (Exception exception)
            {
                // A read-only target, a file open in another process, or a filesystem that cannot
                // do the swap. The previous content is already journalled, so a plain write is safe.
                Debug.LogWarning($"[FileWriteService] Atomic replace failed, writing directly instead: {exception.GetType().Name}: {exception.Message}");
                File.WriteAllBytes(absoluteFilePath, contentBytes);
            }
        }

        static void DeleteFileIfItStillExists(string filePath)
        {
            if (string.IsNullOrEmpty(filePath))
            {
                return;
            }

            try
            {
                if (File.Exists(filePath))
                {
                    File.Delete(filePath);
                }
            }
            catch (Exception exception)
            {
                Debug.LogWarning($"[FileWriteService] A temporary write file could not be removed: {exception.Message}");
            }
        }

        /// <summary>
        /// Puts the most recent change back: a created file is deleted, an overwritten file gets its
        /// exact previous bytes. Returns false with a reason meant for the USER, since /undo is a
        /// slash command and not something the model calls.
        /// </summary>
        public bool TryUndoLastChange(out string undoneDisplayPath, out string failureReason)
        {
            undoneDisplayPath = string.Empty;
            failureReason = null;

            var journalEntries = ReadJournalEntries();

            if (journalEntries.Count == 0)
            {
                failureReason = "there is nothing to undo.";
                return false;
            }

            var lastChange = journalEntries[journalEntries.Count - 1];

            if (!TryResolveJournalEntryPath(lastChange, out string absoluteFilePath, out failureReason))
            {
                return false;
            }

            if (!RevertOneChange(lastChange, absoluteFilePath, out failureReason))
            {
                return false;
            }

            RemoveLastJournalEntry();
            DeleteBackupFileOf(lastChange);

            undoneDisplayPath = lastChange.RelativePath;
            return true;
        }

        // The journal outlives a session and the workspace can change between sessions, so the
        // recorded path is re-resolved through the CURRENT sandbox rather than trusted as it stands.
        bool TryResolveJournalEntryPath(ChangeJournalEntry journalEntry, out string absoluteFilePath, out string failureReason)
        {
            absoluteFilePath = null;
            failureReason = null;

            bool wasMadeInAnotherWorkspace = !string.Equals(journalEntry.WorkspaceRootPath,
                _pathSandbox.WorkspaceRootPath, StringComparison.OrdinalIgnoreCase);

            if (wasMadeInAnotherWorkspace)
            {
                failureReason = $"the last change was made in another project folder ({journalEntry.WorkspaceRootPath}), so it cannot be undone from here.";
                return false;
            }

            if (!_pathSandbox.TryResolvePath(journalEntry.RelativePath, out absoluteFilePath, out string rejectionReason))
            {
                failureReason = $"{journalEntry.RelativePath} can no longer be reached: {rejectionReason}";
                return false;
            }

            return true;
        }

        bool RevertOneChange(ChangeJournalEntry journalEntry, string absoluteFilePath, out string failureReason)
        {
            failureReason = null;

            try
            {
                if (!journalEntry.DidFileExistBefore)
                {
                    if (File.Exists(absoluteFilePath))
                    {
                        File.Delete(absoluteFilePath);
                    }

                    return true;
                }

                string backupFilePath = Path.Combine(_backupFolderPath, journalEntry.BackupFileName);

                if (!File.Exists(backupFilePath))
                {
                    failureReason = $"the saved copy of {journalEntry.RelativePath} is gone, so it cannot be restored.";
                    return false;
                }

                byte[] previousContentBytes = File.ReadAllBytes(backupFilePath);

                if (!TryWriteBytesAtomically(absoluteFilePath, previousContentBytes, out string writeFailure))
                {
                    failureReason = $"{journalEntry.RelativePath} could not be restored: {writeFailure}";
                    return false;
                }

                return true;
            }
            catch (Exception exception)
            {
                failureReason = $"{journalEntry.RelativePath} could not be restored: {exception.Message}";
                return false;
            }
        }

        void DeleteBackupFileOf(ChangeJournalEntry journalEntry)
        {
            if (string.IsNullOrEmpty(journalEntry.BackupFileName))
            {
                return;
            }

            DeleteFileIfItStillExists(Path.Combine(_backupFolderPath, journalEntry.BackupFileName));
        }

        List<ChangeJournalEntry> ReadJournalEntries()
        {
            var journalEntries = new List<ChangeJournalEntry>();

            try
            {
                if (!File.Exists(_journalFilePath))
                {
                    return journalEntries;
                }

                foreach (string journalLine in File.ReadAllLines(_journalFilePath))
                {
                    if (TryParseJournalLine(journalLine, out var journalEntry))
                    {
                        journalEntries.Add(journalEntry);
                    }
                }
            }
            catch (Exception exception)
            {
                Debug.LogWarning($"[FileWriteService] The undo journal could not be read: {exception.Message}");
            }

            return journalEntries;
        }

        static bool TryParseJournalLine(string journalLine, out ChangeJournalEntry journalEntry)
        {
            journalEntry = null;

            if (string.IsNullOrWhiteSpace(journalLine))
            {
                return false;
            }

            string[] journalFields = journalLine.Split(new[] { k_journalFieldSeparator }, k_journalFieldCount);

            if (journalFields.Length != k_journalFieldCount)
            {
                Debug.LogWarning("[FileWriteService] A malformed undo journal line was skipped.");
                return false;
            }

            if (!int.TryParse(journalFields[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out int sequenceNumber))
            {
                return false;
            }

            if (!long.TryParse(journalFields[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out long timeStampTicks))
            {
                return false;
            }

            journalEntry = new ChangeJournalEntry(sequenceNumber, new DateTime(timeStampTicks, DateTimeKind.Utc),
                journalFields[4], journalFields[5], journalFields[2] == "1", journalFields[3]);

            return true;
        }

        void RemoveLastJournalEntry()
        {
            try
            {
                if (!File.Exists(_journalFilePath))
                {
                    return;
                }

                var journalLines = new List<string>(File.ReadAllLines(_journalFilePath));

                while (journalLines.Count > 0 && string.IsNullOrWhiteSpace(journalLines[journalLines.Count - 1]))
                {
                    journalLines.RemoveAt(journalLines.Count - 1);
                }

                if (journalLines.Count > 0)
                {
                    journalLines.RemoveAt(journalLines.Count - 1);
                }

                File.WriteAllText(_journalFilePath, journalLines.Count == 0 ? string.Empty : string.Join("\n", journalLines) + "\n");
            }
            catch (Exception exception)
            {
                Debug.LogWarning($"[FileWriteService] The undo journal could not be rewritten: {exception.Message}");
            }
        }
    }
}
