# ihomer Performance: Concurrency Workshop

A workshop for learning the **Producer-Consumer pattern** and **async I/O** in C#, using a simulated OCPP 1.6 charging station backend.

## The Problem

The `Server` project handles OCPP messages from charging stations over WebSockets. It works, but has two scalability issues:

1. **Thread per connection** — Every WebSocket connection spawns a dedicated thread that blocks while waiting for data. With 1000 stations connected, that's 1000 threads sitting idle most of the time.
2. **Single consumer** — All incoming messages funnel through one processing thread. Even with 16 CPU cores, only one message is handled at a time.

Your task: fix both issues by applying the changes found in `ServerSolution/`.

## Running the Projects

```bash
# Terminal 1: Start the server
dotnet run --project Server

# Terminal 2: Start 10 charging stations
dotnet run --project ChargingStations 10
```

Watch the server output — messages are processed sequentially on a single thread, with noticeable delays stacking up.

## Architecture Overview

### Before (Server/)
```
100 connections = 100 blocked threads (wasteful)
         │
         ▼
   [ BlockingCollection queue ]
         │
         ▼
   1 worker thread (bottleneck)
```

### After
```
100 connections = ~few ThreadPool threads (efficient)
         │
         ▼
   [ BlockingCollection queue ]
         │
         ▼
   N worker threads (parallel processing)
```

---

## Step-by-step Plan

### Step 1 — Multiple worker threads (Producer-Consumer pattern)

**What it solves:** The single-consumer bottleneck. `BlockingCollection<T>` is already thread-safe — multiple threads can call `GetConsumingEnumerable()` simultaneously. The runtime distributes items across consumers automatically.

**Changes in `Server/Program.cs`:**

1. Add a worker count based on available CPU cores:
```csharp
int WorkerCount = Environment.ProcessorCount;
```

2. Replace the single processing thread with a loop that spawns N workers:
```csharp
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
```

3. Update `ProcessMessages` to accept and pass through a `workerId`:
```csharp
static void ProcessMessages(BlockingCollection<QueuedMessage> queue, int workerId)
```

4. Pass `workerId` to `OcppMessageHandler.HandleMessage`:
```csharp
var response = OcppMessageHandler.HandleMessage(item.StationId, item.Message, workerId);
```

**Changes in `Server/OcppMessageHandler.cs`:**

5. Add `workerId` parameter to `HandleMessage` and include it in the log output:
```csharp
public static string? HandleMessage(string stationId, string json, int workerId)
{
    // ...
    Log($"[{stationId}] {action} (Worker {workerId})");
    // ...
}
```

**Verify:** Run the server again with 10 stations. You should now see messages being processed in parallel by different workers, with timestamps overlapping instead of sequential.

---

### Step 2 — Async I/O (eliminate thread-per-connection)

**What it solves:** Thread waste on I/O-bound waiting. Each WebSocket connection currently blocks a dedicated thread while waiting for data. With async I/O, the thread returns to the ThreadPool during the wait — one ThreadPool can serve thousands of connections.

**Changes in `Server/Program.cs`:**

1. Replace the blocking accept loop with an async method call:
```csharp
// Before:
while (true)
{
    var context = listener.GetContext();
    // ...
}

// After:
await AcceptConnectionsAsync(listener, messageQueue, connections);
```

2. Create the `AcceptConnectionsAsync` method using `GetContextAsync` and `AcceptWebSocketAsync`:
```csharp
static async Task AcceptConnectionsAsync(
    HttpListener listener,
    BlockingCollection<QueuedMessage> queue,
    ConcurrentDictionary<string, WebSocket> connections)
{
    while (true)
    {
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

        _ = ReceiveMessagesAsync(stationId, webSocket, queue, connections);

        OcppMessageHandler.Log($"[CONNECT] Station '{stationId}' connected");
    }
}
```

3. Convert `ReceiveMessages` to `ReceiveMessagesAsync` — replace all `.GetAwaiter().GetResult()` calls with `await`:
```csharp
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
```

Note the fire-and-forget pattern (`_ = ReceiveMessagesAsync(...)`) — each connection runs independently without a dedicated thread.

**Verify:** Run the server with 10+ stations. The behavior should be the same as Step 1, but now using far fewer OS threads. The ThreadPool manages waiting efficiently.

---

## Key Concepts

| Concept | What | Why |
|---------|------|-----|
| `BlockingCollection<T>` | Thread-safe queue | Multiple consumers can call `GetConsumingEnumerable()` concurrently |
| `Environment.ProcessorCount` | Number of CPU cores | Optimal worker count for CPU-bound work |
| `async/await` | Non-blocking I/O | Thread returns to pool while waiting for data |
| Fire-and-forget (`_ = ...`) | Start async task without awaiting | Each connection runs independently |
| `.GetAwaiter().GetResult()` | Sync-over-async (anti-pattern) | Blocks the thread — exactly what we're removing |

## Projects

| Project | Description |
|---------|-------------|
| **Server** | Your starting point — thread-per-connection, single consumer |
| **ServerSolution** | The completed solution for reference |
| **ChargingStations** | Simulated OCPP 1.6 charging stations (test client) |
