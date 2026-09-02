using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEngine;

namespace Amberline.Agent
{
    // The half of the pinned block that changes with the workspace: the name of the folder the
    // agent is in, and what is already at the top of it. AgentRunner appends this under
    // SystemPromptText and pins the two together.
    //
    // It exists because of a bug that looked like a path bug and was not one. Every path example in
    // the system prompt used to be spelled "src/Player.cs", the model was never shown the real
    // project, and so it wrote its work to a src folder it invented on the spot - correctly, as far
    // as the sandbox was concerned, since src was inside the workspace. A listing is the cheapest
    // possible fix: with the real folder names in front of it the model stops guessing.
    //
    // Two properties are load-bearing:
    //
    // 1. IT IS BUILT ONCE PER WORKSPACE, not once per turn. The pinned block has to stay
    //    byte-identical for the whole session or llama.cpp re-reads the entire prompt on every
    //    turn. That also rules out anything that varies on its own - no file sizes, no timestamps.
    //
    // 2. IT IS ORDERED AND CAPPED. Directory enumeration order is not guaranteed to be stable
    //    across runs, so the entries are sorted; and a folder with a thousand files would otherwise
    //    spend the whole context window on a listing nobody asked for.
    public static class WorkspaceBriefText
    {
        // Folders first, then files, each alphabetically - the shape a person expects from a
        // listing, and stable across sessions, which point 1 above depends on.
        const int k_maximumEntriesShown = 30;
        const int k_maximumBriefCharacters = 500;
        const string k_separatorBetweenEntries = "  ";

        const string k_ruleAboutWhereToWrite =
            "Write new files at the top level of this folder unless the task names a folder. Do not invent one.";

        // The listing below hides every dot-prefixed entry, so .git never appears in it and the model
        // has no evidence at all that it is standing in a repository - it does not reach for git on
        // its own, and when it does, it reads the first failure as proof git does not exist here. One
        // sentence is cheaper than either mistake. The folder itself stays closed: PathSandbox still
        // refuses to read anything under .git.
        const string k_noteAboutAGitRepository =
            "\nThis folder is a git repository, so run_command can run git in it.";

        // Unity regenerates a .csproj per assembly and a solution beside them every time it
        // reloads. Measured on this project: eleven of them, which filled the cap and pushed the
        // folders the agent actually needs out of the listing entirely. None of them is ever the
        // answer to a coding task, and the deny-list in PathSandbox is about FOLDERS, so this is
        // the one place that can drop them.
        static readonly string[] k_extensionsOfGeneratedProjectFiles = { ".csproj", ".sln", ".slnx", ".user", ".suo" };

        /// <summary>
        /// The workspace paragraph that goes underneath the system prompt. Never throws and never
        /// returns empty: a folder that cannot be read still produces the name and the rule, because
        /// a model told nothing about its workspace is exactly the state this class exists to end.
        /// </summary>
        public static string BuildBriefForWorkspaceFolder(string workspaceFolderPath, PathSandbox pathSandbox)
        {
            string folderName = TakeNameOfFolder(workspaceFolderPath);
            string noteAboutAGitRepository = BuildNoteAboutAGitRepositoryIfThereIsOne(workspaceFolderPath);
            string listingOfTheTopLevel = BuildListingOfTheTopLevel(workspaceFolderPath, pathSandbox);

            if (listingOfTheTopLevel.Length == 0)
            {
                return $"You are working in the project folder \"{folderName}\", which is empty.\n" +
                       $"{k_ruleAboutWhereToWrite}{noteAboutAGitRepository}";
            }

            return $"You are working in the project folder \"{folderName}\". Its top level:\n" +
                   $"{listingOfTheTopLevel}\n{k_ruleAboutWhereToWrite}{noteAboutAGitRepository}";
        }

        static string TakeNameOfFolder(string workspaceFolderPath)
        {
            if (string.IsNullOrEmpty(workspaceFolderPath))
            {
                return "project";
            }

            // A path ending in a separator makes GetFileName return empty, and a drive root has no
            // name at all - in both cases the full path is the most useful thing left to say.
            string folderName = Path.GetFileName(workspaceFolderPath.TrimEnd(
                Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));

            return folderName.Length == 0 ? workspaceFolderPath : folderName;
        }

        // Only the presence of the folder is reported, never anything inside it, so the brief stays
        // as stable across a session as point 1 above needs it to be.
        static string BuildNoteAboutAGitRepositoryIfThereIsOne(string workspaceFolderPath)
        {
            if (string.IsNullOrEmpty(workspaceFolderPath)) return string.Empty;

            try
            {
                bool isAGitRepository = Directory.Exists(Path.Combine(workspaceFolderPath, ".git"));

                return isAGitRepository ? k_noteAboutAGitRepository : string.Empty;
            }
            catch (Exception exception)
            {
                // A path that cannot even be combined is not a reason to lose the whole brief.
                Debug.LogWarning($"[WorkspaceBriefText] The workspace could not be checked for a git repository: {exception.GetType().Name}: {exception.Message}");
                return string.Empty;
            }
        }

        static string BuildListingOfTheTopLevel(string workspaceFolderPath, PathSandbox pathSandbox)
        {
            var entryNames = CollectTopLevelEntryNames(workspaceFolderPath, pathSandbox);

            if (entryNames.Count == 0)
            {
                return string.Empty;
            }

            return JoinEntriesWithinBudget(entryNames);
        }

        // Folders are listed with a trailing slash and before the files, so the model can tell the
        // two apart without a second tool call. Anything the sandbox would refuse to open is left
        // out: showing Library or .git only invites a read_file that is going to be rejected.
        static List<string> CollectTopLevelEntryNames(string workspaceFolderPath, PathSandbox pathSandbox)
        {
            var folderNames = new List<string>();
            var fileNames = new List<string>();

            try
            {
                foreach (string folderPath in Directory.GetDirectories(workspaceFolderPath))
                {
                    string folderName = Path.GetFileName(folderPath);

                    if (IsEntryWorthShowing(folderName, pathSandbox))
                        folderNames.Add(folderName + "/");
                }

                foreach (string filePath in Directory.GetFiles(workspaceFolderPath))
                {
                    string fileName = Path.GetFileName(filePath);

                    if (IsEntryWorthShowing(fileName, pathSandbox))
                        fileNames.Add(fileName);
                }
            }
            catch (Exception exception)
            {
                // A folder that cannot be read is not a reason to fail the session - the caller
                // falls back to naming the folder and nothing else.
                Debug.LogWarning($"[WorkspaceBriefText] The workspace folder could not be listed: {exception.GetType().Name}: {exception.Message}");
                return new List<string>();
            }

            folderNames.Sort(StringComparer.OrdinalIgnoreCase);
            fileNames.Sort(StringComparer.OrdinalIgnoreCase);

            folderNames.AddRange(fileNames);
            return folderNames;
        }

        static bool IsEntryWorthShowing(string entryName, PathSandbox pathSandbox)
        {
            if (string.IsNullOrEmpty(entryName)) return false;

            // Hidden entries are the user's tooling, not their project: .vs, .idea, .vscode.
            if (entryName.StartsWith(".", StringComparison.Ordinal)) return false;

            if (IsGeneratedProjectFile(entryName)) return false;

            // Unity writes one .meta beside every asset, so a listing that kept them would be half
            // meta files - and the sandbox refuses to open them anyway.
            return pathSandbox == null || !pathSandbox.IsDeniedFileOrDirectoryName(entryName);
        }

        static bool IsGeneratedProjectFile(string entryName)
        {
            foreach (string generatedExtension in k_extensionsOfGeneratedProjectFiles)
            {
                if (entryName.EndsWith(generatedExtension, StringComparison.OrdinalIgnoreCase))
                    return true;
            }

            return false;
        }

        // Both caps end the same way: with "..." in place of what was dropped, so the model is
        // never left believing it has seen the whole folder.
        static string JoinEntriesWithinBudget(List<string> entryNames)
        {
            var listingBuilder = new StringBuilder();
            int entriesWritten = 0;

            foreach (string entryName in entryNames)
            {
                if (entriesWritten >= k_maximumEntriesShown) break;

                int lengthAfterThisEntry = listingBuilder.Length + entryName.Length + k_separatorBetweenEntries.Length;
                if (lengthAfterThisEntry > k_maximumBriefCharacters) break;

                if (entriesWritten > 0)
                    listingBuilder.Append(k_separatorBetweenEntries);

                listingBuilder.Append(entryName);
                entriesWritten++;
            }

            if (entriesWritten < entryNames.Count)
                listingBuilder.Append(k_separatorBetweenEntries).Append("...");

            return listingBuilder.ToString();
        }
    }
}
