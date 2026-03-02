using System.Text.Json.Nodes;
using ChargingStations.Ocpp;

namespace ChargingStations;

public enum ConnectorStatus
{
    Available,
    Preparing,
    Charging,
    Finishing,
    Unavailable,
    Faulted
}

public class Transaction
{
    public int Id { get; }
    public string IdTag { get; }
    public DateTime StartTime { get; }
    public DateTime? StopTime { get; private set; }
    public double MeterStartWh { get; }
    public double MeterStopWh { get; private set; }
    public bool IsActive => StopTime == null;

    public double EnergyDeliveredWh => MeterStopWh - MeterStartWh;
    public double EnergyDeliveredKwh => EnergyDeliveredWh / 1000.0;

    public Transaction(int id, string idTag, double meterStartWh)
    {
        Id = id;
        IdTag = idTag;
        StartTime = DateTime.UtcNow;
        MeterStartWh = meterStartWh;
        MeterStopWh = meterStartWh;
    }

    public void UpdateMeter(double currentMeterWh)
    {
        MeterStopWh = currentMeterWh;
    }

    public void Stop()
    {
        StopTime = DateTime.UtcNow;
    }
}

public class ChargingStation : IAsyncDisposable
{
    private readonly OcppClient _ocppClient;
    private double _totalEnergyWh = 0;

    public string StationId { get; }
    public int ConnectorId { get; } = 1;
    public ConnectorStatus Status { get; private set; } = ConnectorStatus.Available;
    public double MaxPowerKw { get; }
    public double CurrentPowerKw { get; private set; }
    public Transaction? ActiveTransaction { get; private set; }

    public double TotalEnergyDeliveredWh => _totalEnergyWh;
    public double TotalEnergyDeliveredKwh => _totalEnergyWh / 1000.0;
    public bool IsConnected => _ocppClient.IsConnected;

    public ChargingStation(string stationId, double maxPowerKw = 22.0)
    {
        StationId = stationId;
        MaxPowerKw = maxPowerKw;
        CurrentPowerKw = 0;
        _ocppClient = new OcppClient(stationId);

        // Handle incoming calls from central system (e.g., RemoteStartTransaction)
        _ocppClient.OnCall += HandleIncomingCallAsync;
    }

    public async Task ConnectAsync(string centralSystemUrl, CancellationToken cancellationToken = default)
    {
        await _ocppClient.ConnectAsync(centralSystemUrl, cancellationToken);
        await SendBootNotificationAsync(cancellationToken);
        await SendStatusNotificationAsync(cancellationToken);
    }

    public async Task DisconnectAsync()
    {
        await _ocppClient.DisconnectAsync();
    }

    private async Task SendBootNotificationAsync(CancellationToken cancellationToken)
    {
        var payload = new JsonObject
        {
            ["chargePointVendor"] = "Workshop",
            ["chargePointModel"] = "Simulator",
            ["chargePointSerialNumber"] = StationId,
            ["firmwareVersion"] = "1.0.0"
        };

        var response = await _ocppClient.CallAsync(OcppAction.BootNotification, payload, cancellationToken);
        var status = response["status"]?.GetValue<string>();
        Console.WriteLine($"BootNotification response: {status}");
    }

    public async Task<bool> AuthorizeAsync(string idTag, CancellationToken cancellationToken = default)
    {
        var payload = new JsonObject
        {
            ["idTag"] = idTag
        };

        var response = await _ocppClient.CallAsync(OcppAction.Authorize, payload, cancellationToken);
        var status = response["idTagInfo"]?["status"]?.GetValue<string>();
        return status == "Accepted";
    }

    public async Task<Transaction> StartTransactionAsync(string idTag, CancellationToken cancellationToken = default)
    {
        if (Status != ConnectorStatus.Available)
        {
            throw new InvalidOperationException($"Cannot start transaction: connector status is {Status}");
        }

        // Authorize first
        var authorized = await AuthorizeAsync(idTag, cancellationToken);
        if (!authorized)
        {
            throw new InvalidOperationException("Authorization rejected");
        }

        Status = ConnectorStatus.Preparing;
        await SendStatusNotificationAsync(cancellationToken);

        var payload = new JsonObject
        {
            ["connectorId"] = ConnectorId,
            ["idTag"] = idTag,
            ["meterStart"] = (int)_totalEnergyWh,
            ["timestamp"] = DateTime.UtcNow.ToString("o")
        };

        var response = await _ocppClient.CallAsync(OcppAction.StartTransaction, payload, cancellationToken);
        var transactionId = response["transactionId"]?.GetValue<int>() ?? throw new InvalidOperationException("No transactionId in response");
        var status = response["idTagInfo"]?["status"]?.GetValue<string>();

        if (status != "Accepted")
        {
            Status = ConnectorStatus.Available;
            await SendStatusNotificationAsync(cancellationToken);
            throw new InvalidOperationException($"Transaction rejected: {status}");
        }

        var transaction = new Transaction(transactionId, idTag, _totalEnergyWh);
        ActiveTransaction = transaction;
        Status = ConnectorStatus.Charging;
        await SendStatusNotificationAsync(cancellationToken);

        return transaction;
    }

    public async Task<Transaction> StopTransactionAsync(string? reason = null, CancellationToken cancellationToken = default)
    {
        if (ActiveTransaction == null || !ActiveTransaction.IsActive)
        {
            throw new InvalidOperationException("No active transaction to stop");
        }

        Status = ConnectorStatus.Finishing;
        await SendStatusNotificationAsync(cancellationToken);

        ActiveTransaction.Stop();

        var payload = new JsonObject
        {
            ["transactionId"] = ActiveTransaction.Id,
            ["idTag"] = ActiveTransaction.IdTag,
            ["meterStop"] = (int)_totalEnergyWh,
            ["timestamp"] = DateTime.UtcNow.ToString("o"),
            ["reason"] = reason ?? "Local"
        };

        await _ocppClient.CallAsync(OcppAction.StopTransaction, payload, cancellationToken);

        var completedTransaction = ActiveTransaction;
        ActiveTransaction = null;
        CurrentPowerKw = 0;
        Status = ConnectorStatus.Available;
        await SendStatusNotificationAsync(cancellationToken);

        return completedTransaction;
    }

    public async Task SendMeterValuesAsync(CancellationToken cancellationToken = default)
    {
        if (ActiveTransaction == null)
        {
            throw new InvalidOperationException("No active transaction");
        }

        var payload = new JsonObject
        {
            ["connectorId"] = ConnectorId,
            ["transactionId"] = ActiveTransaction.Id,
            ["meterValue"] = new JsonArray
            {
                new JsonObject
                {
                    ["timestamp"] = DateTime.UtcNow.ToString("o"),
                    ["sampledValue"] = new JsonArray
                    {
                        new JsonObject
                        {
                            ["value"] = _totalEnergyWh.ToString("F2"),
                            ["context"] = "Sample.Periodic",
                            ["measurand"] = "Energy.Active.Import.Register",
                            ["location"] = "Outlet",
                            ["unit"] = "Wh"
                        },
                        new JsonObject
                        {
                            ["value"] = (CurrentPowerKw * 1000).ToString("F2"),
                            ["context"] = "Sample.Periodic",
                            ["measurand"] = "Power.Active.Import",
                            ["location"] = "Outlet",
                            ["unit"] = "W"
                        }
                    }
                }
            }
        };

        await _ocppClient.CallAsync(OcppAction.MeterValues, payload, cancellationToken);
    }

    public async Task SendHeartbeatAsync(CancellationToken cancellationToken = default)
    {
        await _ocppClient.CallAsync(OcppAction.Heartbeat, new JsonObject(), cancellationToken);
    }

    private async Task SendStatusNotificationAsync(CancellationToken cancellationToken = default)
    {
        var payload = new JsonObject
        {
            ["connectorId"] = ConnectorId,
            ["errorCode"] = "NoError",
            ["status"] = Status.ToString()
        };

        await _ocppClient.CallAsync(OcppAction.StatusNotification, payload, cancellationToken);
    }

    public void SetPowerKw(double powerKw)
    {
        if (powerKw < 0)
        {
            throw new ArgumentException("Power cannot be negative", nameof(powerKw));
        }

        if (powerKw > MaxPowerKw)
        {
            throw new ArgumentException($"Power {powerKw} kW exceeds maximum {MaxPowerKw} kW", nameof(powerKw));
        }

        CurrentPowerKw = powerKw;
    }

    /// <summary>
    /// Simulates charging for a given duration. Updates the meter values based on current power.
    /// </summary>
    public void Charge(int durationMs)
    {
        if (ActiveTransaction == null)
        {
            throw new InvalidOperationException("No active transaction");
        }

        if (Status != ConnectorStatus.Charging)
        {
            throw new InvalidOperationException($"Cannot charge: connector status is {Status}");
        }

        // Calculate energy: Power (kW) * Time (hours) = Energy (kWh)
        double energyKwh = CurrentPowerKw * (durationMs / 3_600_000.0);
        double energyWh = energyKwh * 1000;

        _totalEnergyWh += energyWh;
        ActiveTransaction.UpdateMeter(_totalEnergyWh);
    }

    private Task<JsonObject> HandleIncomingCallAsync(WampCall call)
    {
        Console.WriteLine($"Received call: {call.Action}");

        return call.Action switch
        {
            OcppAction.RemoteStartTransaction => HandleRemoteStartAsync(call.Payload),
            OcppAction.RemoteStopTransaction => HandleRemoteStopAsync(call.Payload),
            _ => Task.FromResult(new JsonObject { ["status"] = "NotImplemented" })
        };
    }

    private async Task<JsonObject> HandleRemoteStartAsync(JsonObject payload)
    {
        var idTag = payload["idTag"]?.GetValue<string>() ?? "UNKNOWN";

        if (Status != ConnectorStatus.Available)
        {
            return new JsonObject { ["status"] = "Rejected" };
        }

        // Start transaction in background (don't await, as we need to respond first)
        _ = Task.Run(async () =>
        {
            try
            {
                await StartTransactionAsync(idTag);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"RemoteStart failed: {ex.Message}");
            }
        });

        return new JsonObject { ["status"] = "Accepted" };
    }

    private async Task<JsonObject> HandleRemoteStopAsync(JsonObject payload)
    {
        var transactionId = payload["transactionId"]?.GetValue<int>();

        if (ActiveTransaction == null || ActiveTransaction.Id != transactionId)
        {
            return new JsonObject { ["status"] = "Rejected" };
        }

        _ = Task.Run(async () =>
        {
            try
            {
                await StopTransactionAsync("Remote");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"RemoteStop failed: {ex.Message}");
            }
        });

        return new JsonObject { ["status"] = "Accepted" };
    }

    public override string ToString()
    {
        return $"ChargingStation[{StationId}] Status={Status}, Power={CurrentPowerKw:F1}/{MaxPowerKw:F1} kW, " +
               $"TotalEnergy={TotalEnergyDeliveredKwh:F2} kWh, ActiveTransaction={ActiveTransaction?.Id.ToString() ?? "none"}";
    }

    public async ValueTask DisposeAsync()
    {
        await _ocppClient.DisposeAsync();
    }
}
