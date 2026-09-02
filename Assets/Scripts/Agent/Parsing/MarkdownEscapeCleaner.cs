using System.Text;

namespace Amberline.Agent
{
    // Takes the markdown escaping back out of a model's reply.
    //
    // This exists because of one measured run and it was the most expensive defect in the product.
    // mistral-7b-instruct-v0.2 writes underscores the way it would inside a markdown document -
    // escaped. Its plan for "create hello.py" came back as, verbatim:
    //
    //     A new file, so write\_file with the whole content.
    //     <tool\_call>{"name": "write\_file", "arguments": {"path": "hello.py", ...}}</tool\_call>
    //
    // That is a completely correct tool call. Every single thing downstream missed it: the loop
    // looks for "<tool_call>" and there is no such substring, so the fast path did not fire; the
    // ACT pass then ran with a plan that already looked finished in front of it and called finish;
    // and the run reported "Wrote hello.py." for a file that was never created. Three separate
    // symptoms - "it does not create files", "it just writes the answer in the thinking block",
    // "it never calls the tools" - all of them this one backslash.
    //
    // The rule that makes this safe: NONE of the characters below is a legal JSON escape. A
    // backslash in front of one cannot be anything a correct reply meant to write, so removing it
    // can only ever repair. The escapes JSON does define - \\ \" \/ \b \f \n \r \t \uXXXX - are
    // left exactly as they are, and a doubled backslash is consumed as a pair so that the second
    // half of it is never mistaken for one of these.
    public static class MarkdownEscapeCleaner
    {
        // Everything a model trained on markdown reaches for a backslash to protect. The underscore
        // is the one that actually happens, several times per reply; the rest cost nothing to
        // cover and each would break a tool call in exactly the same way.
        const string k_charactersThatAreOnlyEverEscapedForMarkdown = "_*#<>[]()+-.!`~|=";

        /// <summary>
        /// Returns <paramref name="modelReplyText"/> with markdown-style backslash escapes removed
        /// and every legal JSON escape left untouched. Returns the input unchanged when it holds no
        /// backslash at all, which is the usual case and the reason this is cheap enough to run
        /// over every completion.
        /// </summary>
        public static string RemoveMarkdownEscapesFromText(string modelReplyText)
        {
            if (string.IsNullOrEmpty(modelReplyText) || modelReplyText.IndexOf('\\') < 0)
            {
                return modelReplyText;
            }

            var cleanedText = new StringBuilder(modelReplyText.Length);

            for (int characterIndex = 0; characterIndex < modelReplyText.Length; characterIndex++)
            {
                char character = modelReplyText[characterIndex];

                bool isTheLastCharacter = characterIndex + 1 >= modelReplyText.Length;
                if (character != '\\' || isTheLastCharacter)
                {
                    cleanedText.Append(character);
                    continue;
                }

                char characterAfterTheBackslash = modelReplyText[characterIndex + 1];

                // An escaped backslash is taken whole, so the one it escapes can never be read as
                // the opening of another escape.
                if (characterAfterTheBackslash == '\\')
                {
                    cleanedText.Append(character).Append(characterAfterTheBackslash);
                    characterIndex++;
                    continue;
                }

                if (k_charactersThatAreOnlyEverEscapedForMarkdown.IndexOf(characterAfterTheBackslash) >= 0)
                {
                    cleanedText.Append(characterAfterTheBackslash);
                    characterIndex++;
                    continue;
                }

                cleanedText.Append(character);
            }

            return cleanedText.ToString();
        }
    }
}
