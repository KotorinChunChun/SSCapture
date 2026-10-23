using KotorinToys;
using SSCapture.Maintenance;
using System.Xml.Linq;

internal static class StartupRegistrationTests
{
    internal static void Run(string output)
    {
        var store = new HostSettingsStore(Path.Combine(output, "startup-" + Guid.NewGuid().ToString("N")));
        var gateway = new Gateway();
        var tool = TargetValidator.Definition with { Capabilities = ["status", "show", "stop", "silent-start"] };
        bool installed = false;
        var service = new StandaloneStartup(store, gateway, tool, Path.Combine(output, "SSCapture.exe"), () => installed, externalReservations: () => []);
        static void Check(bool ok) { if (!ok) throw new Exception("自動起動の期待値不一致"); }
        Check(!service.State().Enabled && !service.SetEnabled(true).Success && gateway.Tasks.Count == 0);
        installed = true;
        Check(service.SetEnabled(true).Success && service.State().Enabled && gateway.Tasks.Count == 1);
        Check(store.Load().StartupModes[tool.Id] == "direct_task");
        Check(gateway.Tasks.Values.Single().Xml.Contains("--startup --launch-origin direct_task"));
        Check(service.SetEnabled(false).Success && !service.State().Enabled && gateway.Tasks.Count == 0);
        gateway.FailAfterCreate = true;
        Check(!service.SetEnabled(true).Success && gateway.Tasks.Count == 0 && store.Journal == null);
        Check(service.SetEnabled(true).Success);
        var own = store.Load().OwnedTasks[tool.Id];
        gateway.Tasks[own.Name] = new(own.Name, own.Xml.Replace("PT0S", "PT72H"));
        Check(!service.SetEnabled(false).Success && gateway.Tasks.Count == 1);
    }
    sealed class Gateway : IStartupGateway
    {
        internal readonly Dictionary<string, ScheduledTaskState> Tasks = new();
        internal bool FailAfterCreate;
        public ScheduledTaskState? Read(string name) => Tasks.GetValueOrDefault(name);
        public ScheduledTaskState Create(TaskPlan plan)
        {
            if (Tasks.ContainsKey(plan.Name)) throw new IOException("同名予約");
            var state = Tasks[plan.Name] = new(plan.Name, Xml(plan));
            if (FailAfterCreate) { FailAfterCreate = false; throw new IOException("登録後に失敗"); }
            return state;
        }
        public ScheduledTaskState Restore(string name, string xml, string securityDescriptor = "") => Tasks[name] = new(name, xml, securityDescriptor);
        public void Delete(string name, string expectedFingerprint)
        {
            if (Read(name)?.Fingerprint != expectedFingerprint) throw new IOException("外部変更");
            Tasks.Remove(name);
        }
        static string Xml(TaskPlan plan)
        {
            XNamespace ns = "http://schemas.microsoft.com/windows/2004/02/mit/task";
            XElement E(string name, object? value) => new(ns + name, value);
            return new XDocument(new XElement(ns + "Task",
                E("RegistrationInfo", E("Description", plan.Marker)),
                E("Principals", E("Principal", new[] { E("UserId", plan.Sid), E("LogonType", "InteractiveToken"), E("RunLevel", "LeastPrivilege") })),
                E("Triggers", E("LogonTrigger", new[] { E("UserId", plan.Sid), E("Delay", "PT5S") })),
                E("Actions", E("Exec", new[] { E("Command", plan.Executable), E("Arguments", plan.Arguments), E("WorkingDirectory", plan.WorkingDirectory) })),
                E("Settings", new[] { E("ExecutionTimeLimit", "PT0S"), E("DisallowStartIfOnBatteries", "false"), E("StopIfGoingOnBatteries", "false"), E("MultipleInstancesPolicy", "IgnoreNew"), E("AllowHardTerminate", "false"), E("StartWhenAvailable", "true"), E("Enabled", "true") }))).ToString();
        }
    }
}
