using System.Collections.Generic;
using System.Text;

namespace Amberline.Agent
{
    // Builds the three prompts an agent turn needs - THINK, ACT and SUMMARISE - and owns the
    // format reminder that is appended to the tail of every tool result.
    //
    // Two rules hold this class together:
    //
    // 1. The transcript is strictly APPEND-ONLY. The format reminder is folded into the tail of
    //    the tool-result text at the moment that result is created, instead of being injected as
    //    a floating reminder turn just before the assistant prefill. Both placements give the
    //    model the same recency benefit, but a floating reminder has to be removed and re-added
    //    every turn, which breaks llama.cpp's KV prefix match exactly where the old reminder used
    //    to sit. Folding it in means nothing already in the transcript is ever rewritten.
    //
    // 2. The ACT prompt is the THINK prompt plus MORE ASSISTANT PREFILL - never a different
    //    transcript and never a rewritten turn. See ChatMlPromptRenderer for why that is what
    //    keeps the prompt cache alive across the two passes of one turn.
    public static class PromptBuilder
    {
        /// <summary>Opens the assistant turn for the THINK pass, so the model writes a short plan
        /// before it writes JSON. The ACT prefill always starts with this exact text.
        /// <para>
        /// It leads with an ALREADY CLOSED think block. Reasoning parsing is off in the LLM
        /// component, so a &lt;think&gt; block Qwen3 emits lands verbatim in the completion text;
        /// closing one for it up front makes it start on the plan instead. Measured in M2: without
        /// the block the completion began with a think tag and "Okay, the user is asking".
        /// </para></summary>
        public const string k_thoughtPrefill = "<think>\n\n</think>\n\nThought:";

        /// <summary>Default opening for the ACT pass. The grammar builder may supply its own
        /// prefill instead, and the two must be produced together: llama.cpp starts grammar
        /// sampling at the first GENERATED token, so text already sitting in the prefill is never
        /// checked against the grammar and must not be emitted a second time.</summary>
        public const string k_toolCallPrefill = "<tool_call>";

        const string k_toolResponseOpenTag = "<tool_response>";
        const string k_toolResponseCloseTag = "</tool_response>";

        // Byte-identical on every single use. If this ever varied per turn, each tool result would
        // push a different string into the transcript and the prompt cache would miss right there.
        // It restates the SHAPE and the one-call-per-turn rule only. It deliberately says nothing
        // about text before or after the tag: the system prompt owns that rule, and repeating
        // "no other text" here would contradict the THINK pass, which asks for a thought first.
        const string k_formatReminder =
            "[format] Next, exactly one <tool_call>{\"name\": ..., \"arguments\": {...}}</tool_call>, or call finish.";

        // Qwen3 reasoning parsing is off in the LLM component, so any <think> block the model emits
        // is not stripped out - it lands inline in the completion text. This marker suppresses it.
        //
        // The previous agent put "/no_think" at the end of its SYSTEM prompt, which was very likely
        // a no-op: Qwen3 was trained to look for the marker in the LAST USER message, not in the
        // system message. So we append it to every user-role turn we create instead. Appending it
        // at creation time (rather than patching the newest user turn at render time) keeps the
        // transcript append-only, which rule 1 above depends on. The price is that the marker
        // repeats inside a merged user turn - a few tokens, paid to never rewrite a turn.
        const string k_noThinkMarker = "/no_think";

        const string k_summaryRequest =
            "Summarise everything above so the work can continue without it. Use exactly these sections, one short line each:\n" +
            "TASK:\nFILES TOUCHED:\nDECISIONS:\nCURRENT STEP:\nNEXT STEP:\n" +
            "Write no tool call.";

        /// <summary>Wraps what the user typed into the user-turn text that enters the transcript.</summary>
        public static string BuildTaskMessageText(string userTaskText)
        {
            return $"{userTaskText}\n{k_noThinkMarker}";
        }

        /// <summary>THINK pass: let the model reason in plain text for a few tokens, unconstrained.</summary>
        public static string BuildThinkPrompt(IReadOnlyList<ChatMessage> transcript)
        {
            return ChatMlPromptRenderer.RenderConversationToChatMl(transcript, k_thoughtPrefill);
        }

        /// <summary>
        /// ACT pass: the same transcript as the THINK pass, with the thought the model just wrote
        /// folded into the prefill and the tool-call opening appended. Pass the previous parse
        /// error when retrying a turn, and null otherwise. Pass the grammar's own prefill when
        /// there is one, and null to use the default.
        /// </summary>
        public static string BuildActPrompt(IReadOnlyList<ChatMessage> transcript, string thoughtText,
            string previousParseError, string toolCallPrefill)
        {
            string actPrefill = BuildActPrefill(thoughtText, previousParseError, toolCallPrefill);
            return ChatMlPromptRenderer.RenderConversationToChatMl(transcript, actPrefill);
        }

        /// <summary>
        /// Just the assistant prefill the ACT pass runs on, without the transcript in front of it.
        /// The loop needs it on its own: the assistant turn it stores has to be prefill + generated
        /// text, or the next prompt would not extend this one and the KV cache would be thrown away
        /// at that turn. Same arguments as <see cref="BuildActPrompt"/>, so the two cannot drift.
        /// </summary>
        public static string BuildActPrefill(string thoughtText, string previousParseError, string toolCallPrefill)
        {
            return BuildActPrefillThatExtendsThinkPrefill(thoughtText, previousParseError, toolCallPrefill);
        }

        // The returned string ALWAYS starts with the exact prefill the THINK pass used, which is
        // what makes the ACT prompt a byte-exact prefix extension of the THINK prompt. Anything
        // added here must be APPENDED - inserting in front of k_thoughtPrefill silently costs a
        // full prompt re-read on every turn.
        static string BuildActPrefillThatExtendsThinkPrefill(string thoughtText, string previousParseError,
            string toolCallPrefill)
        {
            var actPrefill = new StringBuilder(k_thoughtPrefill);

            if (!string.IsNullOrEmpty(thoughtText))
            {
                actPrefill.Append(thoughtText.TrimEnd());
            }

            if (!string.IsNullOrEmpty(previousParseError))
            {
                // Written in the assistant's own voice, because the model continues this text
                // directly - a second-person scolding here would read as someone else talking.
                actPrefill.Append("\nMy last tool call was not valid: ").Append(previousParseError);
                actPrefill.Append(". I will write exactly one correct tool call now.");
            }

            actPrefill.Append('\n');
            actPrefill.Append(string.IsNullOrEmpty(toolCallPrefill) ? k_toolCallPrefill : toolCallPrefill);
            return actPrefill.ToString();
        }

        /// <summary>
        /// Wraps one tool result into the user-turn text that enters the transcript, with the
        /// format reminder on its tail. Results come back as user turns rather than a tool role,
        /// because we render ChatML ourselves and an arbitrary GGUF template may not know a tool
        /// role at all. Truncate the output before calling this - it goes into history as is.
        /// </summary>
        public static string BuildToolResultMessageText(string toolOutputText)
        {
            return $"{k_toolResponseOpenTag}\n{toolOutputText}\n{k_toolResponseCloseTag}\n{k_formatReminder}\n{k_noThinkMarker}";
        }

        /// <summary>
        /// SUMMARISE pass, used by /compact. This one is a throwaway generation rather than part of
        /// the turn chain, so it may append a request turn that never enters the transcript.
        /// </summary>
        public static string BuildSummarisePrompt(IReadOnlyList<ChatMessage> transcript)
        {
            var transcriptWithSummaryRequest = new List<ChatMessage>(transcript);
            string summaryRequestText = $"{k_summaryRequest}\n{k_noThinkMarker}";

            // Token count zero: this message is never stored, so nothing ever reads the count.
            transcriptWithSummaryRequest.Add(new ChatMessage(ChatRole.User, summaryRequestText, 0));

            return ChatMlPromptRenderer.RenderConversationToChatMl(transcriptWithSummaryRequest, string.Empty);
        }
    }
}
