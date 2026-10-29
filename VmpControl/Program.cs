using VmpControl.Core;
using VmpControl.UI;

namespace VmpControl;

internal static class Program
{
    private const string MutexName = @"Local\VmpControl.SingleInstance";
    private const string ShowEventName = @"Local\VmpControl.ShowWindow";

    [STAThread]
    private static int Main(string[] args)
    {
        var options = CommandLineOptions.Parse(args);
        ApplicationConfiguration.Initialize();
        var screenshot = options.ScreenshotFolder != null;

        if (options.Problems.Count > 0)
        {
            Complain("VMP Control: " + string.Join("; ", options.Problems) + "\nSwitches: --mock --profile <name> --screenshot <folder> --minimized --tab <name> --server-folder <path> --api-port <n> --settings <file>", screenshot);
            return 2;
        }

        // One instance per user: a second launch only asks the first one to show its window. Screenshot runs are throw-away and skip this.
        EventWaitHandle showEvent = null;
        Mutex mutex = null;
        if (!screenshot)
        {
            showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ShowEventName);
            mutex = new Mutex(true, MutexName, out var first);
            if (!first)
            {
                showEvent.Set();
                mutex.Dispose();
                showEvent.Dispose();
                return 0;
            }
        }

        try
        {
            var settingsPath = options.SettingsPath ?? AppSettings.DefaultPath;
            var settings = AppSettings.Load(settingsPath);
            var profile = ChooseProfile(options, settings);
            if (profile == null) return 2;

            while (!options.Mock && profile.Validate() != null)
            {
                if (screenshot)
                {
                    Console.Error.WriteLine("Profile '" + profile.Name + "' is not usable: " + profile.Validate());
                    return 2;
                }

                // First run on a PC where the template profile is not filled in yet: let the user fix it, or leave.
                using var dialog = new SettingsForm(settings, profile.Name);
                dialog.StartPosition = FormStartPosition.CenterScreen;
                if (dialog.ShowDialog() != DialogResult.OK) return 1;
                settings = dialog.Result;
                settings.Save(settingsPath);
                profile = settings.Pick(settings.ActiveProfile);
            }

            Application.ThreadException += (_, e) => MessageBox.Show("VMP Control hit an unexpected problem:\n\n" + e.Exception.Message, "VMP Control", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            using var form = new MainForm(options, settings, settingsPath, profile, showEvent);
            if (screenshot)
            {
                var code = 0;
                form.Shown += async (_, _) => code = await form.RunScreenshotsAsync(options.ScreenshotFolder);
                Application.Run(form);
                return code;
            }

            Application.Run(form);
            return 0;
        }
        finally
        {
            mutex?.ReleaseMutex();
            mutex?.Dispose();
            showEvent?.Dispose();
        }
    }

    private static Profile ChooseProfile(CommandLineOptions options, AppSettings settings)
    {
        if (options.ServerFolder != null)
        {
            // An in-memory host profile for testing against any server folder; it is never written to the settings file.
            return new Profile { Name = options.Profile ?? "Command line", Mode = ProfileMode.Host, ServerFolder = options.ServerFolder, ApiPort = options.ApiPort ?? 18900 };
        }

        var profile = settings.Pick(options.Profile);
        if (options.Profile != null && settings.Find(options.Profile) == null)
        {
            Complain("VMP Control: there is no profile called '" + options.Profile + "'. Profiles: " + string.Join(", ", settings.Profiles.Select(p => p.Name)), options.ScreenshotFolder != null);
            return null;
        }

        if (profile != null && options.ApiPort != null)
        {
            profile = profile.Clone();
            if (profile.IsRemote) profile.RemoteApiPort = options.ApiPort.Value;
            else profile.ApiPort = options.ApiPort.Value;
        }

        return profile ?? Profile.CreateRemoteDefault();
    }

    private static void Complain(string message, bool quiet)
    {
        if (quiet) Console.Error.WriteLine(message);
        else MessageBox.Show(message, "VMP Control", MessageBoxButtons.OK, MessageBoxIcon.Warning);
    }
}
