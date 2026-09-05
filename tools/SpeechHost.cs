using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Speech.Synthesis;
using System.Text;
using System.Threading;

namespace BP0108SpeechHost
{
    internal static class Program
    {
        private const int MaximumSpeechCharacters = 420;

        [STAThread]
        private static int Main(string[] args)
        {
            try
            {
                var options = HostOptions.Parse(args);
                if (options.ParentProcessId > 0)
                {
                    StartParentWatcher(options.ParentProcessId);
                }

                Directory.CreateDirectory(options.WaveDirectory);
                using (var synthesizer = new SpeechSynthesizer())
                {
                    var voiceName = SelectEnglishVoice(synthesizer);
                    synthesizer.Rate = 4;
                    synthesizer.Volume = 100;
                    WriteProtocol("READY|" + Encode(voiceName));

                    if (options.ProbeOnly)
                    {
                        return 0;
                    }

                    var sequence = 0;
                    string line;
                    while ((line = Console.In.ReadLine()) != null)
                    {
                        if (string.Equals(line, "QUIT", StringComparison.Ordinal))
                        {
                            break;
                        }

                        var separator = line.IndexOf('|');
                        if (separator <= 0 || separator >= line.Length - 1)
                        {
                            WriteProtocol("ERROR|unknown|" + Encode("Malformed speech request."));
                            continue;
                        }

                        var requestId = line.Substring(0, separator);
                        try
                        {
                            var text = Decode(line.Substring(separator + 1));
                            if (text.Length > MaximumSpeechCharacters)
                            {
                                text = text.Substring(0, MaximumSpeechCharacters);
                            }

                            if (string.IsNullOrWhiteSpace(text))
                            {
                                WriteProtocol("ERROR|" + requestId + "|" + Encode("Speech text was empty."));
                                continue;
                            }

                            sequence++;
                            var wavePath = Path.Combine(
                                options.WaveDirectory,
                                string.Format(CultureInfo.InvariantCulture, "speech_{0:D4}_{1}.wav", sequence, SafeFileToken(requestId)));
                            var stopwatch = Stopwatch.StartNew();
                            WriteProtocol("START|" + requestId);
                            synthesizer.SetOutputToWaveFile(wavePath);
                            try
                            {
                                synthesizer.Speak(text);
                            }
                            finally
                            {
                                synthesizer.SetOutputToNull();
                            }
                            stopwatch.Stop();
                            WriteProtocol(string.Format(
                                CultureInfo.InvariantCulture,
                                "DONE|{0}|{1}|{2}",
                                requestId,
                                stopwatch.ElapsedMilliseconds,
                                Encode(wavePath)));
                        }
                        catch (Exception ex)
                        {
                            try
                            {
                                synthesizer.SetOutputToNull();
                            }
                            catch
                            {
                            }

                            WriteProtocol("ERROR|" + requestId + "|" + Encode(ex.GetType().Name + ": " + ex.Message));
                        }
                    }
                }

                return 0;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine(ex);
                return 2;
            }
        }

        private static string SelectEnglishVoice(SpeechSynthesizer synthesizer)
        {
            string fallback = null;
            string englishFallback = null;
            foreach (var installedVoice in synthesizer.GetInstalledVoices())
            {
                if (!installedVoice.Enabled)
                {
                    continue;
                }

                var info = installedVoice.VoiceInfo;
                if (fallback == null)
                {
                    fallback = info.Name;
                }

                if (info.Culture != null && string.Equals(info.Culture.Name, "en-US", StringComparison.OrdinalIgnoreCase))
                {
                    synthesizer.SelectVoice(info.Name);
                    return info.Name;
                }
                if (englishFallback == null
                    && info.Culture != null
                    && string.Equals(info.Culture.TwoLetterISOLanguageName, "en", StringComparison.OrdinalIgnoreCase))
                {
                    englishFallback = info.Name;
                }
            }

            if (!string.IsNullOrWhiteSpace(englishFallback))
            {
                synthesizer.SelectVoice(englishFallback);
                return englishFallback;
            }
            if (!string.IsNullOrWhiteSpace(fallback))
            {
                synthesizer.SelectVoice(fallback);
                return fallback;
            }

            return synthesizer.Voice == null ? "Windows default voice" : synthesizer.Voice.Name;
        }

        private static void StartParentWatcher(int parentProcessId)
        {
            var watcher = new Thread(() =>
            {
                while (true)
                {
                    try
                    {
                        using (var parent = Process.GetProcessById(parentProcessId))
                        {
                            if (parent.HasExited)
                            {
                                Environment.Exit(0);
                            }
                        }
                    }
                    catch
                    {
                        Environment.Exit(0);
                    }

                    Thread.Sleep(1000);
                }
            });
            watcher.IsBackground = true;
            watcher.Name = "BP0108 speech host parent watcher";
            watcher.Start();
        }

        private static string Encode(string value)
        {
            return Convert.ToBase64String(Encoding.UTF8.GetBytes(value ?? string.Empty));
        }

        private static string Decode(string value)
        {
            return Encoding.UTF8.GetString(Convert.FromBase64String(value));
        }

        private static string SafeFileToken(string value)
        {
            var builder = new StringBuilder();
            foreach (var character in value ?? string.Empty)
            {
                if (char.IsLetterOrDigit(character) || character == '-' || character == '_')
                {
                    builder.Append(character);
                }
            }

            if (builder.Length == 0)
            {
                return "request";
            }

            return builder.Length <= 48 ? builder.ToString() : builder.ToString(0, 48);
        }

        private static void WriteProtocol(string value)
        {
            Console.Out.WriteLine(value);
            Console.Out.Flush();
        }

        private sealed class HostOptions
        {
            public int ParentProcessId;
            public bool ProbeOnly;
            public string WaveDirectory;

            public static HostOptions Parse(string[] args)
            {
                var options = new HostOptions
                {
                    WaveDirectory = Path.Combine(Path.GetTempPath(), "BP0108SpeechHost"),
                };

                for (var index = 0; index < args.Length; index++)
                {
                    if (string.Equals(args[index], "--probe", StringComparison.OrdinalIgnoreCase))
                    {
                        options.ProbeOnly = true;
                    }
                    else if (string.Equals(args[index], "--parent-pid", StringComparison.OrdinalIgnoreCase) && index + 1 < args.Length)
                    {
                        int.TryParse(args[++index], NumberStyles.Integer, CultureInfo.InvariantCulture, out options.ParentProcessId);
                    }
                    else if (string.Equals(args[index], "--wave-dir", StringComparison.OrdinalIgnoreCase) && index + 1 < args.Length)
                    {
                        options.WaveDirectory = Path.GetFullPath(args[++index]);
                    }
                }

                return options;
            }
        }
    }
}
