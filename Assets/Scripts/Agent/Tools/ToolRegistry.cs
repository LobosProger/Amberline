using System.Collections.Generic;
using UnityEngine;

namespace Amberline.Agent
{
    /// <summary>
    /// How far the agent has been let into the workspace. The phases are ADDITIVE: Edit is
    /// Explore plus the three mutating tools, never a different set. Swapping the sets instead of
    /// growing them stranded the previous agent - after one read it could no longer call grep,
    /// which kills every task shaped like "find where X is used and then fix it".
    /// </summary>
    public enum ToolPhase
    {
        /// <summary>Read-only. The phase every run starts in.</summary>
        Explore,

        /// <summary>Explore plus write_file, edit_file and run_command. Opens after the first
        /// successful read-only tool call and never closes again for the rest of the run.</summary>
        Edit
    }

    // Owns the tool executors and answers the one question the loop asks every turn: which tools
    // may be called right now. Three separate facts are kept apart on purpose, because the layers
    // above need to tell them apart to write a useful message back to the model:
    //
    // 1. Is this name one of our seven tools at all?      -> IsToolNameKnownToRegistry
    // 2. Is it allowed in the phase we are in?            -> IsToolNameAllowedInCurrentPhase
    // 3. Is there actually an executor behind it?         -> FindExecutorForToolName
    //
    // Fact 3 exists because the mutating executors land in later milestones. Today the Edit phase
    // names write_file, edit_file and run_command while none of them is registered, and nothing
    // may break because of that: GetDefinitionsOfCallableTools - what the grammar is built from -
    // returns only tools that are BOTH allowed by the phase AND registered, so the model is never
    // constrained into a call that has nothing to run it.
    //
    // Order is stable (the Explore names, then the Edit-only names, always in the same sequence)
    // because the grammar text is derived from it and a grammar that reshuffles between turns
    // would look like a different string to every cache that sees it.
    public class ToolRegistry
    {
        readonly Dictionary<string, IToolExecutor> _executorsByToolName = new Dictionary<string, IToolExecutor>();

        ToolPhase _currentPhase = ToolPhase.Explore;

        public const string k_readFileToolName = "read_file";
        public const string k_listDirToolName = "list_dir";
        public const string k_grepToolName = "grep";
        public const string k_finishToolName = "finish";
        public const string k_writeFileToolName = "write_file";
        public const string k_editFileToolName = "edit_file";
        public const string k_runCommandToolName = "run_command";

        static readonly string[] k_exploreToolNames =
        {
            k_readFileToolName,
            k_listDirToolName,
            k_grepToolName,
            k_finishToolName
        };

        static readonly string[] k_toolNamesAddedByEditPhase =
        {
            k_writeFileToolName,
            k_editFileToolName,
            k_runCommandToolName
        };

        /// <summary>The phase the agent is in right now. Starts at <see cref="ToolPhase.Explore"/>.</summary>
        public ToolPhase CurrentPhase => _currentPhase;

        /// <summary>
        /// Adds one executor. Registering the same tool name twice replaces the earlier executor,
        /// so a re-composed runner cannot end up holding two objects for one name.
        /// </summary>
        public void RegisterExecutor(IToolExecutor toolExecutor)
        {
            if (toolExecutor == null || toolExecutor.Definition == null)
            {
                Debug.LogWarning("[ToolRegistry] Ignored an executor with no definition.");
                return;
            }

            string toolName = toolExecutor.Definition.Name;
            if (string.IsNullOrEmpty(toolName))
            {
                Debug.LogWarning("[ToolRegistry] Ignored an executor whose definition has no name.");
                return;
            }

            if (!IsToolNameKnownToRegistry(toolName))
            {
                // Registering an eighth tool would silently widen the grammar and the prompt would
                // never mention it, so the model could be constrained into a call it was never told about.
                Debug.LogWarning($"[ToolRegistry] '{toolName}' is not one of the seven tools in the system prompt and was ignored.");
                return;
            }

            _executorsByToolName[toolName] = toolExecutor;
        }

        /// <summary>
        /// Opens the Edit phase. Additive - everything callable in Explore stays callable. Safe to
        /// call repeatedly; after the first call it does nothing.
        /// </summary>
        public void UnlockEditPhase()
        {
            _currentPhase = ToolPhase.Edit;
        }

        /// <summary>Drops back to the read-only phase. Used by /clear, which starts a fresh run.</summary>
        public void ResetToExplorePhase()
        {
            _currentPhase = ToolPhase.Explore;
        }

        /// <summary>True when the name is one of the seven tools, whatever the phase.</summary>
        public bool IsToolNameKnownToRegistry(string toolName)
        {
            return ContainsToolName(k_exploreToolNames, toolName) || ContainsToolName(k_toolNamesAddedByEditPhase, toolName);
        }

        /// <summary>True when the current phase allows the name. Says nothing about whether an
        /// executor exists for it - ask <see cref="FindExecutorForToolName"/> for that.</summary>
        public bool IsToolNameAllowedInCurrentPhase(string toolName)
        {
            if (ContainsToolName(k_exploreToolNames, toolName))
            {
                return true;
            }

            return _currentPhase == ToolPhase.Edit && ContainsToolName(k_toolNamesAddedByEditPhase, toolName);
        }

        /// <summary>The executor for the name, or null when none is registered. Does not check the
        /// phase, so the caller can tell "not built yet" apart from "not unlocked yet".</summary>
        public IToolExecutor FindExecutorForToolName(string toolName)
        {
            if (string.IsNullOrEmpty(toolName))
            {
                return null;
            }

            return _executorsByToolName.TryGetValue(toolName, out var toolExecutor) ? toolExecutor : null;
        }

        /// <summary>
        /// The tools that can really be called this turn: allowed by the phase AND backed by a
        /// registered executor. This is what the grammar is built from, so a tool the current
        /// build has no executor for is simply never offered to the model.
        /// </summary>
        public IReadOnlyList<ToolDefinition> GetDefinitionsOfCallableTools()
        {
            var callableDefinitions = new List<ToolDefinition>();
            AppendCallableDefinitionsFrom(k_exploreToolNames, callableDefinitions);

            if (_currentPhase == ToolPhase.Edit)
            {
                AppendCallableDefinitionsFrom(k_toolNamesAddedByEditPhase, callableDefinitions);
            }

            return callableDefinitions;
        }

        /// <summary>The names behind <see cref="GetDefinitionsOfCallableTools"/>, for /tools and for
        /// the "you cannot call that yet" messages the model reads.</summary>
        public IReadOnlyList<string> GetNamesOfCallableTools()
        {
            var callableDefinitions = GetDefinitionsOfCallableTools();
            var callableNames = new List<string>(callableDefinitions.Count);

            foreach (var toolDefinition in callableDefinitions)
            {
                callableNames.Add(toolDefinition.Name);
            }

            return callableNames;
        }

        void AppendCallableDefinitionsFrom(string[] toolNames, List<ToolDefinition> callableDefinitions)
        {
            foreach (string toolName in toolNames)
            {
                var toolExecutor = FindExecutorForToolName(toolName);
                if (toolExecutor == null)
                {
                    continue;
                }

                callableDefinitions.Add(toolExecutor.Definition);
            }
        }

        static bool ContainsToolName(string[] toolNames, string toolName)
        {
            if (string.IsNullOrEmpty(toolName))
            {
                return false;
            }

            foreach (string knownToolName in toolNames)
            {
                if (knownToolName == toolName)
                {
                    return true;
                }
            }

            return false;
        }
    }
}
