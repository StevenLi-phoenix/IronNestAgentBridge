using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using MelonLoader;

namespace IronNestAgentBridge.Http;

/// <summary>
/// Local HTTP API for external agents. Binds 127.0.0.1 only — this is a local control
/// surface for the player's own agent, not a network service.
///
///   GET  /state                     full snapshot (map, teleprinters, guns, FCS)
///   GET  /events?since=N&amp;timeoutMs=25000   long-poll for new events
///   POST /fire                      queue a fire mission (see FireMissionRequest)
///   POST /print                     print lines on a teleprinter {"which":"primary","lines":[...]}
/// </summary>
public class BridgeServer
{
    public const int Port = 17171;

    private readonly HttpListener _listener = new();
    private readonly AgentBridgeMod _mod;
    private volatile bool _running;

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = false,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    public BridgeServer(AgentBridgeMod mod) => _mod = mod;

    public void Start()
    {
        _listener.Prefixes.Add($"http://127.0.0.1:{Port}/");
        _listener.Start();
        _running = true;
        for (var i = 0; i < 4; i++)
        {
            var thread = new Thread(ListenLoop) { IsBackground = true, Name = $"AgentBridge-http-{i}" };
            thread.Start();
        }
        MelonLogger.Msg($"[AgentBridge] HTTP API listening on http://127.0.0.1:{Port}/");
    }

    public void Stop()
    {
        _running = false;
        try { _listener.Stop(); } catch { }
    }

    private void ListenLoop()
    {
        while (_running)
        {
            HttpListenerContext ctx;
            try { ctx = _listener.GetContext(); }
            catch { return; }
            try { Handle(ctx); }
            catch (Exception ex)
            {
                MelonLogger.Warning($"[AgentBridge] request failed: {ex.Message}");
                TryWrite(ctx, 500, new { error = ex.Message });
            }
        }
    }

    private void Handle(HttpListenerContext ctx)
    {
        var path = ctx.Request.Url?.AbsolutePath?.TrimEnd('/') ?? "";
        var method = ctx.Request.HttpMethod;

        switch (method, path)
        {
            case ("GET", "/state"):
            {
                var snapshot = MainThread.Run(() => _mod.BuildSnapshot()).GetAwaiter().GetResult();
                TryWrite(ctx, 200, snapshot);
                break;
            }
            case ("GET", "/events"):
            {
                var q = ctx.Request.QueryString;
                long since = long.TryParse(q["since"], out var s) ? s : EventLog.LatestSeq;
                int timeout = int.TryParse(q["timeoutMs"], out var t) ? Math.Clamp(t, 0, 60_000) : 25_000;
                var events = EventLog.WaitForEvents(since, timeout);
                TryWrite(ctx, 200, new { latest = EventLog.LatestSeq, events });
                break;
            }
            case ("POST", "/fire"):
            {
                var req = ReadBody<FireMissionRequest>(ctx);
                if (req == null) { TryWrite(ctx, 400, new { error = "invalid JSON body" }); break; }
                var result = MainThread.Run(() => _mod.QueueFireMission(req)).GetAwaiter().GetResult();
                TryWrite(ctx, result == "ok" ? 200 : 409, new { result });
                break;
            }
            case ("GET", "/markers"):
            {
                var info = MainThread.Run(() => GameState.MapDrawer.Inspect()).GetAwaiter().GetResult();
                TryWrite(ctx, 200, info);
                break;
            }
            case ("POST", "/draw"):
            {
                var req = ReadBody<DrawRequest>(ctx);
                if (req?.PrefabName == null) { TryWrite(ctx, 400, new { error = "need {placerIndex, prefabName, ox, oy, tx, ty}" }); break; }
                var result = MainThread.Run(() => GameState.MapDrawer.Draw(
                    req.PlacerIndex, req.PrefabName!,
                    new UnityEngine.Vector2(req.Ox, req.Oy), new UnityEngine.Vector2(req.Tx, req.Ty))).GetAwaiter().GetResult();
                TryWrite(ctx, 200, new { result });
                break;
            }
            case ("POST", "/requisition"):
            {
                var req = ReadBody<RequisitionRequest>(ctx);
                if (req?.CardId == null) { TryWrite(ctx, 400, new { error = "need {cardId, bearingDeg?}" }); break; }
                var result = MainThread.Run(() =>
                    GameState.RequisitionOperator.StartPurchase(req.CardId!, req.BearingDeg, null), 15_000)
                    .GetAwaiter().GetResult();
                TryWrite(ctx, 200, new { result });
                break;
            }
            case ("GET", "/console"):
            {
                var info = MainThread.Run(() => GameState.RequisitionOperator.InspectConsole()).GetAwaiter().GetResult();
                TryWrite(ctx, 200, info);
                break;
            }
            case ("POST", "/scoutplane"):
            {
                var req = ReadBody<ScoutPlaneRequest>(ctx);
                if (req == null) { TryWrite(ctx, 400, new { error = "need {kmX, kmY, bearingDeg}" }); break; }
                var result = MainThread.Run(() => GameState.ScoutPlaneOperator.Spawn(req.KmX, req.KmY, req.BearingDeg))
                    .GetAwaiter().GetResult();
                TryWrite(ctx, 200, result);
                break;
            }
            case ("POST", "/draw/clear"):
            {
                var result = MainThread.Run(() => GameState.MapDrawer.ClearAll()).GetAwaiter().GetResult();
                TryWrite(ctx, 200, new { result });
                break;
            }
            case ("POST", "/print"):
            {
                var req = ReadBody<PrintRequest>(ctx);
                if (req == null || req.Lines == null || req.Lines.Length == 0)
                { TryWrite(ctx, 400, new { error = "need {which, lines[]}" }); break; }
                var ok = MainThread.Run(() => _mod.PrintOnTeleprinter(req.Which ?? "secondary", req.Lines))
                    .GetAwaiter().GetResult();
                TryWrite(ctx, ok ? 200 : 409, new { result = ok ? "ok" : "printer not available" });
                break;
            }
            default:
                TryWrite(ctx, 404, new
                {
                    error = "unknown endpoint",
                    endpoints = new[] { "GET /state", "GET /events?since=N", "POST /fire", "POST /print" },
                });
                break;
        }
    }

    private class PrintRequest
    {
        public string? Which { get; set; }
        public string[]? Lines { get; set; }
    }

    private class RequisitionRequest
    {
        public string? CardId { get; set; }
        public float? BearingDeg { get; set; }
    }

    private class ScoutPlaneRequest
    {
        public float KmX { get; set; }
        public float KmY { get; set; }
        public float BearingDeg { get; set; }
    }

    private class DrawRequest
    {
        public int PlacerIndex { get; set; }
        public string? PrefabName { get; set; }
        public float Ox { get; set; }
        public float Oy { get; set; }
        public float Tx { get; set; }
        public float Ty { get; set; }
    }

    private static T? ReadBody<T>(HttpListenerContext ctx) where T : class
    {
        try
        {
            using var reader = new StreamReader(ctx.Request.InputStream, Encoding.UTF8);
            return JsonSerializer.Deserialize<T>(reader.ReadToEnd(), Json);
        }
        catch { return null; }
    }

    private static void TryWrite(HttpListenerContext ctx, int status, object payload)
    {
        try
        {
            var bytes = JsonSerializer.SerializeToUtf8Bytes(payload, Json);
            ctx.Response.StatusCode = status;
            ctx.Response.ContentType = "application/json; charset=utf-8";
            ctx.Response.ContentLength64 = bytes.Length;
            ctx.Response.OutputStream.Write(bytes, 0, bytes.Length);
            ctx.Response.Close();
        }
        catch { /* client went away */ }
    }
}
