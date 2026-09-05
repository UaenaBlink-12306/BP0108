using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Windows.Forms;

internal static class BP0108DisplayLauncher
{
    private const string UnityExeName = "BP0108.exe";
    private const string LogFileName = "BP0108_DisplayLauncher.log";
    private const string RuntimeLogFileName = "BP0108_Runtime.log";

    [STAThread]
    private static int Main(string[] args)
    {
        try
        {
            string launcherDirectory = AppDomain.CurrentDomain.BaseDirectory;
            string unityExePath = Path.Combine(launcherDirectory, UnityExeName);

            if (!File.Exists(unityExePath))
            {
                MessageBox.Show(
                    "Could not find " + UnityExeName + " next to this launcher.",
                    "BP0108 Display Launcher",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Error);
                return 1;
            }

            int monitorNumber = PickUnityMonitorNumber();
            string runtimeLogPath = Path.Combine(launcherDirectory, RuntimeLogFileName);
            string unityArguments = BuildUnityArguments(args, monitorNumber, runtimeLogPath);

            Process runningInstance = FindRunningUnityInstance(unityExePath);
            if (runningInstance != null)
            {
                WriteLog(launcherDirectory, "Already running", monitorNumber, unityArguments, runningInstance.Id, null);
                return 0;
            }

            int restartCount = 0;
            while (true)
            {
                RotateRuntimeLog(runtimeLogPath);
                ProcessStartInfo startInfo = new ProcessStartInfo
                {
                    FileName = unityExePath,
                    Arguments = unityArguments,
                    WorkingDirectory = launcherDirectory,
                    UseShellExecute = false
                };

                using (Process game = Process.Start(startInfo))
                {
                    if (game == null)
                    {
                        throw new InvalidOperationException("Unity did not return a process handle.");
                    }

                    WriteLog(launcherDirectory, "Started", monitorNumber, unityArguments, game.Id, null);
                    game.WaitForExit();
                    int exitCode = game.ExitCode;
                    WriteLog(launcherDirectory, "Exited", monitorNumber, unityArguments, game.Id, exitCode);

                    if (exitCode == 0)
                    {
                        return 0;
                    }
                }

                restartCount++;
                if (restartCount > 50)
                {
                    WriteLog(launcherDirectory, "Restart limit reached", monitorNumber, unityArguments, 0, null);
                    return 2;
                }

                System.Threading.Thread.Sleep(Math.Min(30000, 3000 * restartCount));
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                ex.Message,
                "BP0108 Display Launcher",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
            return 1;
        }
    }

    private static int PickUnityMonitorNumber()
    {
        Screen[] screens = Screen.AllScreens;
        if (screens.Length <= 1)
        {
            return 1;
        }

        for (int i = 0; i < screens.Length; i++)
        {
            if (!screens[i].Primary)
            {
                return i + 1;
            }
        }

        return 1;
    }

    private static Process FindRunningUnityInstance(string unityExePath)
    {
        string expectedPath = Path.GetFullPath(unityExePath);
        foreach (Process process in Process.GetProcessesByName(Path.GetFileNameWithoutExtension(UnityExeName)))
        {
            try
            {
                if (string.Equals(process.MainModule.FileName, expectedPath, StringComparison.OrdinalIgnoreCase))
                {
                    return process;
                }
            }
            catch
            {
                process.Dispose();
            }
        }

        return null;
    }

    private static string BuildUnityArguments(string[] originalArgs, int monitorNumber, string runtimeLogPath)
    {
        List<string> args = new List<string>();

        if (!ContainsUnitySwitch(originalArgs, "-monitor"))
        {
            args.Add("-monitor");
            args.Add(monitorNumber.ToString());
        }

        if (!ContainsUnitySwitch(originalArgs, "-screen-fullscreen"))
        {
            args.Add("-screen-fullscreen");
            args.Add("1");
        }

        if (!ContainsUnitySwitch(originalArgs, "-screen-width"))
        {
            args.Add("-screen-width");
            args.Add("1920");
        }

        if (!ContainsUnitySwitch(originalArgs, "-screen-height"))
        {
            args.Add("-screen-height");
            args.Add("1080");
        }

        if (!ContainsUnitySwitch(originalArgs, "-logFile"))
        {
            args.Add("-logFile");
            args.Add(runtimeLogPath);
        }

        args.AddRange(originalArgs);
        return QuoteArguments(args);
    }

    private static bool ContainsUnitySwitch(string[] args, string switchName)
    {
        foreach (string arg in args)
        {
            if (string.Equals(arg, switchName, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static string QuoteArguments(IEnumerable<string> args)
    {
        StringBuilder builder = new StringBuilder();
        foreach (string arg in args)
        {
            if (builder.Length > 0)
            {
                builder.Append(' ');
            }

            builder.Append(QuoteArgument(arg));
        }

        return builder.ToString();
    }

    private static string QuoteArgument(string arg)
    {
        if (string.IsNullOrEmpty(arg))
        {
            return "\"\"";
        }

        bool needsQuotes = arg.IndexOfAny(new[] { ' ', '\t', '\n', '\v', '"' }) >= 0;
        if (!needsQuotes)
        {
            return arg;
        }

        StringBuilder builder = new StringBuilder();
        builder.Append('"');

        int backslashes = 0;
        foreach (char c in arg)
        {
            if (c == '\\')
            {
                backslashes++;
                continue;
            }

            if (c == '"')
            {
                builder.Append('\\', backslashes * 2 + 1);
                builder.Append('"');
                backslashes = 0;
                continue;
            }

            builder.Append('\\', backslashes);
            builder.Append(c);
            backslashes = 0;
        }

        builder.Append('\\', backslashes * 2);
        builder.Append('"');
        return builder.ToString();
    }

    private static void RotateRuntimeLog(string runtimeLogPath)
    {
        try
        {
            if (!File.Exists(runtimeLogPath) || new FileInfo(runtimeLogPath).Length < 8 * 1024 * 1024)
            {
                return;
            }

            string archivePath = runtimeLogPath + ".previous";
            if (File.Exists(archivePath))
            {
                File.Delete(archivePath);
            }

            File.Move(runtimeLogPath, archivePath);
        }
        catch
        {
        }
    }

    private static void WriteLog(string launcherDirectory, string status, int monitorNumber, string unityArguments, int processId, int? exitCode)
    {
        try
        {
            string logPath = Path.Combine(launcherDirectory, LogFileName);
            string line = string.Format(
                "{0:yyyy-MM-dd HH:mm:ss} | {1} | screens={2} monitor={3} pid={4} exit={5} | {6}{7}",
                DateTime.Now,
                status,
                Screen.AllScreens.Length,
                monitorNumber,
                processId,
                exitCode.HasValue ? exitCode.Value.ToString() : "-",
                unityArguments,
                Environment.NewLine);
            File.AppendAllText(logPath, line);
        }
        catch
        {
            // The launcher should still work if logging is blocked.
        }
    }
}
