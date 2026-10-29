using LmpCommon;
using LmpCommon.Time;
using Server.Admin;
using Server.Client;
using Server.Command;
using Server.Context;
using Server.Events;
using Server.Exit;
using Server.Log;
using Server.Plugin;
using Server.Server;
using Server.Settings;
using Server.Settings.Structures;
using Server.System;
using Server.System.Backup;
using Server.System.PersistentSync;
using Server.Upnp;
using Server.Utilities;
using Server.Web;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Server
{
    public class MainServer
    {
        public static readonly TaskFactory LongRunTaskFactory = new TaskFactory(TaskCreationOptions.LongRunning, TaskContinuationOptions.None);

        private static readonly ManualResetEvent QuitEvent = new ManualResetEvent(false);

        private static readonly WinExitSignal ExitSignal = Common.PlatformIsWindows() ? new WinExitSignal() : null;

        private static readonly List<Task> TaskContainer = new List<Task>();

        public static readonly CancellationTokenSource CancellationTokenSrc = new CancellationTokenSource();
        private static int _exiting;

        public static void Main(string[] args)
        {
            // Offline verbs (Server.exe backup --out <folder>, Server.exe restore <file> --to <folder>) run before anything else starts.
            if (BackupCli.TryRun(args, Console.Out, Console.Error, AppDomain.CurrentDomain.BaseDirectory, out var cliExitCode))
            {
                Environment.ExitCode = cliExitCode;
                return;
            }

            try
            {
                if (Common.PlatformIsWindows())
                    Console.Title = $"VMP Server {LmpVersioning.CurrentVersion}";

                Console.OutputEncoding = Encoding.UTF8;

                AppDomain.CurrentDomain.UnhandledException += (_, args) =>
                {
                    try
                    {
                        var ex = args.ExceptionObject as Exception;
                        LunaLog.Fatal(ex != null
                            ? $"Unhandled domain exception: {ex}"
                            : $"Unhandled domain exception: {args.ExceptionObject}");
                    }
                    catch
                    {
                        // Avoid throwing from the handler.
                    }
                };

                TaskScheduler.UnobservedTaskException += (_, args) =>
                {
                    try
                    {
                        LunaLog.Fatal($"Unobserved task exception: {args.Exception}");
                        args.SetObserved();
                    }
                    catch
                    {
                        // Avoid throwing from the handler.
                    }
                };

                LunaLog.Info("VMP: quit with Control+C so backups can run before the server process stops.");

                if (Common.PlatformIsWindows())
                    ExitSignal.Exit += (sender, args) => Exit();
                else
                {
                    Console.CancelKeyPress += (sender, args) =>
                    {
                        args.Cancel = true;
                        Exit();
                    };
                }

                //We disable quick edit as otherwise when you select some text for copy/paste then you can't write to the console and server freezes
                //This just happens on windows....
                if (Common.PlatformIsWindows())
                    ConsoleUtil.DisableConsoleQuickEdit();

                //We cannot run more than 6 instances ofd servers + clients as otherwise the sync time will fail (30 seconds / 5 seconds = 6) but we use 3 for safety
                if (GetRunningInstances() > 3)
                    throw new HandledException("Cannot run more than 3 servers at a time!");

                //Start the server clock
                ServerContext.ServerClock.Start();

                ServerContext.ServerStarting = true;

                //Set day for log change
                ServerContext.Day = LunaNetworkTime.Now.Day;

                LunaLog.Normal($"VMP server {LmpVersioning.CurrentVersion} — {AppContext.BaseDirectory}");
                ServerSelfUpdater.TryOfferUpdateOnStartup();

                Universe.CheckUniverse();
                LoadSettingsAndGroups();
                VesselStoreSystem.LoadExistingVessels();
                var scenariosCreated = ScenarioSystem.GenerateDefaultScenarios();
                ScenarioStoreSystem.LoadExistingScenarios(scenariosCreated);
                PersistentSyncRegistry.Initialize(scenariosCreated);
                LmpPluginHandler.LoadPlugins();
                WarpSystem.Reset();
                TimeSystem.Reset();

                LunaLog.Normal($"Starting '{GeneralSettings.SettingsStore.ServerName}' on Address {ConnectionSettings.SettingsStore.ListenAddress} Port {ConnectionSettings.SettingsStore.Port}... ");

                LidgrenServer.SetupLidgrenServer();
                LmpPortMapper.OpenLmpPort().Wait();
                LmpPortMapper.OpenWebPort().Wait();
                ServerContext.ServerRunning = true;
                WebServer.StartWebServer();
                AdminApi.Start();

                //Do not add the command handler thread to the TaskContainer as it's a blocking task
                LongRunTaskFactory.StartNew(CommandHandler.ThreadMain, CancellationTokenSrc.Token);

                TaskContainer.Add(LongRunTaskFactory.StartNew(WebServer.RefreshWebServerInformation, CancellationTokenSrc.Token));

                TaskContainer.Add(LongRunTaskFactory.StartNew(LmpPortMapper.RefreshUpnpPort, CancellationTokenSrc.Token));
                TaskContainer.Add(LongRunTaskFactory.StartNew(LogThread.RunLogThread, CancellationTokenSrc.Token));
                TaskContainer.Add(LongRunTaskFactory.StartNew(ClientMainThread.ThreadMain, CancellationTokenSrc.Token));

                TaskContainer.Add(LongRunTaskFactory.StartNew(() => BackupSystem.PerformBackups(CancellationTokenSrc.Token), CancellationTokenSrc.Token));
                TaskContainer.Add(LongRunTaskFactory.StartNew(LidgrenServer.StartReceivingMessages, CancellationTokenSrc.Token));
                TaskContainer.Add(LongRunTaskFactory.StartNew(LidgrenMasterServer.RegisterWithMasterServer, CancellationTokenSrc.Token));

                TaskContainer.Add(LongRunTaskFactory.StartNew(VersionChecker.RefreshLatestVersion, CancellationTokenSrc.Token));

                TaskContainer.Add(LongRunTaskFactory.StartNew(() => GcSystem.PerformGarbageCollection(CancellationTokenSrc.Token), CancellationTokenSrc.Token));

                while (ServerContext.ServerStarting)
                    Thread.Sleep(500);

                LunaLog.Normal("All systems up and running. Поехали!");
                LmpPluginHandler.FireOnServerStart();

                // Local management scripts can request the normal save/shutdown path even
                // when Task Scheduler runs the server without an interactive console.
                var shutdownRequest = Path.Combine(AppContext.BaseDirectory, "shutdown.request");
                while (!QuitEvent.WaitOne(1000))
                {
                    if (!File.Exists(shutdownRequest)) continue;
                    File.Delete(shutdownRequest);
                    Exit();
                }

                LmpPluginHandler.FireOnServerStop();

                LunaLog.Normal("So long and thanks for all the fish!");
            }
            catch (Exception e)
            {
                LunaLog.Fatal(e is HandledException ? e.Message : $"Error in main server thread, Exception: {e}");
                Environment.ExitCode = 1;
                if (Environment.UserInteractive && !Console.IsInputRedirected)
                    Console.ReadLine();
            }
        }

        private static void LoadSettingsAndGroups()
        {
            LunaLog.Debug("Loading groups...");
            GroupSystem.LoadGroups();
            LunaLog.Debug("Loading settings...");
            SettingsHandler.LoadSettings();
            SettingsHandler.ValidateDifficultySettings();

            if (GeneralSettings.SettingsStore.ModControl)
            {
                LunaLog.Debug("Loading mod control...");
                ModFileSystem.LoadModFile();
            }

            if (Common.PlatformIsWindows())
            {
                Console.Title += $" ({GeneralSettings.SettingsStore.ServerName})";
#if DEBUG
                Console.Title += " DEBUG";
#endif
            }
        }

        /// <summary>Limit concurrent test instances. <see cref="Process"/>.ProcessName is without extension, e.g. "Server" for <c>Server.exe</c>.</summary>
        private static int GetRunningInstances()
        {
            var count = 0;
            var ownPath = Environment.ProcessPath;
            foreach (var process in Process.GetProcessesByName("Server"))
            {
                using (process)
                {
                    try
                    {
                        if (string.Equals(process.MainModule?.FileName, ownPath, StringComparison.OrdinalIgnoreCase)) count++;
                    }
                    catch (global::System.ComponentModel.Win32Exception) { }
                    catch (InvalidOperationException) { }
                }
            }
            return count;
        }

        /// <summary>
        /// Runs the exit logic
        /// </summary>
        private static void Exit()
        {
            if (Interlocked.Exchange(ref _exiting, 1) != 0) return;
            try
            {
                ServerSelfUpdater.TryOfferUpdateBeforeExit();
            }
            catch
            { }

            LunaLog.Normal("Exiting... Please wait until all threads are finished");
            ExitEvent.Exit();

            CancellationTokenSrc.Cancel();
            Task.WaitAll(TaskContainer.ToArray());

            ServerContext.Shutdown("Server is shutting down");

            QuitEvent.Set();
        }

        /// <summary>
        /// Runs the restart logic
        /// </summary>
        public static void Restart()
        {
            //Perform Backups
            BackupSystem.PerformBackups(CancellationTokenSrc.Token);
            LunaLog.Normal("Restarting...  Please wait until all threads are finished");

            ServerContext.Shutdown("Server is restarting");
            CancellationTokenSrc.Cancel();

            Task.WaitAll(TaskContainer.ToArray());
            QuitEvent.Set();

            //Start new server
            var serverExePath = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location) + "\\Server.exe";
            var newProcLmpServer = new ProcessStartInfo { FileName = serverExePath };
            Process.Start(newProcLmpServer);
        }
    }
}
