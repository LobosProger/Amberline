using System.Collections.Generic;
using System.Text;

namespace Amberline.Agent
{
    // Renders our transcript into the ChatML envelope the Qwen family is trained on.
    //
    // We have to do this ourselves. LLM for Unity v3.0.1 deleted its whole C# ChatTemplate
    // hierarchy and moved templating into the native LlamaLib, so LLMClient.Completion(prompt) is
    // raw: the string we pass reaches the model verbatim, with no template, no BOS handling and no
    // history. Without this class Qwen3 behaves as a plain text continuer and narrates its own
    // reasoning instead of answering.
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
        /// model can only continue as the assistant. Consecutive messages with the same role are
        /// merged into one turn: a small model follows one long user turn better than six short
        /// ones, and merging saves an envelope's worth of tokens per merged message.
        /// The prefill is appended raw after the opening of the assistant turn and may be empty.
        /// </summary>
        public static string RenderConversationToChatMl(IReadOnlyList<ChatMessage> messages, string assistantPrefill)
        {
            var renderedPrompt = new StringBuilder();
            AppendAllTurnsWithSameRoleMerged(renderedPrompt, messages);
            AppendOpeningOfAssistantTurn(renderedPrompt, assistantPrefill);
            return renderedPrompt.ToString();
        }

        static void AppendAllTurnsWithSameRoleMerged(StringBuilder renderedPrompt, IReadOnlyList<ChatMessage> messages)
        {
            if (messages == null)
            {
                return;
            }

            var textOfCurrentTurn = new StringBuilder();
            ChatRole roleOfCurrentTurn = ChatRole.System;

            for (int messageIndex = 0; messageIndex < messages.Count; messageIndex++)
            {
                ChatMessage message = messages[messageIndex];

                // An empty turn teaches the model nothing and still costs the envelope tokens.
                if (message == null || string.IsNullOrEmpty(message.Text))
                {
                    continue;
                }

                bool isStartOfADifferentRole = textOfCurrentTurn.Length > 0 && message.Role != roleOfCurrentTurn;
                if (isStartOfADifferentRole)
                {
                    AppendOneTurn(renderedPrompt, roleOfCurrentTurn, textOfCurrentTurn);
                    textOfCurrentTurn.Clear();
                }

                roleOfCurrentTurn = message.Role;

                if (textOfCurrentTurn.Length > 0)
                {
                    textOfCurrentTurn.Append('\n');
                }

                textOfCurrentTurn.Append(message.Text);
            }

            AppendOneTurn(renderedPrompt, roleOfCurrentTurn, textOfCurrentTurn);
        }

        static void AppendOneTurn(StringBuilder renderedPrompt, ChatRole role, StringBuilder textOfTurn)
        {
            if (textOfTurn.Length == 0)
            {
                return;
            }

            renderedPrompt.Append(k_turnStartTag).Append(GetRoleName(role)).Append('\n');
            renderedPrompt.Append(textOfTurn);
            renderedPrompt.Append(k_turnEndTag).Append('\n');
        }

        static string GetRoleName(ChatRole role)
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

        static void AppendOpeningOfAssistantTurn(StringBuilder renderedPrompt, string assistantPrefill)
        {
            renderedPrompt.Append(k_turnStartTag).Append(k_assistantRoleName).Append('\n');

            if (!string.IsNullOrEmpty(assistantPrefill))
            {
                renderedPrompt.Append(assistantPrefill);
            }
        }
    }
}
