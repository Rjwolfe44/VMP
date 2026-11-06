// VmpScenarioClient seed <file>
// VmpScenarioClient run <host> <port> [Universe/Scenarios/KerbalKonstructsSettings.txt] [saveWaitSeconds]
if (args.Length >= 2 && args[0] == "seed")
{
    File.WriteAllText(args[1], ScenarioScript.SeedBlob());
    Console.WriteLine("seed written: " + args[1]);
    return 0;
}
if (args.Length >= 3 && args[0] == "run")
    return ScenarioScript.Run(args[1], int.Parse(args[2]), args.Length > 3 ? args[3] : null, args.Length > 4 ? int.Parse(args[4]) : 60);

Console.Error.WriteLine("VmpScenarioClient seed <file> | run <host> <port> [scenarioFileOnDisk] [saveWaitSeconds]");
return 2;
