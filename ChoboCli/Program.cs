using ChoboCli.Cli;
using ChoboCli.Commands;
using ChoboCli.Infrastructure;

var registry = new CommandRegistry()
    .Add(new ServerCommands())
    .Add(new UserCommands())
    .Add(new ClusterCommands())
    .Add(new TargetCommands())
    .Add(new PolicyCommands())
    .Add(new ScheduleCommands())
    .Add(new DashboardCommands())
    .Add(new MetricsCommands())
    .Add(new BackupCommand())
    .Add(new BackupsCommands())
    .Add(new RestoreCommand())
    .Add(new RestoresCommands())
    .Add(new LogCommands())
    .Add(new AuditCommands())
    .Add(new ImportExportCommands("data"))
    .Add(new ImportExportCommands("config"));

var app = new CliApplication(registry, new ChoboApiClientFactory(), new ProfileStore(), new JsonOutputWriter());
return await app.RunAsync(args);
