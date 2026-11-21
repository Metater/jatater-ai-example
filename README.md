# Jatater AI for Unity

Make a single-player phone call to Dorothy Mayfield, Brayden “Tugboat” Mills, or Cletus “Mudbug” Thibodeaux from *Scam With Your Friends*. Type a line or record one, then hear the caller answer. Each character has their original personality, greeting and Pocket voice reference.

## Start a call

1. Open this project in Unity 6000.3.26f1 or import the JataterAI unitypackage into an existing Unity 6 project. Open **Tools → Jatater AI → Setup**.
2. Open your Jatater workspace. Create a project, select its development environment and publish the **generic** prompt with **Client controlled** system instructions. The sample sends the caller’s personality with every turn. In a development environment using full provider passthrough, select the provider model explicitly as described in the integration guide.
3. Enable scoped API keys in that environment and create a testing key with a $0.50 budget and the scopes `llm:generic` and `feedback`. Add `stt` or `tts` if you enable the corresponding cloud service. The workspace needs available credit. Copy the full project environment URL from the dashboard into **Select connection settings**.
4. When using the example repository, the integrations are already declared. When importing the standalone package, install the adapters you want in **Setup**. For local audio, download the two English model sets there. Their hashes are checked against the game’s files. The example targets Windows x64 for local Pocket TTS. Choose **Text** playback to get started without models, or choose **Inworld** and configure your own Inworld credential and service in the dashboard.
5. Click **Open phone call scene**, press Play, enter the scoped testing key and select a caller. You can also supply `JATATER_SCOPED_KEY` to the Editor process or a development build. The key stays in memory. The feedback button sends a report to your workspace.

Use Steam authentication for shipped clients. A shared key in a distributed client can be extracted. Keep all provider and Steam publisher keys in the dashboard vault.

[Download the Unity package](https://github.com/Metater/jatater-ai-example/releases) · [Integration guide](Assets/JataterAI/Integration.md) · [Credits](Assets/JataterAI/Notices/Credits.md)

## Repository layout and releases

`Assets/JataterAI` is the drop-in library, including the phone scene. `Integrations` holds optional UPM adapters. To prepare a release, run `python Tools/package_release.py` from this folder after opening the project once in Unity so asset metadata exists. The script embeds the optional adapter sources and writes `Builds/JataterAI-0.1.0.unitypackage`. Use `unity assets inspect Builds/JataterAI-0.1.0.unitypackage` to inspect its asset list. Model weights are downloaded separately.
