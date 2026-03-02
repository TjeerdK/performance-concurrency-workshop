using System.Text.Json;
using System.Text.Json.Nodes;

namespace ChargingStations.Ocpp;

public enum WampMessageType
{
    Call = 2,
    CallResult = 3,
    CallError = 4
}

public abstract class WampMessage
{
    public WampMessageType MessageTypeId { get; }
    public string MessageId { get; }

    protected WampMessage(WampMessageType messageTypeId, string messageId)
    {
        MessageTypeId = messageTypeId;
        MessageId = messageId;
    }

    public abstract string ToJson();

    public static WampMessage Parse(string json)
    {
        var array = JsonNode.Parse(json)?.AsArray()
            ?? throw new ArgumentException("Invalid WAMP message format");

        var messageTypeId = (WampMessageType)array[0]!.GetValue<int>();
        var messageId = array[1]!.GetValue<string>();

        return messageTypeId switch
        {
            WampMessageType.Call => new WampCall(
                messageId,
                array[2]!.GetValue<string>(),
                array[3]?.AsObject() ?? new JsonObject()
            ),
            WampMessageType.CallResult => new WampCallResult(
                messageId,
                array[2]?.AsObject() ?? new JsonObject()
            ),
            WampMessageType.CallError => new WampCallError(
                messageId,
                array[2]!.GetValue<string>(),
                array[3]!.GetValue<string>(),
                array[4]?.AsObject() ?? new JsonObject()
            ),
            _ => throw new ArgumentException($"Unknown message type: {messageTypeId}")
        };
    }
}

/// <summary>
/// CALL message: [2, "messageId", "action", {payload}]
/// Sent by charging station to central system
/// </summary>
public class WampCall : WampMessage
{
    public string Action { get; }
    public JsonObject Payload { get; }

    public WampCall(string messageId, string action, JsonObject payload)
        : base(WampMessageType.Call, messageId)
    {
        Action = action;
        Payload = payload;
    }

    public override string ToJson()
    {
        var array = new JsonArray
        {
            (int)MessageTypeId,
            MessageId,
            Action,
            Payload
        };
        return array.ToJsonString();
    }
}

/// <summary>
/// CALL_RESULT message: [3, "messageId", {payload}]
/// Response to a CALL message
/// </summary>
public class WampCallResult : WampMessage
{
    public JsonObject Payload { get; }

    public WampCallResult(string messageId, JsonObject payload)
        : base(WampMessageType.CallResult, messageId)
    {
        Payload = payload;
    }

    public override string ToJson()
    {
        var array = new JsonArray
        {
            (int)MessageTypeId,
            MessageId,
            Payload
        };
        return array.ToJsonString();
    }
}

/// <summary>
/// CALL_ERROR message: [4, "messageId", "errorCode", "errorDescription", {details}]
/// Error response to a CALL message
/// </summary>
public class WampCallError : WampMessage
{
    public string ErrorCode { get; }
    public string ErrorDescription { get; }
    public JsonObject ErrorDetails { get; }

    public WampCallError(string messageId, string errorCode, string errorDescription, JsonObject errorDetails)
        : base(WampMessageType.CallError, messageId)
    {
        ErrorCode = errorCode;
        ErrorDescription = errorDescription;
        ErrorDetails = errorDetails;
    }

    public override string ToJson()
    {
        var array = new JsonArray
        {
            (int)MessageTypeId,
            MessageId,
            ErrorCode,
            ErrorDescription,
            ErrorDetails
        };
        return array.ToJsonString();
    }
}
