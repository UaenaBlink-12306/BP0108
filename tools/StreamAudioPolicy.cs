using System;
using System.Collections.Generic;
using System.Text;

namespace CodexRuntimePatch
{
    /// <summary>
    /// A speech request that is independent of Unity and of the concrete TTS backend.
    /// </summary>
    public sealed class StreamSpeechRequest
    {
        public StreamSpeechRequest(string category, string text, bool priority, long sequence)
        {
            Category = category;
            Text = text;
            Priority = priority;
            Sequence = sequence;
        }

        public string Category { get; private set; }
        public string Text { get; private set; }
        public bool Priority { get; private set; }
        public long Sequence { get; private set; }
    }

    /// <summary>
    /// Thread-safe policy state for stream announcements. The runtime owns actual speech
    /// playback; this type owns deterministic queuing and announcement deduplication.
    /// </summary>
    public sealed class StreamAudioPolicy
    {
        public const string IntroductionCategory = "introduction";
        public const string WelcomeCategory = "welcome";
        public const string FirstCorrectCategory = "first-correct";

        public const string PersonalPlayInstructions =
            "Type your answer in Twitch chat. No command is needed. The first correct answer wins.";

        public const string TeamPlayInstructions =
            "Your Twitch team decides your side. Type your answer in Twitch chat. No command is needed. The first correct answer damages the other team.";

        private const int MaxSpokenNameLength = 48;
        private const int MaxNamesPerWelcome = 3;

        private readonly object _gate = new object();
        private readonly int _maxRegularQueue;
        private readonly Queue<StreamSpeechRequest> _priorityQueue = new Queue<StreamSpeechRequest>();
        private readonly LinkedList<StreamSpeechRequest> _regularQueue = new LinkedList<StreamSpeechRequest>();
        private readonly HashSet<string> _welcomedUserIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _announcedQuestionTokens = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private bool _introductionQueued;
        private long _nextSequence;

        public StreamAudioPolicy(int maxRegularQueue)
        {
            if (maxRegularQueue < 1)
            {
                throw new ArgumentOutOfRangeException("maxRegularQueue", "The regular speech queue must hold at least one request.");
            }

            _maxRegularQueue = maxRegularQueue;
        }

        public int QueueCount
        {
            get
            {
                lock (_gate)
                {
                    return _priorityQueue.Count + _regularQueue.Count;
                }
            }
        }

        public int PriorityQueueCount
        {
            get
            {
                lock (_gate)
                {
                    return _priorityQueue.Count;
                }
            }
        }

        public int RegularQueueCount
        {
            get
            {
                lock (_gate)
                {
                    return _regularQueue.Count;
                }
            }
        }

        public bool HasWelcomed(string userId)
        {
            var normalized = NormalizeKey(userId);
            if (normalized.Length == 0)
            {
                return false;
            }

            lock (_gate)
            {
                return _welcomedUserIds.Contains(normalized);
            }
        }

        public bool TryMarkWelcomed(string userId)
        {
            var normalized = NormalizeKey(userId);
            if (normalized.Length == 0)
            {
                return false;
            }

            lock (_gate)
            {
                return _welcomedUserIds.Add(normalized);
            }
        }

        /// <summary>
        /// Queues one priority announcement for a unique question presentation token.
        /// Callers should include the round/session generation in the token, for example
        /// "round-12/question-3", so a reused question index in a later round is distinct.
        /// </summary>
        public bool TryQueueFirstCorrect(string questionToken, string displayName)
        {
            var normalizedToken = NormalizeKey(questionToken);
            if (normalizedToken.Length == 0)
            {
                return false;
            }

            lock (_gate)
            {
                if (_announcedQuestionTokens.Contains(normalizedToken))
                {
                    return false;
                }

                var queued = EnqueueUnderLock(
                    FirstCorrectCategory,
                    BuildFirstCorrectSpeech(displayName),
                    true);
                if (queued)
                {
                    _announcedQuestionTokens.Add(normalizedToken);
                }

                return queued;
            }
        }

        public bool TryQueueIntroduction(string text)
        {
            lock (_gate)
            {
                if (_introductionQueued)
                {
                    return false;
                }

                var queued = EnqueueUnderLock(IntroductionCategory, text, false);
                if (queued)
                {
                    _introductionQueued = true;
                }

                return queued;
            }
        }

        public bool Enqueue(string category, string text, bool priority)
        {
            lock (_gate)
            {
                return EnqueueUnderLock(category, text, priority);
            }
        }

        /// <summary>
        /// Puts interrupted regular speech back at the front of the regular queue. Priority
        /// announcements still run first, so a first-correct message can interrupt a welcome
        /// without permanently losing that welcome. The normal queue bound is deliberately
        /// bypassed for this one already-admitted request.
        /// </summary>
        public bool RequeueInterrupted(StreamSpeechRequest request)
        {
            if (request == null || string.IsNullOrWhiteSpace(request.Text))
            {
                return false;
            }

            lock (_gate)
            {
                var normalizedCategory = string.IsNullOrWhiteSpace(request.Category)
                    ? "speech"
                    : request.Category.Trim();
                _regularQueue.AddFirst(new StreamSpeechRequest(
                    normalizedCategory,
                    request.Text.Trim(),
                    false,
                    ++_nextSequence));
                return true;
            }
        }

        public bool TryDequeue(out StreamSpeechRequest request)
        {
            lock (_gate)
            {
                if (_priorityQueue.Count > 0)
                {
                    request = _priorityQueue.Dequeue();
                    return true;
                }

                if (_regularQueue.Count > 0)
                {
                    request = _regularQueue.First.Value;
                    _regularQueue.RemoveFirst();
                    return true;
                }

                request = null;
                return false;
            }
        }

        public static string BuildWelcomeSpeech(IList<string> names, int total, bool personal)
        {
            var uniqueNames = new List<string>();
            var seenNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (names != null)
            {
                for (var i = 0; i < names.Count; i++)
                {
                    if (string.IsNullOrWhiteSpace(names[i]))
                    {
                        continue;
                    }

                    var sanitized = SanitizeSpokenName(names[i]);
                    if (seenNames.Add(sanitized))
                    {
                        uniqueNames.Add(sanitized);
                    }
                }
            }

            var effectiveTotal = Math.Max(Math.Max(0, total), uniqueNames.Count);
            string greeting;
            if (uniqueNames.Count == 0)
            {
                if (effectiveTotal == 1)
                {
                    greeting = "Welcome to our new viewer!";
                }
                else if (effectiveTotal > 1)
                {
                    greeting = string.Format("Welcome to our {0} new viewers!", effectiveTotal);
                }
                else
                {
                    greeting = "Welcome to the Twitch History Challenge!";
                }
            }
            else
            {
                var labels = new List<string>();
                var namesToSpeak = Math.Min(MaxNamesPerWelcome, uniqueNames.Count);
                for (var i = 0; i < namesToSpeak; i++)
                {
                    labels.Add(uniqueNames[i]);
                }

                var otherCount = effectiveTotal - namesToSpeak;
                if (otherCount == 1)
                {
                    labels.Add("one other viewer");
                }
                else if (otherCount > 1)
                {
                    labels.Add(string.Format("{0} other viewers", otherCount));
                }

                greeting = "Welcome, " + JoinForSpeech(labels) + "!";
            }

            return greeting + " " + (personal ? PersonalPlayInstructions : TeamPlayInstructions);
        }

        public static string BuildFirstCorrectSpeech(string displayName)
        {
            return string.Format(
                "Congratulations, {0}! You got the question correct first.",
                SanitizeSpokenName(displayName));
        }

        public static string SanitizeSpokenName(string displayName)
        {
            if (string.IsNullOrWhiteSpace(displayName))
            {
                return "viewer";
            }

            string normalized;
            try
            {
                normalized = displayName.Normalize(NormalizationForm.FormKC);
            }
            catch (ArgumentException)
            {
                normalized = displayName;
            }

            var builder = new StringBuilder();
            var separatorPending = false;
            for (var i = 0; i < normalized.Length && builder.Length < MaxSpokenNameLength; i++)
            {
                var character = normalized[i];
                if (char.IsLetterOrDigit(character))
                {
                    if (separatorPending && builder.Length > 0 && builder[builder.Length - 1] != ' ')
                    {
                        builder.Append(' ');
                    }

                    if (builder.Length < MaxSpokenNameLength)
                    {
                        builder.Append(character);
                    }

                    separatorPending = false;
                    continue;
                }

                if ((character == '\'' || character == '-')
                    && builder.Length > 0
                    && builder[builder.Length - 1] != ' '
                    && builder[builder.Length - 1] != character)
                {
                    builder.Append(character);
                    separatorPending = false;
                    continue;
                }

                separatorPending = builder.Length > 0;
            }

            var safe = builder.ToString().Trim(' ', '-', '\'');
            return safe.Length == 0 ? "viewer" : safe;
        }

        private bool EnqueueUnderLock(string category, string text, bool priority)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                return false;
            }

            if (!priority && _regularQueue.Count >= _maxRegularQueue)
            {
                return false;
            }

            var normalizedCategory = string.IsNullOrWhiteSpace(category) ? "speech" : category.Trim();
            var request = new StreamSpeechRequest(
                normalizedCategory,
                text.Trim(),
                priority,
                ++_nextSequence);
            if (priority)
            {
                _priorityQueue.Enqueue(request);
            }
            else
            {
                _regularQueue.AddLast(request);
            }

            return true;
        }

        private static string NormalizeKey(string value)
        {
            return string.IsNullOrWhiteSpace(value) ? string.Empty : value.Trim();
        }

        private static string JoinForSpeech(IList<string> labels)
        {
            if (labels == null || labels.Count == 0)
            {
                return "new viewers";
            }

            if (labels.Count == 1)
            {
                return labels[0];
            }

            if (labels.Count == 2)
            {
                return labels[0] + " and " + labels[1];
            }

            var builder = new StringBuilder();
            for (var i = 0; i < labels.Count; i++)
            {
                if (i > 0)
                {
                    builder.Append(i == labels.Count - 1 ? ", and " : ", ");
                }

                builder.Append(labels[i]);
            }

            return builder.ToString();
        }
    }

    /// <summary>
    /// Pure numeric state for ducking one background-music source while speech is active
    /// or queued. It deliberately has no Unity dependencies.
    /// </summary>
    public sealed class StreamMusicDuckState
    {
        public StreamMusicDuckState()
            : this(1f)
        {
        }

        public StreamMusicDuckState(float initialVolume)
        {
            CurrentVolume = ClampVolume(initialVolume, 1f);
        }

        public float CurrentVolume { get; private set; }

        public float Step(
            float baseVolume,
            float duckVolume,
            float attackSeconds,
            float releaseSeconds,
            float deltaSeconds,
            bool speechActiveOrQueued)
        {
            var safeBase = ClampVolume(baseVolume, 1f);
            var safeDuck = Math.Min(safeBase, ClampVolume(duckVolume, 0f));
            if (float.IsNaN(CurrentVolume) || float.IsInfinity(CurrentVolume))
            {
                CurrentVolume = safeBase;
            }

            var target = speechActiveOrQueued ? safeDuck : safeBase;
            var duration = speechActiveOrQueued ? attackSeconds : releaseSeconds;
            if (float.IsNaN(duration) || float.IsInfinity(duration) || duration <= 0f)
            {
                CurrentVolume = target;
                return CurrentVolume;
            }

            var safeDelta = deltaSeconds;
            if (float.IsNaN(safeDelta) || float.IsInfinity(safeDelta) || safeDelta < 0f)
            {
                safeDelta = 0f;
            }

            var configuredRange = Math.Abs(safeBase - safeDuck);
            var remainingRange = Math.Abs(CurrentVolume - target);
            var movementRange = Math.Max(configuredRange, remainingRange);
            if (movementRange <= 0.000001f)
            {
                CurrentVolume = target;
                return CurrentVolume;
            }

            var maximumChange = movementRange * safeDelta / duration;
            CurrentVolume = MoveTowards(CurrentVolume, target, maximumChange);
            CurrentVolume = ClampVolume(CurrentVolume, target);
            return CurrentVolume;
        }

        public float RecoverAfterFailure(float baseVolume)
        {
            CurrentVolume = ClampVolume(baseVolume, 1f);
            return CurrentVolume;
        }

        public void Reset(float volume)
        {
            CurrentVolume = ClampVolume(volume, 1f);
        }

        private static float MoveTowards(float current, float target, float maximumChange)
        {
            if (maximumChange <= 0f)
            {
                return current;
            }

            if (Math.Abs(target - current) <= maximumChange)
            {
                return target;
            }

            return current + Math.Sign(target - current) * maximumChange;
        }

        private static float ClampVolume(float value, float fallback)
        {
            if (float.IsNaN(value) || float.IsInfinity(value))
            {
                value = fallback;
            }

            if (value < 0f)
            {
                return 0f;
            }

            if (value > 1f)
            {
                return 1f;
            }

            return value;
        }
    }
}
