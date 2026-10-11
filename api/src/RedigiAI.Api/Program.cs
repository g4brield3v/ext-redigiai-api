using RedigiAI.Api;

// Um executável, vários modos: `api` (padrão), `worker`, `migrate` e `seed`.
string[] modes = ["api", "worker", "migrate", "seed"];
var mode = args.Length > 0 && modes.Contains(args[0]) ? args[0] : "api";
var rest = args.Length > 0 && args[0] == mode ? args[1..] : args;

DotEnv.LoadIfPresent();

return mode switch
{
    "worker" => await WorkerHost.RunAsync(rest),
    "migrate" => await DatabaseCommands.MigrateAsync(rest),
    "seed" => await DatabaseCommands.SeedAsync(rest),
    _ => await ApiHost.RunAsync(rest),
};

public partial class Program;
