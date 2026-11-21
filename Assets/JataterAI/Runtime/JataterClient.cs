using System;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.Networking;

namespace JataterAI
{
    [Serializable] public class Message
    {
        public string role, content;
        public Message(string role, string content) { this.role = role; this.content = content; }
    }

    // Use from Unity's main thread. The caller owns the lifetime of its cancellation token.
    public sealed class JataterClient
    {
        [Serializable] class Grant { public string token; public int expires_in; }
        [Serializable] class Ticket { public string ticket; }
        [Serializable] class Choice { public Message message; }
        [Serializable] class Completion { public Choice[] choices; }
        [Serializable] class ChatRequest { public string prompt_id; public string model; public Message[] messages; public int max_tokens = 150; }
        [Serializable] class Transcript { public string text; }
        [Serializable] class Speech { public string text; }
        [Serializable] class Report { public string id, description, version, platform; public string kind = "feedback"; }
        readonly ConnectionSettings config;
        readonly Func<string> testKey;
        readonly ISteamTicketSource steam;
        readonly SemaphoreSlim authLock = new SemaphoreSlim(1, 1);
        string aiToken, sessionToken, activeEndpoint;
        double renewAt, sessionRenewAt;

        public JataterClient(ConnectionSettings settings, Func<string> testKey, ISteamTicketSource steam = null)
        {
            config = settings;
            this.testKey = testKey;
            this.steam = steam;
        }

        public async Task<string> ChatAsync(Message[] messages, CancellationToken cancellation)
        {
            byte[] bytes = await Authorized("/v1/chat/completions", Json(new ChatRequest {
                prompt_id = config.promptId, model = string.IsNullOrWhiteSpace(config.providerModel) ? config.promptId : config.providerModel, messages = messages }), "application/json", cancellation);
            var response = Parse<Completion>(bytes);
            string answer = response?.choices?.Length > 0 ? response.choices[0].message?.content : null;
            if (string.IsNullOrWhiteSpace(answer)) throw new Exception("The chat provider returned an empty reply.");
            return answer;
        }

        public async Task<string> TranscribeAsync(byte[] pcmWav, CancellationToken cancellation)
        {
            string path = "/v1/audio/transcriptions";
            if (!string.IsNullOrWhiteSpace(config.language)) path += "?language=" + Uri.EscapeDataString(config.language.Trim());
            byte[] bytes = await Authorized(path, pcmWav, "audio/wav", cancellation);
            return Parse<Transcript>(bytes)?.text?.Trim() ?? "";
        }

        public Task<byte[]> SpeakAsync(string text, CancellationToken cancellation) =>
            Authorized("/v1/audio/speech", Json(new Speech { text = text }), "application/json", cancellation);

        public Task<byte[]> FeedbackAsync(string id, string text, CancellationToken cancellation) =>
            Authorized("/v1/feedback", Json(new Report { id = id, description = text,
                version = Application.version, platform = Application.platform.ToString() }), "application/json", cancellation);

        async Task<byte[]> Authorized(string path, byte[] data, string media, CancellationToken cancellation)
        {
            await Authenticate(cancellation);
            try { return await Send(path, aiToken, data, media, cancellation); }
            catch (HttpFailure e) when (e.Status == 401)
            {
                aiToken = null;
                await Authenticate(cancellation);
                return await Send(path, aiToken, data, media, cancellation);
            }
        }

        async Task Authenticate(CancellationToken cancellation)
        {
            await authLock.WaitAsync(cancellation);
            try
            {
                if (activeEndpoint != null && !string.IsNullOrEmpty(aiToken) && Time.realtimeSinceStartupAsDouble < renewAt) return;
                if (activeEndpoint == null) activeEndpoint = await SelectEndpoint(cancellation);
                Grant grant;
                if (config.authentication == Authentication.Steam)
                {
                    if (steam == null) throw new Exception("Install the Steam adapter using Tools > Jatater AI > Setup.");
                    if (string.IsNullOrEmpty(sessionToken) || Time.realtimeSinceStartupAsDouble >= sessionRenewAt)
                    {
                        try
                        {
                            var ticket = await steam.AcquireAsync(cancellation);
                            var session = Parse<Grant>(await Send("/auth/steam", null, Json(new Ticket { ticket = ticket }), "application/json", cancellation));
                            sessionToken = RequireToken(session);
                            sessionRenewAt = RefreshTime(session);
                        }
                        finally { steam.Release(); }
                    }
                    try { grant = Parse<Grant>(await Send("/auth/ai-token", sessionToken, null, null, cancellation)); }
                    catch (HttpFailure e) when (e.Status == 401) { sessionToken = null; throw; }
                }
                else
                {
                    string key = testKey?.Invoke()?.Trim();
                    if (string.IsNullOrEmpty(key)) throw new Exception("Enter a scoped testing key or select Steam authentication.");
                    grant = Parse<Grant>(await Send("/auth/key", key, null, null, cancellation));
                }
                aiToken = RequireToken(grant);
                renewAt = RefreshTime(grant);
            }
            finally { authLock.Release(); }
        }

        static string RequireToken(Grant grant) => !string.IsNullOrEmpty(grant?.token) ? grant.token : throw new Exception("Missing authentication grant.");
        static double RefreshTime(Grant grant) => Time.realtimeSinceStartupAsDouble + Math.Max(1, grant.expires_in - 20);
        static T Parse<T>(byte[] bytes) => JsonUtility.FromJson<T>(Encoding.UTF8.GetString(bytes));
        static byte[] Json(object data) => Encoding.UTF8.GetBytes(JsonUtility.ToJson(data));

        static string Endpoint(string value)
        {
            if (!Uri.TryCreate(value?.Trim(), UriKind.Absolute, out var uri) || uri.Scheme != "https"
                || !string.IsNullOrEmpty(uri.UserInfo + uri.Query + uri.Fragment))
                throw new Exception("Set a trusted HTTPS project endpoint in Connection settings.");
            return uri.AbsoluteUri.TrimEnd('/');
        }

        bool preferAlternate;
        async Task<string> SelectEndpoint(CancellationToken cancellation)
        {
            if (config.routing == Route.Alternate) return Endpoint(config.alternateEndpoint);
            string direct = Endpoint(config.endpoint);
            if (config.routing == Route.Direct || string.IsNullOrWhiteSpace(config.alternateEndpoint)) return direct;
            string alternate = Endpoint(config.alternateEndpoint);
            string first = preferAlternate ? alternate : direct;
            string second = preferAlternate ? direct : alternate;
            if (await Reachable(first, cancellation)) return first;
            if (await Reachable(second, cancellation)) return second;
            throw new Exception("The project could not be reached on either configured route.");
        }

        static async Task<bool> Reachable(string endpoint, CancellationToken cancellation)
        {
            using var probe = UnityWebRequest.Get(endpoint + "/v1/ready");
            probe.timeout = 5;
            probe.redirectLimit = 0;
            await Wait(probe, cancellation);
            var payload = probe.downloadHandler.data;
            return probe.responseCode == 200 && probe.result == UnityWebRequest.Result.Success
                && payload?.Length == 32768 && Encoding.ASCII.GetString(payload, 0, 16) == "jatater-ready-v1";
        }

        async Task<byte[]> Send(string path, string token, byte[] body, string media, CancellationToken cancellation)
        {
            string endpoint = activeEndpoint;
            using var req = new UnityWebRequest(endpoint + path, "POST") { downloadHandler = new DownloadHandlerBuffer(), timeout = 50, redirectLimit = 0 };
            if (token != null) req.SetRequestHeader("Authorization", "Bearer " + token);
            if (body != null)
            {
                req.uploadHandler = new UploadHandlerRaw(body);
                req.SetRequestHeader("Content-Type", media);
            }
            await Wait(req, cancellation);
            if (req.result == UnityWebRequest.Result.Success) return req.downloadHandler.data;
            if (req.result == UnityWebRequest.Result.ConnectionError && config.routing == Route.Automatic)
            {
                // A timed-out chargeable request may already have completed upstream.
                // Select the other route for the next user action, never replay this POST.
                preferAlternate = !string.Equals(endpoint, config.alternateEndpoint?.Trim().TrimEnd('/'), StringComparison.OrdinalIgnoreCase);
                activeEndpoint = null;
            }
            string code = req.responseCode > 0 ? "HTTP " + req.responseCode : "Connection failed";
            throw new HttpFailure(req.responseCode, code + " at " + path + ". " + (req.responseCode == 402 ? "Check workspace credit or scoped key budget." : req.error));
        }

        static async Task Wait(UnityWebRequest request, CancellationToken cancellation)
        {
            cancellation.ThrowIfCancellationRequested();
            var op = request.SendWebRequest();
            while (!op.isDone)
            {
                if (cancellation.IsCancellationRequested) { request.Abort(); cancellation.ThrowIfCancellationRequested(); }
                await Task.Yield();
            }
            cancellation.ThrowIfCancellationRequested();
        }

        sealed class HttpFailure : Exception
        {
            public readonly long Status;
            public HttpFailure(long status, string message) : base(message) { Status = status; }
        }
    }
}
