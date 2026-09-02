using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Newtonsoft.Json;
using UnityEngine;

namespace Amberline.Agent
{
    /// <summary>
    /// Keeps the transcript on disk between sessions, one file per workspace folder, so exiting
    /// Play mode stops throwing away work that took minutes to build up.
    /// </summary>
    /// <remarks>
    /// Written after every run and read back only when the user asks for it with /resume. Nothing
    /// is restored automatically: an agent that silently remembers a conversation the user has
    /// forgotten is worse than one that starts clean.
    /// <para>
    /// The pinned system block is deliberately NOT saved. It is rebuilt at startup from the system
    /// prompt and a fresh listing of the workspace, and the folder will have moved on since - so a
    /// saved copy would describe a project that no longer looks like that.
    /// </para>
    /// <para>
    /// Lives beside the undo journal, under <c>Application.persistentDataPath</c>, which means this
    /// class must be constructed on the main thread.
    /// </para>
    /// </remarks>
    public class SessionStore
    {
        readonly string _sessionsFolderPath;

        const string k_sessionFileExtension = ".json";
        const int k_maximumFolderNameCharactersInAFileName = 40;

        // The two magic numbers of FNV-1a, 64 bit. The hash is what keeps two folders with the same
        // name - every "src" on the machine - from overwriting each other's session.
        const ulong k_fnvOffsetBasis = 14695981039346656037UL;
        const ulong k_fnvPrime = 1099511628211UL;

        /// <param name="sessionsFolderPath">Overrides where sessions are kept. For tests only -
        /// left null it sits beside the undo journal under the persistent data path.</param>
        public SessionStore(string sessionsFolderPath = null)
        {
            _sessionsFolderPath = string.IsNullOrEmpty(sessionsFolderPath)
                ? BuildDefaultSessionsFolderPath()
                : sessionsFolderPath;
        }

        static string BuildDefaultSessionsFolderPath()
        {
            return Path.Combine(Application.persistentDataPath, "amberline", "sessions");
        }

        /// <summary>Where the session files are kept. Shown by /resume when there is nothing to load.</summary>
        public string SessionsFolderPath => _sessionsFolderPath;

        /// <summary>
        /// Writes the conversation for this workspace, replacing whatever was there. Failures are
        /// logged and swallowed: not being able to save is never a reason to end a run badly.
        /// </summary>
        public void Save(string workspaceFolderPath, IReadOnlyList<ChatMessage> messages)
        {
            if (string.IsNullOrEmpty(workspaceFolderPath)) return;

            var messagesWorthSaving = TakeMessagesWorthSaving(messages);

            if (messagesWorthSaving.Count == 0)
            {
                Delete(workspaceFolderPath);
                return;
            }

            var savedSession = new SavedSession
            {
                WorkspaceFolderPath = workspaceFolderPath,
                SavedAtUtc = DateTime.UtcNow.ToString("o"),
                Messages = messagesWorthSaving
            };

            try
            {
                Directory.CreateDirectory(_sessionsFolderPath);
                File.WriteAllText(BuildSessionFilePath(workspaceFolderPath),
                    JsonConvert.SerializeObject(savedSession, Formatting.Indented), new UTF8Encoding(false));
            }
            catch (Exception exception)
            {
                Debug.LogWarning($"[SessionStore] The session could not be saved: {exception.GetType().Name}: {exception.Message}");
            }
        }

        // The pinned block is dropped, and so is a trailing tool call that never got an answer.
        //
        // A turn writes the assistant's call into the transcript BEFORE the tool runs, so a run
        // stopped on an approval card or on an open question leaves a call with nothing after it.
        // Restored as-is, the model would open the next session looking at its own unanswered call
        // and would most likely just make it again. A trailing `finish` is the one exception: that
        // call never gets a response by design, and it is the normal way a run ends.
        static List<SavedMessage> TakeMessagesWorthSaving(IReadOnlyList<ChatMessage> messages)
        {
            var savedMessages = new List<SavedMessage>();

            if (messages == null) return savedMessages;

            foreach (var message in messages)
            {
                if (message == null || message.Role == ChatRole.System) continue;

                savedMessages.Add(SavedMessage.From(message));
            }

            RemoveTrailingToolCallThatWasNeverAnswered(savedMessages);

            return savedMessages;
        }

        static void RemoveTrailingToolCallThatWasNeverAnswered(List<SavedMessage> savedMessages)
        {
            if (savedMessages.Count == 0) return;

            var lastMessage = savedMessages[savedMessages.Count - 1];
            if (lastMessage.Role != ChatRole.Assistant.ToString()) return;

            var parseResult = ToolCallParser.Parse(lastMessage.Text);
            if (!parseResult.IsToolCall || parseResult.Call == null) return;

            if (parseResult.Call.ToolName == ToolRegistry.k_finishToolName) return;

            savedMessages.RemoveAt(savedMessages.Count - 1);
        }

        /// <summary>
        /// Reads back the conversation saved for this workspace. Returns false with a sentence for
        /// the user - never for the model - when there is nothing to read or it cannot be read.
        /// </summary>
        public bool TryLoad(string workspaceFolderPath, out List<ChatMessage> messages, out string failureReason)
        {
            messages = new List<ChatMessage>();
            failureReason = null;

            if (string.IsNullOrEmpty(workspaceFolderPath))
            {
                failureReason = "there is no workspace folder, so there is no session to resume.";
                return false;
            }

            string sessionFilePath = BuildSessionFilePath(workspaceFolderPath);

            if (!File.Exists(sessionFilePath))
            {
                failureReason = "nothing was saved for this folder yet.";
                return false;
            }

            SavedSession savedSession;

            try
            {
                savedSession = JsonConvert.DeserializeObject<SavedSession>(File.ReadAllText(sessionFilePath));
            }
            catch (Exception exception)
            {
                failureReason = $"the saved session could not be read: {exception.Message}";
                return false;
            }

            if (savedSession?.Messages == null || savedSession.Messages.Count == 0)
            {
                failureReason = "the saved session is empty.";
                return false;
            }

            messages = RebuildMessages(savedSession.Messages);

            if (messages.Count == 0)
            {
                failureReason = "the saved session held nothing that could be restored.";
                return false;
            }

            return true;
        }

        // The saved token counts are taken as they are, rather than measured again. It is the same
        // principle the transcript already runs on - counted once, at insertion - and re-measuring
        // would mean one blocking native call per message on the main thread. Load a session under
        // a different model and the numbers are approximate; the status bar can live with that.
        static List<ChatMessage> RebuildMessages(List<SavedMessage> savedMessages)
        {
            var messages = new List<ChatMessage>();

            foreach (var savedMessage in savedMessages)
            {
                if (savedMessage == null || string.IsNullOrEmpty(savedMessage.Text)) continue;
                if (!Enum.TryParse(savedMessage.Role, out ChatRole role)) continue;
                if (role == ChatRole.System) continue;

                messages.Add(new ChatMessage(role, savedMessage.Text, savedMessage.TokenCount,
                    savedMessage.ShorterTextThatCanReplaceThis));
            }

            return messages;
        }

        /// <summary>Forgets the session for this workspace. Backs /clear, which starts over.</summary>
        public void Delete(string workspaceFolderPath)
        {
            if (string.IsNullOrEmpty(workspaceFolderPath)) return;

            try
            {
                string sessionFilePath = BuildSessionFilePath(workspaceFolderPath);

                if (File.Exists(sessionFilePath))
                    File.Delete(sessionFilePath);
            }
            catch (Exception exception)
            {
                Debug.LogWarning($"[SessionStore] The saved session could not be deleted: {exception.GetType().Name}: {exception.Message}");
            }
        }

        // The folder's own name, so the file is recognisable to a person looking in the folder, plus
        // a hash of the whole path, so two projects that both end in "src" stay separate.
        string BuildSessionFilePath(string workspaceFolderPath)
        {
            string readableName = BuildReadableNameOfFolder(workspaceFolderPath);
            ulong pathHash = HashOfPath(workspaceFolderPath);

            return Path.Combine(_sessionsFolderPath, $"{readableName}-{pathHash:x16}{k_sessionFileExtension}");
        }

        static string BuildReadableNameOfFolder(string workspaceFolderPath)
        {
            string folderName;

            try
            {
                folderName = Path.GetFileName(workspaceFolderPath.TrimEnd(
                    Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            }
            catch (Exception)
            {
                folderName = string.Empty;
            }

            var readableNameBuilder = new StringBuilder();

            foreach (char character in folderName)
            {
                if (readableNameBuilder.Length >= k_maximumFolderNameCharactersInAFileName) break;

                readableNameBuilder.Append(char.IsLetterOrDigit(character) ? character : '-');
            }

            return readableNameBuilder.Length == 0 ? "workspace" : readableNameBuilder.ToString();
        }

        // Case-insensitive, because Windows paths are: the same folder reached as C:\Proj and
        // c:\proj has to be the same session.
        static ulong HashOfPath(string workspaceFolderPath)
        {
            ulong hashValue = k_fnvOffsetBasis;

            foreach (char character in workspaceFolderPath.ToLowerInvariant())
            {
                hashValue = (hashValue ^ character) * k_fnvPrime;
            }

            return hashValue;
        }

        // Plain field-bearing types, because Newtonsoft writes and reads them without any attribute
        // ceremony and the file is meant to stay readable by a person.
        class SavedSession
        {
            public string WorkspaceFolderPath;
            public string SavedAtUtc;
            public List<SavedMessage> Messages;
        }

        class SavedMessage
        {
            public string Role;
            public string Text;
            public int TokenCount;
            public string ShorterTextThatCanReplaceThis;

            public static SavedMessage From(ChatMessage message)
            {
                return new SavedMessage
                {
                    Role = message.Role.ToString(),
                    Text = message.Text,
                    TokenCount = message.TokenCount,
                    ShorterTextThatCanReplaceThis = message.ShorterTextThatCanReplaceThis
                };
            }
        }
    }
}
