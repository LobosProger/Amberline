using System;
using System.Collections.Generic;
using System.Globalization;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Amberline.Agent
{
    // The parse ladder: everything between "the model produced text" and "the runner has a ToolCall".
    //
    // A small model emits nearly correct output constantly, so each rung here exists because of a
    // failure seen in practice rather than a hypothetical one:
    // - S0 extraction pulls the JSON out of whatever the model wrapped it in. It takes the LAST
    //   tool_call block, because a model that restates its plan puts the real call last, and it
    //   tolerates a missing closing tag, markdown fences and no tags at all.
    // - S1 repair hands the extracted text to JsonRepairer and tries again.
    // - S3 coercion fixes argument shapes - a value that arrived as a bool, an array or a nested
    //   object where a string was wanted, a key or a tool name in the wrong case.
    // Whichever rung was needed is recorded in the returned ToolCallParseStage, so a run that only
    // worked because of aggressive repair is visible instead of looking like a clean one.
    //
    // Truncation is the load-bearing part. A parse that only succeeded because an unterminated
    // string had to be closed comes back with WasTruncated set, and the runner refuses truncated
    // mutating calls - repair will happily turn a cut-off write_file into valid JSON that writes
    // half a source file over a good one. Whether a tool mutates is not decided here: this class
    // does not know the tool set, and its job is to flag honestly, not to judge.
    //
    // Collaborators: JsonRepairer does S1; AgentLoop calls Parse and reads Stage and WasTruncated.
    public static class ToolCallParser
    {
        const string k_toolCallOpenTag = "<tool_call>";
        const string k_toolCallCloseTag = "</tool_call>";

        // The field names the model is told to use, followed by the spellings it reaches for anyway.
        // The FIRST entry is the canonical one; anything else counts as a coercion, because reading
        // a field the wire format never specified is exactly the kind of leniency worth seeing.
        static readonly string[] k_acceptedNamesForTheToolNameField = { "name", "tool_name", "tool", "function" };
        static readonly string[] k_acceptedNamesForTheArgumentsField = { "arguments", "args", "parameters", "params" };

        /// <summary>
        /// Runs the whole ladder over one model reply. Never throws: a reply with no tool call in
        /// it is an ordinary outcome that the loop turns into a reminder for the model.
        /// </summary>
        public static ToolCallParseResult Parse(string modelResponseText)
        {
            if (string.IsNullOrWhiteSpace(modelResponseText))
            {
                return ToolCallParseResult.NotAToolCall("the model returned no text");
            }

            if (!TryExtractToolCallJsonText(modelResponseText, out string extractedJsonText))
            {
                return ToolCallParseResult.NotAToolCall("the reply contains no tool call");
            }

            var stageReached = ToolCallParseStage.DirectExtraction;
            bool wasTruncated = false;

            if (!JsonRepairer.TryParseJsonObject(extractedJsonText, out JObject toolCallObject))
            {
                var repairResult = JsonRepairer.TryRepair(extractedJsonText);
                if (!repairResult.IsSuccess)
                {
                    return ToolCallParseResult.NotAToolCall("the tool call JSON could not be repaired: " + repairResult.FailureMessage);
                }

                toolCallObject = repairResult.ParsedObject;
                stageReached = ToolCallParseStage.JsonRepair;
                wasTruncated = repairResult.WasTruncated;
            }

            return BuildToolCallFromJsonObject(toolCallObject, stageReached, wasTruncated);
        }

        // S0. The tags narrow the search; the brace scanner does the actual extraction, which is
        // what makes markdown fences, a leading "Sure, I will" and a trailing sentence all harmless.
        static bool TryExtractToolCallJsonText(string modelResponseText, out string extractedJsonText)
        {
            string regionAfterTheLastOpenTag = SelectRegionAfterLastToolCallOpenTag(modelResponseText);

            if (TryFindLastBraceBalancedJsonObject(regionAfterTheLastOpenTag, out extractedJsonText))
            {
                return true;
            }

            // The tags were there but held nothing usable - an empty block, or the model closed the
            // tag before writing the call. Fall back to the whole reply.
            bool wasRegionNarrowed = !ReferenceEquals(regionAfterTheLastOpenTag, modelResponseText);
            if (wasRegionNarrowed && TryFindLastBraceBalancedJsonObject(modelResponseText, out extractedJsonText))
            {
                return true;
            }

            extractedJsonText = null;
            return false;
        }

        // The LAST block wins: a model that thinks out loud writes a draft call, corrects itself and
        // writes the real one underneath. A missing closing tag means "to the end of the reply".
        static string SelectRegionAfterLastToolCallOpenTag(string modelResponseText)
        {
            int indexOfOpenTag = modelResponseText.LastIndexOf(k_toolCallOpenTag, StringComparison.OrdinalIgnoreCase);
            if (indexOfOpenTag < 0)
            {
                return modelResponseText;
            }

            int indexOfBlockStart = indexOfOpenTag + k_toolCallOpenTag.Length;
            int indexOfCloseTag = modelResponseText.IndexOf(k_toolCallCloseTag, indexOfBlockStart, StringComparison.OrdinalIgnoreCase);
            if (indexOfCloseTag < 0)
            {
                return modelResponseText.Substring(indexOfBlockStart);
            }

            return modelResponseText.Substring(indexOfBlockStart, indexOfCloseTag - indexOfBlockStart);
        }

        // Only braces are counted, never square brackets, so a call wrapped in an array still yields
        // the object inside it. Braces inside JSON strings are skipped - a write_file payload is
        // full of them.
        static bool TryFindLastBraceBalancedJsonObject(string text, out string foundJsonText)
        {
            int braceDepth = 0;
            int indexOfCurrentObjectStart = -1;
            string lastCompleteObject = null;
            string lastCompleteObjectThatLooksLikeACall = null;
            bool isInsideString = false;
            bool isPreviousCharacterAnEscape = false;

            for (int characterIndex = 0; characterIndex < text.Length; characterIndex++)
            {
                char character = text[characterIndex];

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
                if (character == '{')
                {
                    if (braceDepth == 0)
                    {
                        indexOfCurrentObjectStart = characterIndex;
                    }
                    braceDepth++;
                    continue;
                }
                if (character != '}' || braceDepth == 0)
                {
                    continue;
                }

                braceDepth--;
                if (braceDepth > 0 || indexOfCurrentObjectStart < 0)
                {
                    continue;
                }

                lastCompleteObject = text.Substring(indexOfCurrentObjectStart, characterIndex - indexOfCurrentObjectStart + 1);
                if (LooksLikeAToolCall(lastCompleteObject))
                {
                    lastCompleteObjectThatLooksLikeACall = lastCompleteObject;
                }
            }

            // An object still open at the end of the text is a generation that ran out of tokens.
            // It is preferred over an earlier complete object only when it really looks like a call,
            // so a stray brace in trailing prose cannot beat a good call that came before it.
            if (braceDepth > 0 && indexOfCurrentObjectStart >= 0)
            {
                string unterminatedTail = text.Substring(indexOfCurrentObjectStart);
                if (lastCompleteObject == null || LooksLikeAToolCall(unterminatedTail))
                {
                    foundJsonText = unterminatedTail;
                    return true;
                }
            }

            foundJsonText = lastCompleteObjectThatLooksLikeACall ?? lastCompleteObject;
            return foundJsonText != null;
        }

        static bool LooksLikeAToolCall(string jsonText)
        {
            return jsonText.IndexOf("\"name\"", StringComparison.OrdinalIgnoreCase) >= 0
                   || jsonText.IndexOf("\"arguments\"", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        // S3. Everything from here down normalises shapes rather than syntax.
        static ToolCallParseResult BuildToolCallFromJsonObject(JObject toolCallObject, ToolCallParseStage stageReachedSoFar, bool wasTruncated)
        {
            JProperty nameProperty = FindPropertyIgnoringCase(toolCallObject, k_acceptedNamesForTheToolNameField);
            string rawToolName = nameProperty == null ? null : ReadTokenAsPlainText(nameProperty.Value);
            if (string.IsNullOrWhiteSpace(rawToolName))
            {
                return ToolCallParseResult.NotAToolCall("the JSON has no tool name, so it is not a tool call");
            }

            // Tool names are lower-case snake_case, so a model shouting READ_FILE still gets there.
            string normalisedToolName = rawToolName.Trim().ToLowerInvariant();

            JProperty argumentsProperty = FindPropertyIgnoringCase(toolCallObject, k_acceptedNamesForTheArgumentsField);
            JToken argumentsToken = argumentsProperty == null ? null : argumentsProperty.Value;
            JObject argumentsObject = ResolveArgumentsObject(argumentsToken, toolCallObject);
            var arguments = ConvertArgumentsToStringDictionary(argumentsObject, out bool didCoerceAnyArgument);

            bool wasArgumentsFieldWrongShaped = argumentsToken == null || argumentsToken.Type != JTokenType.Object;
            bool didCoerceAnything = didCoerceAnyArgument
                                     || wasArgumentsFieldWrongShaped
                                     || !string.Equals(normalisedToolName, rawToolName, StringComparison.Ordinal)
                                     || IsFieldSpeltDifferentlyFromTheCanonicalName(nameProperty, k_acceptedNamesForTheToolNameField)
                                     || IsFieldSpeltDifferentlyFromTheCanonicalName(argumentsProperty, k_acceptedNamesForTheArgumentsField);

            // The stages are a ladder, so the deepest rung that fired is the one reported.
            var stageToReport = didCoerceAnything ? ToolCallParseStage.Coercion : stageReachedSoFar;
            return ToolCallParseResult.Parsed(new ToolCall(normalisedToolName, arguments), stageToReport, wasTruncated);
        }

        static JProperty FindPropertyIgnoringCase(JObject source, string[] acceptedPropertyNames)
        {
            foreach (string acceptedPropertyName in acceptedPropertyNames)
            {
                foreach (var property in source.Properties())
                {
                    if (string.Equals(property.Name, acceptedPropertyName, StringComparison.OrdinalIgnoreCase))
                    {
                        return property;
                    }
                }
            }

            return null;
        }

        static string ReadTokenAsPlainText(JToken token)
        {
            return token.Type == JTokenType.String ? token.Value<string>() : token.ToString();
        }

        static JObject ResolveArgumentsObject(JToken argumentsToken, JObject toolCallObject)
        {
            if (argumentsToken is JObject argumentsObject)
            {
                return argumentsObject;
            }

            // Some models put the whole arguments object inside a string, OpenAI style.
            if (argumentsToken != null && argumentsToken.Type == JTokenType.String)
            {
                return ParseArgumentsHiddenInsideAString(argumentsToken.Value<string>());
            }
            if (argumentsToken != null)
            {
                return null;
            }

            return CollectTopLevelPropertiesAsArguments(toolCallObject);
        }

        static JObject ParseArgumentsHiddenInsideAString(string argumentsJsonText)
        {
            if (JsonRepairer.TryParseJsonObject(argumentsJsonText, out JObject parsedArguments))
            {
                return parsedArguments;
            }

            var repairResult = JsonRepairer.TryRepair(argumentsJsonText);
            return repairResult.IsSuccess ? repairResult.ParsedObject : null;
        }

        // No arguments field at all usually means the model wrote the parameters next to the name.
        static JObject CollectTopLevelPropertiesAsArguments(JObject toolCallObject)
        {
            var argumentsObject = new JObject();

            foreach (var property in toolCallObject.Properties())
            {
                if (IsOneOfTheAcceptedNames(property.Name, k_acceptedNamesForTheToolNameField))
                {
                    continue;
                }
                argumentsObject.Add(property.Name, property.Value.DeepClone());
            }

            return argumentsObject;
        }

        static bool IsOneOfTheAcceptedNames(string propertyName, string[] acceptedPropertyNames)
        {
            foreach (string acceptedPropertyName in acceptedPropertyNames)
            {
                if (string.Equals(propertyName, acceptedPropertyName, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            return false;
        }

        static Dictionary<string, string> ConvertArgumentsToStringDictionary(JObject argumentsObject, out bool didCoerceAnyArgument)
        {
            var arguments = new Dictionary<string, string>();
            didCoerceAnyArgument = false;

            if (argumentsObject == null)
            {
                return arguments;
            }

            foreach (var property in argumentsObject.Properties())
            {
                // Parameter names are lower-case snake_case everywhere in this project, so "Path"
                // and "path" are the same argument.
                string normalisedName = property.Name.Trim().ToLowerInvariant();
                if (!string.Equals(normalisedName, property.Name, StringComparison.Ordinal))
                {
                    didCoerceAnyArgument = true;
                }
                if (arguments.ContainsKey(normalisedName))
                {
                    continue;
                }

                arguments[normalisedName] = ConvertArgumentValueToString(property.Value, out bool didCoerceThisValue);
                didCoerceAnyArgument = didCoerceAnyArgument || didCoerceThisValue;
            }

            return arguments;
        }

        static string ConvertArgumentValueToString(JToken argumentValue, out bool didCoerceValue)
        {
            didCoerceValue = false;

            switch (argumentValue.Type)
            {
                case JTokenType.String:
                    return argumentValue.Value<string>() ?? string.Empty;

                // A number is not counted as a coercion. The wire format asks for start_line and
                // end_line as JSON numbers, so flagging them would make every correct read_file
                // report Coercion and turn the stage signal into noise.
                case JTokenType.Integer:
                case JTokenType.Float:
                    return Convert.ToString(((JValue)argumentValue).Value, CultureInfo.InvariantCulture) ?? string.Empty;

                case JTokenType.Boolean:
                    didCoerceValue = true;
                    return argumentValue.Value<bool>() ? "true" : "false";

                case JTokenType.Null:
                case JTokenType.Undefined:
                    didCoerceValue = true;
                    return string.Empty;

                case JTokenType.Array:
                    didCoerceValue = true;
                    return ConvertArrayArgumentToString((JArray)argumentValue);

                default:
                    didCoerceValue = true;
                    return argumentValue.ToString(Formatting.None);
            }
        }

        // A model that lists file content line by line is trying to write those lines, not a JSON
        // array, so an array of plain values joins with newlines. Anything structured stays JSON,
        // because guessing a separator for it would be worse than handing the tool the raw shape.
        static string ConvertArrayArgumentToString(JArray arrayValue)
        {
            var lines = new List<string>();

            foreach (var element in arrayValue)
            {
                if (element.Type == JTokenType.Object || element.Type == JTokenType.Array)
                {
                    return arrayValue.ToString(Formatting.None);
                }
                lines.Add(element.Type == JTokenType.String ? element.Value<string>() : element.ToString());
            }

            return string.Join("\n", lines);
        }

        // Reading "tool_name" instead of "name" is leniency worth seeing in the stage, the same way
        // a repaired brace is: the model did not write what the wire format asked for.
        static bool IsFieldSpeltDifferentlyFromTheCanonicalName(JProperty property, string[] acceptedPropertyNames)
        {
            return property != null && !string.Equals(property.Name, acceptedPropertyNames[0], StringComparison.Ordinal);
        }
    }
}
