using System.Collections.Concurrent;
using System.Net;
using System.Net.WebSockets;
using System.Text;
using ServerSolution;

int WorkerCount = Environment.ProcessorCount;

var messageQueue = new BlockingCollection<QueuedMessage>();
var connections = new ConcurrentDictionary<string, WebSocket>();

// Start bounded worker threads for CPU-bound processing
var workerThreads = new Thread[WorkerCount];
for (int i = 0; i < WorkerCount; i++)
{
    var workerId = i + 1;
    workerThreads[i] = new Thread(() => ProcessMessages(messageQueue, workerId))
    {
        IsBackground = true,
        Name = $"OCPP-Worker-{workerId}"
    };
    workerThreads[i].Start();
}

OcppMessageHandler.Log($"Started {WorkerCount} worker threads for parallel processing");

var listener = new HttpListener();
listener.Prefixes.Add("http://localhost:8080/");
listener.Start();
OcppMessageHandler.Log("Listening on ws://localhost:8080/{stationId}");

// Async accept loop - no blocking on the main thread
await AcceptConnectionsAsync(listener, messageQueue, connections);

static async Task AcceptConnectionsAsync(
    HttpListener listener,
    BlockingCollection<QueuedMessage> queue,
    ConcurrentDictionary<string, WebSocket> connections)
{
    while (true)
    {
        // Async wait for incoming connection - doesn't block a thread
        var context = await listener.GetContextAsync();

        if (!context.Request.IsWebSocketRequest)
        {
            context.Response.StatusCode = 400;
            context.Response.Close();
            continue;
        }

        var stationId = context.Request.Url?.AbsolutePath.TrimStart('/') ?? "unknown";
        var wsContext = await context.AcceptWebSocketAsync("ocpp1.6");
        var webSocket = wsContext.WebSocket;

        connections[stationId] = webSocket;

        // Fire-and-forget async receive - no dedicated thread needed
        // The ThreadPool handles the waiting efficiently
        _ = ReceiveMessagesAsync(stationId, webSocket, queue, connections);

        OcppMessageHandler.Log($"[CONNECT] Station '{stationId}' connected");
    }
}

static async Task ReceiveMessagesAsync(
    string stationId,
    WebSocket webSocket,
    BlockingCollection<QueuedMessage> queue,
    ConcurrentDictionary<string, WebSocket> connections)
{
    var buffer = new byte[4096];

    try
    {
        while (webSocket.State == WebSocketState.Open)
        {
            // Async receive - thread returns to pool while waiting for data
            var result = await webSocket.ReceiveAsync(buffer, CancellationToken.None);

            if (result.MessageType == WebSocketMessageType.Close)
            {
                await webSocket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Closing", CancellationToken.None);
                break;
            }

            var message = Encoding.UTF8.GetString(buffer, 0, result.Count);
            queue.Add(new QueuedMessage(stationId, message, webSocket));
        }
    }
    catch { }
    finally
    {
        connections.TryRemove(stationId, out _);
        OcppMessageHandler.Log($"[DISCONNECT] Station '{stationId}' disconnected");
    }
}

static void ProcessMessages(BlockingCollection<QueuedMessage> queue, int workerId)
{
    foreach (var item in queue.GetConsumingEnumerable())
    {
        try
        {
            // Artificial delay to simulate slow CPU-bound processing
            Thread.Sleep(500);

            // Handle the OCPP message
            var response = OcppMessageHandler.HandleMessage(item.StationId, item.Message, workerId);

            // Send response back to the charging station
            if (response != null && item.WebSocket.State == WebSocketState.Open)
            {
                var bytes = Encoding.UTF8.GetBytes(response);
                item.WebSocket.SendAsync(bytes, WebSocketMessageType.Text, true, CancellationToken.None)
                    .GetAwaiter().GetResult();
            }

            // Additional delay
            Thread.Sleep(200);
        }
        catch { }
    }
}

record QueuedMessage(string StationId, string Message, WebSocket WebSocket);
