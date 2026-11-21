using System;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;

namespace JataterAI
{
    public enum Authentication { ScopedTestKey, Steam }
    public enum Recognition { Whisper, Inworld }
    public enum Playback { Pocket, Inworld, Text }
    public enum Route { Automatic, Direct, Alternate }

    [CreateAssetMenu(menuName = "Jatater AI/Connection settings")]
    public sealed class ConnectionSettings : ScriptableObject
    {
        [Tooltip("Copy the full project environment endpoint from the dashboard.")]
        public string endpoint = "";
        [Tooltip("Optional trusted HTTPS alternate endpoint for the same project and environment. It receives credentials.")]
        public string alternateEndpoint = "";
        public Route routing = Route.Automatic;
        public Authentication authentication = Authentication.ScopedTestKey;
        [Tooltip("Use a published Client controlled prompt. The sample supplies each caller's personality.")]
        public string promptId = "generic";
        [Tooltip("Provider model for full development passthrough. For a published enforced prompt leave this empty.")]
        public string providerModel = "";
        public Recognition recognition = Recognition.Whisper;
        public Playback playback = Playback.Pocket;
        public string language = "en";
        public string whisperModel = "Whisper/ggml-base.en.bin";
        [Tooltip("Enable this only if the game already initializes Steam and pumps callbacks.")]
        public bool externalSteamManager;
        [Tooltip("The host voice-chat system calls SubmitMicrophoneTurn with captured PCM.")]
        public bool externalMicrophone;
        public bool compactConversation = true;
        [Min(1)] public int maxTurnPairs = 128;
        [Min(0)] public int retainTurnPairs = 32;
        [Min(128)] public int estimatedTokenLimit = 10000;
    }

    // Optional UPM integrations register factories before the scene starts.
    public static class Adapters
    {
        public static Func<GameObject, bool, ISteamTicketSource> Steam;
        public static Func<GameObject, string, string, IRecognizer> Whisper;
        public static Func<GameObject, ISpeaker> Pocket;
    }
    public interface ISteamTicketSource
    {
        Task<string> AcquireAsync(CancellationToken cancellation);
        void Release();
    }
    public interface IRecognizer
    {
        Task<string> TranscribeAsync(float[] pcm, int rate, int channels, CancellationToken cancellation);
    }
    public interface ISpeaker
    {
        Task SpeakAsync(string text, string referencePath, CancellationToken cancellation);
    }
}
