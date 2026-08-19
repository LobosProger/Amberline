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
        public async UniTask AddUserMessageAsync(string text)
        {
            await AddMessageToEndOfTranscriptAsync(ChatRole.User, text);
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

        async UniTask<ChatMessage> CreateMessageWithMeasuredTokenCountAsync(ChatRole role, string text)
        {
            int amountOfTokensInText = await CountTokensInTextAsync(text);
            return new ChatMessage(role, text, amountOfTokensInText);
        }

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

            return await _countTokensInTextAsync(text);
        }

        int EstimateTokenCountFromCharacterLength(string text)
        {
            return Math.Max(1, text.Length / k_averageCharactersPerTokenForEstimate);
        }

        async UniTask AddMessageToEndOfTranscriptAsync(ChatRole role, string text)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                Debug.LogWarning($"ContextManager: an empty {role} message was not added to the transcript.");
                return;
            }

            var messageWithMeasuredTokens = await CreateMessageWithMeasuredTokenCountAsync(role, text);
            _messages.Add(messageWithMeasuredTokens);
        }

        int GetIndexOfFirstMessageAfterPinnedBlock()
        {
            return HasPinnedSystemBlock ? 1 : 0;
        }
    }
}
