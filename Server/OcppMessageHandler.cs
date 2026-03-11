using System.Text.Json;
using System.Text.Json.Nodes;

namespace Server;

public static class OcppMessageHandler
{
    public static string? HandleMessage(string stationId, string json, int workerId)
    {
        try
        {
            var array = JsonNode.Parse(json)?.AsArray();
            if (array == null) return null;

            var messageType = array[0]?.GetValue<int>() ?? 0;
            var messageId = array[1]?.GetValue<string>() ?? "";

            // CALL message (type 2)
            if (messageType == 2)
            {
                var action = array[2]?.GetValue<string>() ?? "";
                var payload = array[3]?.AsObject() ?? new JsonObject();

                Log($"[{stationId}] {action} (Worker {workerId})");

                var response = HandleAction(stationId, action, payload);

                // Build CALLRESULT [3, messageId, payload]
                var result = new JsonArray { 3, messageId, response };
                return result.ToJsonString();
            }
        }
        catch { }

        return null;
    }

    private static JsonObject HandleAction(string stationId, string action, JsonObject payload)
    {
        // Simulate processing time based on action complexity
        var processingTime = action switch
        {
            "BootNotification" => 1000,
            "Authorize" => 800,
            "StartTransaction" => 1200,
            "StopTransaction" => 1000,
            "MeterValues" => 600,
            "Heartbeat" => 300,
            "StatusNotification" => 400,
            _ => 500
        };

        Thread.Sleep(processingTime);

        return action switch
        {
            "BootNotification" => new JsonObject
            {
                ["status"] = "Accepted",
                ["currentTime"] = DateTime.UtcNow.ToString("o"),
                ["interval"] = 300
            },
            "Authorize" => new JsonObject
            {
                ["idTagInfo"] = new JsonObject
                {
                    ["status"] = "Accepted",
                    ["expiryDate"] = DateTime.UtcNow.AddDays(30).ToString("o")
                }
            },
            "StartTransaction" => new JsonObject
            {
                ["transactionId"] = Random.Shared.Next(1000, 9999),
                ["idTagInfo"] = new JsonObject
                {
                    ["status"] = "Accepted"
                }
            },
            "StopTransaction" => new JsonObject
            {
                ["idTagInfo"] = new JsonObject
                {
                    ["status"] = "Accepted"
                }
            },
            "Heartbeat" => new JsonObject
            {
                ["currentTime"] = DateTime.UtcNow.ToString("o")
            },
            "MeterValues" => new JsonObject(),
            "StatusNotification" => new JsonObject(),
            _ => new JsonObject { ["status"] = "Accepted" }
        };
    }

    public static void Log(string message)
    {
        var timestamp = DateTime.Now.ToString("HH:mm:ss.fff");
        Console.WriteLine($"[{timestamp}] {message}");
    }
}
