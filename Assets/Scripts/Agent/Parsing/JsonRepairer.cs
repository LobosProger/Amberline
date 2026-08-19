using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Amberline.Agent
{
    /// <summary>
    /// Outcome of one repair attempt. A repaired text is only ever handed back after a real parse
    /// succeeded on it, so the caller never receives a guess.
    /// </summary>
    public class JsonRepairResult
    {
        public bool IsSuccess { get; }

        /// <summary>The object that came out of the repaired text. Null when the repair failed.</summary>
        public JObject ParsedObject { get; }

        /// <summary>The exact text that finally parsed. Null when the repair failed.</summary>
        public string RepairedJsonText { get; }

        /// <summary>
        /// True when the JSON only parsed because an unterminated string or bracket had to be
        /// closed - which means the model was cut off mid-call. The caller must treat the arguments
        /// as incomplete: a cut-off write_file, repaired into valid JSON, would write half a file.
        /// </summary>
        public bool WasTruncated { get; }

        public string FailureMessage { get; }

        JsonRepairResult(bool isSuccess, JObject parsedObject, string repairedJsonText, bool wasTruncated, string failureMessage)
        {
            IsSuccess = isSuccess;
            ParsedObject = parsedObject;
            RepairedJsonText = repairedJsonText;
            WasTruncated = wasTruncated;
            FailureMessage = failureMessage ?? string.Empty;
        }

        public static JsonRepairResult Repaired(JObject parsedObject, string repairedJsonText, bool wasTruncated)
        {
            return new JsonRepairResult(true, parsedObject, repairedJsonText, wasTruncated, null);
        }

        public static JsonRepairResult Failed(string failureMessage)
        {
            return new JsonRepairResult(false, null, null, false, failureMessage);
        }
    }

    // Turns the almost-JSON a small model emits into JSON that parses.
    //
    // The structure matters more than the individual repairs: this is a speculative cascade. One
    // repair is applied, the text is re-parsed, and the repair survives only because that parse
    // succeeded - otherwise it is rolled back and the next one is tried. Nothing is ever returned
    // on a guess. The reason is write_file: its payload is arbitrary source code living inside a
    // JSON string, and a repair that "fixes" the JSON by mangling that string would write mangled
    // code to disk. So the cheapest repair that works wins - every repair is first tried alone,
    // then the chain of repairs that provably cannot touch string content, and only then the full
    // chain including the two rewrites that can.
    //
    // Two dialect rules live in TryParseJsonObject rather than in a repair, because both failures
    // are silent - the text parses, into the wrong value:
    // - dates are never auto-converted, or file content whose first line is "2024-01-15" comes back
    //   as a timezone-shifted DateTime instead of the text the model sent;
    // - \b and \f are rejected as escapes although JSON allows them, or the Windows path C:\bin
    //   parses into a backspace character followed by "in".
    // \n, \r, \t and \u cannot be rejected the same way - a model needs them for real newlines and
    // tabs inside file content - so C:\temp is still a path this cannot save. The system prompt
    // forbidding absolute paths is the only defence there.
    //
    // Collaborator: ToolCallParser is the only caller.
    public static class JsonRepairer
    {
        // One rung of the cascade. IsSafeForStringContent marks the repairs that provably cannot
        // alter what is inside a JSON string, so they can be chained before the riskier rewrites.
        class JsonRepairStep
        {
            public Func<string, string> ApplyRepair { get; }
            public bool IsSafeForStringContent { get; }
            public bool ClosesUnterminatedStructures { get; }

            public JsonRepairStep(Func<string, string> applyRepair, bool isSafeForStringContent, bool closesUnterminatedStructures)
            {
                ApplyRepair = applyRepair;
                IsSafeForStringContent = isSafeForStringContent;
                ClosesUnterminatedStructures = closesUnterminatedStructures;
            }
        }

        // Ordered by how often a small model actually needs them. The closer is last so the
        // truncation flag is only raised once every cheaper explanation has been ruled out.
        static readonly JsonRepairStep[] k_repairStepsInOrderOfHowOftenTheyAreNeeded =
        {
            new JsonRepairStep(EscapeRawControlCharactersInsideStrings, true, false),
            new JsonRepairStep(EscapeInvalidBackslashEscapes, true, false),
            new JsonRepairStep(RemoveTrailingCommasBeforeClosingBrackets, true, false),
            new JsonRepairStep(ConvertSingleQuotedStringsToDoubleQuoted, false, false),
            new JsonRepairStep(QuoteUnquotedPropertyNames, false, false),
            new JsonRepairStep(CloseUnterminatedStringsAndBrackets, true, true)
        };

        /// <summary>
        /// Tries to turn <paramref name="possiblyBrokenJsonText"/> into a parseable JSON object.
        /// The returned text is always one that a parser accepted; on failure nothing is returned
        /// at all, because a half-repaired payload is more dangerous than no payload.
        /// </summary>
        public static JsonRepairResult TryRepair(string possiblyBrokenJsonText)
        {
            if (string.IsNullOrWhiteSpace(possiblyBrokenJsonText))
            {
                return JsonRepairResult.Failed("there was no JSON to repair");
            }

            string trimmedJsonText = possiblyBrokenJsonText.Trim();

            if (TryParseJsonObject(trimmedJsonText, out JObject alreadyValidObject))
            {
                return JsonRepairResult.Repaired(alreadyValidObject, trimmedJsonText, false);
            }

            var resultOfSingleRepair = TryEveryRepairOnItsOwn(trimmedJsonText);
            if (resultOfSingleRepair.IsSuccess)
            {
                return resultOfSingleRepair;
            }

            var resultOfSafeChain = TryChainOfRepairs(trimmedJsonText, true);
            if (resultOfSafeChain.IsSuccess)
            {
                return resultOfSafeChain;
            }

            var resultOfFullChain = TryChainOfRepairs(trimmedJsonText, false);
            if (resultOfFullChain.IsSuccess)
            {
                return resultOfFullChain;
            }

            return JsonRepairResult.Failed("the JSON stayed unparseable after every repair");
        }

        /// <summary>
        /// Parses <paramref name="jsonText"/> as a JSON object under this project's dialect rules:
        /// no automatic date conversion, and \b / \f refused as escapes. Trailing prose after the
        /// object is ignored, because a model that appends a sentence after the call still made
        /// the call.
        /// </summary>
        public static bool TryParseJsonObject(string jsonText, out JObject parsedObject)
        {
            parsedObject = null;

            if (string.IsNullOrWhiteSpace(jsonText))
            {
                return false;
            }
            if (ContainsBackspaceOrFormFeedEscape(jsonText))
            {
                return false;
            }

            try
            {
                using (var stringReader = new StringReader(jsonText))
                using (var jsonReader = new JsonTextReader(stringReader))
                {
                    // Without this a value beginning "2024-01-15" is rewritten into a DateTime and
                    // comes back shifted by the local time zone - silently corrupting file content.
                    jsonReader.DateParseHandling = DateParseHandling.None;

                    parsedObject = JToken.ReadFrom(jsonReader) as JObject;
                    return parsedObject != null;
                }
            }
            catch (JsonException)
            {
                return false;
            }
            catch (ArgumentException)
            {
                return false;
            }
        }

        // Cheapest first: a text that only needs its control characters escaped must never have the
        // single-quote rewrite applied to it, so every repair gets a solo attempt before any chain.
        static JsonRepairResult TryEveryRepairOnItsOwn(string jsonText)
        {
            foreach (var repairStep in k_repairStepsInOrderOfHowOftenTheyAreNeeded)
            {
                string candidateJsonText = repairStep.ApplyRepair(jsonText);
                if (string.Equals(candidateJsonText, jsonText, StringComparison.Ordinal))
                {
                    continue;
                }
                if (TryParseJsonObject(candidateJsonText, out JObject parsedObject))
                {
                    return JsonRepairResult.Repaired(parsedObject, candidateJsonText, repairStep.ClosesUnterminatedStructures);
                }
            }

            return JsonRepairResult.Failed("no single repair was enough");
        }

        // Repairs stack up here, but only inside a local: if the chain never reaches a successful
        // parse every speculative change is dropped and the caller keeps the original text.
        static JsonRepairResult TryChainOfRepairs(string jsonText, bool onlyStepsSafeForStringContent)
        {
            string currentJsonText = jsonText;
            bool didCloseUnterminatedStructure = false;

            foreach (var repairStep in k_repairStepsInOrderOfHowOftenTheyAreNeeded)
            {
                if (onlyStepsSafeForStringContent && !repairStep.IsSafeForStringContent)
                {
                    continue;
                }

                string candidateJsonText = repairStep.ApplyRepair(currentJsonText);
                if (string.Equals(candidateJsonText, currentJsonText, StringComparison.Ordinal))
                {
                    continue;
                }

                bool wouldHaveClosedStructure = didCloseUnterminatedStructure || repairStep.ClosesUnterminatedStructures;
                if (TryParseJsonObject(candidateJsonText, out JObject parsedObject))
                {
                    return JsonRepairResult.Repaired(parsedObject, candidateJsonText, wouldHaveClosedStructure);
                }

                currentJsonText = candidateJsonText;
                didCloseUnterminatedStructure = wouldHaveClosedStructure;
            }

            return JsonRepairResult.Failed("the chain of repairs did not produce parseable JSON");
        }

        // A real newline or tab typed straight into a JSON string instead of the escape sequence -
        // the most common thing a small model gets wrong. Measured note: Newtonsoft's JsonTextReader
        // already tolerates raw control characters and keeps them verbatim, so in practice this rung
        // rarely has to fire. It stays because the tolerance is a reader implementation detail, not
        // a promise, and because the rung is provably safe to run.
        static string EscapeRawControlCharactersInsideStrings(string jsonText)
        {
            var builder = new StringBuilder(jsonText.Length + 16);
            bool isInsideString = false;
            bool isPreviousCharacterAnEscape = false;

            foreach (char character in jsonText)
            {
                if (isInsideString && character < ' ')
                {
                    AppendControlCharacterAsEscapeSequence(builder, character);
                    isPreviousCharacterAnEscape = false;
                    continue;
                }

                builder.Append(character);

                if (!isInsideString)
                {
                    if (character == '"')
                    {
                        isInsideString = true;
                    }
                    continue;
                }
                if (isPreviousCharacterAnEscape)
                {
                    isPreviousCharacterAnEscape = false;
                    continue;
                }
                if (character == '\\')
                {
                    isPreviousCharacterAnEscape = true;
                    continue;
                }
                if (character == '"')
                {
                    isInsideString = false;
                }
            }

            return builder.ToString();
        }

        static void AppendControlCharacterAsEscapeSequence(StringBuilder builder, char controlCharacter)
        {
            switch (controlCharacter)
            {
                case '\n':
                    builder.Append("\\n");
                    return;
                case '\r':
                    builder.Append("\\r");
                    return;
                case '\t':
                    builder.Append("\\t");
                    return;
                default:
                    builder.Append("\\u").Append(((int)controlCharacter).ToString("x4"));
                    return;
            }
        }

        // A backslash that does not open a valid escape becomes a literal backslash, which is what a
        // model writing a Windows path meant. \b and \f count as invalid on purpose - see the class
        // comment: they are the two escapes that turn a path into a control character in silence.
        static string EscapeInvalidBackslashEscapes(string jsonText)
        {
            var builder = new StringBuilder(jsonText.Length + 16);
            bool isInsideString = false;

            for (int characterIndex = 0; characterIndex < jsonText.Length; characterIndex++)
            {
                char character = jsonText[characterIndex];

                if (!isInsideString)
                {
                    builder.Append(character);
                    if (character == '"')
                    {
                        isInsideString = true;
                    }
                    continue;
                }
                if (character != '\\')
                {
                    builder.Append(character);
                    if (character == '"')
                    {
                        isInsideString = false;
                    }
                    continue;
                }

                char escapedCharacter = characterIndex + 1 < jsonText.Length ? jsonText[characterIndex + 1] : '\0';
                if (IsEscapeAllowedInThisDialect(jsonText, characterIndex, escapedCharacter))
                {
                    builder.Append(character);
                    builder.Append(escapedCharacter);
                    characterIndex++;
                    continue;
                }

                builder.Append("\\\\");
            }

            return builder.ToString();
        }

        static bool IsEscapeAllowedInThisDialect(string jsonText, int indexOfBackslash, char escapedCharacter)
        {
            bool isPlainEscape = escapedCharacter == '"' || escapedCharacter == '\\' || escapedCharacter == '/'
                                 || escapedCharacter == 'n' || escapedCharacter == 'r' || escapedCharacter == 't';
            if (isPlainEscape)
            {
                return true;
            }
            if (escapedCharacter != 'u')
            {
                return false;
            }

            return AreFourHexDigitsPresentAfter(jsonText, indexOfBackslash + 1);
        }

        static bool AreFourHexDigitsPresentAfter(string jsonText, int indexOfUnicodeMarker)
        {
            if (indexOfUnicodeMarker + 4 >= jsonText.Length)
            {
                return false;
            }
            for (int offset = 1; offset <= 4; offset++)
            {
                if (!Uri.IsHexDigit(jsonText[indexOfUnicodeMarker + offset]))
                {
                    return false;
                }
            }

            return true;
        }

        // Written as a scanner rather than the obvious regex on purpose: a regex for a comma
        // followed by a closing bracket also matches inside a string, and the strings here are
        // write_file payloads full of C# array and object initialisers.
        static string RemoveTrailingCommasBeforeClosingBrackets(string jsonText)
        {
            var builder = new StringBuilder(jsonText.Length);
            bool isInsideString = false;
            bool isPreviousCharacterAnEscape = false;

            for (int characterIndex = 0; characterIndex < jsonText.Length; characterIndex++)
            {
                char character = jsonText[characterIndex];

                if (isInsideString)
                {
                    builder.Append(character);
                    if (isPreviousCharacterAnEscape)
                    {
                        isPreviousCharacterAnEscape = false;
                        continue;
                    }
                    if (character == '\\')
                    {
                        isPreviousCharacterAnEscape = true;
                        continue;
                    }
                    if (character == '"')
                    {
                        isInsideString = false;
                    }
                    continue;
                }

                if (character == '"')
                {
                    builder.Append(character);
                    isInsideString = true;
                    continue;
                }
                if (character == ',' && IsNextMeaningfulCharacterAClosingBracket(jsonText, characterIndex + 1))
                {
                    continue;
                }

                builder.Append(character);
            }

            return builder.ToString();
        }

        static bool IsNextMeaningfulCharacterAClosingBracket(string jsonText, int startIndex)
        {
            for (int characterIndex = startIndex; characterIndex < jsonText.Length; characterIndex++)
            {
                char character = jsonText[characterIndex];
                if (char.IsWhiteSpace(character))
                {
                    continue;
                }
                return character == '}' || character == ']';
            }

            return false;
        }

        // Risky rung: an apostrophe inside prose looks exactly like an opening quote. It is marked
        // unsafe for string content and only ever runs once the safe chain has already failed.
        static string ConvertSingleQuotedStringsToDoubleQuoted(string jsonText)
        {
            var builder = new StringBuilder(jsonText.Length + 8);
            bool isInsideDoubleQuotedString = false;
            bool isPreviousCharacterAnEscape = false;

            for (int characterIndex = 0; characterIndex < jsonText.Length; characterIndex++)
            {
                char character = jsonText[characterIndex];

                if (isInsideDoubleQuotedString)
                {
                    builder.Append(character);
                    if (isPreviousCharacterAnEscape)
                    {
                        isPreviousCharacterAnEscape = false;
                        continue;
                    }
                    if (character == '\\')
                    {
                        isPreviousCharacterAnEscape = true;
                        continue;
                    }
                    if (character == '"')
                    {
                        isInsideDoubleQuotedString = false;
                    }
                    continue;
                }

                if (character == '"')
                {
                    builder.Append(character);
                    isInsideDoubleQuotedString = true;
                    continue;
                }
                if (character != '\'')
                {
                    builder.Append(character);
                    continue;
                }

                int indexOfClosingSingleQuote = FindClosingSingleQuote(jsonText, characterIndex + 1);
                if (indexOfClosingSingleQuote < 0)
                {
                    builder.Append(character);
                    continue;
                }

                AppendSingleQuotedBodyAsDoubleQuotedString(builder, jsonText, characterIndex + 1, indexOfClosingSingleQuote);
                characterIndex = indexOfClosingSingleQuote;
            }

            return builder.ToString();
        }

        static int FindClosingSingleQuote(string jsonText, int startIndex)
        {
            for (int characterIndex = startIndex; characterIndex < jsonText.Length; characterIndex++)
            {
                if (jsonText[characterIndex] == '\\')
                {
                    characterIndex++;
                    continue;
                }
                if (jsonText[characterIndex] == '\'')
                {
                    return characterIndex;
                }
            }

            return -1;
        }

        static void AppendSingleQuotedBodyAsDoubleQuotedString(StringBuilder builder, string jsonText, int bodyStartIndex, int indexOfClosingSingleQuote)
        {
            builder.Append('"');

            for (int characterIndex = bodyStartIndex; characterIndex < indexOfClosingSingleQuote; characterIndex++)
            {
                char character = jsonText[characterIndex];

                bool isEscapedSingleQuote = character == '\\'
                                            && characterIndex + 1 < indexOfClosingSingleQuote
                                            && jsonText[characterIndex + 1] == '\'';
                if (isEscapedSingleQuote)
                {
                    builder.Append('\'');
                    characterIndex++;
                    continue;
                }
                if (character == '"')
                {
                    builder.Append("\\\"");
                    continue;
                }

                builder.Append(character);
            }

            builder.Append('"');
        }

        // Also risky: only an identifier sitting right after an opening brace or a comma and
        // followed by a colon counts as a key, so JSON values like true or null are never touched.
        static string QuoteUnquotedPropertyNames(string jsonText)
        {
            var builder = new StringBuilder(jsonText.Length + 8);
            bool isInsideString = false;
            bool isPreviousCharacterAnEscape = false;
            char lastMeaningfulCharacter = '\0';

            for (int characterIndex = 0; characterIndex < jsonText.Length; characterIndex++)
            {
                char character = jsonText[characterIndex];

                if (isInsideString)
                {
                    builder.Append(character);
                    if (isPreviousCharacterAnEscape)
                    {
                        isPreviousCharacterAnEscape = false;
                        continue;
                    }
                    if (character == '\\')
                    {
                        isPreviousCharacterAnEscape = true;
                        continue;
                    }
                    if (character == '"')
                    {
                        isInsideString = false;
                        lastMeaningfulCharacter = '"';
                    }
                    continue;
                }

                if (character == '"')
                {
                    builder.Append(character);
                    isInsideString = true;
                    continue;
                }

                bool canStartAPropertyName = (lastMeaningfulCharacter == '{' || lastMeaningfulCharacter == ',')
                                             && IsIdentifierStartCharacter(character);
                if (canStartAPropertyName && TryAppendQuotedPropertyName(builder, jsonText, characterIndex, out int indexAfterPropertyName))
                {
                    lastMeaningfulCharacter = '"';
                    characterIndex = indexAfterPropertyName - 1;
                    continue;
                }

                builder.Append(character);
                if (!char.IsWhiteSpace(character))
                {
                    lastMeaningfulCharacter = character;
                }
            }

            return builder.ToString();
        }

        static bool TryAppendQuotedPropertyName(StringBuilder builder, string jsonText, int startIndex, out int indexAfterPropertyName)
        {
            int endIndexOfName = startIndex;
            while (endIndexOfName < jsonText.Length && IsIdentifierCharacter(jsonText[endIndexOfName]))
            {
                endIndexOfName++;
            }

            int indexOfColonCandidate = endIndexOfName;
            while (indexOfColonCandidate < jsonText.Length && char.IsWhiteSpace(jsonText[indexOfColonCandidate]))
            {
                indexOfColonCandidate++;
            }

            indexAfterPropertyName = endIndexOfName;
            if (indexOfColonCandidate >= jsonText.Length || jsonText[indexOfColonCandidate] != ':')
            {
                return false;
            }

            builder.Append('"');
            builder.Append(jsonText, startIndex, endIndexOfName - startIndex);
            builder.Append('"');
            return true;
        }

        static bool IsIdentifierStartCharacter(char character)
        {
            return char.IsLetter(character) || character == '_' || character == '$';
        }

        static bool IsIdentifierCharacter(char character)
        {
            return char.IsLetterOrDigit(character) || character == '_' || character == '$';
        }

        // The truncation rung. Whatever it has to add is the evidence that the model ran out of
        // tokens mid-call, which is why the caller is told about it rather than just handed JSON.
        static string CloseUnterminatedStringsAndBrackets(string jsonText)
        {
            var openBrackets = new Stack<char>();
            bool isInsideString = false;
            bool isPreviousCharacterAnEscape = false;

            foreach (char character in jsonText)
            {
                if (isInsideString)
                {
                    if (isPreviousCharacterAnEscape)
                    {
                        isPreviousCharacterAnEscape = false;
                        continue;
                    }
                    if (character == '\\')
                    {
                        isPreviousCharacterAnEscape = true;
                        continue;
                    }
                    if (character == '"')
                    {
                        isInsideString = false;
                    }
                    continue;
                }

                if (character == '"')
                {
                    isInsideString = true;
                    continue;
                }
                if (character == '{' || character == '[')
                {
                    openBrackets.Push(character);
                    continue;
                }
                if ((character == '}' || character == ']') && openBrackets.Count > 0)
                {
                    openBrackets.Pop();
                }
            }

            if (!isInsideString && openBrackets.Count == 0)
            {
                return jsonText;
            }

            var builder = new StringBuilder(jsonText);

            // A generation cut off right after a backslash leaves an escape with nothing to escape.
            if (isPreviousCharacterAnEscape && builder.Length > 0)
            {
                builder.Length -= 1;
            }
            if (isInsideString)
            {
                builder.Append('"');
            }

            ReplaceDanglingSeparatorAtEnd(builder);

            while (openBrackets.Count > 0)
            {
                builder.Append(openBrackets.Pop() == '{' ? '}' : ']');
            }

            return builder.ToString();
        }

        // After the cut-off string is closed the text can still end on a separator with nothing
        // after it. A dangling comma is dropped; a dangling colon gets an empty string, so the key
        // the model had started survives with an obviously empty value.
        static void ReplaceDanglingSeparatorAtEnd(StringBuilder builder)
        {
            while (builder.Length > 0 && char.IsWhiteSpace(builder[builder.Length - 1]))
            {
                builder.Length -= 1;
            }
            if (builder.Length == 0)
            {
                return;
            }

            char lastCharacter = builder[builder.Length - 1];
            if (lastCharacter == ',')
            {
                builder.Length -= 1;
            }
            else if (lastCharacter == ':')
            {
                builder.Append("\"\"");
            }
        }

        // JSON allows \b and \f, this dialect does not - so the check runs before the parser sees
        // the text, and a path like C:\bin is pushed down to the repair rung instead of parsing
        // into a backspace character. A doubled backslash followed by b is a literal and is left alone.
        static bool ContainsBackspaceOrFormFeedEscape(string jsonText)
        {
            bool isInsideString = false;

            for (int characterIndex = 0; characterIndex < jsonText.Length; characterIndex++)
            {
                char character = jsonText[characterIndex];

                if (!isInsideString)
                {
                    if (character == '"')
                    {
                        isInsideString = true;
                    }
                    continue;
                }
                if (character == '"')
                {
                    isInsideString = false;
                    continue;
                }
                if (character != '\\')
                {
                    continue;
                }

                char escapedCharacter = characterIndex + 1 < jsonText.Length ? jsonText[characterIndex + 1] : '\0';
                if (escapedCharacter == 'b' || escapedCharacter == 'f')
                {
                    return true;
                }

                characterIndex++;
            }

            return false;
        }
    }
}
