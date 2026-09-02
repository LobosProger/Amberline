using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using Cysharp.Threading.Tasks;
using UnityEngine;

namespace Amberline.Agent
{
    // Owns the transcript: the ordered list of messages that becomes the prompt every turn.
    // The pinned system block always occupies index 0 - it can only be written through
    // SetPinnedSystemBlockAsync, and no other method inserts before it or removes it, so the
    // system prompt can never drift into the middle of the conversation.
    // Token counts are measured once, when a message is added, because counting is a native call
    // that blocks the main thread and the text never changes after insertion. The counter itself
    // is injected, so this class stays free of any LLM dependency.
    // Collaborators: the prompt renderer reads Messages, the status bar reads GetContextUsage,
    // /clear calls Clear and /compact calls CompactOlderMessagesAsync with a summariser.
    public class ContextManager
    {
        readonly List<ChatMessage> _messages = new List<ChatMessage>();
        readonly ReadOnlyCollection<ChatMessage> _readOnlyViewOfMessages;
        readonly Func<string, UniTask<int>> _countTokensInTextAsync;

        // Rough fallback when no real tokenizer is supplied: English text averages about four
        // characters per token. Only good enough for a status bar, never for a hard budget.
        const int k_averageCharactersPerTokenForEstimate = 4;

        // Three tool round trips (assistant call + user result each) stay verbatim by default.
        const int k_defaultAmountOfRecentMessagesKeptWhenCompacting = 6;

        // Hard floor. The newest tool result and the assistant call that produced it must always
        // survive compaction - stubbing the last result made the model immediately re-read the
        // file it had just read, which then tripped the repeat detector.
        const int k_minimumAmountOfRecentMessagesKept = 2;

        // Replacing a single old message with a summary of it buys nothing.
        const int k_minimumAmountOfOlderMessagesWorthCompacting = 2;

        // Below this, the sentence that says an output was dropped can cost more than the output.
        const int k_defaultMinimumCharactersWorthDropping = 400;

        const string k_prefixOfCompactedSummaryMessage = "[Summary of the earlier conversation]\n";

        /// <summary>
        /// Creates the transcript. <paramref name="countTokensInTextAsync"/> is the model's own
        /// tokenizer, supplied by the caller so this class never touches the LLM package. Pass
        /// null to fall back to a cheap character-based estimate.
        /// </summary>
        public ContextManager(Func<string, UniTask<int>> countTokensInTextAsync = null)
        {
            _countTokensInTextAsync = countTokensInTextAsync;
            _readOnlyViewOfMessages = new ReadOnlyCollection<ChatMessage>(_messages);
        }

        /// <summary>
        /// The whole transcript in order, pinned system block first. Read-only on purpose: the
        /// prompt renderer must never be able to edit history it is only supposed to render.
        /// </summary>
        public IReadOnlyList<ChatMessage> Messages => _readOnlyViewOfMessages;

        /// <summary>True once a system prompt has been pinned at index 0.</summary>
        public bool HasPinnedSystemBlock => _messages.Count > 0 && _messages[0].Role == ChatRole.System;

        /// <summary>
        /// Installs or replaces the pinned system prompt at index 0. Empty text is ignored with a
        /// warning, so a failed prompt build cannot silently wipe the existing system block.
        /// </summary>
        public async UniTask SetPinnedSystemBlockAsync(string systemPromptText)
        {
            if (string.IsNullOrWhiteSpace(systemPromptText))
            {
                Debug.LogWarning("ContextManager: the pinned system block was ignored because its text is empty.");
                return;
            }

            var pinnedSystemMessage = await CreateMessageWithMeasuredTokenCountAsync(ChatRole.System, systemPromptText);

            if (HasPinnedSystemBlock)
            {
                _messages[0] = pinnedSystemMessage;
                return;
            }

            // Insert, never append. Appending would put the system prompt after the first
            // conversation turn, and the model reads it as an ordinary mid-chat message.
            _messages.Insert(0, pinnedSystemMessage);
        }

        /// <summary>
        /// Appends a user turn. Tool results come through here too - this agent has no separate
        /// tool role, they are fed back as user turns.
        /// </summary>
        /// <param name="shorterTextThatCanReplaceThis">A one-line stand-in this message may be cut
        /// down to when the window fills up, or null when it has to be kept whole.</param>
        public async UniTask AddUserMessageAsync(string text, string shorterTextThatCanReplaceThis = null)
        {
            await AddMessageToEndOfTranscriptAsync(ChatRole.User, text, shorterTextThatCanReplaceThis);
        }

        /// <summary>Appends an assistant turn, normally the text the model just generated.</summary>
        public async UniTask AddAssistantMessageAsync(string text)
        {
            await AddMessageToEndOfTranscriptAsync(ChatRole.Assistant, text);
        }

        /// <summary>Sum of the token counts measured when each message was added.</summary>
        public int GetTotalTokenCount()
        {
            int totalAmountOfTokens = 0;
            foreach (var message in _messages)
            {
                totalAmountOfTokens += message.TokenCount;
            }

            return totalAmountOfTokens;
        }

        /// <summary>
        /// How full the window is, for the status bar. The usable window comes from the caller
        /// because it is read off the LLM component, where 0 means "use the model default" - a
        /// non-positive max simply reports a fill ratio of zero.
        /// </summary>
        public ContextUsage GetContextUsage(int usableContextWindowInTokens)
        {
            int maxTokensToReport = Math.Max(0, usableContextWindowInTokens);
            return new ContextUsage(GetTotalTokenCount(), maxTokensToReport);
        }

        /// <summary>
        /// Backs /clear: drops the conversation but keeps the pinned system block, so the next
        /// message starts a fresh chat against the same system prompt.
        /// </summary>
        public void Clear()
        {
            int indexOfFirstConversationMessage = GetIndexOfFirstMessageAfterPinnedBlock();
            int amountOfConversationMessages = _messages.Count - indexOfFirstConversationMessage;
            if (amountOfConversationMessages <= 0)
            {
                return;
            }

            _messages.RemoveRange(indexOfFirstConversationMessage, amountOfConversationMessages);
        }

        /// <summary>
        /// Replaces the conversation with one read back from disk, leaving the pinned system block
        /// exactly as it is. Backs /resume.
        /// </summary>
        /// <remarks>
        /// The token counts come with the messages rather than being measured again. That is the
        /// same principle the transcript already runs on - counted once, at insertion - and
        /// re-measuring would mean one blocking native call per message on the main thread.
        /// </remarks>
        public void RestoreMessages(IReadOnlyList<ChatMessage> restoredMessages)
        {
            Clear();

            if (restoredMessages == null) return;

            foreach (var message in restoredMessages)
            {
                if (message == null || message.Role == ChatRole.System) continue;
                if (string.IsNullOrWhiteSpace(message.Text)) continue;

                _messages.Add(message);
            }
        }

        /// <summary>
        /// Cuts the older tool outputs down to their one-line stand-ins and returns how many tokens
        /// that freed. The cheap half of making room: nothing is summarised, nothing is reworded,
        /// and the model can call the tool again if it turns out to still need what was dropped.
        /// </summary>
        /// <remarks>
        /// Only messages that carry a stand-in are touched, which today means the output of a
        /// read-only tool. A message shorter than
        /// <paramref name="minimumCharactersWorthDropping"/> is left alone, because replacing a
        /// short result with a sentence about a short result can cost more than it saves.
        /// <para>
        /// The newest messages are never touched, and that limit is not cosmetic. Stubbing the last
        /// result was measured making the model immediately re-read the file it had just read,
        /// which then tripped the repeat detector - see the note on
        /// <see cref="k_minimumAmountOfRecentMessagesKept"/>. The caller has a second obligation
        /// for the same reason: after this returns a non-zero count, the run's memory of which
        /// calls it has already made has to be reset, or the model is refused the one move the
        /// stand-in just told it to make.
        /// </para>
        /// </remarks>
        public async UniTask<int> ReplaceOlderToolOutputsWithTheirShorterTextAsync(
            int amountOfRecentMessagesToKeep = k_defaultAmountOfRecentMessagesKeptWhenCompacting,
            int minimumCharactersWorthDropping = k_defaultMinimumCharactersWorthDropping)
        {
            int amountOfMessagesKeptAtTail = Math.Max(k_minimumAmountOfRecentMessagesKept, amountOfRecentMessagesToKeep);
            int indexOfFirstOlderMessage = GetIndexOfFirstMessageAfterPinnedBlock();
            int indexAfterLastOlderMessage = _messages.Count - amountOfMessagesKeptAtTail;

            int amountOfTokensFreed = 0;

            for (int messageIndex = indexOfFirstOlderMessage; messageIndex < indexAfterLastOlderMessage; messageIndex++)
            {
                amountOfTokensFreed += await ReplaceOneMessageWithItsShorterTextAsync(messageIndex, minimumCharactersWorthDropping);
            }

            return amountOfTokensFreed;
        }

        // Returns the tokens this one message gave back, and zero whenever it was left as it was.
        // A message is rebuilt rather than edited: ChatMessage is immutable, and the replacement
        // has to be re-measured anyway.
        async UniTask<int> ReplaceOneMessageWithItsShorterTextAsync(int messageIndex, int minimumCharactersWorthDropping)
        {
            var messageBeingTrimmed = _messages[messageIndex];

            if (!messageBeingTrimmed.CanBeTrimmed)
            {
                return 0;
            }

            if (messageBeingTrimmed.Text.Length < minimumCharactersWorthDropping)
            {
                return 0;
            }

            // The stand-in must never be empty. An empty turn is dropped entirely at render time,
            // which would merge the turns on either side of it into one and move every byte after
            // this point - a far larger change than the one being made here.
            if (string.IsNullOrWhiteSpace(messageBeingTrimmed.ShorterTextThatCanReplaceThis))
            {
                Debug.LogWarning("ContextManager: a message carried an empty stand-in, so it was left whole.");
                return 0;
            }

            var trimmedMessage = await CreateMessageWithMeasuredTokenCountAsync(
                messageBeingTrimmed.Role,
                messageBeingTrimmed.ShorterTextThatCanReplaceThis,
                messageBeingTrimmed.ShorterTextThatCanReplaceThis);

            _messages[messageIndex] = trimmedMessage;

            return Math.Max(0, messageBeingTrimmed.TokenCount - trimmedMessage.TokenCount);
        }

        /// <summary>
        /// Backs /compact: replaces the older part of the transcript with one summary message.
        /// The summary is produced by <paramref name="summariseOlderMessagesAsync"/> - this class
        /// never calls a model itself. The newest <paramref name="amountOfRecentMessagesToKeep"/>
        /// messages are always left verbatim. Returns true only if the transcript was rewritten;
        /// every failure path leaves it exactly as it was.
        /// </summary>
        public async UniTask<bool> CompactOlderMessagesAsync(
            Func<IReadOnlyList<ChatMessage>, UniTask<string>> summariseOlderMessagesAsync,
            int amountOfRecentMessagesToKeep = k_defaultAmountOfRecentMessagesKeptWhenCompacting)
        {
            if (summariseOlderMessagesAsync == null)
            {
                Debug.LogWarning("ContextManager: compaction skipped because no summariser was supplied.");
                return false;
            }

            int amountOfMessagesKeptAtTail = Math.Max(k_minimumAmountOfRecentMessagesKept, amountOfRecentMessagesToKeep);
            int indexOfFirstOlderMessage = GetIndexOfFirstMessageAfterPinnedBlock();
            int amountOfOlderMessages = _messages.Count - amountOfMessagesKeptAtTail - indexOfFirstOlderMessage;

            if (amountOfOlderMessages < k_minimumAmountOfOlderMessagesWorthCompacting)
            {
                Debug.LogWarning("ContextManager: compaction skipped because the transcript is still short.");
                return false;
            }

            // A copy, so the summariser cannot reach back into the live transcript.
            var olderMessagesToSummarise = _messages.GetRange(indexOfFirstOlderMessage, amountOfOlderMessages);
            int amountOfMessagesBeforeSummarising = _messages.Count;

            string summaryText = await summariseOlderMessagesAsync(olderMessagesToSummarise);

            // Abort untouched. Dropping the history and putting nothing in its place is strictly
            // worse than not compacting at all.
            if (string.IsNullOrWhiteSpace(summaryText))
            {
                Debug.LogWarning("ContextManager: compaction aborted because the summary came back empty.");
                return false;
            }

            var summaryMessage = await CreateMessageWithMeasuredTokenCountAsync(
                ChatRole.User, k_prefixOfCompactedSummaryMessage + summaryText);

            // Summarising is async, so a turn may have appended while we waited. The indexes
            // measured above would then point at different messages - abort instead of guessing.
            if (_messages.Count != amountOfMessagesBeforeSummarising)
            {
                Debug.LogWarning("ContextManager: compaction aborted because the transcript changed while summarising.");
                return false;
            }

            _messages.RemoveRange(indexOfFirstOlderMessage, amountOfOlderMessages);
            _messages.Insert(indexOfFirstOlderMessage, summaryMessage);
            return true;
        }

        async UniTask<ChatMessage> CreateMessageWithMeasuredTokenCountAsync(ChatRole role, string text,
            string shorterTextThatCanReplaceThis = null)
        {
            int amountOfTokensInText = await CountTokensInTextAsync(text);
            return new ChatMessage(role, text, amountOfTokensInText, shorterTextThatCanReplaceThis);
        }

        // A zero for text that is plainly not empty means the tokenizer refused - it declines while
        // a generation holds the single slot, and it swallows its own errors and answers zero. Left
        // as it comes back, that zero would be stored as this message's real cost, and enough of
        // them would hold the running total below the threshold that triggers compaction, so the
        // window would fill up for real with nothing ever reporting that it was close.
        async UniTask<int> CountTokensInTextAsync(string text)
        {
            if (string.IsNullOrEmpty(text))
            {
                return 0;
            }

            if (_countTokensInTextAsync == null)
            {
                return EstimateTokenCountFromCharacterLength(text);
            }

            int measuredTokenCount = await _countTokensInTextAsync(text);
            if (measuredTokenCount > 0)
            {
                return measuredTokenCount;
            }

            Debug.LogWarning("ContextManager: the tokenizer reported zero tokens for a message that has text, so the length estimate was used instead.");
            return EstimateTokenCountFromCharacterLength(text);
        }

        int EstimateTokenCountFromCharacterLength(string text)
        {
            return Math.Max(1, text.Length / k_averageCharactersPerTokenForEstimate);
        }

        async UniTask AddMessageToEndOfTranscriptAsync(ChatRole role, string text,
            string shorterTextThatCanReplaceThis = null)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                Debug.LogWarning($"ContextManager: an empty {role} message was not added to the transcript.");
                return;
            }

            var messageWithMeasuredTokens =
                await CreateMessageWithMeasuredTokenCountAsync(role, text, shorterTextThatCanReplaceThis);

            _messages.Add(messageWithMeasuredTokens);
        }

        int GetIndexOfFirstMessageAfterPinnedBlock()
        {
            return HasPinnedSystemBlock ? 1 : 0;
        }
    }
}
