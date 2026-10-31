// Tsuki.Cli entry point (ARCHITECTURE 3.1, 10.1-10.3; ROADMAP M0 scope 6).
// The commands live in Commands/; CliApp.Run is also what the tests call, so exit codes are tested in-process.
return Tsuki.Cli.CliApp.Run(args, Console.Out, Console.Error);
