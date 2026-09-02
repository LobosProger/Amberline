using System.Collections.Generic;
using System.Text;

namespace Amberline.Agent
{
    // The ChatML envelope, and the turn merging every envelope needs.
    //
    // ChatML is no longer what amberline renders by default - see ChatTemplateRenderer, which asks
    // the loaded model for its own template and only falls back to this class when there is none
    // to ask. It stays because that fallback has to exist: a GGUF with no chat template in its
    // metadata still has to be talked to somehow, and ChatML is the format the largest share of
    // open instruct models understand.
    //
    // We have to render at all because LLM for Unity v3.0.1 deleted its whole C# ChatTemplate
    // hierarchy and moved templating into the native LlamaLib, so LLMClient.Completion(prompt) is
    // raw: the string we pass reaches the model verbatim, with no template, no BOS handling and no
    // history.
    //
    // LOAD-BEARING PROPERTY - do not break it:
    // rendering is a pure function of (messages, assistantPrefill), and the prefill is the very
    // last thing written. So Render(messages, "A") is always a byte-exact PREFIX of
    // Render(messages, "AB"). The ACT prompt is built as the THINK prompt plus more prefill, and
    // that is the only reason llama.cpp's cachePrompt can reuse the KV cache for the second pass of
    // a turn. Never rewrite an earlier turn when new messages are appended - that would move the
    // divergence point back to the rewritten turn and throw the whole cache away.
    public static class ChatMlPromptRenderer
    {
        public const string k_turnStartTag = "<|im_start|>";
        public const string k_turnEndTag = "<|im_end|>";

        const string k_systemRoleName = "system";
        const string k_userRoleName = "user";
        const string k_assistantRoleName = "assistant";

        /// <summary>
        /// Renders the ordered transcript as ChatML turns and then opens an assistant turn, so the
        /// model can only continue as the assistant. The prefill is appended raw after the opening
        /// of the assistant turn and may be empty.
        /// </summary>
        public static string RenderConversationToChatMl(IReadOnlyList<ChatMessage> messages, string assistantPrefill)
        {
            string renderedConversation = RenderMergedConversationToChatMl(MergeConsecutiveTurnsWithTheSameRole(messages));

            return string.IsNullOrEmpty(assistantPrefill)
                ? renderedConversation
                : renderedConversation + assistantPrefill;
        }

        /// <summary>
        /// The same thing for messages that have already been merged, so the caller does not pay
        /// for merging twice when it needed the merged list for something else.
        /// </summary>
        public static string RenderMergedConversationToChatMl(IReadOnlyList<ChatMessage> mergedMessages)
        {
            var renderedPrompt = new StringBuilder();

            if (mergedMessages != null)
            {
                foreach (var message in mergedMessages)
                {
                    renderedPrompt.Append(k_turnStartTag).Append(GetRoleName(message.Role)).Append('\n');
                    renderedPrompt.Append(message.Text);
                    renderedPrompt.Append(k_turnEndTag).Append('\n');
                }
            }

            renderedPrompt.Append(k_turnStartTag).Append(k_assistantRoleName).Append('\n');
            return renderedPrompt.ToString();
        }

        /// <summary>
        /// Folds runs of same-role messages into one turn each and drops empty ones. Two reasons,
        /// and both matter: a small model follows one long user turn better than six short ones,
        /// and several chat templates - Mistral's among them - are only defined for strictly
        /// alternating user and assistant turns, so a pair of consecutive user messages renders
        /// wrong or throws.
        /// <para>
        /// The token counts of the merged messages are meaningless and are set to zero. Nothing
        /// downstream of rendering reads them; the transcript keeps the measured ones.
        /// </para>
        /// </summary>
        public static IReadOnlyList<ChatMessage> MergeConsecutiveTurnsWithTheSameRole(IReadOnlyList<ChatMessage> messages)
        {
            var mergedMessages = new List<ChatMessage>();
            if (messages == null)
            {
                return mergedMessages;
            }

            var textOfCurrentTurn = new StringBuilder();
            ChatRole roleOfCurrentTurn = ChatRole.System;

            foreach (var message in messages)
            {
                // An empty turn teaches the model nothing and still costs the envelope tokens.
                if (message == null || string.IsNullOrEmpty(message.Text))
                {
                    continue;
                }

                bool isStartOfADifferentRole = textOfCurrentTurn.Length > 0 && message.Role != roleOfCurrentTurn;
                if (isStartOfADifferentRole)
                {
                    mergedMessages.Add(new ChatMessage(roleOfCurrentTurn, textOfCurrentTurn.ToString(), 0));
                    textOfCurrentTurn.Clear();
                }

                roleOfCurrentTurn = message.Role;

                if (textOfCurrentTurn.Length > 0)
                {
                    textOfCurrentTurn.Append('\n');
                }

                textOfCurrentTurn.Append(message.Text);
            }

            if (textOfCurrentTurn.Length > 0)
            {
                mergedMessages.Add(new ChatMessage(roleOfCurrentTurn, textOfCurrentTurn.ToString(), 0));
            }

            return mergedMessages;
        }

        /// <summary>The wire name of a role. Public because the gateway needs the same spelling
        /// when it hands the messages to the model's own template.</summary>
        public static string GetRoleName(ChatRole role)
        {
            if (role == ChatRole.System)
            {
                return k_systemRoleName;
            }

            if (role == ChatRole.User)
            {
                return k_userRoleName;
            }

            return k_assistantRoleName;
        }
    }
}
