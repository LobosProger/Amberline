<h1 align="center">amberline</h1>

<p align="center">
  <b>A coding agent that runs entirely on your own computer</b><br>
  — inside a Unity window that looks like a 1980s amber terminal.
</p>

<p align="center">
  <img alt="Unity 6000.4.11f1" src="https://img.shields.io/badge/Unity-6000.4.11f1-000000?logo=unity">
  <img alt="Platform: Windows" src="https://img.shields.io/badge/platform-Windows-0078D6">
  <img alt="Inference: 100% local" src="https://img.shields.io/badge/inference-100%25%20local-orange">
  <img alt="No API key" src="https://img.shields.io/badge/API%20key-none-brightgreen">
  <img alt="License: MIT" src="https://img.shields.io/badge/license-MIT-green">
</p>

> *"create tools/wordcount.py that opens the file named in sys.argv[1] and prints how many words it contains"*

![The agent reading the task, writing the file, and waiting for approval before it touches the disk](docs/media/writes-a-script.gif)

<p align="center">
  <sub><b>Real speed, nothing sped up.</b> You type a task in plain English; it thinks, writes the file,
  shows you exactly what it wants to put on disk — and waits for a keypress. Six seconds, start to
  finish, with the model running on the graphics card in that machine and nothing leaving it.</sub>
</p>

---

## What this is, in plain words

You give it a folder. You type what you want in ordinary English.

It reads the files, writes the code, runs the commands — and **stops to ask you before it changes
anything**. You answer with one key: `y` or `n`.

The part that makes it unusual: the AI is not in the cloud. There is no API key anywhere in this
project, no account, no subscription, and no bill. The model is a file on the disk, and it runs on
the graphics card in the machine. Unplug the network and it keeps working.

The part that makes it fun: the whole thing lives in a **game engine**. The terminal is real Unity
UI, rendered to a texture and pushed through a CRT shader — scanlines, glow, the lot. Press Play and
you get a coding assistant.

> I built it after months of using [Claude Code](https://claude.com/claude-code), because I wanted to
> know what is actually inside one of these things — and whether Unity, which everyone thinks of as a
> tool for making games, could host a serious developer tool. It can.

---

## See it work

Two more clips from the same session, on the same terms — nothing sped up, nothing staged. A
4-billion-parameter model on one consumer graphics card, working in a small throwaway project.

### 1 · It uses the terminal, and commits its own work

> *"now add tools/wordcount.py to git and commit it with a short message"*

![The agent running git and committing the file](docs/media/commits-it.gif)

Nobody told it which command to run. It chose `git add … && git commit -m "Add word count tool"`,
asked permission, streamed the output live as it ran, read the result — `1 file changed, 6
insertions(+)` — and reported back. The commit message is its own.

### 2 · It takes "no" for an answer

> *"delete data/sample.txt, we do not need it any more"*

![The agent trying five ways to delete a file, and being refused every time](docs/media/approval-gate.gif)

This is the clip I care most about.

The model wanted that file gone and tried **five different ways** to do it: `del`, then `move`, then
`ren`, then editing the project's README to mark the file obsolete, then rewriting that README from
scratch. Every single attempt stopped and asked first. Every single one was refused.

The file is still there.

**That is the entire design in one clip.** A small model is a fast, tireless, cheerful junior who is
sometimes confidently wrong. So nothing it does reaches your disk without a keypress from you — and
that rule has no exceptions, not even a setting to turn it off for shell commands.

---

## Why it is unusual

**The model runs on your machine.** Not "privacy-friendly", not "we don't train on your data" —
there is no network call at all. The file is `Qwen3-4B-Q4_K_M.gguf`, 2.3 GB, sitting on the disk,
executed by llama.cpp on the GPU. In the recordings above it generated between roughly 50 and 95
tokens a second on an RTX 4060.

**The permission gate is the product, not a safety feature bolted on.** A 4B model on a consumer
card is not trustworthy enough to write to your disk unattended, so it never does. Every write,
every edit and every shell command stops the loop and draws a card you answer with a key.

**Unity is only the host.** The agent knows nothing about Unity — no editor APIs, no game objects,
no scenes. It checks its own work by running a shell command, so `dotnet build`, `npm run build` and
`pytest` are all the same thing to it. The demo above is a Python project, and the milestone that
proved shell commands work drove a plain `dotnet new console` project *outside* the Unity project on
purpose.

---

## What it can do

Nine tools. All of them available from the first turn.

| Tool | What it does |
|---|---|
| `read_file` | reads a file with line numbers, up to 200 lines at a time |
| `list_dir` | lists a folder, two levels deep |
| `grep` | finds text across files — plain text, not regex, because small models mangle escaping |
| `find_file` | finds a file by name anywhere in the workspace |
| `write_file` | creates or replaces a file — **shows a diff and waits** |
| `edit_file` | changes one exact passage in a file — **shows a diff and waits** |
| `run_command` | runs a shell command — **always waits, in every mode** |
| `ask_user` | asks you a question when the task is ambiguous, and uses your answer |
| `finish` | ends the run with a summary |

Eleven slash commands, typed straight into the terminal:

`/help` · `/cwd` · `/cd` · `/context` · `/tools` · `/approve-mode` · `/compact` · `/resume` ·
`/undo` · `/clear` · `/exit`

`/undo` puts the last file change back, byte for byte, from a journal kept outside the workspace.
`/resume` brings back the previous conversation in this folder. `Escape` cancels a run in flight.

<table>
<tr>
<td><img alt="A file change waiting for approval" src="docs/media/approval-diff.png"></td>
<td><img alt="A shell command waiting for approval" src="docs/media/shell-command.png"></td>
</tr>
</table>

---

## How it works

```
you type a task
      |
      v
 THINK  ............ the model plans out loud; the plan streams onto the screen
      |
      +--> did the plan already contain a complete tool call?  -- yes --> use it
      |
      v no
 ACT  .............. ask again, this time forcing the answer into a valid shape
      |
      v
 repair  ........... five stages of fixing broken JSON, because it will be broken
      |
      v
 sandbox  .......... every path the model names is checked against the granted folder
      |
      v
 permission gate  .. writes, edits and commands stop here and wait for a keypress
      |
      v
 run it, trim the output, remember what happened, loop
```

A run gets at most 14 trips to the model, so it always ends — with an answer, or with an honest
"I could not do this."

---

## What building it actually taught me

Everything below was measured. Where a good idea failed a measurement, it was cut rather than kept.
This is the part I would read first.

<details>
<summary><b>One configuration number made every run 14× slower — and nothing anywhere reported an error</b></summary>

<br>

Runs got dramatically slower as the conversation grew: about 15 s early on, 90 s later, **8 minutes**
once the transcript reached 5,400 tokens. No error, no warning, no log line. Just slower.

The obvious suspect — a broken prompt cache — was ruled out first because it was cheapest to rule
out. Same prompt twice, then the same prompt plus a suffix:

| Probe | Time to first token |
|---|---|
| cold, 2177 tokens | 177,511 ms |
| the same prompt again | **276 ms** |
| the same prompt plus a suffix | **347 ms** |

A 643× gap. The cache was perfect. Raw processing of a *new* prefix was simply slow.

It was video memory. The context window had been set to 40,960 tokens, which asks for about 13 GB on
an 8 GB card. On Windows that allocation does not fail — the driver quietly backs the overflow with
ordinary system RAM and reaches it over the PCIe bus. The performance counters showed the Unity
process holding 5,269 MB of real video memory and **8,589 MB of system memory pretending to be video
memory**. Every batch was streaming the model across the bus.

Sizing the context to the hardware fixed it:

| Prompt | Before | After | |
|---|---|---|---|
| 552 tokens | 15,799 ms | **866 ms** | 18× |
| 2,178 tokens | 177,338 ms | **5,149 ms** | 34× |
| 5,427 tokens | 905,001 ms | **24,891 ms** | 36× |

A full five-step run went from about **12 minutes to 50 seconds**.

The standard fix for attention memory — flash attention — was measured too, and **rejected**: on this
build it is 2.2× *slower* (377 tok/s off, 169 tok/s on). It stays off. The lesson I keep is that
"slower as it goes on, with no error" is what a resource ceiling looks like from the inside, and the
only way through it was to measure rather than guess.

</details>

<details>
<summary><b>The best argument for the permission gate is a run that <i>passed</i></b></summary>

<br>

An earlier acceptance run: a small C# project that would not compile. The agent ran the build, read
the real compiler error — `'Calculator' does not contain a definition for 'Multiply'` — wrote the
missing method, ran the build again, and watched it go green. Nine steps, every one correct in shape.

The method it wrote returns `firstNumber + secondNumber`.

From a function called `Multiply`.

The compiler cannot catch that. A green build is not the same as the job being done. But the change
was on screen, in green, with the wrong operator in it, and a human could have said no. There is no
amount of model quality that removes the need for that moment — it just makes it rarer.

</details>

<details>
<summary><b>The sandbox check that looked right and was not</b></summary>

<br>

The first version of the sandbox checked whether the resolved path *started with* the workspace
folder. That reads as correct and is not, because it does not respect folder boundaries. With a root
of `C:\Test` it accepted `..\TestDirectory\x.txt` — a folder outside — for reads **and** for writes.
With a root of `C:\proj` it accepted every file in a neighbouring `C:\proj2`.

It now asks the question that was actually being asked: *what is the route from the root to this
path, and does it start by walking out of the root?*

On top of that: `..` segments, absolute paths, `~`, drive letters, Windows' hidden alternate data
streams, wildcards, control characters, paths over 400 characters, the reserved device names
(`CON`, `NUL`, `COM1`…) at any depth, and a deny-list of build and version-control folders matched
segment by segment. Symlinks are **refused rather than followed**, because a followed link is a
second, invisible way out.

Every refusal is written as a sentence for the *model* to read, because the model is the one that has
to pick a different path on the next turn.

</details>

<details>
<summary><b>Quitting mid-thought could kill the Unity Editor, and once did</b></summary>

<br>

The inference library unloads its native code the moment Play mode ends — while a generation may
still be running inside it on a background thread. Nothing in the package waits or cancels first. It
took the Editor down once, for real.

Shutting down cleanly is now this project's own code: cancel everything in flight, then actually wait
for the native call to return, with a log line once the wait passes 750 ms so a frozen Editor at
least says why. The upper bound is not decoration — one measured shutdown took **9.25 seconds**, and
an earlier 5-second limit would have unloaded the library out from under a live call on that very
exit.

</details>

<details>
<summary><b>The speedometer was lying by 36%, and only measuring it found that out</b></summary>

<br>

The status bar shows a live tokens-per-second reading. The inference library reports no statistics at
all, so the number is measured here — and the first version estimated it by dividing characters by a
calibrated ratio.

Checked against a stopwatch and the real tokeniser, it was **36% high**: 218 tokens reported where
160 were generated. One ratio cannot cover both a six-word thought and six sentences of prose.

The native layer calls back once per token, so the fix was to count the calls:

| | before | after |
|---|---|---|
| tokens reported (160 actual) | 218 | **161** |
| error | 36.3% | **0.6%** |

That deleted an entire apparatus — two ratios, a clamp, a constant and a tokeniser call after every
pass — and made the number exact instead of approximate.

*Postscript, found while recording the clips at the top of this page:* the same status bar could
briefly flash a nonsense figure like `6452776 tok/s`. The clock starts at the first token, so the
very report that *delivers* the first token was dividing one token by almost no time at all. It now
says nothing until the window is wide enough to divide by. Recording a demo turns out to be a
surprisingly good code review.

</details>

<details>
<summary><b>Small things that only turned up by using it</b></summary>

<br>

- **The terminal stopped following its own output after the first approval.** Auto-scroll kept the
  view pinned by watching for the scroll position dropping — but a log that gets *shorter* does that
  too, and this app shortens its log twice: on `/clear`, and whenever an approval card loses its row
  of keys. So the very first approval unpinned the view for the rest of the session and everything
  after it landed below the fold.

- **"Press Escape to cancel" could never have worked.** Locking the input row while the agent works
  was done by disabling it — and a disabled element receives no key events at all. It is locked as
  read-only now, which keeps the focus and the keys.

- **The model ignores a rejection.** Told "the user rejected that edit, do not repeat it", it
  re-proposed the same change three times, narrating "I need to try a different approach" and then
  sending the same one. The gate held every time, but each retry cost a keypress. It now remembers
  refusals by tool and target — deliberately *not* by an exact hash of the arguments, which is
  exactly what a model slips past by nudging a single character.

- **Running Unity in the background is a correctness setting, not a convenience.** With it off and
  the window not focused, the game loop freezes while native calls keep running on other threads. A
  coding agent runs for minutes and you *will* switch windows, so this shows up as "it hangs
  sometimes" rather than as a clean stop.

</details>

---

## Honest limits

Please read this before the setup section, not after.

- **Windows only.** macOS and Linux are not verified and are out of scope. The shell integration and
  the filename rules are Windows-shaped.
- **A 4B model is a 4B model.** It fixes a compiler error it can see. It also writes `a + b` in a
  function called `Multiply`, and it will propose the same edit you just rejected. Competent junior,
  no memory, no judgement — which is why the gate exists.
- **The context window is small.** 8,192 tokens is what fits comfortably on an 8 GB card alongside
  the model. A reading-heavy session fills it, and `/compact` is the way out: it first drops the
  bodies of old file reads (which cost nothing to fetch again) and only then asks the model to
  summarise. On a transcript sitting at 99% full, dropping old output alone returned **5,511 tokens**
  and took it to 29% — with no call to the model at all.
- **One tool call per turn.** No parallel calls, no repo-wide map, no patch-format edits.
- **Not offline on day one.** Inference is fully local, but the first launch downloads 3.77 GB of
  native binaries and you then download a 2.3 GB model. Offline *after* setup.
- **No automated test suite is committed.** The JSON repair ladder was checked against a 52-case
  harness (all 52 pass) and a further 59 assertions over the pure classes were run inside the Editor;
  everything else was verified by driving the real thing in Play mode and reading the bytes back off
  disk. There is no CI, which is why there is no CI badge.
- **The approval card is keyboard-first.** `y`, `n` and `a` are the product; the on-screen chips are
  a label for them. The CRT shader warps the image *after* the mouse position is worked out, so a
  click near an edge lands a few pixels off.

---

## Run it yourself

| | |
|---|---|
| OS | Windows 10/11 |
| Unity | **6000.4.11f1** (URP) |
| GPU | NVIDIA, 8 GB. Built and measured on an RTX 4060 8 GB |
| Disk | ~3.8 GB of native binaries, plus ~2.3 GB for the model |
| Network | For those two downloads. Nothing after that |

1. **Clone the repo and open it in Unity 6000.4.11f1.**

2. **Wait for the first import.** The LLM for Unity package downloads and unpacks LlamaLib v2.0.2 —
   3.77 GB — on first open. Those binaries are not in this repository: seven of them are over
   GitHub's 100 MB per-file limit, and the CUDA build carries NVIDIA redistributables whose terms do
   not clearly cover shipping them inside someone else's source tree. It is a one-time download, and
   it is why this repo itself clones in seconds.

3. **Download the model.** It is not fetched automatically. Select the `LLM Host` object in the
   scene and pick **Qwen 3 4B** in the `LLM` component's model dropdown; the package downloads
   `Qwen3-4B-Q4_K_M.gguf` into `%APPDATA%\LLMUnity\models`, outside the project. Any GGUF chat model
   will work — the prompt format is read from the model's own template — but this is the one measured
   end to end here.

4. **Check the settings.** The scene ships with the configuration that was measured to fit an 8 GB
   card:

   | Setting | Value | Why |
   |---|---|---|
   | Context size | 8192 | Larger silently spills into system RAM and collapses — see above |
   | Batch size | 512 | |
   | GPU layers | 50 | All of them, for this model |
   | Flash attention | off | Measured 2.2× slower on this build |
   | Parallel prompts | 1 | One conversation at a time |

   With more video memory, raise the context first. It is the binding constraint, not speed.

5. **Point it at a folder.** On the `Terminal UI` object, set `Workspace Folder Path`. Left empty it
   uses the folder containing this Unity project, and `/cd <path>` moves it at any time.
   **Point it at a throwaway project the first time.** The sandbox will stop it leaving the folder;
   nothing stops it being wrong *inside* the folder except you.

6. **Open `Assets/Scenes/SampleScene.unity` and press Play.** This is what you get:

![The terminal, just booted](docs/media/boot.png)

`Run In Background` is already enabled in the project settings and needs to stay that way — see the
notes above for what happens otherwise.

---

## Inside the repo

54 C# files, about 15,600 lines including comments. The comments carry the reasoning; several files
record what was measured and rejected as well as what shipped.

```
Assets/Scripts/
  Agent/                       namespace Amberline.Agent
    Core/      the run loop, the event stream, the data types
    Llm/       LlmGateway     — the only class that touches the inference package
    Prompt/    building the exact bytes the model sees
    Parsing/   turning what comes back into a tool call, however broken it is
    Grammar/   forcing valid output when the model will not produce it
    Tools/     the registry, the runner, and nine executors
    Safety/    PathSandbox, ApprovalGate
    Files/     atomic writes, diffs, an undo journal
    Context/   keeping the conversation inside the window
    Shell/     running a command and killing its whole process tree
  Ui/                          namespace Amberline.Ui
    the terminal controller, the slash commands, the approval flow, and the views

Assets/UI Toolkit/    UXML screens, USS styles, rendered to a texture
Assets/Shaders/CRT/   the CRT shader graph, applied as a URP full-screen pass
docs/implementation-plan.md   the plan of record: decisions, milestones, the risk
                              register, and every measurement quoted on this page
```

The dependency runs one way: the UI depends on the agent, never the reverse. Nothing in
`Amberline.Agent` mentions a view, and `LlmGateway` is the only place the inference backend is named
— which is what makes it replaceable.

Every number on this page comes from
[`docs/implementation-plan.md`](docs/implementation-plan.md).

---

## Licence

amberline is MIT — see [`LICENSE`](LICENSE).

Third-party components and their licences are in
[`THIRD-PARTY-NOTICES.md`](THIRD-PARTY-NOTICES.md). In short:
[LLM for Unity](https://github.com/undreamai/LLMUnity) and LlamaLib are Apache-2.0,
[llama.cpp](https://github.com/ggml-org/llama.cpp) is MIT, [UniTask](https://github.com/Cysharp/UniTask)
is MIT, and [JetBrains Mono](https://github.com/JetBrains/JetBrainsMono) — the only third-party asset
actually redistributed here — is under the SIL Open Font License 1.1.

No model weights are distributed with amberline. Each model carries its own licence.

## Thanks

[LLM for Unity](https://github.com/undreamai/LLMUnity) by UndreamAI, which turns local inference in
Unity into a package reference instead of a project. [llama.cpp](https://github.com/ggml-org/llama.cpp),
underneath all of it. [JetBrains Mono](https://github.com/JetBrains/JetBrainsMono), which is what the
terminal is made of. And [Claude Code](https://claude.com/claude-code), for the idea.
