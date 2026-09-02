using System.Collections.Generic;
using UnityEngine;

namespace Amberline.Agent
{
    // Owns the tool executors and answers the one question the loop asks every turn: which tools
    // may be called right now. Two separate facts are kept apart on purpose, because the layers
    // above need to tell them apart to write a useful message back to the model:
    //
    // 1. Is this name one of the tools we ship at all?  -> IsToolNameKnownToRegistry
    // 2. Is there actually an executor behind it?     -> FindExecutorForToolName
    //
    // THERE USED TO BE A THIRD: a two-phase gate that started every session read-only and only
    // unlocked write_file, edit_file and run_command after a read-only tool had succeeded. It is
    // gone, and it is worth saying why, because it looked prudent and was not.
    //
    // The grammar is built from exactly this list. So on the first turn of a session the model was
    // CONSTRAINED OUT of the three tools it had just been told about in the system prompt: asked
    // to create a file, it could not, and asked to run a command, it could not. What it did
    // instead was call finish - and the run ended, having done nothing, on the one task the user
    // most obviously wanted done. Everything about that reads to the user as a model too small to
    // follow instructions. It was the tool list lying to it.
    //
    // Nothing was gained for the risk, either. Every write shows a diff card and every command is
    // approval-gated in every permission mode, so the user still authorises each change; and the
    // system prompt still says to read a file before editing it. The gate only ever added a way
    // for a correct plan to be refused.
    //
    // Order is stable because the grammar text is derived from it, and a grammar that reshuffles
    // between turns would look like a different string to every cache that sees it.
    public class ToolRegistry
    {
        readonly Dictionary<string, IToolExecutor> _executorsByToolName = new Dictionary<string, IToolExecutor>();

        public const string k_readFileToolName = "read_file";
        public const string k_listDirToolName = "list_dir";
        public const string k_grepToolName = "grep";
        public const string k_findFileToolName = "find_file";
        public const string k_finishToolName = "finish";
        public const string k_writeFileToolName = "write_file";
        public const string k_editFileToolName = "edit_file";
        public const string k_runCommandToolName = "run_command";

        // The order the model reads them in, in the system prompt and in the grammar: look first,
        // then change, then stop.
        static readonly string[] k_allToolNames =
        {
            k_readFileToolName,
            k_listDirToolName,
            k_grepToolName,
            k_findFileToolName,
            k_writeFileToolName,
            k_editFileToolName,
            k_runCommandToolName,
            k_finishToolName
        };

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
                // Registering a tool that is not on the list would silently widen the grammar while
                // the prompt never mentioned it, so the model could be constrained into a call it was
                // never told about. Adding a tool means adding it HERE and in SystemPromptText.
                Debug.LogWarning($"[ToolRegistry] '{toolName}' is not one of the tools listed in the system prompt and was ignored.");
                return;
            }

            _executorsByToolName[toolName] = toolExecutor;
        }

        /// <summary>True when the name is one of the tools this registry ships.</summary>
        public bool IsToolNameKnownToRegistry(string toolName)
        {
            if (string.IsNullOrEmpty(toolName))
            {
                return false;
            }

            foreach (string knownToolName in k_allToolNames)
            {
                if (knownToolName == toolName)
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>The executor for the name, or null when none is registered. A name that is
        /// known but has no executor is "not built yet", which the caller says out loud.</summary>
        public IToolExecutor FindExecutorForToolName(string toolName)
        {
            if (string.IsNullOrEmpty(toolName))
            {
                return null;
            }

            return _executorsByToolName.TryGetValue(toolName, out var toolExecutor) ? toolExecutor : null;
        }

        /// <summary>
        /// The tools that can really be called: the ones backed by a registered executor. This is
        /// what the grammar is built from, so a tool this build has no executor for is simply never
        /// offered to the model rather than being offered and then refused.
        /// </summary>
        public IReadOnlyList<ToolDefinition> GetDefinitionsOfCallableTools()
        {
            var callableDefinitions = new List<ToolDefinition>();

            foreach (string toolName in k_allToolNames)
            {
                var toolExecutor = FindExecutorForToolName(toolName);
                if (toolExecutor == null)
                {
                    continue;
                }

                callableDefinitions.Add(toolExecutor.Definition);
            }

            return callableDefinitions;
        }

        /// <summary>The names behind <see cref="GetDefinitionsOfCallableTools"/>, for /tools and for
        /// the "there is no tool called that" messages the model reads.</summary>
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
    }
}
