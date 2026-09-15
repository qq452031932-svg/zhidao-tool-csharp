using System.Text.Json;

if (args.Length < 3)
{
    return 90;
}

var outputPath = args[0];
var delayMilliseconds = int.Parse(args[1]);
var exitCode = int.Parse(args[2]);
var forwardedArguments = args.Skip(3).ToArray();

File.WriteAllText(outputPath + ".pid", Environment.ProcessId.ToString());
File.WriteAllText(outputPath, JsonSerializer.Serialize(forwardedArguments));
Thread.Sleep(delayMilliseconds);
File.WriteAllText(outputPath + ".done", "done");
return exitCode;
