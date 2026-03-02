using ihomer_concurrency_workshop;

// Central system WebSocket URL (e.g., ws://localhost:8080/ocpp or your OCPP backend)
var centralSystemUrl = args.Length > 0 ? args[0] : "ws://localhost:8080/ocpp";

await using var station = new ChargingStation("CS001", maxPowerKw: 22.0);

Console.WriteLine($"Connecting to {centralSystemUrl}...");
try
{
    await station.ConnectAsync(centralSystemUrl);
    Console.WriteLine("Connected!");
    Console.WriteLine(station);

    // Start a transaction
    Console.WriteLine("\nStarting transaction...");
    var transaction = await station.StartTransactionAsync(idTag: "RFID-12345");
    Console.WriteLine($"Started transaction {transaction.Id}");

    // Set charging power
    station.SetPowerKw(11.0);
    Console.WriteLine($"Set power to {station.CurrentPowerKw} kW");

    // Simulate charging with periodic meter values
    for (int i = 0; i < 5; i++)
    {
        // Simulate 10 minutes of charging
        station.Charge(durationMs: 600_000);
        await station.SendMeterValuesAsync();
        Console.WriteLine($"Energy delivered: {transaction.EnergyDeliveredKwh:F2} kWh");

        await Task.Delay(1000); // Small delay between meter values
    }

    // Stop the transaction
    Console.WriteLine("\nStopping transaction...");
    var completed = await station.StopTransactionAsync();
    Console.WriteLine($"Stopped transaction {completed.Id}");
    Console.WriteLine($"Final energy: {completed.EnergyDeliveredKwh:F2} kWh");
    Console.WriteLine(station);
}
catch (Exception ex)
{
    Console.WriteLine($"Error: {ex.Message}");
}

Console.WriteLine("\nPress any key to exit...");
Console.ReadKey();
