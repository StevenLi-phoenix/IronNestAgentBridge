using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using IronNestAgentBridge.GameState;
using MelonLoader;
using UnityEngine;

namespace IronNestAgentBridge.Http;

/// <summary>
/// The local JSON control plane: battlefield state, the event log's long poll, and the action
/// endpoints that can fire the guns, spend requisition points and draw on the map.
///
/// Security posture, in full: the listener binds <c>127.0.0.1</c> only and the whole server is
/// off by default (<c>AgentBridge/EnableHttpApi</c>). There is no token, no rate limit and no
/// body-size limit, so those two rules are the entire defence — never widen the prefix to
/// <c>+</c>, <c>*</c> or <c>0.0.0.0</c>, and never default the switch on. The one addition is a
/// blanket 403 for requests carrying an <c>Origin</c> header, which stops a web page in the
/// player's browser from driving the guns by CSRF; curl and the in-process agent send no Origin
/// and are unaffected.
///
/// Threading: handlers run on the listener threads and block. Everything that touches Unity is
/// marshalled through <see cref="MainThread"/>; the listener threads themselves never read a
/// game object. <c>GET /events</c> and <c>POST /command</c> are deliberately kept off the main
/// thread so the event channel and the commander's direct orders keep working while the game is
/// paused, unfocused or loading a scene.
/// </summary>
public sealed class BridgeServer
{
    /// <summary>Fixed port of the control plane; exposed for logs and diagnostics.</summary>
    public const int Port = 17171;

    /// <summary>
    /// Four listener threads is a hard requirement, not a tuning knob: one <c>GET /events</c>
    /// long poll parks a thread for up to 60 seconds, and a single-threaded listener would
    /// starve every other endpoint behind it.
    /// </summary>
    private const int ListenerThreads = 4;

    private const int DefaultTimeoutMs = 10_000;

    /// <summary>A physical card purchase inserts the card, turns dials and waits for the button.</summary>
    private const int PurchaseTimeoutMs = 15_000;

    private const int DefaultPollTimeoutMs = 25_000;
    private const int MaxPollTimeoutMs = 60_000;

    /// <summary>
    /// Wording <c>MapDrawer.Draw</c> is pinned to when it refuses an out-of-range placer index.
    /// That refusal is a bad parameter, so it must surface as 400 rather than as a business 409.
    /// </summary>
    private const string DrawIndexRejected = "placerIndex out of range";

    private static readonly string Prefix = $"http://127.0.0.1:{Port}/";

    /// <summary>
    /// One options instance for both directions.
    /// <list type="bullet">
    /// <item>camelCase on the way out: every response field is camelCase, anonymous objects included.</item>
    /// <item>Case-insensitive on the way in: <c>kmX</c> / <c>KmX</c> / <c>kmx</c> all bind.</item>
    /// <item>Nulls are omitted entirely, so clients must treat every nullable field as "may be absent".</item>
    /// <item>The relaxed encoder must stay: event texts and refusal messages are Chinese, and the
    /// default encoder would escape them into \uXXXX, poisoning both hand debugging and the LLM context.</item>
    /// </list>
    /// </summary>
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = false,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>Complete route list; a client that guessed wrong gets the whole menu back.</summary>
    private static readonly object NotFoundPayload = new
    {
        error = "unknown endpoint",
        endpoints = new[]
        {
            "GET /state",
            "GET /events?since=N&timeoutMs=M",
            "POST /fire",
            "GET /markers",
            "POST /draw",
            "POST /turret",
            "POST /requisition",
            "GET /find?q=NAME",
            "GET /console",
            "POST /adjust",
            "POST /command",
            "POST /horn",
            "POST /scoutplane",
            "POST /draw/clear",
            "POST /print",
        },
    };

    private readonly AgentBridgeMod _mod;
    private readonly Thread[] _threads = new Thread[ListenerThreads];

    private HttpListener? _listener;

    /// <summary>Read by the listener threads; set once by <see cref="Stop"/>.</summary>
    private volatile bool _stopping;

    public BridgeServer(AgentBridgeMod mod) => _mod = mod;

    /// <summary>
    /// Binds the loopback prefix and spawns the listener threads. Throws if the port is taken;
    /// the caller is responsible for logging that and keeping the mod alive.
    /// </summary>
    public void Start()
    {
        var listener = new HttpListener();
        listener.Prefixes.Add(Prefix);
        listener.Start();
        _listener = listener;

        for (var i = 0; i < ListenerThreads; i++)
        {
            var thread = new Thread(ListenLoop)
            {
                IsBackground = true,
                Name = $"AgentBridge-http-{i}",
            };
            _threads[i] = thread;
            thread.Start();
        }

        MelonLogger.Msg($"[AgentBridge] HTTP API listening on {Prefix}");
    }

    /// <summary>
    /// Flags the shutdown first, then closes the listener: the threads see the flag, and the
    /// <c>GetContext</c> they are parked in throws, which ends them.
    /// </summary>
    public void Stop()
    {
        _stopping = true;
        try { _listener?.Stop(); }
        catch { /* already closed, or never opened */ }
    }

    private void ListenLoop()
    {
        var listener = _listener;
        if (listener == null) return;

        while (!_stopping)
        {
            HttpListenerContext ctx;
            try
            {
                ctx = listener.GetContext();
            }
            catch
            {
                // The listener is gone. This thread is done — no retry loop, no log spam.
                return;
            }

            try
            {
                Handle(ctx);
            }
            catch (Exception ex)
            {
                // Includes the main-thread TimeoutException, whose message tells the client the
                // game was unfocused or loading a scene.
                MelonLogger.Warning($"[AgentBridge] request failed: {ex.Message}");
                Write(ctx, 500, new { error = ex.Message });
            }
        }
    }

    private void Handle(HttpListenerContext ctx)
    {
        var request = ctx.Request;

        // Only a browser attaches Origin. Refusing it costs local tooling nothing and closes the
        // CSRF hole that "loopback only" does not cover.
        if (!string.IsNullOrEmpty(request.Headers["Origin"]))
        {
            Write(ctx, 403, new { error = "Origin header present — browser-originated requests are refused" });
            return;
        }

        // "/state" and "/state/" are the same route; "/" normalises to "" and falls through to 404.
        var path = (request.Url?.AbsolutePath ?? "").TrimEnd('/');
        var route = $"{request.HttpMethod} {path}";

        switch (route)
        {
            case "GET /state": HandleState(ctx); return;
            case "GET /events": HandleEvents(ctx); return;
            case "POST /fire": HandleFire(ctx); return;
            case "GET /markers": HandleMarkers(ctx); return;
            case "POST /draw": HandleDraw(ctx); return;
            case "POST /turret": HandleTurret(ctx); return;
            case "POST /requisition": HandleRequisition(ctx); return;
            case "GET /find": HandleFind(ctx); return;
            case "GET /console": HandleConsole(ctx); return;
            case "POST /adjust": HandleAdjust(ctx); return;
            case "POST /command": HandleCommand(ctx); return;
            case "POST /horn": HandleHorn(ctx); return;
            case "POST /scoutplane": HandleScoutPlane(ctx); return;
            case "POST /draw/clear": HandleDrawClear(ctx); return;
            case "POST /print": HandlePrint(ctx); return;
            default: Write(ctx, 404, NotFoundPayload); return;
        }
    }

    // ---------------------------------------------------------------- data endpoints
    // These answer with the bare object, never wrapped in {"result": …}.

    private void HandleState(HttpListenerContext ctx)
    {
        var snapshot = OnMainThread(() =>
        {
            var snap = _mod.BuildSnapshot();

            // The snapshot builder stamps the cursor while it is on the main thread; filling it
            // here only matters if it did not, and an empty log reads 0 either way. Without it a
            // client cannot tell whether the snapshot predates the events it already holds.
            if (snap.LatestSeq == 0) snap.LatestSeq = EventLog.LatestSeq;
            return snap;
        });

        Write(ctx, 200, snapshot);
    }

    /// <summary>
    /// Long poll. Never enters the main thread, so it keeps serving while the game is paused.
    /// </summary>
    private void HandleEvents(HttpListenerContext ctx)
    {
        var query = ctx.Request.QueryString;

        // Missing or malformed "since" means "only what happens from now on". Deliberate: a new
        // client must not have the whole buffer replayed at it. Pass since=0 to ask for history.
        var since = TryParseLong(query["since"]) ?? EventLog.LatestSeq;

        var timeoutMs = Math.Clamp(TryParseInt(query["timeoutMs"]) ?? DefaultPollTimeoutMs, 0, MaxPollTimeoutMs);

        var events = EventLog.WaitForEvents(since, timeoutMs);

        // Both cursors are read after the wait: "latest" lets a client heal its cursor, "oldest"
        // lets it detect a gap (it dropped out too long, the ring wrapped, or a reset cleared it).
        Write(ctx, 200, new
        {
            latest = EventLog.LatestSeq,
            oldest = EventLog.OldestSeq,
            events,
        });
    }

    private void HandleMarkers(HttpListenerContext ctx)
        => Write(ctx, 200, OnMainThread(() => MapDrawer.Inspect()));

    private void HandleConsole(HttpListenerContext ctx)
        => Write(ctx, 200, OnMainThread(() => RequisitionOperator.InspectConsole()));

    private void HandleFind(HttpListenerContext ctx)
    {
        // A substring shorter than three characters would sweep half the scene graph.
        if (ctx.Request.QueryString["q"] is not { Length: >= 3 } q)
        {
            Write(ctx, 400, new { error = "need ?q=<name substring, >=3 chars>" });
            return;
        }

        Write(ctx, 200, OnMainThread(() => SceneFinder.Find(q)));
    }

    /// <summary>Debug back door: spawns the plane prefab directly, bypassing the punch card.</summary>
    private void HandleScoutPlane(HttpListenerContext ctx)
    {
        if (ReadJson<ScoutPlaneRequest>(ctx.Request) is not { } body)
        {
            Write(ctx, 400, new { error = "need {kmX, kmY, bearingDeg}" });
            return;
        }

        var payload = OnMainThread(() => ScoutPlaneOperator.Spawn(body.KmX, body.KmY, body.BearingDeg));
        Write(ctx, CarriesError(payload) ? 409 : 200, payload);
    }

    // ---------------------------------------------------------------- action endpoints
    // These answer {"result": "<human readable>"}: 200 accepted, 409 refused.

    private void HandleFire(HttpListenerContext ctx)
    {
        if (ReadJson<FireMissionRequest>(ctx.Request) is not { } body)
        {
            Write(ctx, 400, new { error = "invalid JSON body" });
            return;
        }

        var result = OnMainThread(() => _mod.QueueFireMission(body));

        // Success is "ok (#N)" plus whatever survey suffixes the pipeline appended, so the test
        // is on the prefix — never on equality with "ok".
        WriteResult(ctx, result, result.StartsWith("ok", StringComparison.Ordinal));
    }

    private void HandleAdjust(HttpListenerContext ctx)
    {
        if (ReadJson<AdjustFireRequest>(ctx.Request) is not { } body)
        {
            Write(ctx, 400, new { error = "need {serial, targetPoint|entityId, offsetKmX?, offsetKmY?}" });
            return;
        }

        var result = OnMainThread(() => _mod.AdjustFireMission(body));

        // Same prefix rule as fire; unlike fire, the suffix is appended on refusals too, which
        // is exactly why the test cannot be an equality.
        WriteResult(ctx, result, result.StartsWith("ok", StringComparison.Ordinal));
    }

    private void HandleTurret(HttpListenerContext ctx)
    {
        if (ReadJson<TurretRequest>(ctx.Request) is not { } body)
        {
            Write(ctx, 400, new { error = "need {kmX, kmY}" });
            return;
        }

        // "{}" is valid JSON and binds to km(0,0). That is submitted as a real request and the
        // out-of-bounds check downstream refuses it — no special case here.
        var result = OnMainThread(() => _mod.SetDeclaredTurret(body.KmX, body.KmY));
        WriteResult(ctx, result, TurretAccepted(result));
    }

    private void HandleRequisition(HttpListenerContext ctx)
    {
        if (ReadJson<RequisitionRequest>(ctx.Request) is not { } body
            || string.IsNullOrWhiteSpace(body.CardId))
        {
            Write(ctx, 400, new { error = "need {cardId, bearingDeg?}" });
            return;
        }

        string cardId = body.CardId;

        // Same entry point as the requisition_card tool, so cards needing the distance dial
        // (MoveDirection) or a start grid (Spotter, LocationReport) are reachable from here too.
        var result = OnMainThread(
            () => _mod.RequestCard(cardId, body.BearingDeg, body.Priority, body.StartGrid, body.DistanceKm),
            PurchaseTimeoutMs);

        // The physical purchase is asynchronous: this only reports that it was accepted, the
        // outcome arrives later as a "requisition" event.
        WriteResult(ctx, result, RequisitionAccepted(result));
    }

    private void HandleHorn(HttpListenerContext ctx)
    {
        var result = OnMainThread(() => _mod.PullSignalHorn());
        WriteResult(ctx, result, HornAccepted(result));
    }

    private void HandleDraw(HttpListenerContext ctx)
    {
        if (ReadJson<DrawRequest>(ctx.Request) is not { PrefabName: { } prefabName } body)
        {
            Write(ctx, 400, new { error = "need {placerIndex, prefabName, ox, oy, tx, ty}" });
            return;
        }

        // Stroke coordinates are the km frame (save-file marker coordinates are km, measured).
        // A dot is a zero-length stroke; the compass prefab reads origin as centre, target as rim.
        var result = OnMainThread(() => MapDrawer.Draw(
            body.PlacerIndex,
            prefabName,
            new Vector2(body.Ox, body.Oy),
            new Vector2(body.Tx, body.Ty)));

        if (result.StartsWith(DrawIndexRejected, StringComparison.OrdinalIgnoreCase))
        {
            Write(ctx, 400, new { error = result });
            return;
        }

        WriteResult(ctx, result, result == "ok");
    }

    /// <summary>Wipes the player's own pencil work along with ours — that is the endpoint's job.</summary>
    private void HandleDrawClear(HttpListenerContext ctx)
        => WriteResult(ctx, OnMainThread(() => MapDrawer.ClearAll()), accepted: true);

    private void HandlePrint(HttpListenerContext ctx)
    {
        if (ReadJson<PrintRequest>(ctx.Request) is not { Lines: { Length: > 0 } lines } body)
        {
            Write(ctx, 400, new { error = "need {which, lines[]}" });
            return;
        }

        // Anything that is not "primary" prints on the battlefield-report machine, typos included.
        string which = string.IsNullOrWhiteSpace(body.Which) ? "secondary" : body.Which;
        var printed = OnMainThread(() => _mod.PrintOnTeleprinter(which, lines));

        if (printed) Write(ctx, 200, new { result = "ok" });
        else Write(ctx, 409, new { result = "printer not available" });
    }

    /// <summary>
    /// The commander's own voice. Stays off the main thread on purpose: a direct order must land
    /// while the game is paused or unfocused, and it outranks both teleprinters.
    /// </summary>
    private void HandleCommand(HttpListenerContext ctx)
    {
        var body = ReadJson<CommandRequest>(ctx.Request);
        var text = body?.Text?.Trim();

        if (string.IsNullOrWhiteSpace(text))
        {
            Write(ctx, 400, new { error = "need {text} — 指挥官口头直令, 权威高于统帅部电文" });
            return;
        }

        EventLog.Append("commander_order", "commander", text);
        Write(ctx, 200, new { result = "ok — 直令已下达" });
    }

    // ---------------------------------------------------------------- outcome classification

    // The mod's action methods answer in prose written for the LLM, so the status code has to be
    // derived from the wording each of them is contractually pinned to. Every rule below quotes
    // the string it keys off; changing those strings changes this classification.

    /// <summary>Mirrors SetDeclaredTurret's own success test ("not" / "rejected" absent).</summary>
    private static bool TurretAccepted(string result)
        => !Mentions(result, "rejected") && !Mentions(result, "not");

    /// <summary>Refusals read "本关场景中没有找到号角装置…" or "号角 '…' 当前不可交互…".</summary>
    private static bool HornAccepted(string result)
        => result.StartsWith("号角已拉响", StringComparison.Ordinal);

    /// <summary>
    /// Accepted either by the FCS console coordinator (result text ends with the events note) or
    /// by the bridge's own physical purchase ("started …"). Everything else — an unaffordable
    /// card, a card that is not on the console, a busy operator — is a refusal.
    /// </summary>
    private static bool RequisitionAccepted(string result)
        => result.StartsWith("started", StringComparison.Ordinal)
           || result.EndsWith("(result arrives via events)", StringComparison.Ordinal);

    private static bool Mentions(string text, string needle)
        => text.IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0;

    /// <summary>
    /// The scene operators hand back anonymous objects shaped either <c>{ result, … }</c> or
    /// <c>{ error }</c>. The payload stays unwrapped; only the status code reflects the refusal.
    /// </summary>
    private static bool CarriesError(object? payload)
    {
        if (payload == null) return true;
        try { return payload.GetType().GetProperty("error") != null; }
        catch { return false; }
    }

    // ---------------------------------------------------------------- plumbing

    /// <summary>
    /// Marshals onto the main thread and blocks. Handlers must be synchronous: the long-poll
    /// thread model is built on blocking, so an async endpoint would break it. A timeout
    /// propagates out as a <see cref="TimeoutException"/> and becomes a 500 carrying its message.
    /// </summary>
    private static T OnMainThread<T>(Func<T> body, int timeoutMs = DefaultTimeoutMs)
        => MainThread.Run(body, timeoutMs).GetAwaiter().GetResult();

    /// <summary>
    /// Reads the whole body as UTF-8 and binds it. Every failure — empty body, malformed JSON,
    /// a type mismatch — degrades to null so the endpoint can answer 400; none of them may
    /// become a 500.
    /// </summary>
    private static T? ReadJson<T>(HttpListenerRequest request) where T : class
    {
        try
        {
            using var reader = new StreamReader(request.InputStream, Encoding.UTF8);
            return JsonSerializer.Deserialize<T>(reader.ReadToEnd(), JsonOptions);
        }
        catch
        {
            return null;
        }
    }

    private static void WriteResult(HttpListenerContext ctx, string result, bool accepted)
        => Write(ctx, accepted ? 200 : 409, new { result });

    /// <summary>
    /// Writes the JSON body. Every failure is swallowed: a client hanging up mid-response is
    /// routine and must not produce a log line, let alone an exception.
    /// </summary>
    private static void Write(HttpListenerContext ctx, int statusCode, object payload)
    {
        try
        {
            var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(payload, JsonOptions));

            var response = ctx.Response;
            response.StatusCode = statusCode;
            response.ContentType = "application/json; charset=utf-8";
            response.ContentLength64 = bytes.Length;
            response.OutputStream.Write(bytes, 0, bytes.Length);
            response.Close();
        }
        catch
        {
            /* client gone */
        }
    }

    private static long? TryParseLong(string? raw)
        => long.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) ? value : null;

    private static int? TryParseInt(string? raw)
        => int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) ? value : null;
}

// Request shapes owned by the HTTP layer alone. The shared contracts (FireMissionRequest,
// AdjustFireRequest) live in Dtos.cs because the agent's tools bind to them as well.

internal sealed class TurretRequest
{
    public float KmX { get; set; }
    public float KmY { get; set; }
}

internal sealed class RequisitionRequest
{
    public string? CardId { get; set; }
    public float? BearingDeg { get; set; }

    /// <summary>Distance dial, needed by MoveDirection.</summary>
    public float? DistanceKm { get; set; }

    /// <summary>Deployment grid, needed by Spotter / LocationReport / ScoutPlane.</summary>
    public string? StartGrid { get; set; }

    public int Priority { get; set; } = 50;
}

internal sealed class ScoutPlaneRequest
{
    public float KmX { get; set; }
    public float KmY { get; set; }
    public float BearingDeg { get; set; }
}

internal sealed class DrawRequest
{
    public int PlacerIndex { get; set; }
    public string? PrefabName { get; set; }

    /// <summary>Stroke origin, km frame.</summary>
    public float Ox { get; set; }
    public float Oy { get; set; }

    /// <summary>Stroke target, km frame; equal to the origin for a dot.</summary>
    public float Tx { get; set; }
    public float Ty { get; set; }
}

internal sealed class PrintRequest
{
    /// <summary>"primary" (High Command) or anything else, which means "secondary".</summary>
    public string? Which { get; set; }

    public string[]? Lines { get; set; }
}

internal sealed class CommandRequest
{
    public string? Text { get; set; }
}
