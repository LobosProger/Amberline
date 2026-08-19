using System.Collections.Generic;
using System.Globalization;
using Amberline.Agent;
using UnityEngine;
using UnityEngine.UIElements;

namespace Amberline.Ui
{
    /// <summary>
    /// Renders a line diff: a bordered block with a header, then one row per line - added in
    /// phosphor green, removed in red, unchanged in dim amber, each with its line number in a
    /// gutter. Holds no state; it turns a list of <see cref="DiffLine"/> into visual elements.
    /// <para>
    /// Two ways in, because a diff is shown in two places. <see cref="AppendDiffToLog"/> puts one
    /// in the scrolling terminal log, which backs /diff. <see cref="BuildDiffElement"/> hands the
    /// block back unattached so <see cref="ApprovalCardView"/> can put it inside a card.
    /// </para>
    /// <para>
    /// The rows it renders are <see cref="Amberline.Agent.DiffLine"/> exactly as the differ built
    /// them - there is no presentation copy of that type. The dependency points from the UI to the
    /// agent core and never the other way, which is the direction the whole project is wired in.
    /// </para>
    /// </summary>
    public class DiffView : MonoBehaviour
    {
        [Header("Templates")]
        [SerializeField] VisualTreeAsset _diffBlockTemplate;
        [SerializeField] VisualTreeAsset _diffRowTemplate;

        VisualElement _terminalContent;

        // A whole-file rewrite is a legitimate change and the model will ask for one. Dropping a
        // thousand rows into the log would bury the run it is meant to explain, and every one of
        // them is a laid-out element for the rest of the session, so the tail is summarised.
        const int k_maximumRenderedDiffRows = 120;

        // Rows do not wrap, so a minified file would otherwise render as one row a mile wide and
        // push the horizontal extent of the whole log out with it.
        const int k_maximumCharactersOnOneDiffRow = 200;

        const string k_terminalContentElementName = "terminal-content";
        const string k_diffHeaderElementName = "diff-block__header";
        const string k_diffRowsElementName = "diff-block__rows";
        const string k_diffRowNumberElementName = "diff-row__number";
        const string k_diffRowTextElementName = "diff-row__text";

        const string k_addedRowTextClassName = "diff-row__text--added";
        const string k_removedRowTextClassName = "diff-row__text--removed";
        const string k_contextRowTextClassName = "diff-row__text--context";
        const string k_noteRowTextClassName = "diff-row__text--note";

        const string k_addedRowMarker = "+ ";
        const string k_removedRowMarker = "- ";
        const string k_contextRowMarker = "  ";

        void OnEnable()
        {
            _terminalContent = GetComponent<UIDocument>().rootVisualElement.Q<VisualElement>(k_terminalContentElementName);
        }

        /// <summary>
        /// Appends a diff block to the scrolling log. The header is usually the workspace-relative
        /// path the change applies to.
        /// </summary>
        public void AppendDiffToLog(string headerText, IReadOnlyList<DiffLine> diffLines)
        {
            if (_terminalContent == null)
            {
                Debug.LogWarning("[DiffView] There is no terminal content element, so the diff was not shown.");
                return;
            }

            _terminalContent.Add(BuildDiffElement(headerText, diffLines));
        }

        /// <summary>
        /// Builds the block without attaching it anywhere, so a caller can place it inside its own
        /// element. Never returns null - an empty or missing diff comes back as a block that says so.
        /// </summary>
        public VisualElement BuildDiffElement(string headerText, IReadOnlyList<DiffLine> diffLines)
        {
            var diffBlockContainer = _diffBlockTemplate.Instantiate();
            var headerLabel = diffBlockContainer.Q<Label>(k_diffHeaderElementName);
            var rowsContainer = diffBlockContainer.Q<VisualElement>(k_diffRowsElementName);

            headerLabel.text = BuildHeaderTextWithCounts(headerText, diffLines);
            FillRowsContainer(rowsContainer, diffLines);

            return diffBlockContainer;
        }

        // The counts belong in the header rather than under the rows, because the reader decides
        // whether to read the body at all from "+3 -1" long before they read the body.
        static string BuildHeaderTextWithCounts(string headerText, IReadOnlyList<DiffLine> diffLines)
        {
            int addedLineCount = CountLinesOfKind(diffLines, DiffLineKind.Added);
            int removedLineCount = CountLinesOfKind(diffLines, DiffLineKind.Removed);

            string headerTextToShow = string.IsNullOrWhiteSpace(headerText) ? "diff" : headerText;

            return $"{headerTextToShow}   +{addedLineCount} -{removedLineCount}";
        }

        static int CountLinesOfKind(IReadOnlyList<DiffLine> diffLines, DiffLineKind diffLineKind)
        {
            if (diffLines == null)
            {
                return 0;
            }

            int matchingLineCount = 0;

            foreach (var diffLine in diffLines)
            {
                if (diffLine.Kind == diffLineKind)
                {
                    matchingLineCount++;
                }
            }

            return matchingLineCount;
        }

        void FillRowsContainer(VisualElement rowsContainer, IReadOnlyList<DiffLine> diffLines)
        {
            if (diffLines == null || diffLines.Count == 0)
            {
                rowsContainer.Add(CreateNoteRow("(no changes)"));
                return;
            }

            int rowsToRenderCount = Mathf.Min(diffLines.Count, k_maximumRenderedDiffRows);

            for (int diffLineIndex = 0; diffLineIndex < rowsToRenderCount; diffLineIndex++)
            {
                rowsContainer.Add(CreateDiffRow(diffLines[diffLineIndex]));
            }

            int hiddenRowCount = diffLines.Count - rowsToRenderCount;

            if (hiddenRowCount > 0)
            {
                rowsContainer.Add(CreateNoteRow($"... {hiddenRowCount} more lines not shown"));
            }
        }

        VisualElement CreateDiffRow(DiffLine diffLine)
        {
            var diffRowContainer = _diffRowTemplate.Instantiate();
            var numberLabel = diffRowContainer.Q<Label>(k_diffRowNumberElementName);
            var textLabel = diffRowContainer.Q<Label>(k_diffRowTextElementName);

            numberLabel.text = BuildGutterTextFor(diffLine);

            textLabel.text = ResolveMarkerFor(diffLine.Kind) + ShortenLineThatIsTooWide(diffLine.Text);
            textLabel.AddToClassList(ResolveTextClassNameFor(diffLine.Kind));

            return diffRowContainer;
        }

        // One gutter column, so each line shows the only number the reader can act on: where the
        // line will be in the file they end up with, and for a removed line - which has no place in
        // that file - where it was in the file they have now. A folded run belongs to neither, so
        // it shows nothing.
        static string BuildGutterTextFor(DiffLine diffLine)
        {
            int lineNumberToShow = diffLine.Kind == DiffLineKind.Removed
                ? diffLine.OldLineNumber
                : diffLine.NewLineNumber;

            return lineNumberToShow > 0
                ? lineNumberToShow.ToString(CultureInfo.InvariantCulture)
                : string.Empty;
        }

        // The marker is written into the text rather than drawn as its own element, so a diff can be
        // selected and copied out of the terminal as the plain text a diff normally is.
        static string ResolveMarkerFor(DiffLineKind diffLineKind)
        {
            return diffLineKind switch
            {
                DiffLineKind.Added   => k_addedRowMarker,
                DiffLineKind.Removed => k_removedRowMarker,
                DiffLineKind.Skipped => string.Empty,
                _                    => k_contextRowMarker
            };
        }

        static string ShortenLineThatIsTooWide(string lineText)
        {
            // Tabs render as a single narrow glyph in UI Toolkit, which throws the indentation of a
            // whole diff out. Four spaces is what the reader expects to see.
            string lineTextWithoutTabs = lineText.Replace("\t", "    ");

            return lineTextWithoutTabs.Length <= k_maximumCharactersOnOneDiffRow
                ? lineTextWithoutTabs
                : lineTextWithoutTabs.Substring(0, k_maximumCharactersOnOneDiffRow) + " ...";
        }

        // A skipped run is the differ talking about the file rather than showing it - its text
        // already reads "... 42 unchanged lines ..." - so it takes the same dim note style the
        // block uses when it talks about itself.
        static string ResolveTextClassNameFor(DiffLineKind diffLineKind)
        {
            return diffLineKind switch
            {
                DiffLineKind.Added   => k_addedRowTextClassName,
                DiffLineKind.Removed => k_removedRowTextClassName,
                DiffLineKind.Skipped => k_noteRowTextClassName,
                _                    => k_contextRowTextClassName
            };
        }

        // The "N more lines" line and the empty-diff line share the row shape so the block keeps
        // one column layout, but carry no number and no marker.
        VisualElement CreateNoteRow(string noteText)
        {
            var diffRowContainer = _diffRowTemplate.Instantiate();
            var numberLabel = diffRowContainer.Q<Label>(k_diffRowNumberElementName);
            var textLabel = diffRowContainer.Q<Label>(k_diffRowTextElementName);

            numberLabel.text = string.Empty;
            textLabel.text = noteText;
            textLabel.AddToClassList(k_noteRowTextClassName);

            return diffRowContainer;
        }
    }
}
