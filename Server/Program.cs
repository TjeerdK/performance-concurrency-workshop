using System.Collections.Concurrent;
using System.Net;
using System.Net.WebSockets;
using System.Text;
using Server;

var messageQueue = new BlockingCollection<QueuedMessage>();
var connections = new ConcurrentDictionary<string, WebSocket>();

var processorThread = new Thread(() => ProcessMessages(messageQueue))
{
    IsBackground = true,
    Name = "OCPP-SingleThread"
};
processorThread.Start();

var listener = new HttpListener();
listener.Prefixes.Add("http://localhost:8080/");
listener.Start();
OcppMessageHandler.Log("Listening on ws://localhost:8080/{stationId}");

while (true)
{
    var context = listener.GetContext();

    if (!context.Request.IsWebSocketRequest)
    {
        context.Response.StatusCode = 400;
        context.Response.Close();
        continue;
    }

    var stationId = context.Request.Url?.AbsolutePath.TrimStart('/') ?? "unknown";
    var wsContext = context.AcceptWebSocketAsync("ocpp1.6").GetAwaiter().GetResult();
    var webSocket = wsContext.WebSocket;

    connections[stationId] = webSocket;

    // Start a thread to receive messages from this connection
    var connectionThread = new Thread(() => ReceiveMessages(stationId, webSocket, messageQueue, connections))
    {
        IsBackground = true,
        Name = $"Connection-{stationId}"
    };
    connectionThread.Start();

    OcppMessageHandler.Log($"[CONNECT] Station '{stationId}' connected");
}

static void ReceiveMessages(
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
            var result = webSocket.ReceiveAsync(buffer, CancellationToken.None).GetAwaiter().GetResult();

            if (result.MessageType == WebSocketMessageType.Close)
            {
                webSocket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Closing", CancellationToken.None)
                    .GetAwaiter().GetResult();
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

static void ProcessMessages(BlockingCollection<QueuedMessage> queue)
{
    foreach (var item in queue.GetConsumingEnumerable())
    {
        try
        {
            // Artificial delay to simulate slow processing
            Thread.Sleep(500);

            // Handle the OCPP message
            var response = OcppMessageHandler.HandleMessage(item.StationId, item.Message);

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
