using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Cysharp.Threading.Tasks;
using LLMUnity;
using Newtonsoft.Json.Linq;
using UnityEngine;
using Stopwatch = System.Diagnostics.Stopwatch;
#if UNITY_EDITOR
using UnityEditor;
#endif

namespace Amberline.Agent
{
	/// <summary>
	/// Sampling values for a single completion. Every value is optional - a null value leaves the
	/// client's configured value untouched for that call.
	/// </summary>
	public class LlmSamplingOverride
	{
		public int? MaximumTokensToGenerate { get; set; }
		public float? Temperature { get; set; }
		public float? TopP { get; set; }
		public int? TopK { get; set; }
		public int? Seed { get; set; }

		/// <summary>
		/// The repetition penalty. It has to be settable per call, and the reason is code: source
		/// repeats itself constantly - the same indentation, the same "self.", the same closing
		/// brace - and a penalty tuned for chat quietly pushes the model off the token it should
		/// have written. The pass that emits a file payload sets this to 1, which is off.
		/// </summary>
		public float? RepeatPenalty { get; set; }

		/// <summary>The minimum probability a token needs to stay in the running. Zero disables it,
		/// which is what a grammar-constrained pass wants: the grammar has already decided which
		/// tokens are legal, and a second filter on top of it can only remove legal ones.</summary>
		public float? MinP { get; set; }
	}

	// The only class in the project allowed to reference the LLMUnity namespace. Everything above it
	// talks in Amberline.Agent types, so the rest of the agent never learns which backend it runs on.
	//
	// Three properties of the package shape this class, and all three are measured, not assumed:
	//
	// 1. Grammar and sampling are sticky native state. Grammar is pushed the moment it is assigned and
	//    survives every later call; sampling is pushed lazily on the next completion. Nothing in the
	//    package ever resets either, so both are applied per call and restored in a finally.
	// 2. There is no stop-sequence support at all. A stop string is implemented by watching the
	//    streaming text and cancelling the slot when it appears.
	// 3. LLMClient has no OnDestroy and LLM.OnDestroy unloads the native module synchronously, while a
	//    generation may still be running inside it on a thread-pool thread. That is a DLL unload under
	//    an active native call. This class therefore cancels and drains the generation itself before
	//    teardown is allowed to continue.
	//
	// The native call is started with Task.Run on purpose. The package marshals its streaming callback
	// through the SynchronizationContext captured at the call site, so calling from the main thread
	// would make both the callback and the completion task depend on the Unity player loop being
	// pumped - and the shutdown drain has to block that very loop. Off the main thread the callback
	// arrives raw on the generation thread, this class buffers it in a field, and the main thread polls
	// that field once per streaming tick. Streaming coalescing and a drainable task fall out of the
	// same decision.
	public class LlmGateway : MonoBehaviour
	{
		[Header("Model")]
		[SerializeField] LLMClient _llmClient;

		[Space]
		[Header("Bounds")]
		[Tooltip("How long to wait for the model to finish loading before reporting failure.")]
		[SerializeField] float _secondsToWaitForModelToLoad = 300f;
		[Tooltip("Safety valve only. Teardown waits for the native call to actually finish; this is the point at which it gives up and unloads anyway.")]
		[SerializeField] float _secondsToWaitForNativeCallOnShutdown = 180f;

		/// <summary>
		/// How fast the model is generating, raised about four times a second while a pass runs and
		/// once more when it ends. Always on the main thread.
		/// </summary>
		public event Action<LlmGenerationStats> OnGenerationStatsProduced;

		Task<string> _inFlightCompletionTask;
		bool _isCompletionInProgress;
		bool _isModelReady;
		bool _isShuttingDown;
		string _stopTextForCurrentCall = "";
		string _lastForwardedText;

		// The speedometer's working state, all of it main-thread only.
		float _secondsToFirstTokenOfThisCall;
		long _millisecondsAtLastSpeedReport;
		float _charactersPerTokenOfThisCall = k_startingCharactersPerToken;
		string _textGeneratedInTheLastCall;
		int _charactersGeneratedInTheLastCall;

		// Two ratios, because the two passes write very different text: the think pass writes prose,
		// the act pass writes JSON carrying escaped source, and a single ratio would sit wrong for
		// both. Which one applies is decided by whether a grammar was set for the call.
		float _charactersPerTokenOfAnUnconstrainedCall = k_startingCharactersPerToken;
		float _charactersPerTokenOfAConstrainedCall = k_startingCharactersPerToken;

		// Written on the generation thread, read on the main thread, so both need a memory barrier.
		volatile string _latestCumulativeText = "";
		volatile bool _wasCancellationRequestedByCaller;
		volatile bool _wasStopTextReached;

		const int k_completionSlotId = 0;
		const int k_millisecondsBetweenStreamingUpdates = 33;
		const int k_millisecondsBetweenShutdownChecks = 10;
		const int k_millisecondsBeforeSayingTheEditorIsWaiting = 750;
		const int k_framesToWaitAfterModelStarted = 2;
		const int k_framesBetweenReadinessProbes = 2;
		const int k_readinessProbeAttempts = 3;
		const string k_readinessProbeText = "ready";

		// Four updates a second. The streaming loop itself ticks every 33 ms, which is far more often
		// than a number on screen can be read, and a label rewritten thirty times a second is a blur.
		const int k_millisecondsBetweenSpeedReports = 250;

		// English prose averages about four characters per token, the same assumption ContextManager
		// starts from. It is only the value used before the first calibration lands.
		const float k_startingCharactersPerToken = 4f;

		// Nothing real sits outside this band, so one bad measurement cannot make the figure absurd.
		const float k_smallestSensibleCharactersPerToken = 2f;
		const float k_largestSensibleCharactersPerToken = 6f;

		// Every native build whose name says it offloads to a GPU. Anything else is CPU only.
		static readonly string[] k_namesOfNativeBuildsThatUseTheGpu = { "cublas", "tinyblas", "vulkan", "metal", "hip", "sycl" };

		/// <summary>True while a completion is running. A second concurrent call is refused.</summary>
		public bool IsCompletionInProgress => _isCompletionInProgress;

		/// <summary>
		/// Tokens this client may actually use. The configured context is shared between the model's
		/// slots, so the per-slot budget is the one the transcript has to fit into. Zero when unknown.
		/// </summary>
		public int UsableContextTokens
		{
			get
			{
				LLM loadedModel = _llmClient == null ? null : _llmClient.llm;
				if (loadedModel == null)
				{
					return 0;
				}

				// A context size of zero means "whatever the model file declares".
				int configuredContextSize = loadedModel.contextSize > 0 ? loadedModel.contextSize : loadedModel.maxContextLength;
				int slotCount = loadedModel.parallelPrompts > 0 ? loadedModel.parallelPrompts : 1;
				return configuredContextSize / slotCount;
			}
		}

		void Awake()
		{
			SubscribeToShutdownEvents();
		}

		void OnDestroy()
		{
			UnsubscribeFromShutdownEvents();
			StopActiveCompletionAndWaitForNativeCall();
		}

		/// <summary>
		/// Waits until the model is loaded and the native client behind it can actually be called.
		/// Bounded - returns false and logs on timeout or on a failed model, and never waits forever.
		/// </summary>
		public async UniTask<bool> WaitUntilReadyAsync(CancellationToken cancellationToken)
		{
			if (_llmClient == null)
			{
				Debug.LogError("[LlmGateway] No LLMClient is assigned. Assign a plain LLMClient, not an LLMAgent.");
				return false;
			}

			if (_isModelReady)
			{
				return true;
			}

			// The model service only starts in Awake, and the frame waits below need a running player
			// loop, so outside Play mode this would wait for something that can never happen.
			if (!Application.isPlaying)
			{
				Debug.LogError("[LlmGateway] The model is only available in Play mode.");
				return false;
			}

			if (_llmClient.llm == null)
			{
				Debug.LogError("[LlmGateway] The LLMClient has no LLM assigned, so there is no model to wait for.");
				return false;
			}

			// LLMClient.Start is async void and needs a frame to take its own start lock. Calling into
			// it before that lock is held reports "LLM caller not initialized" instead of waiting.
			await UniTask.NextFrame();

			if (!await WaitUntilModelServiceStartedAsync(cancellationToken))
			{
				return false;
			}

			await UniTask.DelayFrame(k_framesToWaitAfterModelStarted);

			_isModelReady = await TryProbeNativeClientAsync();

			if (_isModelReady)
			{
				ReportWhichNativeLibraryIsInUse();
			}

			return _isModelReady;
		}

		/// <summary>
		/// Runs one completion. Sampling and grammar apply to this call only and are restored
		/// afterwards. Partial text arrives on the main thread, coalesced to about one update per
		/// 33 ms, and is always the full text so far - assign it, never append it. A non-empty stopText
		/// ends generation as soon as it appears, and the text is trimmed to include it. Never throws:
		/// every failure comes back as a failure result.
		/// </summary>
		public async UniTask<LlmCompletionResult> CompleteAsync(
			string prompt,
			Action<string> onPartialText,
			LlmSamplingOverride sampling,
			string grammar,
			string stopText,
			CancellationToken cancellationToken)
		{
			if (string.IsNullOrEmpty(prompt))
			{
				return LlmCompletionResult.Failure("The prompt is empty. The native layer throws on an empty prompt.");
			}

			if (_isShuttingDown)
			{
				return LlmCompletionResult.Failure("The gateway is shutting down.");
			}

			if (_isCompletionInProgress)
			{
				return LlmCompletionResult.Failure("Another completion is already running. This model has a single slot, so calls cannot overlap.");
			}

			_isCompletionInProgress = true;
			bool isThisCallConstrainedByAGrammar = !string.IsNullOrEmpty(grammar);
			_charactersPerTokenOfThisCall = isThisCallConstrainedByAGrammar
				? _charactersPerTokenOfAConstrainedCall
				: _charactersPerTokenOfAnUnconstrainedCall;

			LlmCompletionResult completionResult;

			try
			{
				completionResult = await RunOneCompletionAsync(prompt, onPartialText, sampling, grammar, stopText, cancellationToken);
			}
			finally
			{
				_isCompletionInProgress = false;
			}

			// Calibration happens HERE and nowhere else. It calls the tokenizer, which refuses and
			// answers zero while a completion holds the single slot, so anywhere inside
			// RunOneCompletionAsync it would silently measure nothing. By this line the flag is
			// clear and that method's own finally has already put the grammar and the sampling back.
			_textGeneratedInTheLastCall = completionResult.Text;
			await CalibrateCharactersPerTokenAsync(isThisCallConstrainedByAGrammar);

			return completionResult;
		}

		/// <summary>
		/// Asks the running generation to stop. The native call then returns normally with the text
		/// produced so far, and the completion reports itself as cancelled rather than failed.
		/// </summary>
		public void CancelActiveCompletion()
		{
			if (!_isCompletionInProgress)
			{
				return;
			}

			_wasCancellationRequestedByCaller = true;
			RequestNativeCancel();
		}

		/// <summary>
		/// Counts the tokens in a piece of text. This is a blocking native call behind an async face,
		/// so it is cheap enough per message and far too expensive per keystroke. Returns 0 on failure.
		/// </summary>
		public async UniTask<int> CountTokensAsync(string text)
		{
			if (string.IsNullOrEmpty(text))
			{
				return 0;
			}

			if (_isCompletionInProgress)
			{
				Debug.LogWarning("[LlmGateway] Token counting was skipped: it runs synchronously on the main thread and a generation is in flight.");
				return 0;
			}

			if (!await WaitUntilReadyAsync(CancellationToken.None))
			{
				return 0;
			}

			try
			{
				List<int> tokens = await _llmClient.Tokenize(text).AsUniTask();
				return tokens == null ? 0 : tokens.Count;
			}
			catch (Exception exception)
			{
				Debug.LogWarning($"[LlmGateway] Tokenizing failed: {exception.GetType().Name}: {exception.Message}");
				return 0;
			}
		}

		/// <summary>
		/// Renders <paramref name="messages"/> with the chat template the loaded MODEL FILE shipped
		/// with, and opens an assistant turn at the end of it. That template - not ChatML, not
		/// anything this project decided - is the envelope the model was actually trained on.
		/// Returns null when there is no template to ask, and the caller then falls back to ChatML.
		/// <para>
		/// The roles must already alternate. Several templates, Mistral's included, are undefined
		/// for two user turns in a row, so merge before calling this.
		/// </para>
		/// </summary>
		public string ApplyTheModelsOwnChatTemplate(IReadOnlyList<ChatMessage> messages)
		{
			if (messages == null || messages.Count == 0)
			{
				return null;
			}

			var llmService = _llmClient == null || _llmClient.llm == null ? null : _llmClient.llm.llmService;
			if (llmService == null)
			{
				return null;
			}

			var messagesForTheTemplate = new JArray();
			foreach (var message in messages)
			{
				messagesForTheTemplate.Add(new JObject
				{
					["role"] = ChatMlPromptRenderer.GetRoleName(message.Role),
					["content"] = message.Text
				});
			}

			// Deliberately not wrapped in a try: the caller has the fallback and needs to see the
			// exception text to report which model could not be templated.
			return llmService.ApplyTemplate(messagesForTheTemplate);
		}

		// Which native library actually got loaded, so "the agent is unusably slow" is never a
		// mystery. LlamaLib picks one at startup and falls back on its own, and a fallback all the
		// way down to a plain CPU build is silent: numGPULayers stays at whatever the inspector
		// says while nothing is offloaded at all. On this machine that was the difference between
		// a few tokens a second and a usable agent.
		void ReportWhichNativeLibraryIsInUse()
		{
			string architecture = _llmClient == null || _llmClient.llm == null ? null : _llmClient.llm.architecture;

			if (string.IsNullOrEmpty(architecture))
			{
				return;
			}

			if (IsAnArchitectureThatUsesTheGpu(architecture))
			{
				Debug.Log($"[LlmGateway] Inference runs on {architecture}.");
				return;
			}

			Debug.LogWarning($"[LlmGateway] Inference runs on {architecture}, which is a CPU build - every layer is on the processor however high numGPULayers is set. On a machine with a supported GPU this is roughly ten times slower than it needs to be. Check the LLM component's GPU settings and that the matching native library is present.");
		}

		static bool IsAnArchitectureThatUsesTheGpu(string architecture)
		{
			foreach (string nameOfAGpuBuild in k_namesOfNativeBuildsThatUseTheGpu)
			{
				if (architecture.IndexOf(nameOfAGpuBuild, StringComparison.OrdinalIgnoreCase) >= 0)
				{
					return true;
				}
			}

			return false;
		}

		void SubscribeToShutdownEvents()
		{
			Application.quitting += HandleApplicationQuitting;
#if UNITY_EDITOR
			EditorApplication.playModeStateChanged += HandlePlayModeStateChanged;
#endif
		}

		void UnsubscribeFromShutdownEvents()
		{
			Application.quitting -= HandleApplicationQuitting;
#if UNITY_EDITOR
			EditorApplication.playModeStateChanged -= HandlePlayModeStateChanged;
#endif
		}

		void HandleApplicationQuitting()
		{
			StopActiveCompletionAndWaitForNativeCall();
		}

#if UNITY_EDITOR
		void HandlePlayModeStateChanged(PlayModeStateChange stateChange)
		{
			// This fires before any object is destroyed, which is the last moment at which the native
			// module is still guaranteed to be loaded.
			if (stateChange != PlayModeStateChange.ExitingPlayMode)
			{
				return;
			}

			StopActiveCompletionAndWaitForNativeCall();
		}
#endif

		void StopActiveCompletionAndWaitForNativeCall()
		{
			_isShuttingDown = true;

			Task<string> runningTask = _inFlightCompletionTask;
			if (runningTask == null || runningTask.IsCompleted)
			{
				return;
			}

			// CANCEL FIRST, THEN WAIT FOR THE CALL ITSELF - not for a fixed span of time.
			//
			// Teardown cannot await, and LLM.OnDestroy unloads the native module as soon as we
			// return, so the main thread blocks here until the generation thread has left that
			// module. Once cancelled the call normally returns in a few milliseconds, but a single
			// prefill batch has no upper bound: llama.cpp only checks for cancellation between
			// batches, and exiting Play mode during a long one is what unloaded the module under a
			// live call and killed the Editor once. A blocking wait during teardown is the lesser
			// evil against a native use-after-free, so the ceiling below is a safety valve and not
			// a schedule - it is generous on purpose and reaching it is a bug worth reporting.
			_wasCancellationRequestedByCaller = true;
			RequestNativeCancel();

			WaitOnTheMainThreadUntilNativeCallFinished(runningTask);

			_inFlightCompletionTask = null;
		}

		void WaitOnTheMainThreadUntilNativeCallFinished(Task<string> runningTask)
		{
			long millisecondsToWaitAtMost = (long)(_secondsToWaitForNativeCallOnShutdown * 1000f);
			bool wasTheWaitAnnounced = false;

			var stopwatch = Stopwatch.StartNew();

			while (!runningTask.IsCompleted && stopwatch.ElapsedMilliseconds < millisecondsToWaitAtMost)
			{
				// The Editor is frozen for as long as this loop runs, so it says why. Without this
				// line a wait of more than a moment looks exactly like a hang.
				if (!wasTheWaitAnnounced && stopwatch.ElapsedMilliseconds >= k_millisecondsBeforeSayingTheEditorIsWaiting)
				{
					wasTheWaitAnnounced = true;
					Debug.Log("[LlmGateway] Waiting for the native generation to stop before the model is unloaded. The Editor is unresponsive until it does.");
				}

				Thread.Sleep(k_millisecondsBetweenShutdownChecks);
			}

			if (runningTask.IsCompleted)
			{
				Debug.Log($"[LlmGateway] Stopped the running generation in {stopwatch.ElapsedMilliseconds} ms before the model was unloaded.");
				return;
			}

			Debug.LogError($"[LlmGateway] The native generation did not stop within {_secondsToWaitForNativeCallOnShutdown} s, so the model is being unloaded while a call is still inside it. That is a native use-after-free and the Editor may die here - please report it with what the agent was doing.");
		}

		void RequestNativeCancel()
		{
			try
			{
				_llmClient.CancelRequest(k_completionSlotId);
			}
			catch (Exception exception)
			{
				Debug.LogWarning($"[LlmGateway] Cancelling the native request failed: {exception.GetType().Name}: {exception.Message}");
			}
		}

		async UniTask<bool> WaitUntilModelServiceStartedAsync(CancellationToken cancellationToken)
		{
			LLM loadedModel = _llmClient.llm;
			var stopwatch = Stopwatch.StartNew();

			while (!loadedModel.started)
			{
				if (loadedModel.failed)
				{
					Debug.LogError("[LlmGateway] The LLM service failed to start. Check the model path and the console above.");
					return false;
				}

				if (cancellationToken.IsCancellationRequested)
				{
					return false;
				}

				if (stopwatch.Elapsed.TotalSeconds > _secondsToWaitForModelToLoad)
				{
					Debug.LogError($"[LlmGateway] The model did not finish loading within {_secondsToWaitForModelToLoad} seconds.");
					return false;
				}

				await UniTask.NextFrame();
			}

			return true;
		}

		async UniTask<bool> TryProbeNativeClientAsync()
		{
			// LLMClient keeps its own "started" flag private with no accessor, so the only way to know
			// its native object exists is to make the cheapest possible call and see whether it throws.
			string lastFailureMessage = "";

			for (int attempt = 0; attempt < k_readinessProbeAttempts; attempt++)
			{
				try
				{
					await _llmClient.Tokenize(k_readinessProbeText).AsUniTask();
					return true;
				}
				catch (Exception exception)
				{
					lastFailureMessage = $"{exception.GetType().Name}: {exception.Message}";
					await UniTask.DelayFrame(k_framesBetweenReadinessProbes);
				}
			}

			Debug.LogError($"[LlmGateway] The model client is loaded but not usable after {k_readinessProbeAttempts} attempts. Last error: {lastFailureMessage}");
			return false;
		}

		async UniTask<LlmCompletionResult> RunOneCompletionAsync(
			string prompt,
			Action<string> onPartialText,
			LlmSamplingOverride sampling,
			string grammar,
			string stopText,
			CancellationToken cancellationToken)
		{
			if (!await WaitUntilReadyAsync(cancellationToken))
			{
				return LlmCompletionResult.Failure("The model is not ready.");
			}

			if (cancellationToken.IsCancellationRequested)
			{
				return LlmCompletionResult.Cancelled("");
			}

			ResetStreamingState(stopText);
			LlmSamplingOverride samplingBeforeThisCall = CaptureCurrentSampling();
			ApplySampling(sampling);
			ApplyGrammar(grammar);

			try
			{
				return await StreamCompletionUntilFinishedAsync(prompt, onPartialText, cancellationToken);
			}
			catch (Exception exception)
			{
				string failureMessage = $"{exception.GetType().Name}: {exception.Message}";
				Debug.LogError($"[LlmGateway] The completion failed: {failureMessage}");
				return LlmCompletionResult.Failure(failureMessage);
			}
			finally
			{
				// Grammar is eager and sticky: leaving it set would silently constrain every later call.
				ApplyGrammar("");
				ApplySampling(samplingBeforeThisCall);
				_inFlightCompletionTask = null;
			}
		}

		void ResetStreamingState(string stopText)
		{
			_latestCumulativeText = "";
			_lastForwardedText = null;
			_wasCancellationRequestedByCaller = false;
			_wasStopTextReached = false;
			_stopTextForCurrentCall = stopText ?? "";

			_secondsToFirstTokenOfThisCall = 0f;
			_millisecondsAtLastSpeedReport = 0;
			_textGeneratedInTheLastCall = null;
			_charactersGeneratedInTheLastCall = 0;
		}

		LlmSamplingOverride CaptureCurrentSampling()
		{
			return new LlmSamplingOverride
			{
				MaximumTokensToGenerate = _llmClient.numPredict,
				Temperature = _llmClient.temperature,
				TopP = _llmClient.topP,
				TopK = _llmClient.topK,
				Seed = _llmClient.seed,
				RepeatPenalty = _llmClient.repeatPenalty,
				MinP = _llmClient.minP
			};
		}

		void ApplySampling(LlmSamplingOverride sampling)
		{
			if (sampling == null)
			{
				return;
			}

			// These fields are cached and pushed to the native object on the next completion, so they
			// have to be set before the call starts - changing them mid-flight does nothing.
			if (sampling.MaximumTokensToGenerate.HasValue)
			{
				_llmClient.numPredict = sampling.MaximumTokensToGenerate.Value;
			}

			if (sampling.Temperature.HasValue)
			{
				_llmClient.temperature = sampling.Temperature.Value;
			}

			if (sampling.TopP.HasValue)
			{
				_llmClient.topP = sampling.TopP.Value;
			}

			if (sampling.TopK.HasValue)
			{
				_llmClient.topK = sampling.TopK.Value;
			}

			if (sampling.Seed.HasValue)
			{
				_llmClient.seed = sampling.Seed.Value;
			}

			if (sampling.RepeatPenalty.HasValue)
			{
				_llmClient.repeatPenalty = sampling.RepeatPenalty.Value;
			}

			if (sampling.MinP.HasValue)
			{
				_llmClient.minP = sampling.MinP.Value;
			}
		}

		void ApplyGrammar(string grammar)
		{
			try
			{
				_llmClient.grammar = grammar ?? "";
			}
			catch (Exception exception)
			{
				Debug.LogWarning($"[LlmGateway] Setting the grammar failed: {exception.GetType().Name}: {exception.Message}");
			}
		}

		async UniTask<LlmCompletionResult> StreamCompletionUntilFinishedAsync(string prompt, Action<string> onPartialText, CancellationToken cancellationToken)
		{
			var stopwatchOfThisCall = Stopwatch.StartNew();

			// Kept in a local as well as in the field: teardown clears the field the moment it has
			// drained the call, and this loop still has to reach its own end cleanly.
			Task<string> completionTask = Task.Run(() => _llmClient.Completion(prompt, HandleRawPartialTextFromGenerationThread, null, k_completionSlotId));
			_inFlightCompletionTask = completionTask;

			while (!completionTask.IsCompleted)
			{
				await UniTask.Delay(k_millisecondsBetweenStreamingUpdates, DelayType.Realtime);

				if (cancellationToken.IsCancellationRequested && !_wasCancellationRequestedByCaller)
				{
					CancelActiveCompletion();
				}

				// This loop runs on the main thread - UniTask.Delay resumes on the player loop
				// whatever thread the generation itself is on - so both of these may touch the UI.
				ForwardTextToListener(onPartialText, _latestCumulativeText);
				ReportGenerationSpeed(stopwatchOfThisCall, _latestCumulativeText);
			}

			string generatedText = await completionTask.AsUniTask();

			_charactersGeneratedInTheLastCall = (generatedText ?? "").Length;
			ReportGenerationSpeed(stopwatchOfThisCall, generatedText, isTheLastReportOfTheCall: true);

			return BuildResultFromGeneratedText(generatedText, onPartialText);
		}

		// Everything about the speed figure is worked out here, because nothing downstream can work
		// it out for itself: see LlmGenerationStats for why the backend reports no timings at all.
		//
		// The clock for tokens per second starts at the FIRST TOKEN, not at the call. The act pass
		// runs on a prompt that is a byte-exact extension of the think pass's, so llama.cpp reuses
		// the KV cache and its prefill is nearly free - folding that into the same number would read
		// as the model having suddenly got ten times faster. Prefill gets its own figure instead,
		// and that figure is the one that shows a model spilling out of VRAM.
		void ReportGenerationSpeed(Stopwatch stopwatchOfThisCall, string cumulativeText, bool isTheLastReportOfTheCall = false)
		{
			if (OnGenerationStatsProduced == null) return;
			if (string.IsNullOrEmpty(cumulativeText)) return;

			if (_secondsToFirstTokenOfThisCall <= 0f)
			{
				_secondsToFirstTokenOfThisCall = (float)stopwatchOfThisCall.Elapsed.TotalSeconds;
			}

			if (!isTheLastReportOfTheCall &&
				stopwatchOfThisCall.ElapsedMilliseconds - _millisecondsAtLastSpeedReport < k_millisecondsBetweenSpeedReports)
			{
				return;
			}

			_millisecondsAtLastSpeedReport = stopwatchOfThisCall.ElapsedMilliseconds;

			int tokensGenerated = Mathf.Max(1, Mathf.RoundToInt(cumulativeText.Length / _charactersPerTokenOfThisCall));
			float secondsSpentDecoding = (float)stopwatchOfThisCall.Elapsed.TotalSeconds - _secondsToFirstTokenOfThisCall;
			float tokensPerSecond = secondsSpentDecoding > 0f ? tokensGenerated / secondsSpentDecoding : 0f;

			RaiseGenerationStats(new LlmGenerationStats(tokensGenerated, tokensPerSecond, _secondsToFirstTokenOfThisCall));
		}

		// Wrapped, and it has to be. AgentEvents deliberately does not guard its subscribers, and an
		// exception raised from in here would be caught by RunOneCompletionAsync and turned into a
		// failed completion - a run killed by a status bar. Worse, that catch runs the finally that
		// clears _inFlightCompletionTask, so the shutdown drain would stop waiting on a native call
		// that is still running, and the model could be unloaded from under it.
		void RaiseGenerationStats(LlmGenerationStats generationStats)
		{
			try
			{
				OnGenerationStatsProduced?.Invoke(generationStats);
			}
			catch (Exception exception)
			{
				Debug.LogError($"[LlmGateway] A generation-stats listener threw, and it was swallowed so the run survives: {exception}");
			}
		}

		// Called once a call is completely over, with the tokenizer free again. Characters per token
		// is what turns a length into a token count while the text is still arriving, and it differs
		// sharply between the two passes - prose runs near four, a tool call carrying escaped source
		// runs far lower - so the two are calibrated apart, keyed on whether a grammar was set.
		async UniTask CalibrateCharactersPerTokenAsync(bool wasThisCallConstrainedByAGrammar)
		{
			if (_charactersGeneratedInTheLastCall <= 0) return;
			if (_textGeneratedInTheLastCall == null) return;

			int measuredTokenCount = await CountTokensAsync(_textGeneratedInTheLastCall);
			_textGeneratedInTheLastCall = null;

			if (measuredTokenCount <= 0) return;

			float measuredCharactersPerToken = (float)_charactersGeneratedInTheLastCall / measuredTokenCount;
			float clampedCharactersPerToken = Mathf.Clamp(measuredCharactersPerToken,
				k_smallestSensibleCharactersPerToken, k_largestSensibleCharactersPerToken);

			if (wasThisCallConstrainedByAGrammar)
			{
				_charactersPerTokenOfAConstrainedCall = clampedCharactersPerToken;
				return;
			}

			_charactersPerTokenOfAnUnconstrainedCall = clampedCharactersPerToken;
		}

		void HandleRawPartialTextFromGenerationThread(string cumulativeText)
		{
			// This runs on the thread the native library generates on, so nothing here may touch the
			// Unity API. It buffers the text and, at most, makes one more native call.
			_latestCumulativeText = cumulativeText ?? "";

			if (_wasStopTextReached || string.IsNullOrEmpty(_stopTextForCurrentCall))
			{
				return;
			}

			if (_latestCumulativeText.IndexOf(_stopTextForCurrentCall, StringComparison.Ordinal) < 0)
			{
				return;
			}

			// The package has no stop-sequence support of any kind, so cancelling the slot is the only
			// way to stop at a marker.
			_wasStopTextReached = true;
			RequestNativeCancel();
		}

		void ForwardTextToListener(Action<string> onPartialText, string text)
		{
			if (onPartialText == null || text == _lastForwardedText)
			{
				return;
			}

			_lastForwardedText = text;

			try
			{
				onPartialText(text);
			}
			catch (Exception exception)
			{
				Debug.LogError($"[LlmGateway] The partial text listener threw: {exception}");
			}
		}

		LlmCompletionResult BuildResultFromGeneratedText(string generatedText, Action<string> onPartialText)
		{
			string finalText = TrimAtStopText(generatedText ?? "");
			ForwardTextToListener(onPartialText, finalText);

			// A cancelled generation returns its partial text through the ordinary success path, so our
			// own flag is the only thing that tells the two apart.
			if (_wasCancellationRequestedByCaller)
			{
				return LlmCompletionResult.Cancelled(finalText);
			}

			return LlmCompletionResult.Success(finalText);
		}

		string TrimAtStopText(string text)
		{
			if (string.IsNullOrEmpty(_stopTextForCurrentCall))
			{
				return text;
			}

			int stopTextIndex = text.IndexOf(_stopTextForCurrentCall, StringComparison.Ordinal);
			if (stopTextIndex < 0)
			{
				return text;
			}

			return text.Substring(0, stopTextIndex + _stopTextForCurrentCall.Length);
		}
	}
}
