#if UNITY_STANDALONE || UNITY_EDITOR
using System;
using System.Threading;
using System.Threading.Tasks;
using Steamworks;
using UnityEngine;

namespace JataterAI.SteamIntegration
{
    public sealed class SteamTicketSource : MonoBehaviour, ISteamTicketSource
    {
        bool external, ownsSteam;
        HAuthTicket handle = HAuthTicket.Invalid;
        Callback<GetTicketForWebApiResponse_t> callback;
        TaskCompletionSource<string> pending;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        static void Register() => Adapters.Steam = (host, externalManager) =>
        {
            var source = host.AddComponent<SteamTicketSource>();
            source.external = externalManager;
            return source;
        };

        public async Task<string> AcquireAsync(CancellationToken cancellation)
        {
            cancellation.ThrowIfCancellationRequested();
            if (external)
            {
                if (!SteamAPI.IsSteamRunning() || !SteamUser.BLoggedOn()) throw new Exception("Initialize the game's Steam manager and sign into Steam first.");
            }
            else if (!ownsSteam)
            {
                if (!Packsize.Test() || !DllCheck.Test()) throw new Exception("Steamworks.NET native and managed libraries do not match.");
                if (!SteamAPI.Init()) throw new Exception("Steam initialization failed. Open Steam, sign in, and set the game's App ID.");
                ownsSteam = true;
            }
            callback ??= Callback<GetTicketForWebApiResponse_t>.Create(OnTicket);
            pending = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            handle = SteamUser.GetAuthTicketForWebApi("jatater");
            if (handle == HAuthTicket.Invalid) throw new Exception("Steam declined the Web API ticket request.");
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
            timeout.CancelAfter(15000);
            var request = pending;
            using var register = timeout.Token.Register(() => request.TrySetCanceled());
            try { return await request.Task; }
            catch (OperationCanceledException) when (!cancellation.IsCancellationRequested) { throw new TimeoutException("Timed out awaiting the Steam ticket callback."); }
        }

        void OnTicket(GetTicketForWebApiResponse_t ticket)
        {
            if (ticket.m_hAuthTicket != handle || pending == null) return;
            if (ticket.m_eResult != EResult.k_EResultOK || ticket.m_cubTicket <= 0 || ticket.m_rgubTicket == null || ticket.m_cubTicket > ticket.m_rgubTicket.Length)
            { pending.TrySetException(new Exception("Steam rejected the Web API ticket.")); return; }
            pending.TrySetResult(BitConverter.ToString(ticket.m_rgubTicket, 0, ticket.m_cubTicket).Replace("-", ""));
        }

        public void Release()
        {
            pending?.TrySetCanceled(); pending = null;
            if (handle == HAuthTicket.Invalid) return;
            SteamUser.CancelAuthTicket(handle); handle = HAuthTicket.Invalid;
        }
        void Update() { if (ownsSteam) SteamAPI.RunCallbacks(); }
        void OnDestroy() { Release(); callback?.Dispose(); if (ownsSteam) SteamAPI.Shutdown(); }
    }
}
#endif
