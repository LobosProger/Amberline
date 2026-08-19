using System.Collections.Generic;
using System.Text;
using UnityEngine;

namespace Amberline.Agent
{
    /// <summary>
    /// A GBNF grammar and the assistant prefill it was built against, always produced together.
    /// <para>
    /// They cannot be separated because llama.cpp starts grammar sampling at the first GENERATED
    /// token: text already sitting in the prefill is never matched against the grammar. So if the
    /// prefill ends with "&lt;tool_call&gt;" then the grammar must NOT open with it, or the model is
    /// forced to emit the tag a second time. Handing both halves back from one call makes that a
    /// property of the type instead of a rule someone has to remember.
    /// </para>
    /// </summary>
    public readonly struct ToolCallGrammar
    {
        /// <summary>The GBNF text. Empty means "generate unconstrained".</summary>
        public string GrammarText { get; }

        /// <summary>The assistant prefill this grammar assumes has already been emitted.</summary>
        public string PrefillText { get; }

        public ToolCallGrammar(string grammarText, string prefillText)
        {
            GrammarText = grammarText ?? string.Empty;
            PrefillText = prefillText ?? string.Empty;
        }

        /// <summary>False when there is no grammar and the model may produce anything.</summary>
        public bool IsConstrained => !string.IsNullOrEmpty(GrammarText);

        /// <summary>No grammar and no prefill of our own, so the caller falls back to its default.</summary>
        public static ToolCallGrammar Unconstrained => new ToolCallGrammar(string.Empty, string.Empty);
    }

    // Emits a GBNF grammar for exactly the tools that can run right now, so a malformed tool call
    // is not something the model can produce. Measured to work: on a plain LLMClient the probe
    // grammar `root ::= "GRAMMAR-PROBE-OK"` produced exactly that literal and nothing else.
    //
    // Three things about the shape below are deliberate:
    //
    // 1. THE ENVELOPE IS SPLIT. The opening <tool_call> tag lives in the PREFILL and the closing
    //    </tool_call> tag lives in the GRAMMAR. See ToolCallGrammar for why.
    //
    // 2. THE CLOSING TAG IS ALSO THE STOP SEQUENCE. llama.cpp may end generation at an accepting
    //    state, and the grammar accepts only after </tool_call>. The package exposes no
    //    stop-sequence API at all, so this is the only stop we get for free.
    //
    // 3. EVERYTHING EXCEPT THE VALUES IS A LITERAL. Key names, colons, commas, braces and the tool
    //    name itself are baked in, matching the wire format in SystemPromptText byte for byte,
    //    spaces included. The grammar has to accept exactly what the prompt asks for - anything
    //    narrower would constrain the model into contradicting its own instructions.
    //
    // Note for anyone reading the older `Agent/Grammar` paragraph of the plan: the ASCII-only
    // string rule and the \uXXXX escaping it describes are OBSOLETE. They mitigated an ANSI
    // marshalling problem that was measured not to exist - Mono marshals LPStr as UTF-8 and a
    // Tokenize/Detokenize round trip of "Привет — ok" came back byte-identical. The string rule
    // below is UTF-8 clean and forbids only what JSON itself forbids.
    public static class GbnfGrammarBuilder
    {
        const string k_toolCallCloseTag = "</tool_call>";
        const string k_rootRuleName = "root";
        const string k_alternationRuleName = "tool-call";
        const string k_ruleNamePrefixForTool = "call-";
        const string k_valueRuleName = "value";

        /// <summary>
        /// Builds the grammar for the given tools, together with the prefill it assumes. Pass the
        /// registry's callable tools, never the full seven - a tool with no executor must never
        /// appear here. Returns <see cref="ToolCallGrammar.Unconstrained"/> when the list is empty.
        /// </summary>
        public static ToolCallGrammar BuildGrammarForTools(IReadOnlyList<ToolDefinition> callableTools)
        {
            if (callableTools == null || callableTools.Count == 0)
            {
                Debug.LogWarning("[GbnfGrammarBuilder] No callable tools, so no grammar is emitted and the model generates unconstrained.");
                return ToolCallGrammar.Unconstrained;
            }

            var grammarText = new StringBuilder();
            AppendHeaderComment(grammarText);
            AppendRootRules(grammarText, callableTools);
            AppendOneBranchPerTool(grammarText, callableTools);
            AppendSharedValueRules(grammarText);

            return new ToolCallGrammar(grammarText.ToString(), PromptBuilder.k_toolCallPrefill);
        }

        static void AppendHeaderComment(StringBuilder grammarText)
        {
            AppendGrammarLine(grammarText, "# amberline tool-call grammar, built from the tools that can run right now.");
            AppendGrammarLine(grammarText, "# The opening <tool_call> tag is NOT in here: it is already in the assistant prefill, and a");
            AppendGrammarLine(grammarText, "# grammar only constrains generated tokens, so repeating it would emit the tag twice.");
            AppendGrammarLine(grammarText, "# The closing tag is in here, and it doubles as the stop sequence.");
            AppendGrammarLine(grammarText, string.Empty);
        }

        static void AppendRootRules(StringBuilder grammarText, IReadOnlyList<ToolDefinition> callableTools)
        {
            AppendGrammarLine(grammarText, $"{k_rootRuleName} ::= {k_alternationRuleName} {BuildGbnfLiteral(k_toolCallCloseTag)}");

            var alternationRule = new StringBuilder();
            alternationRule.Append(k_alternationRuleName).Append(" ::=");

            for (int toolIndex = 0; toolIndex < callableTools.Count; toolIndex++)
            {
                if (toolIndex > 0)
                {
                    alternationRule.Append(" |");
                }

                alternationRule.Append(' ').Append(BuildRuleNameForTool(callableTools[toolIndex].Name));
            }

            AppendGrammarLine(grammarText, alternationRule.ToString());
            AppendGrammarLine(grammarText, string.Empty);
        }

        static void AppendOneBranchPerTool(StringBuilder grammarText, IReadOnlyList<ToolDefinition> callableTools)
        {
            AppendGrammarLine(grammarText, "# One branch per tool. Only the values are free - the tool name cannot even be misspelled.");

            foreach (var toolDefinition in callableTools)
            {
                AppendGrammarLine(grammarText, BuildBranchRuleForTool(toolDefinition));
            }

            AppendGrammarLine(grammarText, string.Empty);
        }

        // Produces, for read_file(path, start_line, end_line):
        // call-read-file ::= "{\"name\": \"read_file\", \"arguments\": {\"path\": " value ", \"start_line\": " value ", \"end_line\": " value "}}"
        static string BuildBranchRuleForTool(ToolDefinition toolDefinition)
        {
            var branchRule = new StringBuilder();
            branchRule.Append(BuildRuleNameForTool(toolDefinition.Name)).Append(" ::= ");

            // The spaces after every colon and comma are copied from the wire format in
            // SystemPromptText. They are part of the literal, so the model cannot drop them.
            var openingJson = new StringBuilder();
            openingJson.Append("{\"name\": \"").Append(toolDefinition.Name).Append("\", \"arguments\": {");

            if (toolDefinition.ParameterNames.Count == 0)
            {
                openingJson.Append("}}");
                branchRule.Append(BuildGbnfLiteral(openingJson.ToString()));
                return branchRule.ToString();
            }

            for (int parameterIndex = 0; parameterIndex < toolDefinition.ParameterNames.Count; parameterIndex++)
            {
                string separatorBeforeKey = parameterIndex == 0 ? string.Empty : ", ";
                string keyText = $"{separatorBeforeKey}\"{toolDefinition.ParameterNames[parameterIndex]}\": ";

                if (parameterIndex == 0)
                {
                    openingJson.Append(keyText);
                    branchRule.Append(BuildGbnfLiteral(openingJson.ToString()));
                }
                else
                {
                    branchRule.Append(' ').Append(BuildGbnfLiteral(keyText));
                }

                branchRule.Append(' ').Append(k_valueRuleName);
            }

            branchRule.Append(' ').Append(BuildGbnfLiteral("}}"));
            return branchRule.ToString();
        }

        // GBNF rule names are conservative about punctuation, and older parsers reject underscores
        // outright, so read_file becomes call-read-file.
        static string BuildRuleNameForTool(string toolName)
        {
            var ruleName = new StringBuilder(k_ruleNamePrefixForTool);

            foreach (char toolNameCharacter in toolName)
            {
                bool isSafeInRuleName = char.IsLetterOrDigit(toolNameCharacter) && toolNameCharacter < 128;
                ruleName.Append(isSafeInRuleName ? char.ToLowerInvariant(toolNameCharacter) : '-');
            }

            return ruleName.ToString();
        }

        static void AppendSharedValueRules(StringBuilder grammarText)
        {
            AppendGrammarLine(grammarText, "# A value is a JSON string or a bare integer. Both spellings have to be accepted, because the");
            AppendGrammarLine(grammarText, "# system prompt shows line numbers unquoted and every other argument quoted - allowing only");
            AppendGrammarLine(grammarText, "# one of them would force the model to contradict the instructions it was given.");
            AppendGrammarLine(grammarText, $"{k_valueRuleName} ::= string | integer");
            AppendGrammarLine(grammarText, string.Empty);
            AppendGrammarLine(grammarText, "# UTF-8 clean. Non-ASCII round-trips byte-identical through the native layer, so there is no");
            AppendGrammarLine(grammarText, "# ASCII-only rule here. The only characters that must be escaped are the ones JSON itself");
            AppendGrammarLine(grammarText, "# forbids raw inside a string: the quote, the backslash, and the control range 00-1F.");

            // Built through BuildGbnfLiteral rather than typed out, because a hand-written GBNF
            // literal for a single double-quote character is four nested levels of escaping and a
            // wrong one fails deep inside llama.cpp with an unhelpful message.
            AppendGrammarLine(grammarText, $"string ::= {BuildGbnfLiteral("\"")} char* {BuildGbnfLiteral("\"")}");
            AppendGrammarLine(grammarText, $@"char ::= [^\""\\\x00-\x1F] | {BuildGbnfLiteral("\\")} escape");
            AppendGrammarLine(grammarText, @"escape ::= [\""\\/bfnrt] | ""u"" hex hex hex hex");
            AppendGrammarLine(grammarText, "hex ::= [0-9a-fA-F]");
            AppendGrammarLine(grammarText, @"integer ::= ""-""? [0-9]+");
        }

        // Wraps raw text as a GBNF string literal. Inside one, a backslash and a double quote are
        // the only characters that need escaping, and the backslash must be handled first or the
        // escapes added for the quotes would themselves get escaped.
        static string BuildGbnfLiteral(string rawText)
        {
            var literal = new StringBuilder("\"");

            foreach (char rawCharacter in rawText)
            {
                if (rawCharacter == '\\' || rawCharacter == '"')
                {
                    literal.Append('\\');
                }

                literal.Append(rawCharacter);
            }

            literal.Append('"');
            return literal.ToString();
        }

        // Always '\n', never Environment.NewLine: the grammar must be the same bytes on every
        // machine, and StringBuilder.AppendLine would quietly write CRLF on Windows.
        static void AppendGrammarLine(StringBuilder grammarText, string lineText)
        {
            grammarText.Append(lineText).Append('\n');
        }
    }
}
