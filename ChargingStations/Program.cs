using ChargingStations;

// Parse arguments: dotnet run <count> [url]
var count = args.Length > 0 && int.TryParse(args[0], out var c) ? c : 1;
var centralSystemUrl = args.Length > 1 ? args[1] : "ws://localhost:8080/ocpp";

Console.WriteLine($"Starting {count} charging station(s) connecting to {centralSystemUrl}");
Console.WriteLine();

var stations = new List<ChargingStation>();
var tasks = new List<Task>();

try
{
    // Create and connect all stations
    for (int i = 1; i <= count; i++)
    {
        var stationId = $"CS{i:D4}";
        var station = new ChargingStation(stationId, maxPowerKw: 22.0);
        stations.Add(station);

        tasks.Add(RunStationAsync(station, centralSystemUrl));
    }

    Console.WriteLine("Press Ctrl+C to stop all stations...\n");

    // Wait for all stations or cancellation
    await Task.WhenAll(tasks);
}
catch (Exception ex)
{
    Console.WriteLine($"Error: {ex.Message}");
}
finally
{
    // Cleanup
    foreach (var station in stations)
    {
        await station.DisposeAsync();
    }
}

async Task RunStationAsync(ChargingStation station, string url)
{
    try
    {
        await station.ConnectAsync(url);
        Console.WriteLine($"[{station.StationId}] Connected");

        // Start a transaction
        var transaction = await station.StartTransactionAsync(idTag: $"RFID-{station.StationId}");
        Console.WriteLine($"[{station.StationId}] Started transaction {transaction.Id}");

        // Set charging power (randomize a bit for variety)
        var power = Random.Shared.Next(7, 22);
        station.SetPowerKw(power);

        // Simulate charging with periodic meter values
        for (int i = 0; i < 5; i++)
        {
            station.Charge(durationMs: 600_000); // 10 minutes
            await station.SendMeterValuesAsync();
            Console.WriteLine($"[{station.StationId}] Energy: {transaction.EnergyDeliveredKwh:F2} kWh");

            await Task.Delay(1000);
        }

        // Stop the transaction
        var completed = await station.StopTransactionAsync();
        Console.WriteLine($"[{station.StationId}] Stopped transaction {completed.Id}, Total: {completed.EnergyDeliveredKwh:F2} kWh");
    }
    catch (Exception ex)
    {
        Console.WriteLine($"[{station.StationId}] Error: {ex.Message}");
    }
}
