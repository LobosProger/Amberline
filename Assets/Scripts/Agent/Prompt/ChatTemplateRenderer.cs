using System;
using System.Collections.Generic;
using UnityEngine;

namespace Amberline.Agent
{
    // Renders the transcript the way the LOADED MODEL was trained to read it, instead of the way
    // one model family happens to spell it.
    //
    // This class exists because of a measured failure, not a worry. amberline used to render
    // ChatML for every model. Loading mistral-7b-instruct-v0.2 and asking the native layer what
    // that model's own template looks like answers "[INST] system\nuser [/INST]" - not one token
    // of ChatML in it. A model handed an envelope it has never seen does not refuse: it keeps
    // writing prose, never emits <tool_call>, and never produces the <|im_end|> we were waiting
    // for, so every pass ran to its full token budget and the terminal sat there for minutes with
    // nothing to show for it. The same model answers perfectly in the package's own ChatBot
    // sample - because that path applies the template the GGUF shipped with.
    //
    // So the envelope is ASKED FOR, never assumed. llama.cpp holds the template baked into the
    // model file and will apply it for us; ChatML is the fallback for when it cannot.
    //
    // THE LOAD-BEARING PROPERTY IS UNCHANGED: Render(messages, "A") is a byte-exact PREFIX of
    // Render(messages, "AB"), because the prefill is appended after whatever the template
    // produced and nothing already rendered is ever rewritten. That is the only reason llama.cpp's
    // KV cache survives the second pass of a turn.
    //
    // Two more things it works out from the same template, because both were hard-coded to ChatML
    // spellings before and both were wrong for every other model:
    // - the marker that ends an assistant turn, which is the only stop sequence this backend has;
    // - the marker that opens the next turn, which is where generated text has to be cut when the
    //   model runs on and starts inventing the user's reply.
    public class ChatTemplateRenderer
    {
        readonly Func<IReadOnlyList<ChatMessage>, string> _applyTheModelsOwnChatTemplate;

        string _assistantTurnEndMarker = ChatMlPromptRenderer.k_turnEndTag;
        string _nextTurnOpeningMarker = ChatMlPromptRenderer.k_turnStartTag;
        bool _isTheModelsOwnTemplateUsable;
        bool _wasTheFormatOfTheModelLearned;
        bool _doesTheModelThinkInTags;

        // Deliberately ugly and unlikely to occur inside a template's own boilerplate: the markers
        // are found by locating these strings in the rendered result and reading what surrounds
        // them, so a probe word that a template happened to contain would find the wrong place.
        const string k_firstUserProbeText = "AMBERLINE_PROBE_USER_ONE";
        const string k_assistantProbeText = "AMBERLINE_PROBE_ASSISTANT";
        const string k_secondUserProbeText = "AMBERLINE_PROBE_USER_TWO";

        const string k_thinkBlockOpenTag = "<think>";

        public ChatTemplateRenderer(Func<IReadOnlyList<ChatMessage>, string> applyTheModelsOwnChatTemplate)
        {
            _applyTheModelsOwnChatTemplate = applyTheModelsOwnChatTemplate;
        }

        /// <summary>The marker that ends an assistant turn - "&lt;|im_end|&gt;" for a ChatML model,
        /// "&lt;/s&gt;" for a Mistral one. Used as the stop text of every generation.</summary>
        public string AssistantTurnEndMarker => _assistantTurnEndMarker;

        /// <summary>The marker that opens the turn after the assistant's. Generated text is cut at
        /// it, because past that point the model is writing the user's next message for them.</summary>
        public string NextTurnOpeningMarker => _nextTurnOpeningMarker;

        /// <summary>
        /// True when the model has a real reasoning token, so the &lt;think&gt; tricks in
        /// <see cref="PromptBuilder"/> mean something to it. False for a model that has never seen
        /// the tag, where the same text is just noise it may start imitating.
        /// </summary>
        public bool DoesTheModelThinkInTags => _doesTheModelThinkInTags;

        /// <summary>True once the model's own template answered and is being used, false while the
        /// ChatML fallback is in play. Reported at startup so a fallback is never silent.</summary>
        public bool IsUsingTheModelsOwnTemplate => _isTheModelsOwnTemplateUsable;

        /// <summary>
        /// Works out the model's envelope by rendering three probe messages through it and reading
        /// what the template put around them. Call once, after the model is loaded; calling again
        /// does nothing, because the answer cannot change while one model is loaded.
        /// <paramref name="doesTheModelThinkInTags"/> comes from the caller, which is the only
        /// place that can ask the tokenizer whether &lt;think&gt; is a token of this model.
        /// </summary>
        public void LearnTheFormatOfTheLoadedModel(bool doesTheModelThinkInTags)
        {
            _doesTheModelThinkInTags = doesTheModelThinkInTags;

            if (_wasTheFormatOfTheModelLearned)
            {
                return;
            }

            _wasTheFormatOfTheModelLearned = true;

            string renderedProbe = RenderProbeConversationThroughTheModelsOwnTemplate();
            if (string.IsNullOrEmpty(renderedProbe))
            {
                Debug.LogWarning("[ChatTemplateRenderer] The model did not answer with a chat template of its own, so ChatML is used. A model that is not a ChatML model will follow instructions badly.");
                return;
            }

            if (!TryReadTheTurnMarkersOutOfTheRenderedProbe(renderedProbe))
            {
                Debug.LogWarning("[ChatTemplateRenderer] The model's chat template rendered something this class could not read, so ChatML is used instead.");
                return;
            }

            _isTheModelsOwnTemplateUsable = true;

            Debug.Log($"[ChatTemplateRenderer] Using the model's own chat template. Assistant turns end with \"{_assistantTurnEndMarker}\", the next turn opens with \"{_nextTurnOpeningMarker}\", reasoning tags: {_doesTheModelThinkInTags}.");
        }

        /// <summary>
        /// Renders the ordered transcript and then opens an assistant turn, so the model can only
        /// continue as the assistant. The prefill is appended raw at the very end and may be empty.
        /// Consecutive messages with the same role are merged into one turn first: a small model
        /// follows one long user turn better than six short ones, and several templates - Mistral's
        /// among them - are only defined for strictly alternating turns in the first place.
        /// </summary>
        public string Render(IReadOnlyList<ChatMessage> messages, string assistantPrefill)
        {
            var mergedMessages = ChatMlPromptRenderer.MergeConsecutiveTurnsWithTheSameRole(messages);

            string renderedConversation = RenderConversationWithoutThePrefill(mergedMessages);

            return string.IsNullOrEmpty(assistantPrefill)
                ? renderedConversation
                : renderedConversation + assistantPrefill;
        }

        string RenderConversationWithoutThePrefill(IReadOnlyList<ChatMessage> mergedMessages)
        {
            if (!_isTheModelsOwnTemplateUsable)
            {
                return ChatMlPromptRenderer.RenderMergedConversationToChatMl(mergedMessages);
            }

            string renderedByTheModelsTemplate = ApplyTheModelsOwnTemplateOrNull(mergedMessages);
            if (!string.IsNullOrEmpty(renderedByTheModelsTemplate))
            {
                return renderedByTheModelsTemplate;
            }

            // One failed render is not a reason to keep failing: the fallback is used for this
            // prompt and the flag stays on, because a transient native error is likelier than the
            // template having stopped existing halfway through a session.
            Debug.LogWarning("[ChatTemplateRenderer] The model's chat template failed for one prompt, so that prompt was rendered as ChatML.");
            return ChatMlPromptRenderer.RenderMergedConversationToChatMl(mergedMessages);
        }

        string RenderProbeConversationThroughTheModelsOwnTemplate()
        {
            // Strictly alternating user / assistant / user, which is the only shape every template
            // in the wild is defined for. A system message is left out on purpose: several
            // templates fold it into the first user turn, which would move the marker we are
            // looking for.
            var probeMessages = new List<ChatMessage>
            {
                new ChatMessage(ChatRole.User, k_firstUserProbeText, 0),
                new ChatMessage(ChatRole.Assistant, k_assistantProbeText, 0),
                new ChatMessage(ChatRole.User, k_secondUserProbeText, 0)
            };

            return ApplyTheModelsOwnTemplateOrNull(probeMessages);
        }

        string ApplyTheModelsOwnTemplateOrNull(IReadOnlyList<ChatMessage> messages)
        {
            if (_applyTheModelsOwnChatTemplate == null)
            {
                return null;
            }

            try
            {
                return _applyTheModelsOwnChatTemplate(messages);
            }
            catch (Exception exception)
            {
                Debug.LogWarning($"[ChatTemplateRenderer] Applying the model's chat template threw: {exception.GetType().Name}: {exception.Message}");
                return null;
            }
        }

        // Everything the template wrote between the end of the assistant probe and the start of the
        // second user probe is, by definition, "close the assistant turn and open a user one". The
        // first marker in it ends the assistant turn; the next one opens the turn after it.
        bool TryReadTheTurnMarkersOutOfTheRenderedProbe(string renderedProbe)
        {
            int indexOfAssistantProbe = renderedProbe.IndexOf(k_assistantProbeText, StringComparison.Ordinal);
            if (indexOfAssistantProbe < 0)
            {
                return false;
            }

            int indexAfterAssistantProbe = indexOfAssistantProbe + k_assistantProbeText.Length;

            int indexOfSecondUserProbe = renderedProbe.IndexOf(k_secondUserProbeText, indexAfterAssistantProbe, StringComparison.Ordinal);
            if (indexOfSecondUserProbe < 0)
            {
                return false;
            }

            string textBetweenTheTwoProbes = renderedProbe.Substring(
                indexAfterAssistantProbe, indexOfSecondUserProbe - indexAfterAssistantProbe);

            string assistantTurnEndMarker = TakeTheFirstMarkerIn(textBetweenTheTwoProbes);
            if (string.IsNullOrEmpty(assistantTurnEndMarker))
            {
                return false;
            }

            _assistantTurnEndMarker = assistantTurnEndMarker;

            int indexAfterTheAssistantMarker =
                textBetweenTheTwoProbes.IndexOf(assistantTurnEndMarker, StringComparison.Ordinal) + assistantTurnEndMarker.Length;

            string textThatOpensTheNextTurn = textBetweenTheTwoProbes.Substring(indexAfterTheAssistantMarker);
            _nextTurnOpeningMarker = TakeTheFirstMarkerIn(textThatOpensTheNextTurn);

            // Generated text is CUT at this marker, so a marker that could plausibly occur inside a
            // plan or a file payload would silently truncate real work. A bracketed token cannot;
            // a template that opens its user turn with plain prose - "### Instruction:" - very much
            // can, and for those the marker is simply dropped and the turn-end one does the job.
            if (!IsABracketedMarker(_nextTurnOpeningMarker))
            {
                _nextTurnOpeningMarker = string.Empty;
            }

            return true;
        }

        static bool IsABracketedMarker(string marker)
        {
            return !string.IsNullOrEmpty(marker) && FindClosingCharacterOfTheBracketAt(marker[0]) != '\0';
        }

        // A marker is either a bracketed token - "<|im_end|>", "</s>", "[INST]" - or, failing that,
        // the first run of non-whitespace characters. Both spellings are in use, and reading the
        // bracket to its close is what stops "<|im_end|>" being cut down to "<|im".
        static string TakeTheFirstMarkerIn(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                return string.Empty;
            }

            string textWithoutLeadingWhitespace = text.TrimStart();

            char closingCharacterOfTheBracket = FindClosingCharacterOfTheBracketAt(textWithoutLeadingWhitespace[0]);
            if (closingCharacterOfTheBracket != '\0')
            {
                int indexOfClosingCharacter = textWithoutLeadingWhitespace.IndexOf(closingCharacterOfTheBracket);
                if (indexOfClosingCharacter > 0)
                {
                    return textWithoutLeadingWhitespace.Substring(0, indexOfClosingCharacter + 1);
                }
            }

            int indexOfFirstWhitespace = textWithoutLeadingWhitespace.IndexOfAny(new[] { ' ', '\n', '\r', '\t' });

            return indexOfFirstWhitespace < 0
                ? textWithoutLeadingWhitespace
                : textWithoutLeadingWhitespace.Substring(0, indexOfFirstWhitespace);
        }

        static char FindClosingCharacterOfTheBracketAt(char openingCharacter)
        {
            if (openingCharacter == '<') return '>';
            if (openingCharacter == '[') return ']';

            return '\0';
        }

        /// <summary>
        /// True when <paramref name="generatedText"/> has run past the end of the assistant turn.
        /// Both markers count: a model that keeps going writes the closing marker, the opening of
        /// the next turn, or both.
        /// </summary>
        public int FindIndexWhereTheAssistantTurnEnds(string generatedText)
        {
            int indexOfEarliestMarker = generatedText.Length;

            indexOfEarliestMarker = TakeEarlierIndexOfMarker(generatedText, _assistantTurnEndMarker, indexOfEarliestMarker);
            indexOfEarliestMarker = TakeEarlierIndexOfMarker(generatedText, _nextTurnOpeningMarker, indexOfEarliestMarker);

            // The ChatML tags are always looked for as well, whatever the loaded model is. A model
            // trained on ChatML data but shipped with another template still reaches for them, and
            // a stray "<|im_start|>" left in the transcript is read as a role switch on every
            // later turn.
            indexOfEarliestMarker = TakeEarlierIndexOfMarker(generatedText, ChatMlPromptRenderer.k_turnEndTag, indexOfEarliestMarker);
            indexOfEarliestMarker = TakeEarlierIndexOfMarker(generatedText, ChatMlPromptRenderer.k_turnStartTag, indexOfEarliestMarker);

            return indexOfEarliestMarker;
        }

        static int TakeEarlierIndexOfMarker(string generatedText, string marker, int indexOfEarliestMarkerSoFar)
        {
            if (string.IsNullOrEmpty(marker))
            {
                return indexOfEarliestMarkerSoFar;
            }

            int indexOfThisMarker = generatedText.IndexOf(marker, StringComparison.Ordinal);

            return indexOfThisMarker >= 0 && indexOfThisMarker < indexOfEarliestMarkerSoFar
                ? indexOfThisMarker
                : indexOfEarliestMarkerSoFar;
        }

        /// <summary>The tag a reasoning model opens its hidden thinking with. Exposed so the prompt
        /// builder and the loop agree on one spelling.</summary>
        public static string ThinkBlockOpenTag => k_thinkBlockOpenTag;
    }
}
