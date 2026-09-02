using System;
using System.Collections.Generic;
using System.IO;

namespace Amberline.Agent
{
    // The security boundary of the whole product. Every path the model supplies passes through
    // here before any file API sees it, and nothing else in the agent is allowed to turn model
    // text into an absolute path.
    //
    // Containment is decided with Path.GetRelativePath, NOT with StartsWith on the resolved path.
    // A StartsWith comparison is not segment aligned, and the previous agent shipped exactly that:
    // with a workspace root of "C:\Test" it accepted "..\TestDirectory\x.txt" - which resolves to
    // "C:\TestDirectory\x.txt" - for reads AND for writes, and with a root of "C:\proj" it accepted
    // every file under a sibling "C:\proj2". GetRelativePath answers the question that was really
    // being asked: what is the route from the root to this path, and does that route begin by
    // walking out of the root.
    //
    // Symbolic links, junctions and mount points are REFUSED, never followed. Resolving one would
    // need FileSystemInfo.ResolveLinkTarget, a .NET 6 API that does not exist on Unity's
    // .NET Standard 2.1 surface, so a link is detected through FileAttributes.ReparsePoint and
    // turned into a rejection instead. Refusing is also the safer default: a followed link is a
    // second, invisible way out of the sandbox.
    //
    // Every rejection produces a reason string written for the MODEL to read, because the model is
    // the one that has to choose a different path on the next turn.
    public class PathSandbox
    {
        readonly string _workspaceRootPath;

        // Folders the agent must never see. Matched against EVERY segment of a path, so
        // "src/Library/x.cs" is refused just as "Library/x.cs" is. Unity's Library and Temp are
        // gigabytes of generated data, ProjectSettings and UserSettings are the Editor's own state,
        // and .git is the user's history - all of them drown the context window and none of them is
        // ever the answer to a coding task. "bin" and "obj" are the same story one level down: after
        // the agent runs its first build, compiled output would otherwise show up in every listing
        // of the project it just built.
        static readonly HashSet<string> k_deniedPathSegmentNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "Library", "Temp", "bin", "obj", "Logs", "UserSettings", ".git", "ProjectSettings"
        };

        // Windows resolves these names as DEVICES no matter which folder they appear in and no
        // matter what extension follows, so "docs/nul.txt" is the null device and not a file. This
        // repository once carried that scar as a stray "nul" file in its root, created by a shell
        // redirect that meant to throw output away; git cannot even check such a file out on Windows.
        static readonly HashSet<string> k_windowsReservedDeviceNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "CON", "PRN", "AUX", "NUL",
            "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
            "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9"
        };

        // Wildcards would turn one path into many, and the rest are illegal in a Windows file name
        // anyway. The colon is checked separately because it carries two distinct attacks: a drive
        // letter ("C:\...") and an alternate data stream ("Player.cs:hidden").
        static readonly char[] k_forbiddenPathCharacters = { '*', '?', '"', '<', '>', '|' };

        const string k_metaFileExtension = ".meta";
        const int k_maximumSuppliedPathLength = 400;

        /// <summary>
        /// The one folder the agent may touch. Pass the workspace root the user granted; an empty
        /// or unusable folder leaves the sandbox closed, and every path is then rejected with a
        /// reason rather than an exception.
        /// </summary>
        public PathSandbox(string workspaceRootPath)
        {
            _workspaceRootPath = NormalizeWorkspaceRootPath(workspaceRootPath);
        }

        static string NormalizeWorkspaceRootPath(string workspaceRootPath)
        {
            if (string.IsNullOrWhiteSpace(workspaceRootPath))
            {
                return string.Empty;
            }

            try
            {
                string fullRootPath = Path.GetFullPath(workspaceRootPath.Trim());
                return fullRootPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            }
            catch (Exception)
            {
                // A root we cannot even resolve leaves the sandbox closed. It must not throw here:
                // the constructor runs while the scene is loading, long before any tool call.
                return string.Empty;
            }
        }

        /// <summary>The absolute workspace root, or an empty string when no usable folder was given.</summary>
        public string WorkspaceRootPath => _workspaceRootPath;

        /// <summary>True when a usable workspace folder was given and paths can be resolved at all.</summary>
        public bool IsWorkspaceFolderAvailable => _workspaceRootPath.Length > 0;

        /// <summary>
        /// Turns a model-supplied relative path into a validated absolute path. Returns false and
        /// fills <paramref name="rejectionReason"/> with a sentence meant for the model whenever the
        /// path is refused. Never throws.
        /// </summary>
        public bool TryResolvePath(string modelSuppliedPath, out string absolutePath, out string rejectionReason)
        {
            absolutePath = null;
            rejectionReason = null;

            if (!IsWorkspaceFolderAvailable)
            {
                rejectionReason = "no project folder is open, so no file can be reached. Tell the user to open one.";
                return false;
            }

            string cleanedRelativePath = CleanUpSuppliedPath(modelSuppliedPath);

            if (!IsSuppliedPathShapeAllowed(cleanedRelativePath, out rejectionReason))
            {
                return false;
            }

            if (!AreAllPathSegmentsAllowed(cleanedRelativePath, out rejectionReason))
            {
                return false;
            }

            if (!TryCombineWithWorkspaceRoot(cleanedRelativePath, out string candidateFullPath))
            {
                rejectionReason = "that is not a usable path. Give a plain path relative to the project folder, like Player.cs";
                return false;
            }

            if (!IsInsideWorkspaceRoot(candidateFullPath, out rejectionReason))
            {
                return false;
            }

            if (!IsFreeOfReparsePoints(candidateFullPath, out rejectionReason))
            {
                return false;
            }

            absolutePath = candidateFullPath;
            return true;
        }

        // Models quote paths, prefix them with "./" and mix slash directions inside one string.
        // None of that is an attack, so it is cleaned up instead of rejected.
        static string CleanUpSuppliedPath(string modelSuppliedPath)
        {
            if (string.IsNullOrWhiteSpace(modelSuppliedPath))
            {
                return string.Empty;
            }

            string cleanedPath = modelSuppliedPath.Trim().Trim('"', '\'').Trim();
            cleanedPath = cleanedPath.Replace('\\', '/');

            while (cleanedPath.Contains("//"))
            {
                cleanedPath = cleanedPath.Replace("//", "/");
            }

            // ".", "./" and "/" all mean the project root in the way a model writes them.
            if (cleanedPath == "." || cleanedPath == "./" || cleanedPath == "/")
            {
                return string.Empty;
            }

            if (cleanedPath.StartsWith("./", StringComparison.Ordinal))
            {
                cleanedPath = cleanedPath.Substring(2);
            }

            return cleanedPath.TrimEnd('/');
        }

        static bool IsSuppliedPathShapeAllowed(string cleanedRelativePath, out string rejectionReason)
        {
            rejectionReason = null;

            if (cleanedRelativePath.Length > k_maximumSuppliedPathLength)
            {
                rejectionReason = $"that path is longer than {k_maximumSuppliedPathLength} characters, which no real file has. Give a short path relative to the project folder.";
                return false;
            }

            if (ContainsControlCharacter(cleanedRelativePath))
            {
                rejectionReason = "that path contains control characters. Write the path as plain text, like Player.cs";
                return false;
            }

            int forbiddenCharacterIndex = cleanedRelativePath.IndexOfAny(k_forbiddenPathCharacters);

            if (forbiddenCharacterIndex >= 0)
            {
                rejectionReason = $"the character {cleanedRelativePath[forbiddenCharacterIndex]} is not allowed in a path. Wildcards do not work here - name one exact file or folder, and use grep to search.";
                return false;
            }

            // One rule catches both a drive letter and an alternate data stream.
            if (cleanedRelativePath.IndexOf(':') >= 0)
            {
                rejectionReason = "a colon is not allowed in a path, so no drive letters and no data streams. Give a path relative to the project folder, like Player.cs";
                return false;
            }

            if (cleanedRelativePath.StartsWith("/", StringComparison.Ordinal) || Path.IsPathRooted(cleanedRelativePath))
            {
                rejectionReason = "absolute paths are not allowed. Give a path relative to the project folder, like Player.cs";
                return false;
            }

            if (cleanedRelativePath.StartsWith("~", StringComparison.Ordinal))
            {
                rejectionReason = "home-folder paths like ~/... are not allowed. Give a path relative to the project folder.";
                return false;
            }

            return true;
        }

        static bool ContainsControlCharacter(string text)
        {
            foreach (char character in text)
            {
                if (character < ' ')
                {
                    return true;
                }
            }

            return false;
        }

        static bool AreAllPathSegmentsAllowed(string cleanedRelativePath, out string rejectionReason)
        {
            rejectionReason = null;

            if (cleanedRelativePath.Length == 0)
            {
                return true;
            }

            string[] pathSegments = cleanedRelativePath.Split('/');

            foreach (string pathSegment in pathSegments)
            {
                if (pathSegment == "..")
                {
                    rejectionReason = "the path contains .. which is not allowed. Every path must stay inside the project folder.";
                    return false;
                }

                // Windows silently drops trailing dots and spaces, so "Library ." and "Library" are
                // the same folder. Compare the trimmed form or the deny-list is bypassed by a space.
                string comparableSegment = pathSegment.TrimEnd('.', ' ');

                if (comparableSegment.Length == 0)
                {
                    continue;
                }

                if (IsWindowsReservedDeviceName(comparableSegment))
                {
                    rejectionReason = $"{pathSegment} is a reserved Windows device name, not a file. Choose a different name.";
                    return false;
                }

                if (comparableSegment.EndsWith(k_metaFileExtension, StringComparison.OrdinalIgnoreCase))
                {
                    rejectionReason = "meta files belong to Unity and the agent must never read or write one. Work on the file next to it instead.";
                    return false;
                }

                if (k_deniedPathSegmentNames.Contains(comparableSegment))
                {
                    rejectionReason = $"{comparableSegment} is generated or tool-owned and is closed to the agent. Look in the project source folders instead.";
                    return false;
                }
            }

            return true;
        }

        static bool IsWindowsReservedDeviceName(string pathSegment)
        {
            // The device name wins even with an extension, so "nul.txt" is still the null device.
            int firstDotIndex = pathSegment.IndexOf('.');
            string nameWithoutExtension = firstDotIndex < 0 ? pathSegment : pathSegment.Substring(0, firstDotIndex);
            nameWithoutExtension = nameWithoutExtension.TrimEnd('.', ' ');

            return k_windowsReservedDeviceNames.Contains(nameWithoutExtension);
        }

        bool TryCombineWithWorkspaceRoot(string cleanedRelativePath, out string candidateFullPath)
        {
            candidateFullPath = null;

            try
            {
                string combinedPath = cleanedRelativePath.Length == 0
                    ? _workspaceRootPath
                    : Path.Combine(_workspaceRootPath, cleanedRelativePath);

                candidateFullPath = Path.GetFullPath(combinedPath)
                    .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

                return candidateFullPath.Length > 0;
            }
            catch (Exception)
            {
                return false;
            }
        }

        // The containment test itself. Everything above only removes obviously bad input; this is
        // the check that actually decides whether the resolved path is inside the workspace.
        bool IsInsideWorkspaceRoot(string candidateFullPath, out string rejectionReason)
        {
            rejectionReason = null;

            string routeFromRootToCandidate;

            try
            {
                routeFromRootToCandidate = Path.GetRelativePath(_workspaceRootPath, candidateFullPath);
            }
            catch (Exception)
            {
                rejectionReason = "that path could not be checked against the project folder. Give a simple relative path, like Player.cs";
                return false;
            }

            // A different drive or a UNC share comes back rooted, because there is no relative
            // route from the workspace root to it at all.
            if (Path.IsPathRooted(routeFromRootToCandidate))
            {
                rejectionReason = "that path is outside the project folder. Only files inside it can be reached.";
                return false;
            }

            string normalizedRoute = routeFromRootToCandidate.Replace('\\', '/');

            if (normalizedRoute == ".." || normalizedRoute.StartsWith("../", StringComparison.Ordinal))
            {
                rejectionReason = "that path leads out of the project folder. Only files inside it can be reached.";
                return false;
            }

            return true;
        }

        // Checked on every component below the root, not only on the last one: a link placed on an
        // intermediate folder redirects everything beneath it just as effectively.
        bool IsFreeOfReparsePoints(string candidateFullPath, out string rejectionReason)
        {
            rejectionReason = null;

            string currentPath = candidateFullPath;

            while (!string.IsNullOrEmpty(currentPath) && !ArePathsEqual(currentPath, _workspaceRootPath))
            {
                if (IsReparsePoint(currentPath))
                {
                    rejectionReason = $"{GetDisplayPath(currentPath)} is a symbolic link or junction, and links are not followed. Use the real path inside the project folder.";
                    return false;
                }

                string parentPath = Path.GetDirectoryName(currentPath);

                if (string.IsNullOrEmpty(parentPath) || ArePathsEqual(parentPath, currentPath))
                {
                    break;
                }

                currentPath = parentPath;
            }

            return true;
        }

        static bool ArePathsEqual(string firstPath, string secondPath)
        {
            return string.Equals(
                firstPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                secondPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// True when the path is a symbolic link, junction or mount point. A path we cannot inspect
        /// counts as one, because refusing an unreadable entry is always safe and following one is
        /// not. Directory walks call this to skip links instead of descending through them.
        /// </summary>
        public bool IsReparsePoint(string absolutePath)
        {
            try
            {
                if (!File.Exists(absolutePath) && !Directory.Exists(absolutePath))
                {
                    return false;
                }

                return (File.GetAttributes(absolutePath) & FileAttributes.ReparsePoint) != 0;
            }
            catch (Exception)
            {
                return true;
            }
        }

        /// <summary>
        /// The deny-list as a single name test, so a directory walk can prune a folder before it
        /// enumerates it. Covers the denied folders, meta files and Windows device names.
        /// </summary>
        public bool IsDeniedFileOrDirectoryName(string fileOrDirectoryName)
        {
            if (string.IsNullOrEmpty(fileOrDirectoryName))
            {
                return true;
            }

            string comparableName = fileOrDirectoryName.TrimEnd('.', ' ');

            if (comparableName.Length == 0)
            {
                return true;
            }

            if (comparableName.EndsWith(k_metaFileExtension, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            return k_deniedPathSegmentNames.Contains(comparableName) || IsWindowsReservedDeviceName(comparableName);
        }

        /// <summary>
        /// The path as the model should see it: relative to the workspace root and always with
        /// forward slashes, so one file has exactly one spelling everywhere in the transcript.
        /// Returns "." for the root itself.
        /// </summary>
        public string GetDisplayPath(string absolutePath)
        {
            if (string.IsNullOrEmpty(absolutePath) || !IsWorkspaceFolderAvailable)
            {
                return absolutePath ?? string.Empty;
            }

            try
            {
                string routeFromRoot = Path.GetRelativePath(_workspaceRootPath, absolutePath);

                if (Path.IsPathRooted(routeFromRoot))
                {
                    return absolutePath;
                }

                return routeFromRoot.Replace('\\', '/');
            }
            catch (Exception)
            {
                return absolutePath;
            }
        }
    }
}
