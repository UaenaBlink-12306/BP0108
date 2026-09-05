using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using UnityEngine;

namespace CodexRuntimePatch
{
    internal sealed class LivestreamAudioDriver : MonoBehaviour
    {
        private const float NormalMusicVolume = 0.22f;
        private const float DuckedMusicVolume = 0.055f;
        private const float DuckAttackSeconds = 0.18f;
        private const float DuckReleaseSeconds = 0.70f;
        private const float WelcomeNameWaitSeconds = 1.25f;
        private const float WelcomeFallbackSeconds = 4f;
        private const int MaximumPendingWelcomes = 500;
        private const int MaximumRegularSpeechQueue = 12;
        private const string AudioTestArgument = "-codex-audio-test";

        private readonly StreamAudioPolicy _policy = new StreamAudioPolicy(MaximumRegularSpeechQueue);
        private readonly StreamMusicDuckState _duckState = new StreamMusicDuckState(NormalMusicVolume);
        private readonly object _pendingWelcomeGate = new object();
        private readonly object _readyPlaybackGate = new object();
        private readonly object _threadLogGate = new object();
        private readonly object _speechHostGate = new object();
        private readonly Dictionary<string, PendingWelcome> _pendingWelcomes =
            new Dictionary<string, PendingWelcome>(StringComparer.OrdinalIgnoreCase);
        private readonly Queue<ThreadLogEntry> _threadLogs = new Queue<ThreadLogEntry>();
        private readonly AutoResetEvent _speechQueueSignal = new AutoResetEvent(false);
        private readonly AutoResetEvent _playbackCompleteSignal = new AutoResetEvent(false);

        private Func<string, string> _displayNameResolver;
        private string _workspaceRoot;
        private string _gameRoot;
        private string _speechHostPath;
        private string _speechCacheDirectory;
        private bool _personalMode;
        private bool _configured;
        private bool _started;
        private bool _audioReady;
        private bool _preserveSpeechWaves;
        private bool _qaEnabled;
        private bool _qaWelcomeInjected;
        private bool _qaCorrectInjected;
        private bool _qaFinished;
        private bool _duckObserved;
        private bool _restoreObserved;
        private float _nextWelcomeFlushAt;
        private float _qaStartedAt;
        private AudioSource _musicSource;
        private AudioSource _voiceSource;
        private AudioClip _musicClip;
        private ActiveVoicePlayback _activeVoice;
        private ReadySpeechPlayback _readyPlayback;
        private Thread _speechWorker;
        private System.Diagnostics.Process _speechHost;
        private volatile bool _shuttingDown;
        private volatile bool _speechActive;
        private volatile bool _speechHostReady;
        private volatile bool _cancelRegularPlaybackRequested;
        private int _playbackResult;
        private int _introductionQueuedCount;
        private int _welcomeBatchQueuedCount;
        private int _welcomedViewerCount;
        private int _firstCorrectQueuedCount;
        private int _speechStartedCount;
        private int _speechCompletedCount;
        private int _speechCanceledCount;
        private int _speechRequeuedCount;
        private int _speechFailedCount;

        public void Configure(
            bool personalMode,
            string workspaceRoot,
            Func<string, string> displayNameResolver)
        {
            _personalMode = personalMode;
            _workspaceRoot = workspaceRoot;
            _displayNameResolver = displayNameResolver;
            _configured = true;
        }

        public void RequestViewerWelcome(string userId, string displayName)
        {
            var normalizedId = string.IsNullOrWhiteSpace(userId) ? string.Empty : userId.Trim();
            if (normalizedId.Length == 0 || _policy.HasWelcomed(normalizedId))
            {
                return;
            }

            lock (_pendingWelcomeGate)
            {
                PendingWelcome pending;
                if (_pendingWelcomes.TryGetValue(normalizedId, out pending))
                {
                    if (!string.IsNullOrWhiteSpace(displayName))
                    {
                        pending.DisplayName = displayName.Trim();
                    }
                    return;
                }

                if (_pendingWelcomes.Count >= MaximumPendingWelcomes)
                {
                    return;
                }

                _pendingWelcomes[normalizedId] = new PendingWelcome
                {
                    UserId = normalizedId,
                    DisplayName = string.IsNullOrWhiteSpace(displayName) ? string.Empty : displayName.Trim(),
                    FirstSeenUtc = DateTime.UtcNow,
                };
            }
        }

        public void QueueFirstCorrect(string questionToken, string displayName)
        {
            if (_policy.TryQueueFirstCorrect(questionToken, displayName))
            {
                Interlocked.Increment(ref _firstCorrectQueuedCount);
                _cancelRegularPlaybackRequested = true;
                _speechQueueSignal.Set();
                QueueThreadLog("First-correct congratulations queued for " + StreamAudioPolicy.SanitizeSpokenName(displayName) + ".");
            }
        }

        private void Start()
        {
            _started = true;
            _qaEnabled = HasCommandLineArgument(AudioTestArgument);
            _preserveSpeechWaves = _qaEnabled;
            _qaStartedAt = Time.unscaledTime;
            Application.runInBackground = true;

            if (!_configured)
            {
                _personalMode = Application.dataPath.IndexOf("nonteam", StringComparison.OrdinalIgnoreCase) >= 0;
                var gameRoot = Directory.GetParent(Application.dataPath);
                _workspaceRoot = gameRoot == null || gameRoot.Parent == null
                    ? Application.dataPath
                    : gameRoot.Parent.FullName;
            }

            try
            {
                InitializeSpeechCache();
                InitializeUnityAudio();
                _audioReady = true;
            }
            catch (Exception ex)
            {
                Debug.LogWarning("[CodexPatch][Audio] Unity audio initialization failed: " + ex);
            }

            StartSpeechWorker();
            var introduction = StreamAudioPolicy.BuildWelcomeSpeech(null, 0, _personalMode);
            if (_policy.TryQueueIntroduction(introduction))
            {
                Interlocked.Increment(ref _introductionQueuedCount);
                _speechQueueSignal.Set();
            }

            Debug.Log(string.Format(
                "[CodexPatch][Audio] Livestream audio ready. music={0:0.000} duck={1:0.000} tts=Unity WAV playback",
                NormalMusicVolume,
                DuckedMusicVolume));
        }

        private void Update()
        {
            FlushThreadLogs();
            if (!_started || _shuttingDown)
            {
                return;
            }

            FlushPendingWelcomes();
            RunQaScenario();

            var shouldDuck = HasReadyPlayback()
                || _activeVoice != null;
            var musicVolume = _duckState.Step(
                NormalMusicVolume,
                DuckedMusicVolume,
                DuckAttackSeconds,
                DuckReleaseSeconds,
                Time.unscaledDeltaTime,
                shouldDuck);
            if (_musicSource != null)
            {
                _musicSource.volume = musicVolume;
                if (!_musicSource.isPlaying && _musicSource.clip != null)
                {
                    _musicSource.Play();
                    Debug.LogWarning("[CodexPatch][Audio] Background music was restarted after an unexpected stop.");
                }
            }

            if (shouldDuck && musicVolume <= DuckedMusicVolume + 0.002f)
            {
                _duckObserved = true;
            }
            else if (!shouldDuck && _duckObserved && musicVolume >= NormalMusicVolume - 0.002f)
            {
                _restoreObserved = true;
            }

            UpdateVoicePlayback(musicVolume);
            EvaluateQaResult();
        }

        private void OnDestroy()
        {
            ShutdownAudioRuntime();
        }

        private void OnApplicationQuit()
        {
            ShutdownAudioRuntime();
        }

        private void ShutdownAudioRuntime()
        {
            if (_shuttingDown)
            {
                return;
            }

            _shuttingDown = true;
            _speechQueueSignal.Set();
            _playbackCompleteSignal.Set();
            if (_speechWorker != null && _speechWorker.IsAlive)
            {
                _speechWorker.Join(1200);
            }

            StopSpeechHost(true);
            if (_voiceSource != null)
            {
                _voiceSource.Stop();
            }
            if (_musicSource != null)
            {
                _musicSource.Stop();
            }

            if (!_preserveSpeechWaves)
            {
                CleanupSpeechCache();
            }
        }

        private void InitializeSpeechCache()
        {
            var gameDirectory = Directory.GetParent(Application.dataPath);
            _gameRoot = gameDirectory == null ? string.Empty : gameDirectory.FullName;
            _speechHostPath = string.IsNullOrWhiteSpace(_gameRoot)
                ? string.Empty
                : Path.Combine(_gameRoot, "BP0108_SpeechHost.exe");
            var suffix = string.Format(
                "{0}_{1}_{2}",
                _personalMode ? "solo" : "team",
                System.Diagnostics.Process.GetCurrentProcess().Id,
                Guid.NewGuid().ToString("N"));
            var cacheRoot = _preserveSpeechWaves
                ? Path.Combine(_workspaceRoot, "tmp", "audio_tts_qa")
                : Path.Combine(Path.GetTempPath(), "BP0108SpeechCache");
            _speechCacheDirectory = Path.Combine(cacheRoot, suffix);
            Directory.CreateDirectory(_speechCacheDirectory);
            Debug.Log("[CodexPatch][Audio] Speech cache: " + _speechCacheDirectory);
        }

        private void InitializeUnityAudio()
        {
            var musicObject = new GameObject("__BP0108BackgroundMusic__");
            musicObject.transform.SetParent(transform, false);
            _musicSource = musicObject.AddComponent<AudioSource>();
            _musicSource.loop = true;
            _musicSource.playOnAwake = false;
            _musicSource.spatialBlend = 0f;
            _musicSource.ignoreListenerPause = true;
            _musicSource.volume = NormalMusicVolume;
            _musicClip = CreateOriginalQuizMusic();
            _musicSource.clip = _musicClip;
            _musicSource.Play();

            var voiceObject = new GameObject("__BP0108TTSVoice__");
            voiceObject.transform.SetParent(transform, false);
            _voiceSource = voiceObject.AddComponent<AudioSource>();
            _voiceSource.loop = false;
            _voiceSource.playOnAwake = false;
            _voiceSource.spatialBlend = 0f;
            _voiceSource.ignoreListenerPause = true;
            _voiceSource.volume = 1f;

            Debug.Log(string.Format(
                "[CodexPatch][Audio] Original background loop started: {0:0.0}s stereo at {1} Hz.",
                _musicClip.length,
                _musicClip.frequency));
        }

        private static AudioClip CreateOriginalQuizMusic()
        {
            const int sampleRate = 44100;
            const int channels = 2;
            const int seconds = 16;
            var frameCount = sampleRate * seconds;
            var samples = new float[frameCount * channels];
            var roots = new[] { 48, 45, 41, 43 };
            var thirds = new[] { 4, 3, 4, 4 };
            var arpeggio = new[] { 0, 1, 2, 1, 0, 2, 1, 2 };
            var tau = Math.PI * 2.0;

            for (var frame = 0; frame < frameCount; frame++)
            {
                var time = (double)frame / sampleRate;
                var chordIndex = Math.Min(roots.Length - 1, (int)(time / 4.0));
                var rootFrequency = MidiToFrequency(roots[chordIndex]);
                var thirdFrequency = MidiToFrequency(roots[chordIndex] + thirds[chordIndex]);
                var fifthFrequency = MidiToFrequency(roots[chordIndex] + 7);

                var pad = 0.24 * Math.Sin(tau * rootFrequency * time)
                    + 0.15 * Math.Sin(tau * thirdFrequency * time + 0.35)
                    + 0.13 * Math.Sin(tau * fifthFrequency * time + 0.7);
                pad *= 0.72 + 0.28 * Math.Sin(tau * 0.125 * time - Math.PI / 2.0);

                var eighthLength = 0.25;
                var eighthIndex = (int)(time / eighthLength);
                var eighthPhase = (time % eighthLength) / eighthLength;
                var toneIndex = arpeggio[eighthIndex % arpeggio.Length];
                var toneFrequency = toneIndex == 0 ? rootFrequency * 2.0
                    : (toneIndex == 1 ? thirdFrequency * 2.0 : fifthFrequency * 2.0);
                var pluckEnvelope = Math.Exp(-5.8 * eighthPhase);
                var pluck = 0.22 * pluckEnvelope * Math.Sin(tau * toneFrequency * time);

                var beatLength = 0.5;
                var beatPhase = (time % beatLength) / beatLength;
                var bassEnvelope = Math.Exp(-4.6 * beatPhase);
                var bass = 0.18 * bassEnvelope * Math.Sin(tau * (rootFrequency * 0.5) * time);
                var kickTime = time % beatLength;
                var kick = 0.10 * Math.Exp(-12.0 * kickTime)
                    * Math.Sin(tau * (72.0 - 24.0 * beatPhase) * kickTime);

                var edgeSeconds = Math.Min(time, seconds - time);
                var edgeFade = Math.Min(1.0, Math.Max(0.0, edgeSeconds / 0.04));
                var baseMix = (pad + bass + kick) * edgeFade;
                var left = (baseMix + pluck * 1.08) * 0.62;
                var right = (baseMix + pluck * 0.92) * 0.62;
                samples[frame * channels] = ClampSample((float)left);
                samples[frame * channels + 1] = ClampSample((float)right);
            }

            var clip = AudioClip.Create("BP0108 Original History Quiz Loop", frameCount, channels, sampleRate, false);
            if (clip == null || !clip.SetData(samples, 0))
            {
                throw new InvalidOperationException("Unity could not create the background-music clip.");
            }

            return clip;
        }

        private static double MidiToFrequency(int midiNote)
        {
            return 440.0 * Math.Pow(2.0, (midiNote - 69) / 12.0);
        }

        private static float ClampSample(float value)
        {
            return Math.Max(-0.92f, Math.Min(0.92f, value));
        }

        private void FlushPendingWelcomes()
        {
            if (Time.unscaledTime < _nextWelcomeFlushAt)
            {
                return;
            }
            _nextWelcomeFlushAt = Time.unscaledTime + 0.35f;

            List<PendingWelcome> snapshot;
            lock (_pendingWelcomeGate)
            {
                snapshot = new List<PendingWelcome>(_pendingWelcomes.Values);
            }

            if (snapshot.Count == 0)
            {
                return;
            }

            var now = DateTime.UtcNow;
            var ready = new List<ResolvedWelcome>();
            for (var index = 0; index < snapshot.Count; index++)
            {
                var pending = snapshot[index];
                if (_policy.HasWelcomed(pending.UserId))
                {
                    lock (_pendingWelcomeGate)
                    {
                        _pendingWelcomes.Remove(pending.UserId);
                    }
                    continue;
                }

                var ageSeconds = Math.Max(0.0, (now - pending.FirstSeenUtc).TotalSeconds);
                if (ageSeconds < WelcomeNameWaitSeconds)
                {
                    continue;
                }

                var resolvedName = pending.DisplayName;
                if (string.IsNullOrWhiteSpace(resolvedName) && _displayNameResolver != null)
                {
                    try
                    {
                        resolvedName = _displayNameResolver(pending.UserId);
                    }
                    catch (Exception ex)
                    {
                        Debug.LogWarning("[CodexPatch][Audio] Viewer-name resolution failed: " + ex.Message);
                    }
                }

                if (string.IsNullOrWhiteSpace(resolvedName) && ageSeconds < WelcomeFallbackSeconds)
                {
                    continue;
                }

                if (string.IsNullOrWhiteSpace(resolvedName) && !LooksLikeOpaqueUserId(pending.UserId))
                {
                    resolvedName = pending.UserId;
                }

                ready.Add(new ResolvedWelcome
                {
                    UserId = pending.UserId,
                    DisplayName = resolvedName,
                });
            }

            if (ready.Count == 0)
            {
                return;
            }

            var names = new List<string>();
            for (var index = 0; index < ready.Count; index++)
            {
                if (!string.IsNullOrWhiteSpace(ready[index].DisplayName))
                {
                    names.Add(ready[index].DisplayName);
                }
            }

            var speech = StreamAudioPolicy.BuildWelcomeSpeech(names, ready.Count, _personalMode);
            if (!_policy.Enqueue(StreamAudioPolicy.WelcomeCategory, speech, false))
            {
                return;
            }

            var claimed = 0;
            lock (_pendingWelcomeGate)
            {
                for (var index = 0; index < ready.Count; index++)
                {
                    if (_policy.TryMarkWelcomed(ready[index].UserId))
                    {
                        claimed++;
                    }
                    _pendingWelcomes.Remove(ready[index].UserId);
                }
            }

            Interlocked.Increment(ref _welcomeBatchQueuedCount);
            Interlocked.Add(ref _welcomedViewerCount, claimed);
            _speechQueueSignal.Set();
            Debug.Log(string.Format("[CodexPatch][Audio] Welcome queued for {0} new viewer(s).", claimed));
        }

        private static bool LooksLikeOpaqueUserId(string userId)
        {
            if (string.IsNullOrWhiteSpace(userId))
            {
                return true;
            }

            var value = userId.Trim();
            if (value.Length > 32)
            {
                return true;
            }

            var digits = 0;
            for (var index = 0; index < value.Length; index++)
            {
                if (char.IsDigit(value[index]))
                {
                    digits++;
                }
                else if (!char.IsLetter(value[index]) && value[index] != '_' && value[index] != '-')
                {
                    return true;
                }
            }

            return digits == value.Length || (value.Length >= 16 && digits >= value.Length * 3 / 4);
        }

        private void StartSpeechWorker()
        {
            _speechWorker = new Thread(SpeechWorkerLoop);
            _speechWorker.IsBackground = true;
            _speechWorker.Name = "BP0108 livestream speech worker";
            _speechWorker.Start();
        }

        private void SpeechWorkerLoop()
        {
            while (!_shuttingDown)
            {
                StreamSpeechRequest request;
                if (!_policy.TryDequeue(out request))
                {
                    _speechQueueSignal.WaitOne(250);
                    continue;
                }

                if (request.Priority)
                {
                    _cancelRegularPlaybackRequested = false;
                }

                _speechActive = true;
                Interlocked.Increment(ref _speechStartedCount);
                QueueThreadLog(string.Format(
                    "Speech preparing: category={0} sequence={1}.",
                    request.Category,
                    request.Sequence));

                try
                {
                    var wavePath = SynthesizeSpeechWithRetry(request);
                    Interlocked.Exchange(ref _playbackResult, 0);
                    lock (_readyPlaybackGate)
                    {
                        _readyPlayback = new ReadySpeechPlayback
                        {
                            Request = request,
                            WavePath = wavePath,
                        };
                    }

                    while (!_shuttingDown && !_playbackCompleteSignal.WaitOne(250))
                    {
                    }

                    if (_shuttingDown)
                    {
                        break;
                    }

                    var playbackResult = Interlocked.CompareExchange(ref _playbackResult, 0, 0);
                    if (playbackResult == 1)
                    {
                        Interlocked.Increment(ref _speechCompletedCount);
                        QueueThreadLog(string.Format(
                            "Speech completed: category={0} sequence={1}.",
                            request.Category,
                            request.Sequence));
                    }
                    else if (playbackResult == 2)
                    {
                        Interlocked.Increment(ref _speechCanceledCount);
                        if (_policy.RequeueInterrupted(request))
                        {
                            Interlocked.Increment(ref _speechRequeuedCount);
                            _speechQueueSignal.Set();
                            QueueThreadLog(string.Format(
                                "Interrupted speech requeued after priority announcements: category={0} sequence={1}.",
                                request.Category,
                                request.Sequence));
                        }
                        else
                        {
                            Interlocked.Increment(ref _speechFailedCount);
                            QueueThreadLog(string.Format(
                                "Interrupted speech could not be requeued: category={0} sequence={1}.",
                                request.Category,
                                request.Sequence),
                                true);
                        }
                        QueueThreadLog(string.Format(
                            "Speech preempted by a first-correct announcement: category={0} sequence={1}.",
                            request.Category,
                            request.Sequence));
                    }
                    else
                    {
                        Interlocked.Increment(ref _speechFailedCount);
                        QueueThreadLog(string.Format(
                            "Speech playback failed: category={0} sequence={1}.",
                            request.Category,
                            request.Sequence),
                            true);
                    }
                }
                catch (Exception ex)
                {
                    Interlocked.Increment(ref _speechFailedCount);
                    QueueThreadLog(string.Format(
                        "Speech synthesis failed: category={0} sequence={1}: {2}",
                        request.Category,
                        request.Sequence,
                        ex.Message),
                        true);
                }
                finally
                {
                    _speechActive = false;
                }
            }

            StopSpeechHost(false);
        }

        private string SynthesizeSpeechWithRetry(StreamSpeechRequest request)
        {
            Exception lastError = null;
            for (var attempt = 1; attempt <= 2 && !_shuttingDown; attempt++)
            {
                try
                {
                    return SynthesizeSpeech(request);
                }
                catch (Exception ex)
                {
                    lastError = ex;
                    StopSpeechHost(true);
                    if (attempt < 2)
                    {
                        QueueThreadLog("Restarting the speech host after an error: " + ex.Message, true);
                        Thread.Sleep(400);
                    }
                }
            }

            throw lastError ?? new InvalidOperationException("Speech synthesis was cancelled.");
        }

        private string SynthesizeSpeech(StreamSpeechRequest request)
        {
            var host = EnsureSpeechHost();
            var requestId = request.Sequence.ToString("D8");
            var encodedText = Convert.ToBase64String(Encoding.UTF8.GetBytes(request.Text));
            host.StandardInput.WriteLine(requestId + "|" + encodedText);
            host.StandardInput.Flush();

            while (!_shuttingDown)
            {
                var response = ReadHostLineWithTimeout(host, 8000, "speech synthesis");
                if (response == null)
                {
                    throw new IOException("The speech host closed its output pipe.");
                }

                var parts = response.Split('|');
                if (parts.Length >= 2 && string.Equals(parts[0], "START", StringComparison.Ordinal))
                {
                    if (string.Equals(parts[1], requestId, StringComparison.Ordinal))
                    {
                        QueueThreadLog("Speech host started synthesis for sequence " + request.Sequence + ".");
                    }
                    continue;
                }

                if (parts.Length >= 4
                    && string.Equals(parts[0], "DONE", StringComparison.Ordinal)
                    && string.Equals(parts[1], requestId, StringComparison.Ordinal))
                {
                    var wavePath = DecodeProtocolValue(parts[3]);
                    ValidateSynthesizedWavePath(wavePath);
                    return wavePath;
                }

                if (parts.Length >= 3
                    && string.Equals(parts[0], "ERROR", StringComparison.Ordinal)
                    && string.Equals(parts[1], requestId, StringComparison.Ordinal))
                {
                    throw new InvalidOperationException(DecodeProtocolValue(parts[2]));
                }
            }

            throw new OperationCanceledException("Speech synthesis stopped during application shutdown.");
        }

        private System.Diagnostics.Process EnsureSpeechHost()
        {
            lock (_speechHostGate)
            {
                if (_speechHost != null && !_speechHost.HasExited && _speechHostReady)
                {
                    return _speechHost;
                }
            }

            StopSpeechHost(true);
            if (string.IsNullOrWhiteSpace(_speechHostPath) || !File.Exists(_speechHostPath))
            {
                throw new FileNotFoundException("BP0108_SpeechHost.exe was not found beside the game.", _speechHostPath);
            }

            var startInfo = new System.Diagnostics.ProcessStartInfo
            {
                FileName = _speechHostPath,
                Arguments = string.Format(
                    "--parent-pid {0} --wave-dir {1}",
                    System.Diagnostics.Process.GetCurrentProcess().Id,
                    QuoteProcessArgument(_speechCacheDirectory)),
                WorkingDirectory = _gameRoot,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                WindowStyle = System.Diagnostics.ProcessWindowStyle.Hidden,
            };

            var process = new System.Diagnostics.Process { StartInfo = startInfo };
            process.ErrorDataReceived += delegate(object sender, System.Diagnostics.DataReceivedEventArgs eventArgs)
            {
                if (!string.IsNullOrWhiteSpace(eventArgs.Data))
                {
                    QueueThreadLog("Speech host error output: " + eventArgs.Data, true);
                }
            };
            if (!process.Start())
            {
                process.Dispose();
                throw new InvalidOperationException("Windows did not start BP0108_SpeechHost.exe.");
            }
            var ownershipTransferred = false;
            try
            {
                process.BeginErrorReadLine();
                var ready = ReadHostLineWithTimeout(process, 8000, "speech-host startup");
                if (string.IsNullOrWhiteSpace(ready) || !ready.StartsWith("READY|", StringComparison.Ordinal))
                {
                    throw new InvalidOperationException("The speech host did not complete its startup handshake.");
                }

                var voiceName = DecodeProtocolValue(ready.Substring("READY|".Length));
                lock (_speechHostGate)
                {
                    _speechHost = process;
                    _speechHostReady = true;
                    ownershipTransferred = true;
                }
                QueueThreadLog("Windows TTS host ready with voice " + voiceName + ".");
                return process;
            }
            finally
            {
                if (!ownershipTransferred)
                {
                    try
                    {
                        if (!process.HasExited)
                        {
                            process.Kill();
                            process.WaitForExit(800);
                        }
                    }
                    catch
                    {
                    }
                    process.Dispose();
                }
            }
        }

        private void StopSpeechHost(bool force)
        {
            System.Diagnostics.Process process;
            lock (_speechHostGate)
            {
                process = _speechHost;
                _speechHost = null;
                _speechHostReady = false;
            }

            if (process == null)
            {
                return;
            }

            try
            {
                if (!process.HasExited && !force)
                {
                    process.StandardInput.WriteLine("QUIT");
                    process.StandardInput.Flush();
                    process.StandardInput.Close();
                    process.WaitForExit(800);
                }
                if (!process.HasExited)
                {
                    process.Kill();
                    process.WaitForExit(800);
                }
            }
            catch
            {
            }
            finally
            {
                process.Dispose();
            }
        }

        private static string QuoteProcessArgument(string value)
        {
            return "\"" + (value ?? string.Empty).Replace("\"", "\\\"") + "\"";
        }

        private static string DecodeProtocolValue(string encoded)
        {
            return Encoding.UTF8.GetString(Convert.FromBase64String(encoded ?? string.Empty));
        }

        private static string ReadHostLineWithTimeout(
            System.Diagnostics.Process process,
            int timeoutMilliseconds,
            string operation)
        {
            string line = null;
            Exception readError = null;
            var completed = new ManualResetEvent(false);
            var readerThread = new Thread(delegate()
            {
                try
                {
                    line = process.StandardOutput.ReadLine();
                }
                catch (Exception ex)
                {
                    readError = ex;
                }
                finally
                {
                    try
                    {
                        completed.Set();
                    }
                    catch
                    {
                    }
                }
            });
            readerThread.IsBackground = true;
            readerThread.Name = "BP0108 speech host reader";
            readerThread.Start();

            if (!completed.WaitOne(timeoutMilliseconds))
            {
                try
                {
                    if (!process.HasExited)
                    {
                        process.Kill();
                    }
                }
                catch
                {
                }
                readerThread.Join(500);
                completed.Close();
                throw new TimeoutException(string.Format(
                    "The speech host exceeded the {0} ms {1} deadline.",
                    timeoutMilliseconds,
                    operation));
            }

            completed.Close();
            if (readError != null)
            {
                throw new IOException("The speech host output pipe failed during " + operation + ".", readError);
            }
            return line;
        }

        private void ValidateSynthesizedWavePath(string wavePath)
        {
            if (string.IsNullOrWhiteSpace(wavePath))
            {
                throw new InvalidDataException("The speech host returned an empty WAV path.");
            }

            var fullWavePath = Path.GetFullPath(wavePath);
            var fullCachePath = Path.GetFullPath(_speechCacheDirectory)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                + Path.DirectorySeparatorChar;
            if (!fullWavePath.StartsWith(fullCachePath, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException("The speech host returned a WAV path outside its cache directory.");
            }
            if (!File.Exists(fullWavePath) || new FileInfo(fullWavePath).Length <= 44)
            {
                throw new InvalidDataException("The synthesized speech WAV was missing or empty.");
            }
        }

        private bool HasReadyPlayback()
        {
            lock (_readyPlaybackGate)
            {
                return _readyPlayback != null;
            }
        }

        private void UpdateVoicePlayback(float musicVolume)
        {
            if (_cancelRegularPlaybackRequested)
            {
                if (_activeVoice != null
                    && IsInterruptibleForFirstCorrect(_activeVoice.Ready.Request.Category))
                {
                    CompleteActiveVoiceInterrupted();
                    return;
                }

                ReadySpeechPlayback canceledReady = null;
                lock (_readyPlaybackGate)
                {
                    if (_readyPlayback != null
                        && IsInterruptibleForFirstCorrect(_readyPlayback.Request.Category))
                    {
                        canceledReady = _readyPlayback;
                        _readyPlayback = null;
                    }
                }
                if (canceledReady != null)
                {
                    DeleteSpeechWaveUnlessPreserved(canceledReady.WavePath);
                    Interlocked.Exchange(ref _playbackResult, 2);
                    _playbackCompleteSignal.Set();
                    return;
                }
            }

            if (_activeVoice != null)
            {
                var elapsed = Time.unscaledTime - _activeVoice.StartedAt;
                var clipDuration = _activeVoice.Clip == null ? 0f : _activeVoice.Clip.length;
                if (elapsed >= clipDuration + 0.20f)
                {
                    CompleteActiveVoice(true, null);
                }
                else if (elapsed >= 0.35f && (_voiceSource == null || !_voiceSource.isPlaying))
                {
                    if (elapsed >= Math.Max(0.1f, clipDuration - 0.25f))
                    {
                        CompleteActiveVoice(true, null);
                    }
                    else
                    {
                        CompleteActiveVoice(false, "Unity stopped TTS playback before the clip ended.");
                    }
                }
                return;
            }

            if (musicVolume > DuckedMusicVolume + 0.003f && _musicSource != null)
            {
                return;
            }

            ReadySpeechPlayback ready;
            lock (_readyPlaybackGate)
            {
                ready = _readyPlayback;
                _readyPlayback = null;
            }
            if (ready == null)
            {
                return;
            }

            try
            {
                if (_voiceSource == null)
                {
                    throw new InvalidOperationException("The Unity TTS AudioSource is unavailable.");
                }

                var clip = LoadPcmWave(ready.WavePath);
                _voiceSource.clip = clip;
                _voiceSource.Play();
                _activeVoice = new ActiveVoicePlayback
                {
                    Ready = ready,
                    Clip = clip,
                    StartedAt = Time.unscaledTime,
                };
                Debug.Log(string.Format(
                    "[CodexPatch][Audio] TTS playing in Unity: category={0} duration={1:0.00}s.",
                    ready.Request.Category,
                    clip.length));
            }
            catch (Exception ex)
            {
                CompleteReadySpeechFailure(ready, ex.Message);
            }
        }

        private void CompleteActiveVoice(bool success, string error)
        {
            var active = _activeVoice;
            _activeVoice = null;
            if (active == null)
            {
                return;
            }

            if (_voiceSource != null)
            {
                _voiceSource.Stop();
                _voiceSource.clip = null;
            }
            if (active.Clip != null)
            {
                UnityEngine.Object.Destroy(active.Clip);
            }
            DeleteSpeechWaveUnlessPreserved(active.Ready.WavePath);

            if (!success && !string.IsNullOrWhiteSpace(error))
            {
                Debug.LogWarning("[CodexPatch][Audio] " + error);
            }
            Interlocked.Exchange(ref _playbackResult, success ? 1 : -1);
            _playbackCompleteSignal.Set();
        }

        private static bool IsInterruptibleForFirstCorrect(string category)
        {
            return string.Equals(category, StreamAudioPolicy.IntroductionCategory, StringComparison.Ordinal)
                || string.Equals(category, StreamAudioPolicy.WelcomeCategory, StringComparison.Ordinal);
        }

        private void CompleteActiveVoiceInterrupted()
        {
            var active = _activeVoice;
            _activeVoice = null;
            if (active == null)
            {
                return;
            }

            if (_voiceSource != null)
            {
                _voiceSource.Stop();
                _voiceSource.clip = null;
            }
            if (active.Clip != null)
            {
                UnityEngine.Object.Destroy(active.Clip);
            }
            DeleteSpeechWaveUnlessPreserved(active.Ready.WavePath);
            Debug.Log("[CodexPatch][Audio] Regular TTS was preempted for a first-correct announcement and will resume afterward.");
            Interlocked.Exchange(ref _playbackResult, 2);
            _playbackCompleteSignal.Set();
        }

        private void CompleteReadySpeechFailure(ReadySpeechPlayback ready, string error)
        {
            DeleteSpeechWaveUnlessPreserved(ready == null ? null : ready.WavePath);
            Debug.LogWarning("[CodexPatch][Audio] TTS WAV playback failed: " + error);
            Interlocked.Exchange(ref _playbackResult, -1);
            _playbackCompleteSignal.Set();
        }

        private void DeleteSpeechWaveUnlessPreserved(string path)
        {
            if (_preserveSpeechWaves || string.IsNullOrWhiteSpace(path))
            {
                return;
            }

            try
            {
                File.Delete(path);
            }
            catch (Exception ex)
            {
                Debug.LogWarning("[CodexPatch][Audio] Could not clean up a speech WAV: " + ex.Message);
            }
        }

        private static AudioClip LoadPcmWave(string path)
        {
            var bytes = File.ReadAllBytes(path);
            if (bytes.Length < 44
                || ReadAscii(bytes, 0, 4) != "RIFF"
                || ReadAscii(bytes, 8, 4) != "WAVE")
            {
                throw new InvalidDataException("Synthesized speech was not a RIFF/WAVE file.");
            }

            var position = 12;
            var audioFormat = 0;
            var channels = 0;
            var sampleRate = 0;
            var bitsPerSample = 0;
            var dataOffset = -1;
            var dataLength = 0;
            while (position + 8 <= bytes.Length)
            {
                var chunkId = ReadAscii(bytes, position, 4);
                var chunkLength = ReadInt32(bytes, position + 4);
                var chunkData = position + 8;
                if (chunkLength < 0 || chunkData + chunkLength > bytes.Length)
                {
                    throw new InvalidDataException("Synthesized speech contained an invalid WAV chunk.");
                }

                if (chunkId == "fmt ")
                {
                    if (chunkLength < 16)
                    {
                        throw new InvalidDataException("Synthesized speech had an incomplete WAV format chunk.");
                    }
                    audioFormat = ReadUInt16(bytes, chunkData);
                    channels = ReadUInt16(bytes, chunkData + 2);
                    sampleRate = ReadInt32(bytes, chunkData + 4);
                    bitsPerSample = ReadUInt16(bytes, chunkData + 14);
                }
                else if (chunkId == "data")
                {
                    dataOffset = chunkData;
                    dataLength = chunkLength;
                }

                position = chunkData + chunkLength + (chunkLength & 1);
            }

            if (audioFormat != 1 || (channels != 1 && channels != 2) || bitsPerSample != 16)
            {
                throw new InvalidDataException("Only 16-bit mono or stereo PCM speech WAV files are supported.");
            }
            if (sampleRate < 8000 || sampleRate > 96000 || dataOffset < 0 || dataLength < channels * 2)
            {
                throw new InvalidDataException("Synthesized speech WAV metadata was invalid.");
            }

            var valueCount = dataLength / 2;
            var frameCount = valueCount / channels;
            var samples = new float[frameCount * channels];
            for (var index = 0; index < samples.Length; index++)
            {
                var byteOffset = dataOffset + index * 2;
                var value = (short)(bytes[byteOffset] | (bytes[byteOffset + 1] << 8));
                samples[index] = value / 32768f;
            }

            var clip = AudioClip.Create("BP0108 TTS", frameCount, channels, sampleRate, false);
            if (clip == null || !clip.SetData(samples, 0))
            {
                throw new InvalidOperationException("Unity could not create the TTS AudioClip.");
            }
            return clip;
        }

        private static int ReadUInt16(byte[] bytes, int offset)
        {
            return bytes[offset] | (bytes[offset + 1] << 8);
        }

        private static int ReadInt32(byte[] bytes, int offset)
        {
            return bytes[offset]
                | (bytes[offset + 1] << 8)
                | (bytes[offset + 2] << 16)
                | (bytes[offset + 3] << 24);
        }

        private static string ReadAscii(byte[] bytes, int offset, int length)
        {
            return Encoding.ASCII.GetString(bytes, offset, length);
        }

        private void QueueThreadLog(string message, bool warning)
        {
            lock (_threadLogGate)
            {
                _threadLogs.Enqueue(new ThreadLogEntry { Message = message, Warning = warning });
            }
        }

        private void QueueThreadLog(string message)
        {
            QueueThreadLog(message, false);
        }

        private void FlushThreadLogs()
        {
            while (true)
            {
                ThreadLogEntry entry;
                lock (_threadLogGate)
                {
                    if (_threadLogs.Count == 0)
                    {
                        return;
                    }
                    entry = _threadLogs.Dequeue();
                }

                if (entry.Warning)
                {
                    Debug.LogWarning("[CodexPatch][Audio] " + entry.Message);
                }
                else
                {
                    Debug.Log("[CodexPatch][Audio] " + entry.Message);
                }
            }
        }

        private void RunQaScenario()
        {
            if (!_qaEnabled || _qaFinished)
            {
                return;
            }

            var elapsed = Time.unscaledTime - _qaStartedAt;
            if (!_qaWelcomeInjected && elapsed >= 0.5f)
            {
                _qaWelcomeInjected = true;
                RequestViewerWelcome("codex-audio-viewer", "Audio_Test_Viewer");
                RequestViewerWelcome("CODEX-AUDIO-VIEWER", "Duplicate Should Not Speak");
            }
            if (!_qaCorrectInjected && elapsed >= 2f)
            {
                if (_activeVoice != null
                    && string.Equals(
                        _activeVoice.Ready.Request.Category,
                        StreamAudioPolicy.WelcomeCategory,
                        StringComparison.Ordinal)
                    && Time.unscaledTime - _activeVoice.StartedAt >= 0.75f)
                {
                    _qaCorrectInjected = true;
                    QueueFirstCorrect("audio-round-1/question-1", "Audio_Test_Viewer");
                    QueueFirstCorrect("AUDIO-ROUND-1/QUESTION-1", "Duplicate Should Not Speak");
                }
            }
        }

        private void EvaluateQaResult()
        {
            if (!_qaEnabled || _qaFinished)
            {
                return;
            }

            var elapsed = Time.unscaledTime - _qaStartedAt;
            var completed = Interlocked.CompareExchange(ref _speechCompletedCount, 0, 0);
            var canceled = Interlocked.CompareExchange(ref _speechCanceledCount, 0, 0);
            var requeued = Interlocked.CompareExchange(ref _speechRequeuedCount, 0, 0);
            var failed = Interlocked.CompareExchange(ref _speechFailedCount, 0, 0);
            var queueEmpty = _policy.QueueCount == 0 && !_speechActive && !HasReadyPlayback() && _activeVoice == null;
            if (completed >= 3
                && canceled == 1
                && requeued == 1
                && failed == 0
                && queueEmpty
                && _duckObserved
                && _restoreObserved
                && _speechHostReady
                && _musicSource != null
                && _musicSource.isPlaying
                && Interlocked.CompareExchange(ref _introductionQueuedCount, 0, 0) == 1
                && Interlocked.CompareExchange(ref _welcomeBatchQueuedCount, 0, 0) == 1
                && Interlocked.CompareExchange(ref _welcomedViewerCount, 0, 0) == 1
                && Interlocked.CompareExchange(ref _firstCorrectQueuedCount, 0, 0) == 1)
            {
                _qaFinished = true;
                Debug.Log(string.Format(
                    "[CodexPatch][AudioQA] PASS completed={0} preempted={1} requeued={2} intro=1 welcomeBatches=1 welcomed=1 firstCorrect=1 duck={3:0.000} restored={4:0.000} waves={5}",
                    completed,
                    canceled,
                    requeued,
                    DuckedMusicVolume,
                    NormalMusicVolume,
                    _speechCacheDirectory));
                Application.Quit(0);
                return;
            }

            if (elapsed >= 50f)
            {
                _qaFinished = true;
                Debug.LogError(string.Format(
                    "[CodexPatch][AudioQA] FAIL elapsed={0:0.0}s completed={1} preempted={2} requeued={3} failed={4} queue={5} active={6} duckObserved={7} restoreObserved={8} hostReady={9} musicPlaying={10}",
                    elapsed,
                    completed,
                    canceled,
                    requeued,
                    failed,
                    _policy.QueueCount,
                    _speechActive,
                    _duckObserved,
                    _restoreObserved,
                    _speechHostReady,
                    _musicSource != null && _musicSource.isPlaying));
                Application.Quit(2);
            }
        }

        private void CleanupSpeechCache()
        {
            if (string.IsNullOrWhiteSpace(_speechCacheDirectory) || !Directory.Exists(_speechCacheDirectory))
            {
                return;
            }

            try
            {
                var directory = new DirectoryInfo(_speechCacheDirectory);
                if (!directory.Name.StartsWith(_personalMode ? "solo_" : "team_", StringComparison.OrdinalIgnoreCase))
                {
                    return;
                }
                foreach (var file in directory.GetFiles("speech_*.wav"))
                {
                    file.Delete();
                }
                directory.Delete(false);
            }
            catch
            {
            }
        }

        private static bool HasCommandLineArgument(string expected)
        {
            var arguments = Environment.GetCommandLineArgs();
            for (var index = 0; index < arguments.Length; index++)
            {
                if (string.Equals(arguments[index], expected, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
            return false;
        }

        private sealed class PendingWelcome
        {
            public string UserId;
            public string DisplayName;
            public DateTime FirstSeenUtc;
        }

        private sealed class ResolvedWelcome
        {
            public string UserId;
            public string DisplayName;
        }

        private sealed class ReadySpeechPlayback
        {
            public StreamSpeechRequest Request;
            public string WavePath;
        }

        private sealed class ActiveVoicePlayback
        {
            public ReadySpeechPlayback Ready;
            public AudioClip Clip;
            public float StartedAt;
        }

        private sealed class ThreadLogEntry
        {
            public string Message;
            public bool Warning;
        }
    }
}
