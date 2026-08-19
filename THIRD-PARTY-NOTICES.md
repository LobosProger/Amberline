# Third-Party Notices

amberline is licensed under the MIT License (see `LICENSE`). It uses the
third-party components listed below, each under its own licence. This file is
provided for attribution; it does not modify any of those licences.

---

## Components redistributed in this repository

### JetBrains Mono
- Files: `Assets/Fonts/Sdf/JetBrainsMono-Bold.ttf` and its generated SDF font asset
- Version: 2.304
- Copyright 2020 The JetBrains Mono Project Authors
  (https://github.com/JetBrains/JetBrainsMono)
- Licence: SIL Open Font License, Version 1.1 — full text in `Assets/Fonts/OFL.txt`
  (https://openfontlicense.org)
- "JetBrains Mono" is a Reserved Font Name under the OFL. JetBrains Mono is a
  trademark of JetBrains s.r.o. The font is redistributed unmodified.

### Terminal UI, CRT shader and volume profile
- Files: `Assets/UI Toolkit/`, `Assets/Shaders/CRT/`,
  `Assets/Settings/Global Volume Profile.asset`
- Original work by Yaroslav Knyazev, ported from the author's own `Devs` project
  and adapted for amberline. Covered by this repository's MIT licence. The only
  third-party dependency embedded in it is JetBrains Mono, above.

---

## Components fetched automatically, not stored in this repository

### LLM for Unity (`ai.undream.llm`) v3.0.1 — UndreamAI
- Resolved by the Unity Package Manager from
  https://github.com/undreamai/LLMUnity.git (pinned to tag `v3.0.1`)
- Licence: Apache License 2.0 (http://www.apache.org/licenses/LICENSE-2.0)

### LlamaLib v2.0.2 — UndreamAI
- Downloaded by LLM for Unity on first import into
  `Assets/StreamingAssets/LlamaLib-v2.0.2/`, from
  https://github.com/undreamai/LlamaLib/releases/tag/v2.0.2
- Licence: Apache License 2.0
- LlamaLib embeds **llama.cpp** — MIT, Copyright (c) 2023-2026 The ggml authors
  (https://github.com/ggml-org/llama.cpp/blob/master/LICENSE) — and, in its
  Windows and Linux CUDA builds, **NVIDIA CUDA redistributable libraries**
  (cuBLAS, cuBLASLt, CUDA Runtime), which are proprietary NVIDIA software
  governed by the NVIDIA CUDA Toolkit EULA (https://docs.nvidia.com/cuda/eula/).
  These binaries are deliberately **not** redistributed in this repository; they
  are obtained directly from UndreamAI's release artefacts on the user's own
  machine.

### Language models
No model weights are distributed with amberline. LLM for Unity downloads GGUF
models at runtime into the user's application-data directory
(`%APPDATA%\LLMUnity\models` on Windows); each model carries its own licence
(Llama 3.x Community Licence, Gemma Terms, Apache-2.0, MIT, ...). See the
"Models" section of LLM for Unity's own `Third Party Notices.md`.

### UniTask v2.5.10 — Cysharp, Inc.
- Resolved from https://github.com/Cysharp/UniTask.git
- Licence: MIT — Copyright (c) 2019 Yoshifumi Kawai / Cysharp, Inc.

### MCP for Unity (`com.ivanmurzak.unity.mcp`) v0.88.0 — Ivan Murzak
- Resolved from the OpenUPM registry; repository
  https://github.com/IvanMurzak/Unity-MCP
- Licence: Apache License 2.0 — Copyright (c) 2025 Ivan Murzak
- **Development-time tooling only**, not part of the shipped application. It is
  how this project was built: the Editor was driven through it. It is kept in
  `Packages/manifest.json` deliberately so the build process is reproducible.
- On import it restores NuGet dependencies into `Assets/Plugins/NuGet/`
  (not committed here), namely:
  - McpPlugin, McpPlugin.Common, ReflectorNet — Apache-2.0, Copyright Ivan Murzak
  - R3 — MIT, Copyright (c) 2024 Cysharp, Inc.
  - Microsoft.CodeAnalysis (Roslyn), Microsoft.AspNetCore.* (SignalR),
    Microsoft.Extensions.*, Microsoft.Bcl.*, System.* — MIT,
    Copyright (c) .NET Foundation and Contributors
  - Extensions.Unity.PlayerPrefsEx — MIT, Copyright (c) 2023 Ivan Murzak

### Unity Engine and Unity packages
Unity 6 (6000.4.11f1) and its packages — Universal Render Pipeline, Input System,
uGUI / TextMeshPro, Test Framework, Timeline, AI Navigation, Newtonsoft Json for
Unity and others — are resolved from the Unity package registry via
`Packages/manifest.json` and are not redistributed here. They are licensed under
the Unity Companion License
(https://unity3d.com/legal/licenses/unity_companion_license), except Visual
Scripting, which uses the Unity Package Distribution License
(https://unity3d.com/legal/licenses/Unity_Package_Distribution_License). Use of
the Unity Editor is governed by the Unity Terms of Service.

TextMeshPro's default `LiberationSans SDF` font asset is referenced but contains
no font outline data — it is a dynamic-population asset with an empty atlas. The
Liberation Sans typeface itself ships with Unity and is **not** redistributed by
this repository. Liberation fonts are Copyright Red Hat, Inc.
