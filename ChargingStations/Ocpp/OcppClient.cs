using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace ChargingStations.Ocpp;

public class OcppClient : IAsyncDisposable
{
    private readonly ClientWebSocket _webSocket = new();
    private readonly string _stationId;
    private readonly ConcurrentDictionary<string, TaskCompletionSource<WampMessage>> _pendingCalls = new();
    private CancellationTokenSource? _receiveCts;
    private Task? _receiveTask;

    public event Func<WampCall, Task<JsonObject>>? OnCall;

    public bool IsConnected => _webSocket.State == WebSocketState.Open;

    public OcppClient(string stationId)
    {
        _stationId = stationId;
        _webSocket.Options.AddSubProtocol("ocpp1.6");
    }

    public async Task ConnectAsync(string centralSystemUrl, CancellationToken cancellationToken = default)
    {
        var uri = new Uri($"{centralSystemUrl}/{_stationId}");
        await _webSocket.ConnectAsync(uri, cancellationToken);

        _receiveCts = new CancellationTokenSource();
        _receiveTask = ReceiveLoopAsync(_receiveCts.Token);
    }

    public async Task DisconnectAsync()
    {
        if (_webSocket.State == WebSocketState.Open)
        {
            await _webSocket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Closing", CancellationToken.None);
        }

        _receiveCts?.Cancel();
        if (_receiveTask != null)
        {
            try { await _receiveTask; } catch { }
        }
    }

    public async Task<JsonObject> CallAsync(string action, JsonObject payload, CancellationToken cancellationToken = default)
    {
        var messageId = Guid.NewGuid().ToString();
        var call = new WampCall(messageId, action, payload);

        var tcs = new TaskCompletionSource<WampMessage>();
        _pendingCalls[messageId] = tcs;

        try
        {
            await SendAsync(call.ToJson(), cancellationToken);

            using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);

            linkedCts.Token.Register(() => tcs.TrySetCanceled());

            var response = await tcs.Task;

            return response switch
            {
                WampCallResult result => result.Payload,
                WampCallError error => throw new OcppException(error.ErrorCode, error.ErrorDescription),
                _ => throw new InvalidOperationException("Unexpected response type")
            };
        }
        finally
        {
            _pendingCalls.TryRemove(messageId, out _);
        }
    }

    private async Task SendAsync(string message, CancellationToken cancellationToken)
    {
        var bytes = Encoding.UTF8.GetBytes(message);
        await _webSocket.SendAsync(bytes, WebSocketMessageType.Text, true, cancellationToken);
        Console.WriteLine($"[OCPP TX] {message}");
    }

    private async Task ReceiveLoopAsync(CancellationToken cancellationToken)
    {
        var buffer = new byte[4096];

        try
        {
            while (!cancellationToken.IsCancellationRequested && _webSocket.State == WebSocketState.Open)
            {
                var result = await _webSocket.ReceiveAsync(buffer, cancellationToken);

                if (result.MessageType == WebSocketMessageType.Close)
                {
                    break;
                }

                var message = Encoding.UTF8.GetString(buffer, 0, result.Count);
                Console.WriteLine($"[OCPP RX] {message}");

                await HandleMessageAsync(message);
            }
        }
        catch (OperationCanceledException) { }
        catch (WebSocketException) { }
    }

    private async Task HandleMessageAsync(string json)
    {
        try
        {
            var message = WampMessage.Parse(json);

            switch (message)
            {
                case WampCallResult result:
                case WampCallError error:
                    if (_pendingCalls.TryGetValue(message.MessageId, out var tcs))
                    {
                        tcs.TrySetResult(message);
                    }
                    break;

                case WampCall call:
                    await HandleIncomingCallAsync(call);
                    break;
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[OCPP ERROR] Failed to handle message: {ex.Message}");
        }
    }

    private async Task HandleIncomingCallAsync(WampCall call)
    {
        try
        {
            JsonObject response;
            if (OnCall != null)
            {
                response = await OnCall(call);
            }
            else
            {
                response = new JsonObject { ["status"] = "NotImplemented" };
            }

            var result = new WampCallResult(call.MessageId, response);
            await SendAsync(result.ToJson(), CancellationToken.None);
        }
        catch (Exception ex)
        {
            var error = new WampCallError(
                call.MessageId,
                "InternalError",
                ex.Message,
                new JsonObject()
            );
            await SendAsync(error.ToJson(), CancellationToken.None);
        }
    }

    public async ValueTask DisposeAsync()
    {
        await DisconnectAsync();
        _webSocket.Dispose();
        _receiveCts?.Dispose();
    }
}

public class OcppException : Exception
{
    public string ErrorCode { get; }

    public OcppException(string errorCode, string message) : base(message)
    {
        ErrorCode = errorCode;
    }
}
