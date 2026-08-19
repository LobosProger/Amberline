# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Project Overview

**amberline** — a general-purpose local coding agent with an amber CRT terminal UI, running in Unity **Play mode**. It reads, searches and edits files in a folder the user grants it, and runs shell commands behind an approval gate. Unity is only the host: the agent is not Unity-specific and works on any project — HTML, web, any language.

Inference is local, via the **LLM for Unity** package (`ai.undream.llm`), which wraps llama.cpp through the LlamaLib native binaries in `Assets/StreamingAssets/LlamaLib-v2.0.2/`. Render pipeline is **URP**.

Unity **6000.4.11f1**. The plan of record — architecture, milestones, measured facts and the risk register — is `docs/implementation-plan.md`. Read it before changing anything structural.

## Key Dependencies

- **LLM for Unity** (v3.0.1) — local inference. Note we drive a plain `LLMClient` with raw `Completion()`, **not** `LLMAgent`: grammar only reaches the native object on `LLMClient`, and owning the transcript ourselves dissolves three package defects. See plan section 1.1.
- **UniTask** (`com.cysharp.unitask`) — async/await for Unity. Use `UniTask` instead of `Task` for new async code.
- **Newtonsoft.Json** — used by the tool-call parse ladder.
- **Input System** (`com.unity.inputsystem`) — new input system.

## Architecture

- `Assets/Scripts/Agent/` — the agent, namespace `Amberline.Agent`. `Core/` (data types, events, loop, runner), `Llm/` (the only class touching `LLMUnity`), `Prompt/`, `Parsing/`, `Context/`, `Tools/` + `Tools/Executors/`, `Safety/`, `Grammar/`.
- `Assets/Scripts/Ui/` — the terminal, namespace `Amberline.Ui`. Views hold no session state; `TerminalCliController` owns the ordering.
- `Assets/UI Toolkit/` — UXML screens and components, USS styles, PanelSettings rendering to a render texture.
- `Assets/Shaders/CRT/` — the CRT shader graph and material, applied as a URP `FullScreenPassRendererFeature`.
- `Assets/Scenes/SampleScene.unity` — the scene. `LLM Host` carries `LLM`, `LLMClient`, `LlmGateway`, `AgentRunner`; `Terminal UI` carries the UIDocument and views; `Terminal Renderer` shows the panel's render texture.

**The dependency runs one way: UI → agent core.** Nothing in `Amberline.Agent` may reference a type from `Amberline.Ui`.

## Build & Run

Open in Unity 6000.4.11f1 and press Play. No command-line build scripts are configured.

To open the project with the MCP bridge attached:

```bash
unity-mcp-cli open .
```

## Important Rules

- **Never create .meta files** — Unity generates these automatically.
- **Never use the `private` keyword** for field declarations (omit it; C# fields are private by default).
- All new scripts go in `Assets/Scripts/`.
- Use `UniTask` and `async UniTask` (with `Async` suffix) for async methods, not `System.Threading.Tasks.Task`.
- The agent is sandboxed to the granted workspace folder. `run_command` passes the approval gate in **every** permission mode.
