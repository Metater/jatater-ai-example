# Unity integration

Use **Tools → Jatater AI → Setup** to open the sample scene, select its Connection settings, install optional integrations and download local models. The library works with typed chat, feedback and cloud audio before any native package is installed. The optional package installer preserves preinstalled upstream dependencies and downloads the pinned dependencies when required.

## Client and authentication

Create a `JataterClient` on Unity's main thread using a `ConnectionSettings` asset, a test-key callback and, for Steam, an `ISteamTicketSource`. Call `ChatAsync`, `TranscribeAsync`, `SpeakAsync` and `FeedbackAsync` with a cancellation token tied to your object lifetime. The client serializes token refresh, renews short-lived grants and avoids replaying ambiguous paid requests. Call `FeedbackAsync` with the same report UUID when retrying.

A scoped key exchanges at `/auth/key`. Its lifetime or periodic spend cap and service scopes come from the dashboard. Disable scoped keys for production. Configure the Steam App ID and publisher credential in your environment. Install the **Steam** adapter in Setup; the example project already includes it. Start the Steam client under an account that owns the game. For Editor testing, create a local ignored `steam_appid.txt` with that App ID at the project root. Select **Steam** in Connection settings. Select **External Steam Manager** if the game initializes Steam and pumps its callbacks. The sample-managed setup initializes Steam on demand, checks the library pair, requests a `GetAuthTicketForWebApi("jatater")` ticket, waits for its matching callback, exchanges it at `/auth/steam` and cancels it after the exchange. The resulting session can renew `/auth/ai-token` without creating another Steam ticket until session renewal is needed. For another authentication provider, request it from the dashboard.

[Steamworks.NET](https://github.com/rlabrecque/Steamworks.NET) · [Valve authentication reference](https://partner.steamgames.com/doc/features/auth)

## Prompts and conversation length

The phone sample supplies a client system message using a **Client controlled** prompt. Enforced provider configuration can still lock the model and output limit. If you intentionally use full development passthrough, enter the provider model in **Provider Model**. **Compact Conversation** keeps the character instructions, opening line and most recent complete user/reply pairs. Defaults follow the game’s limits of 128 completed pairs or roughly 10,000 tokens, retaining up to 32 pairs. The input estimate is local. Compaction discards older pairs. Change `maxTurnPairs`, `retainTurnPairs` and `estimatedTokenLimit` in Connection settings.

## Speech and shared microphones

For standalone use, click **Record a turn**, speak and click **Send recording**. The sample captures up to 20 seconds of mono PCM at 16 kHz. Whisper runs offline. The English model defaults to `StreamingAssets/Whisper/ggml-base.en.bin`. Choose another compatible model from the Whisper model directory and change the model path and language in Connection settings. Use a multilingual model for non-English calls.

Use one microphone capture owner per input device. If Meta Voice Chat, Dissonance or Vivox already captures the player's microphone, enable **External Microphone** and tap its local captured PCM before network encoding. Collect one local player turn, then call `PhoneCall.SubmitMicrophoneTurn(float[] samples, int sampleRate, int channels)` on the Unity main thread. Copy reused audio buffers before handing them across threads. Pass the actual sample rate and channel count and exclude playback and remote participants. The Unity sample uses completed WAV uploads for Inworld. Configure the Inworld STT model and key for that environment.

For a realtime integration with the project streaming endpoint, use signed 16-bit little-endian mono PCM at 16 kHz. Send a known call-language hint. For push-to-talk, send the last audio frame, then send a single `closeStream` for a one-shot socket or `endTurn` when keeping the same socket open. For hands-free detection, keep sending silence between speech frames. Inworld’s recommended initial end-of-turn settings are confidence threshold 0.4, VAD threshold 0.35, minimum confident silence 300 ms and maximum turn silence 800 ms. Tune with real players and natural pauses. If also timing a local fallback, begin around 0.8–1 s. Commit only final, nonempty transcription results. A language hint helps recognition in noise but cannot guarantee the detected language.

[Whisper Unity](https://github.com/Macoron/whisper.unity) · [Whisper models](https://huggingface.co/ggerganov/whisper.cpp/tree/main) · [Meta Voice Chat](https://github.com/Metater/MetaVoiceChat) · [Dissonance](https://placeholder-software.co.uk/dissonance/docs/) · [Vivox](https://docs.unity.com/ugs/en-us/manual/vivox-unity/manual/Unity/Unity) · [Inworld STT](https://docs.inworld.ai/stt/overview)

## Playback and regional service

**Pocket** uses the game’s patched CPU runtime with the ONNX CPU memory arena disabled. The supplied English voice references match the selected caller. **Inworld** sends text through the environment's configured TTS service. It uses the environment's cloud voice and incurs provider usage. **Text** displays the transcript. Play one local reply at a time.

Choose **Automatic** routing and optionally provide a trusted alternate HTTPS project endpoint that your team operates. It must route the same environment and support its authentication endpoints. Configure it only when you trust it to receive bearer tokens and Steam tickets. A 32 KiB readiness check precedes credential transmission in Automatic mode when an alternate is configured. The next user action can select the alternative after a network failure. You can also explicitly choose **Direct** or **Alternate**. Set geographic AI-provider policies on the workspace server so clients send only to the Jatater service. No shared public relay or provider credential ships in this package.
