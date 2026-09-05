using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using Newtonsoft.Json.Linq;
using UnityEngine;
using UnityEngine.UI;

namespace CodexRuntimePatch
{
    public static class QuestionPatch
    {
        private const int RoundSize = 10;
        private const int MaxQuestionPool = 300;
        private const long MinStreamImageBytes = 1000000;
        private const int MinStreamImageWidth = 1024;
        private const int MinStreamImageHeight = 576;
        private const int QuestionSentenceCount = 2;
        private const int QuestionBaseFontSize = 36;
        private const int QuestionMinFontSize = 20;
        private const int MaxCachedQuestionSprites = 14;
        private const float LobbyAutoStartSeconds = 9f;
        private const float StartRequestFallbackSeconds = 9f;
        private static readonly string QuestionFramePath = BuildQuestionFramePath();
        private static readonly Vector2 QuestionAuditBoxSize = new Vector2(874f, 174f);
        private static readonly System.Random Random = new System.Random();
        private static readonly object Gate = new object();
        private static readonly Dictionary<string, Sprite> SpriteCache = new Dictionary<string, Sprite>(StringComparer.OrdinalIgnoreCase);
        private static readonly List<string> SpriteCacheOrder = new List<string>();
        private static readonly Dictionary<string, string> LegacyUiTextTranslations = BuildLegacyUiTextTranslations();
        private static List<QuestionRecord> _selectedRoundQuestions = new List<QuestionRecord>();
        private static string _lastAppliedQuestionId;
        private static int _lastAppliedQuestionIndex = -1;
        private static bool _questionFitAuditWritten;
        private static Font _builtInFont;
        private static Canvas _questionOverlayCanvas;
        private static Text _questionOverlayText;
        private static readonly Dictionary<string, LocalScoreRecord> LocalScores = new Dictionary<string, LocalScoreRecord>(StringComparer.OrdinalIgnoreCase);
        private static readonly Dictionary<string, LiveViewerRecord> LiveViewers = new Dictionary<string, LiveViewerRecord>(StringComparer.OrdinalIgnoreCase);
        private static readonly HashSet<string> AnsweredUsersForQuestion = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private static int _answeredUsersQuestionIndex = -1;
        private static bool _personalBackendConfigured;
        private static int _roundSerial;
        private static LivestreamAudioDriver _livestreamAudioDriver;

        private static string BuildQuestionFramePath()
        {
            return "UI/down/" + new string(new[] { (char)0x9898, (char)0x76ee, (char)0x6570 });
        }

        private static Dictionary<string, string> BuildLegacyUiTextTranslations()
        {
            return new Dictionary<string, string>
            {
                { TextFromCodes(0x4e0b, 0x4e00, 0x9898), "Next Question" },
                { TextFromCodes(0x5f00, 0x59cb, 0x6e38, 0x620f), "Start Game" },
                { TextFromCodes(0x65e0, 0x4eba), "No Team" },
                { TextFromCodes(0x7ea2, 0x961f), "Red Team" },
                { TextFromCodes(0x7ed3, 0x675f, 0x6e38, 0x620f), "End Game" },
                { TextFromCodes(0x83b7, 0x53d6, 0x961f, 0x4f0d, 0x6210, 0x5458), "Get Team Members" },
                { TextFromCodes(0x83b7, 0x53d6, 0x9898, 0x76ee, 0x5217, 0x8868), "Get Question List" },
                { TextFromCodes(0x84dd, 0x961f), "Blue Team" },
            };
        }

        private static string TextFromCodes(params int[] codes)
        {
            var chars = new char[codes.Length];
            for (var i = 0; i < codes.Length; i++)
            {
                chars[i] = (char)codes[i];
            }

            return new string(chars);
        }

        private static void TranslateLegacyUiText()
        {
            foreach (var text in Resources.FindObjectsOfTypeAll<Text>())
            {
                if (text == null || string.IsNullOrEmpty(text.text))
                {
                    continue;
                }

                string translated;
                if (LegacyUiTextTranslations.TryGetValue(text.text.Trim(), out translated))
                {
                    text.text = translated;
                }
            }
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        public static void Bootstrap()
        {
            try
            {
                var existing = GameObject.Find("__CodexQuestionPatch__");
                if (existing != null)
                {
                    _livestreamAudioDriver = existing.GetComponent<LivestreamAudioDriver>();
                    if (_livestreamAudioDriver != null)
                    {
                        _livestreamAudioDriver.Configure(IsPersonalMode(), GetWorkspaceRoot(), ResolveKnownTwitchName);
                    }
                    return;
                }

                var go = new GameObject("__CodexQuestionPatch__");
                UnityEngine.Object.DontDestroyOnLoad(go);
                _livestreamAudioDriver = go.AddComponent<LivestreamAudioDriver>();
                _livestreamAudioDriver.Configure(IsPersonalMode(), GetWorkspaceRoot(), ResolveKnownTwitchName);
                if (Environment.GetCommandLineArgs().Any(argument => string.Equals(argument, "-codex-audio-test", StringComparison.OrdinalIgnoreCase)))
                {
                    Debug.Log("[CodexPatch][AudioQA] Isolated synthetic audio QA mode; live game, Twitch, and backend drivers are disabled.");
                    return;
                }
                go.AddComponent<QuestionImageDriver>();
                go.AddComponent<DanmakuLayerDriver>();
                if (IsPersonalMode())
                {
                    go.AddComponent<NonTeamLivestreamDriver>();
                }
                else
                {
                    go.AddComponent<TeamLivestreamDriver>();
                }
                go.AddComponent<StartMenuDriver>();
                go.AddComponent<AutonomousSessionDriver>();
                go.AddComponent<GameUxDriver>();
                if (Environment.GetCommandLineArgs().Any(argument => string.Equals(argument, "-codex-endurance-test", StringComparison.OrdinalIgnoreCase)))
                {
                    go.AddComponent<EnduranceQaDriver>();
                }
                TranslateLegacyUiText();
                WriteQuestionFitAudit();
                Debug.Log("[CodexPatch] Runtime patch bootstrap complete.");
            }
            catch (Exception ex)
            {
                Debug.LogWarning("[CodexPatch] Bootstrap failed: " + ex.Message);
            }
        }

        public static void PrepareRoundQuestions()
        {
            try
            {
                Bootstrap();
                EnsureRoundSelectionPrepared(true);
            }
            catch (Exception ex)
            {
                Debug.LogWarning("[CodexPatch] PrepareRoundQuestions failed: " + ex);
            }
        }

        public static void RefreshQuestionImage()
        {
            try
            {
                var gameFlowManagerType = FindAssemblyType("GameFlowManager");
                var uiManagerType = FindAssemblyType("UIManager");
                if (gameFlowManagerType == null || uiManagerType == null)
                {
                    return;
                }

                var gameFlowManager = GetStaticPropertyValue(gameFlowManagerType, "Instance");
                var uiManager = GetStaticPropertyValue(uiManagerType, "Instance");
                if (gameFlowManager == null || uiManager == null)
                {
                    return;
                }

                var currentQuestionIndex = Convert.ToInt32(GetInstancePropertyValue(gameFlowManager, "CurrentQuestionIndex"));
                if (currentQuestionIndex < 1 && _selectedRoundQuestions != null && _selectedRoundQuestions.Count > 0)
                {
                    currentQuestionIndex = 1;
                }

                var questions = GetInstanceFieldValue(gameFlowManager, "questions") as IList;
                if (currentQuestionIndex < 1)
                {
                    return;
                }

                var selectedQuestion = GetSelectedQuestion(currentQuestionIndex);
                if (selectedQuestion == null && (questions == null || currentQuestionIndex > questions.Count))
                {
                    return;
                }

                var questionText = ReadPropertyOrField(uiManager, "QUESTIONText") as Text;
                var subjectText = ReadPropertyOrField(uiManager, "SubjectText") as Text;
                var questionId = selectedQuestion == null
                    ? Convert.ToString(ReadPropertyOrField(questions[currentQuestionIndex - 1], "id"))
                    : selectedQuestion.Id;
                if (string.IsNullOrWhiteSpace(questionId))
                {
                    return;
                }

                if (questionText != null)
                {
                    FitQuestionTextToBluePanel(questionText);
                    ApplyQuestionTextStyle(questionText, questionText.text);
                }

                if (selectedQuestion != null)
                {
                    if (questionText != null)
                    {
                        questionText.text = selectedQuestion.Question ?? string.Empty;
                        ApplyQuestionTextStyle(questionText, questionText.text);
                        UpdateQuestionOverlay(questionText.text);
                        questionText.enabled = false;
                    }

                    if (subjectText != null)
                    {
                        subjectText.text = string.Empty;
                    }
                }

                if (currentQuestionIndex == _lastAppliedQuestionIndex && string.Equals(questionId, _lastAppliedQuestionId, StringComparison.Ordinal))
                {
                    return;
                }

                var subjectImage = ReadPropertyOrField(uiManager, "SubjectImage") as Image;
                if (subjectImage == null)
                {
                    return;
                }

                var sprite = LoadSpriteForQuestion(questionId);
                if (sprite == null)
                {
                    return;
                }

                subjectImage.sprite = sprite;
                subjectImage.preserveAspect = true;
                _lastAppliedQuestionId = questionId;
                _lastAppliedQuestionIndex = currentQuestionIndex;
            }
            catch (Exception ex)
            {
                Debug.LogWarning("[CodexPatch] RefreshQuestionImage failed: " + ex.Message);
            }
        }

        public static void HandlePersonalAnswerResult(object gameFlowManager, object answerResult)
        {
            try
            {
                // The remote service still evaluates its original built-in question set. Once the
                // local livestream pool is active, those answer_result events refer to a different
                // question and must not change this game's score or flow.
                if (_selectedRoundQuestions != null && _selectedRoundQuestions.Count > 0)
                {
                    return;
                }

                if (gameFlowManager == null || answerResult == null)
                {
                    return;
                }

                var isFirst = Convert.ToBoolean(ReadPropertyOrField(answerResult, "first"));
                var hasFirst = Convert.ToBoolean(GetInstanceFieldValue(gameFlowManager, "hasFirstAnswerInCurrentQuestion"));
                if (isFirst && hasFirst)
                {
                    return;
                }

                if (!Convert.ToBoolean(GetInstancePropertyValue(gameFlowManager, "IsGameActive")))
                {
                    Debug.LogWarning("[CodexPatch] Personal mode ignored answer while game is inactive.");
                    return;
                }

                var questionIndex = Convert.ToInt32(ReadPropertyOrField(answerResult, "questionIndex"));
                var currentQuestionIndex = Convert.ToInt32(GetInstancePropertyValue(gameFlowManager, "CurrentQuestionIndex"));
                var userName = ResolveAnswerDisplayName(answerResult);

                if (isFirst && questionIndex == currentQuestionIndex)
                {
                    if (Convert.ToBoolean(GetInstanceFieldValue(gameFlowManager, "_isWaitingForNextQuestion")))
                    {
                        Debug.LogWarning("[CodexPatch] Personal mode ignored first answer while next question is pending.");
                        return;
                    }

                    TryInvokeInstanceMethod(gameFlowManager, "StopCountdown");
                    TrySetPropertyOrField(gameFlowManager, "hasFirstAnswerInCurrentQuestion", true);
                    QueueFirstCorrectSpeech(gameFlowManager, questionIndex, userName);
                    KeepPersonalHealthNeutral(gameFlowManager);
                    ShowPersonalMessage(gameFlowManager, string.Format("Viewer {0} answered correctly.", userName), 3f);
                    TrySetPropertyOrField(gameFlowManager, "_isWaitingForNextQuestion", true);

                    var routine = InvokeInstanceMethod(gameFlowManager, "DelayedNextQuestion", 0f) as IEnumerator;
                    var behaviour = gameFlowManager as MonoBehaviour;
                    if (routine != null && behaviour != null)
                    {
                        behaviour.StartCoroutine(routine);
                    }
                }
                else
                {
                    ShowPersonalMessage(gameFlowManager, string.Format("Viewer {0} answered correctly.", userName), 2f);
                }

                RefreshTwitchUserNamesThenLeaderboard(gameFlowManager);
            }
            catch (Exception ex)
            {
                Debug.LogWarning("[CodexPatch] Personal answer result failed: " + ex.Message);
            }
        }

        public static void HandlePersonalLeaderboardJson(object gameFlowManager, string json)
        {
            try
            {
                if (_selectedRoundQuestions != null && _selectedRoundQuestions.Count > 0)
                {
                    RenderLocalLeaderboard();
                    return;
                }

                var users = JObject.Parse(json)["users"] as JArray;
                if (users == null)
                {
                    return;
                }

                var uiManagerType = FindAssemblyType("UIManager");
                if (uiManagerType == null)
                {
                    return;
                }

                var uiManager = GetStaticPropertyValue(uiManagerType, "Instance");
                var entryType = uiManagerType.GetNestedType("LeaderboardEntry", BindingFlags.Public | BindingFlags.NonPublic);
                if (uiManager == null || entryType == null)
                {
                    return;
                }

                var listType = typeof(List<>).MakeGenericType(entryType);
                var entries = (IList)Activator.CreateInstance(listType);
                foreach (var token in users)
                {
                    var userId = ReadJsonString(token, "userId");
                    var entry = Activator.CreateInstance(entryType);
                    TrySetPropertyOrField(entry, "userId", userId);
                    TrySetPropertyOrField(entry, "name", ResolveLeaderboardDisplayName(token, userId));
                    TrySetPropertyOrField(entry, "score", ReadJsonInt(token, "score"));
                    TrySetPropertyOrField(entry, "team", ReadJsonString(token, "team"));
                    entries.Add(entry);
                }

                InvokeInstanceMethod(uiManager, "UpdateLeaderboard", entries);
            }
            catch (Exception ex)
            {
                Debug.LogWarning("[CodexPatch] Personal leaderboard update failed: " + ex.Message);
            }
        }

        public static void HandleLivestreamChatMessage(object chatMessage)
        {
            try
            {
                if (chatMessage == null)
                {
                    return;
                }

                var chatUserId = Convert.ToString(ReadPropertyOrField(chatMessage, "userId"));
                var chatDisplayName = Convert.ToString(ReadPropertyOrField(chatMessage, "name"));
                RegisterLiveViewer(
                    chatUserId,
                    chatDisplayName,
                    Convert.ToString(ReadPropertyOrField(chatMessage, "team")));
                QueueViewerWelcome(chatUserId, chatDisplayName);

                if (_selectedRoundQuestions == null || _selectedRoundQuestions.Count == 0)
                {
                    return;
                }

                var gameFlowManagerType = FindAssemblyType("GameFlowManager");
                var gameFlowManager = gameFlowManagerType == null ? null : GetStaticPropertyValue(gameFlowManagerType, "Instance");
                if (gameFlowManager == null || !Convert.ToBoolean(GetInstancePropertyValue(gameFlowManager, "IsGameActive")))
                {
                    return;
                }

                var currentQuestionIndex = Convert.ToInt32(GetInstancePropertyValue(gameFlowManager, "CurrentQuestionIndex"));
                var question = GetSelectedQuestion(currentQuestionIndex);
                var answerText = Convert.ToString(ReadPropertyOrField(chatMessage, "text")) ?? string.Empty;
                if (question == null || !AnswerMatcher.Matches(answerText, question.Answer, question.Aliases))
                {
                    return;
                }

                if (Convert.ToBoolean(GetInstanceFieldValue(gameFlowManager, "_isWaitingForNextQuestion")))
                {
                    return;
                }

                var userId = FirstNonBlank(
                    Convert.ToString(ReadPropertyOrField(chatMessage, "userId")),
                    Convert.ToString(ReadPropertyOrField(chatMessage, "name")),
                    "viewer");
                var userName = FirstNonBlank(
                    Convert.ToString(ReadPropertyOrField(chatMessage, "name")),
                    ResolveKnownTwitchName(userId),
                    userId,
                    "viewer");
                var team = (Convert.ToString(ReadPropertyOrField(chatMessage, "team")) ?? string.Empty).Trim().ToLowerInvariant();

                if (!IsPersonalMode() && team != "red" && team != "blue")
                {
                    Debug.LogWarning(string.Format("[CodexPatch] Ignored correct answer from {0}: no red/blue team was supplied.", userName));
                    return;
                }

                lock (Gate)
                {
                    if (_answeredUsersQuestionIndex != currentQuestionIndex)
                    {
                        _answeredUsersQuestionIndex = currentQuestionIndex;
                        AnsweredUsersForQuestion.Clear();
                    }

                    if (!AnsweredUsersForQuestion.Add(userId))
                    {
                        return;
                    }

                    LocalScoreRecord score;
                    if (!LocalScores.TryGetValue(userId, out score))
                    {
                        score = new LocalScoreRecord { UserId = userId };
                        LocalScores[userId] = score;
                    }

                    score.Name = userName;
                    score.Team = team;
                    score.Score += 1;
                }

                var isFirst = !Convert.ToBoolean(GetInstanceFieldValue(gameFlowManager, "hasFirstAnswerInCurrentQuestion"));
                if (IsPersonalMode())
                {
                    HandleLocalPersonalCorrect(gameFlowManager, currentQuestionIndex, userName, isFirst);
                }
                else
                {
                    HandleLocalTeamCorrect(gameFlowManager, currentQuestionIndex, userName, team, isFirst);
                }

                RenderLocalLeaderboard();
                Debug.Log(string.Format(
                    "[CodexPatch] Local answer matched question {0}: {1} ({2}).",
                    currentQuestionIndex,
                    userName,
                    IsPersonalMode() ? "personal" : team));
            }
            catch (Exception ex)
            {
                Debug.LogWarning("[CodexPatch] Local Twitch answer handling failed: " + ex);
            }
        }

        public static void HandleLivestreamUserJoined(object userJoinedMessage)
        {
            try
            {
                if (userJoinedMessage == null)
                {
                    return;
                }

                var joinedUserId = Convert.ToString(ReadPropertyOrField(userJoinedMessage, "userId"));
                RegisterLiveViewer(
                    joinedUserId,
                    null,
                    Convert.ToString(ReadPropertyOrField(userJoinedMessage, "team")));
                QueueViewerWelcome(joinedUserId, null);
            }
            catch (Exception ex)
            {
                Debug.LogWarning("[CodexPatch] Live viewer join tracking failed: " + ex.Message);
            }
        }

        private static void QueueViewerWelcome(string userId, string displayName)
        {
            var audioDriver = _livestreamAudioDriver;
            if (audioDriver != null)
            {
                audioDriver.RequestViewerWelcome(userId, displayName);
            }
        }

        private static void QueueFirstCorrectSpeech(object gameFlowManager, int questionIndex, string displayName)
        {
            var audioDriver = _livestreamAudioDriver;
            if (audioDriver == null)
            {
                return;
            }

            var selectedQuestion = GetSelectedQuestion(questionIndex);
            var questionIdentity = selectedQuestion == null ? string.Empty : selectedQuestion.Id;
            var remoteStartTime = gameFlowManager == null
                ? string.Empty
                : Convert.ToString(GetInstanceFieldValue(gameFlowManager, "questionStartTime"));
            var token = string.Format(
                "{0}:{1}:{2}:{3}",
                _roundSerial,
                questionIndex,
                questionIdentity ?? string.Empty,
                remoteStartTime ?? string.Empty);
            audioDriver.QueueFirstCorrect(token, displayName);
        }

        private static void RegisterLiveViewer(string userId, string name, string team)
        {
            var resolvedId = FirstNonBlank(userId, name);
            if (string.IsNullOrWhiteSpace(resolvedId))
            {
                return;
            }

            lock (Gate)
            {
                LiveViewerRecord viewer;
                if (!LiveViewers.TryGetValue(resolvedId, out viewer))
                {
                    viewer = new LiveViewerRecord { UserId = resolvedId };
                    LiveViewers[resolvedId] = viewer;
                }

                viewer.Name = FirstNonBlank(name, ResolveKnownTwitchName(resolvedId), viewer.Name, resolvedId);
                viewer.Team = FirstNonBlank(team, viewer.Team);
                viewer.LastSeenUtc = DateTime.UtcNow;
            }
        }

        private static void SyncLiveViewersFromKnownNames()
        {
            try
            {
                var uiManagerType = FindAssemblyType("UIManager");
                var uiManager = uiManagerType == null ? null : GetStaticPropertyValue(uiManagerType, "Instance");
                var names = uiManager == null ? null : GetInstanceFieldValue(uiManager, "userNameDict") as IDictionary<string, string>;
                lock (Gate)
                {
                    if (names != null)
                    {
                        foreach (var viewer in LiveViewers.Values)
                        {
                            string displayName;
                            if (!string.IsNullOrWhiteSpace(viewer.UserId)
                                && names.TryGetValue(viewer.UserId, out displayName)
                                && !string.IsNullOrWhiteSpace(displayName))
                            {
                                viewer.Name = displayName.Trim();
                            }
                        }
                    }

                    foreach (var score in LocalScores.Values)
                    {
                        if (string.IsNullOrWhiteSpace(score.UserId))
                        {
                            continue;
                        }

                        LiveViewerRecord viewer;
                        if (!LiveViewers.TryGetValue(score.UserId, out viewer))
                        {
                            viewer = new LiveViewerRecord { UserId = score.UserId };
                            LiveViewers[score.UserId] = viewer;
                        }
                        viewer.Name = FirstNonBlank(score.Name, viewer.Name, score.UserId);
                        viewer.Team = FirstNonBlank(score.Team, viewer.Team);
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning("[CodexPatch] Live viewer roster sync failed: " + ex.Message);
            }
        }

        private static List<LiveViewerRecord> GetLiveViewerSnapshot()
        {
            SyncLiveViewersFromKnownNames();
            lock (Gate)
            {
                return LiveViewers.Values
                    .Select(viewer => new LiveViewerRecord
                    {
                        UserId = viewer.UserId,
                        Name = viewer.Name,
                        Team = viewer.Team,
                        LastSeenUtc = viewer.LastSeenUtc,
                    })
                    .OrderBy(viewer => string.Equals(viewer.Team, "red", StringComparison.OrdinalIgnoreCase) ? 0
                        : (string.Equals(viewer.Team, "blue", StringComparison.OrdinalIgnoreCase) ? 1 : 2))
                    .ThenBy(viewer => FirstNonBlank(viewer.Name, viewer.UserId), StringComparer.OrdinalIgnoreCase)
                    .ToList();
            }
        }

        public static bool AnswerMatchesForTest(string message, string answer, string aliasesJson)
        {
            var aliases = new List<string>();
            if (!string.IsNullOrWhiteSpace(aliasesJson))
            {
                var parsed = JArray.Parse(aliasesJson);
                aliases.AddRange(parsed.Values<string>().Where(value => !string.IsNullOrWhiteSpace(value)));
            }

            return AnswerMatcher.Matches(message, answer, aliases);
        }

        private static void HandleLocalPersonalCorrect(object gameFlowManager, int questionIndex, string userName, bool isFirst)
        {
            if (isFirst && questionIndex == Convert.ToInt32(GetInstancePropertyValue(gameFlowManager, "CurrentQuestionIndex")))
            {
                TryInvokeInstanceMethod(gameFlowManager, "StopCountdown");
                TrySetPropertyOrField(gameFlowManager, "hasFirstAnswerInCurrentQuestion", true);
                QueueFirstCorrectSpeech(gameFlowManager, questionIndex, userName);
                KeepPersonalHealthNeutral(gameFlowManager);
                ShowPersonalMessage(gameFlowManager, string.Format("Viewer {0} answered correctly!", userName), 3f);
                BeginNextQuestion(gameFlowManager);
                return;
            }

            ShowPersonalMessage(gameFlowManager, string.Format("Viewer {0} answered correctly!", userName), 2f);
        }

        private static void HandleLocalTeamCorrect(object gameFlowManager, int questionIndex, string userName, string team, bool isFirst)
        {
            if (!isFirst || questionIndex != Convert.ToInt32(GetInstancePropertyValue(gameFlowManager, "CurrentQuestionIndex")))
            {
                ShowPersonalMessage(gameFlowManager, string.Format("Viewer {0} ({1}) answered correctly!", userName, team.ToUpperInvariant()), 2f);
                return;
            }

            TryInvokeInstanceMethod(gameFlowManager, "StopCountdown");
            TrySetPropertyOrField(gameFlowManager, "hasFirstAnswerInCurrentQuestion", true);
            QueueFirstCorrectSpeech(gameFlowManager, questionIndex, userName);

            var elapsed = Math.Max(0f, Time.time - Convert.ToSingle(GetInstanceFieldValue(gameFlowManager, "questionStartTime")));
            var timeLimit = Math.Max(0.1f, Convert.ToSingle(GetInstanceFieldValue(gameFlowManager, "timeLimitPerQuestion")));
            var baseDamage = Math.Max(1f, Convert.ToSingle(GetInstanceFieldValue(gameFlowManager, "baseDamage")));
            var progress = Mathf.Clamp01(elapsed / timeLimit);
            var damage = Mathf.Max(1, Mathf.RoundToInt(baseDamage * Mathf.Pow(1f - progress, 3f)));
            var redHealth = Convert.ToInt32(GetInstanceFieldValue(gameFlowManager, "redHealth"));
            var blueHealth = Convert.ToInt32(GetInstanceFieldValue(gameFlowManager, "blueHealth"));

            if (team == "red")
            {
                blueHealth = Math.Max(0, blueHealth - damage);
                TrySetPropertyOrField(gameFlowManager, "blueHealth", blueHealth);
            }
            else
            {
                redHealth = Math.Max(0, redHealth - damage);
                TrySetPropertyOrField(gameFlowManager, "redHealth", redHealth);
            }

            var onHealthChanged = GetInstanceFieldValue(gameFlowManager, "OnHealthChanged") as Delegate;
            if (onHealthChanged != null)
            {
                onHealthChanged.DynamicInvoke(redHealth, blueHealth);
            }

            var target = team == "red" ? "BLUE" : "RED";
            ShowPersonalMessage(
                gameFlowManager,
                string.Format("Viewer {0} ({1}) answered correctly! {2} takes {3} damage.", userName, team.ToUpperInvariant(), target, damage),
                3f);

            if (redHealth <= 0 || blueHealth <= 0)
            {
                TryInvokeInstanceMethod(gameFlowManager, "CheckGameOver");
                return;
            }

            BeginNextQuestion(gameFlowManager);
        }

        private static void BeginNextQuestion(object gameFlowManager)
        {
            TrySetPropertyOrField(gameFlowManager, "_isWaitingForNextQuestion", true);
            var routine = InvokeInstanceMethod(gameFlowManager, "DelayedNextQuestion", 0f) as IEnumerator;
            var behaviour = gameFlowManager as MonoBehaviour;
            if (routine != null && behaviour != null)
            {
                behaviour.StartCoroutine(routine);
            }
        }

        private static bool TryStartLocalRound(string reason)
        {
            try
            {
                var gameFlowManagerType = FindAssemblyType("GameFlowManager");
                var gameFlowManager = gameFlowManagerType == null ? null : GetStaticPropertyValue(gameFlowManagerType, "Instance");
                if (gameFlowManager == null)
                {
                    return false;
                }

                EnsureRoundSelectionPrepared(true);
                if (_selectedRoundQuestions == null || _selectedRoundQuestions.Count == 0)
                {
                    return false;
                }

                InvokeInstanceMethod(
                    gameFlowManager,
                    "GameStarted",
                    "local-" + DateTime.UtcNow.Ticks,
                    _selectedRoundQuestions.Count);
                EnsureRoundQuestionsAppliedToLiveGame();
                TryInvokeInstanceMethod(gameFlowManager, "UpdateCurrentQuestionText");
                TryInvokeInstanceMethod(gameFlowManager, "StartCountdown");
                RefreshQuestionImage();

                var active = Convert.ToBoolean(GetInstancePropertyValue(gameFlowManager, "IsGameActive"));
                if (active)
                {
                    Debug.Log("[CodexPatch] Local continuity round started: " + reason);
                }

                return active;
            }
            catch (Exception ex)
            {
                Debug.LogWarning("[CodexPatch] Local continuity start failed: " + ex.Message);
                return false;
            }
        }

        private static bool TryAdvanceQuestionLocally(object gameFlowManager, int nextQuestionIndex, string reason)
        {
            try
            {
                if (gameFlowManager == null || _selectedRoundQuestions == null || _selectedRoundQuestions.Count == 0)
                {
                    return false;
                }

                if (nextQuestionIndex < 1 || nextQuestionIndex > _selectedRoundQuestions.Count)
                {
                    return false;
                }

                TryInvokeInstanceMethod(gameFlowManager, "StopCountdown");
                TrySetPropertyOrField(gameFlowManager, "_isWaitingForNextQuestion", false);
                TrySetPropertyOrField(gameFlowManager, "hasFirstAnswerInCurrentQuestion", false);
                TrySetPropertyOrField(gameFlowManager, "CurrentQuestionIndex", nextQuestionIndex);
                TrySetPropertyOrField(gameFlowManager, "currentQuestionIndex", nextQuestionIndex);
                TrySetPropertyOrField(gameFlowManager, "questionStartTime", Time.time);
                TryInvokeInstanceMethod(gameFlowManager, "UpdateCurrentQuestionText");
                TryInvokeInstanceMethod(gameFlowManager, "StartCountdown");
                _lastAppliedQuestionIndex = -1;
                _lastAppliedQuestionId = null;
                RefreshQuestionImage();
                Debug.Log(string.Format("[CodexPatch] Continuity watchdog advanced to question {0}: {1}", nextQuestionIndex, reason));
                return true;
            }
            catch (Exception ex)
            {
                Debug.LogWarning("[CodexPatch] Local question advance failed: " + ex.Message);
                return false;
            }
        }

        private static bool TryEndRoundLocally(object gameFlowManager, string reason)
        {
            try
            {
                if (gameFlowManager == null)
                {
                    return false;
                }

                TryInvokeInstanceMethod(gameFlowManager, "StopCountdown");
                TrySetPropertyOrField(gameFlowManager, "_isWaitingForNextQuestion", false);
                if (!TryInvokeInstanceMethod(gameFlowManager, "GameEnded"))
                {
                    TrySetPropertyOrField(gameFlowManager, "IsGameActive", false);
                }

                Debug.Log("[CodexPatch] Continuity watchdog completed the round locally: " + reason);
                return true;
            }
            catch (Exception ex)
            {
                Debug.LogWarning("[CodexPatch] Local round completion failed: " + ex.Message);
                return false;
            }
        }

        private static void RenderLocalLeaderboard()
        {
            var uiManagerType = FindAssemblyType("UIManager");
            var uiManager = uiManagerType == null ? null : GetStaticPropertyValue(uiManagerType, "Instance");
            var entryType = uiManagerType == null ? null : uiManagerType.GetNestedType("LeaderboardEntry", BindingFlags.Public | BindingFlags.NonPublic);
            if (uiManager == null || entryType == null)
            {
                return;
            }

            var listType = typeof(List<>).MakeGenericType(entryType);
            var entries = (IList)Activator.CreateInstance(listType);
            List<LocalScoreRecord> scores;
            lock (Gate)
            {
                scores = LocalScores.Values
                    .OrderByDescending(score => score.Score)
                    .ThenBy(score => score.Name, StringComparer.OrdinalIgnoreCase)
                    .ToList();
            }

            foreach (var score in scores)
            {
                var entry = Activator.CreateInstance(entryType);
                TrySetPropertyOrField(entry, "userId", score.UserId);
                TrySetPropertyOrField(entry, "name", score.Name);
                TrySetPropertyOrField(entry, "score", score.Score);
                TrySetPropertyOrField(entry, "team", score.Team);
                entries.Add(entry);
            }

            InvokeInstanceMethod(uiManager, "UpdateLeaderboard", entries);
        }


        private static string ResolveAnswerDisplayName(object answerResult)
        {
            var userId = Convert.ToString(ReadPropertyOrField(answerResult, "userId")) ?? string.Empty;
            return FirstNonBlank(
                Convert.ToString(ReadPropertyOrField(answerResult, "name")),
                Convert.ToString(ReadPropertyOrField(answerResult, "userName")),
                Convert.ToString(ReadPropertyOrField(answerResult, "username")),
                Convert.ToString(ReadPropertyOrField(answerResult, "displayName")),
                Convert.ToString(ReadPropertyOrField(answerResult, "twitchUsername")),
                ResolveKnownTwitchName(userId),
                userId,
                "viewer");
        }

        private static string ResolveLeaderboardDisplayName(JToken token, string userId)
        {
            return FirstNonBlank(
                ReadJsonString(token, "name"),
                ReadJsonString(token, "userName"),
                ReadJsonString(token, "username"),
                ReadJsonString(token, "displayName"),
                ReadJsonString(token, "twitchUsername"),
                ResolveKnownTwitchName(userId));
        }

        private static string ResolveKnownTwitchName(string userId)
        {
            if (string.IsNullOrWhiteSpace(userId))
            {
                return string.Empty;
            }

            var uiManagerType = FindAssemblyType("UIManager");
            var uiManager = uiManagerType == null ? null : GetStaticPropertyValue(uiManagerType, "Instance");
            var userNameDict = uiManager == null ? null : GetInstanceFieldValue(uiManager, "userNameDict") as IDictionary<string, string>;
            if (userNameDict != null && userNameDict.ContainsKey(userId))
            {
                return userNameDict[userId];
            }

            return string.Empty;
        }

        private static void RefreshTwitchUserNamesThenLeaderboard(object gameFlowManager)
        {
            try
            {
                var httpCenterType = FindAssemblyType("HttpCenter");
                var httpCenter = httpCenterType == null ? null : GetStaticPropertyValue(httpCenterType, "Instance");
                var demoUiType = FindAssemblyType("GameApiDemoUI");
                var demoUi = demoUiType == null ? null : GetStaticPropertyValue(demoUiType, "Instance");
                if (httpCenter == null || demoUi == null)
                {
                    TryInvokeInstanceMethod(gameFlowManager, "RefreshLeaderboard");
                    return;
                }

                Action<string> onTeams = teamsJson =>
                {
                    TryInvokeInstanceMethod(demoUi, "UpdateUserNamesFromTeams", teamsJson);
                    TryInvokeInstanceMethod(gameFlowManager, "RefreshLeaderboard");
                };
                Action<string> onError = error =>
                {
                    Debug.LogWarning("[CodexPatch] Could not refresh Twitch names before leaderboard: " + error);
                    TryInvokeInstanceMethod(gameFlowManager, "RefreshLeaderboard");
                };

                var method = httpCenter.GetType().GetMethod("GetAsync", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (method == null)
                {
                    TryInvokeInstanceMethod(gameFlowManager, "RefreshLeaderboard");
                    return;
                }

                method.Invoke(httpCenter, new object[] { "/game/teams", onTeams, onError, false });
            }
            catch (Exception ex)
            {
                Debug.LogWarning("[CodexPatch] Twitch-name refresh failed: " + ex.Message);
                TryInvokeInstanceMethod(gameFlowManager, "RefreshLeaderboard");
            }
        }

        private static string ReadJsonString(JToken token, string name)
        {
            var value = token == null ? null : token[name];
            return value == null ? string.Empty : value.ToString();
        }

        private static int ReadJsonInt(JToken token, string name)
        {
            var value = token == null ? null : token[name];
            return value == null ? 0 : value.Value<int>();
        }

        private static string FirstNonBlank(params string[] values)
        {
            foreach (var value in values)
            {
                if (!string.IsNullOrWhiteSpace(value))
                {
                    return value;
                }
            }

            return string.Empty;
        }

        public static void HandlePersonalCheckGameOver(object gameFlowManager)
        {
            try
            {
                if (gameFlowManager == null || !Convert.ToBoolean(GetInstancePropertyValue(gameFlowManager, "IsGameActive")))
                {
                    return;
                }

                var currentQuestionIndex = Convert.ToInt32(GetInstancePropertyValue(gameFlowManager, "CurrentQuestionIndex"));
                var totalQuestions = Convert.ToInt32(GetInstancePropertyValue(gameFlowManager, "TotalQuestions"));
                var gameOverMessage = string.Empty;

                if (IsPersonalMode())
                {
                    if (currentQuestionIndex < totalQuestions)
                    {
                        return;
                    }

                    gameOverMessage = "Round complete!";
                }
                else
                {
                    var redHealth = Convert.ToInt32(GetInstanceFieldValue(gameFlowManager, "redHealth"));
                    var blueHealth = Convert.ToInt32(GetInstanceFieldValue(gameFlowManager, "blueHealth"));
                    if (redHealth <= 0 && blueHealth <= 0)
                    {
                        gameOverMessage = "Draw! Both teams were eliminated.";
                    }
                    else if (redHealth <= 0)
                    {
                        gameOverMessage = "Blue Team wins!";
                    }
                    else if (blueHealth <= 0)
                    {
                        gameOverMessage = "Red Team wins!";
                    }
                    else if (currentQuestionIndex < totalQuestions)
                    {
                        return;
                    }
                    else if (redHealth > blueHealth)
                    {
                        gameOverMessage = "Red Team wins!";
                    }
                    else if (blueHealth > redHealth)
                    {
                        gameOverMessage = "Blue Team wins!";
                    }
                    else
                    {
                        gameOverMessage = "Draw!";
                    }
                }

                ShowGameOverMessage(gameFlowManager, gameOverMessage);
                TrySetPropertyOrField(gameFlowManager, "_isWaitingForNextQuestion", false);

                var autoEndCoroutine = GetInstanceFieldValue(gameFlowManager, "autoEndCoroutine") as Coroutine;
                var behaviour = gameFlowManager as MonoBehaviour;
                if (autoEndCoroutine != null && behaviour != null)
                {
                    behaviour.StopCoroutine(autoEndCoroutine);
                }

                var routine = InvokeInstanceMethod(gameFlowManager, "AutoEndGameAfterDelay", 10f) as IEnumerator;
                if (routine != null && behaviour != null)
                {
                    TrySetPropertyOrField(gameFlowManager, "autoEndCoroutine", behaviour.StartCoroutine(routine));
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning("[CodexPatch] Game-over handling failed: " + ex.Message);
            }
        }

        private static List<QuestionRecord> LoadQuestionPool()
        {
            var workspaceRoot = GetWorkspaceRoot();
            var preferredPath = Path.Combine(workspaceRoot, "stream_questions_100.json");
            var fallbackPath = Path.Combine(workspaceRoot, "questions.json");
            var sourcePath = File.Exists(preferredPath) ? preferredPath : fallbackPath;

            if (!File.Exists(sourcePath))
            {
                throw new FileNotFoundException("Question source file was not found.", sourcePath);
            }

            var root = JObject.Parse(File.ReadAllText(sourcePath));
            var items = root["items"] as JArray;
            if (items == null)
            {
                return new List<QuestionRecord>();
            }

            var parsed = items
                .Take(MaxQuestionPool)
                .Select(ParseQuestionRecord)
                .Where(record => record != null && !string.IsNullOrWhiteSpace(record.Id) && !string.IsNullOrWhiteSpace(record.Question))
                .ToList();

            var ready = parsed
                .Where(record => FindStreamReadyImagePath(record.Id) != null)
                .ToList();

            var excludedCount = parsed.Count - ready.Count;
            if (excludedCount > 0)
            {
                Debug.LogWarning(string.Format(
                    "[CodexPatch] Excluded {0} questions whose images were missing or below the stream-quality threshold.",
                    excludedCount));
            }

            return ready;
        }

        private static string FindStreamReadyImagePath(string questionId)
        {
            if (string.IsNullOrWhiteSpace(questionId))
            {
                return null;
            }

            var basePath = Path.Combine(GetWorkspaceRoot(), "question_images");
            var candidates = new[]
            {
                Path.Combine(basePath, questionId + ".png"),
                Path.Combine(basePath, questionId + ".jpg"),
                Path.Combine(basePath, questionId + ".jpeg"),
                Path.Combine(basePath, questionId + ".webp")
            };

            return candidates.FirstOrDefault(path =>
            {
                try
                {
                    return File.Exists(path) && new FileInfo(path).Length >= MinStreamImageBytes;
                }
                catch
                {
                    return false;
                }
            });
        }

        private static void EnsureRoundSelectionPrepared(bool forceRefresh)
        {
            if (!forceRefresh && _selectedRoundQuestions != null && _selectedRoundQuestions.Count > 0)
            {
                return;
            }

            var pool = LoadQuestionPool();
            if (pool.Count == 0)
            {
                Debug.LogWarning("[CodexPatch] No questions were loaded from disk.");
                return;
            }

            ResetLocalRoundState();
            _roundSerial++;

            var selected = pool
                .OrderBy(_ => Random.Next())
                .Take(Math.Min(RoundSize, pool.Count))
                .ToList();

            ApplyRoundQuestions(selected);
            _lastAppliedQuestionId = null;
            _lastAppliedQuestionIndex = -1;

            Debug.Log(string.Format("[CodexPatch] Prepared {0} round questions from a pool of {1}.", selected.Count, pool.Count));
        }

        private static QuestionRecord ParseQuestionRecord(JToken token)
        {
            var meta = token["meta"];

            return new QuestionRecord
            {
                Id = ValueOrEmpty(token["id"]),
                Question = NormalizeQuestionText(ValueOrEmpty(token["question"]), 220),
                Answer = ValueOrEmpty(token["answer"]),
                Aliases = (token["aliases"] as JArray) == null
                    ? new List<string>()
                    : ((JArray)token["aliases"]).Values<string>().Where(value => !string.IsNullOrWhiteSpace(value)).ToList(),
                Category = NormalizeLabelText(ValueOrEmpty(meta == null ? null : meta["category"]), 28),
                Era = NormalizeLabelText(ValueOrEmpty(meta == null ? null : meta["era"]), 24),
                Source = NormalizeLabelText(ValueOrEmpty(meta == null ? null : meta["source"]), 16)
            };
        }

        private static void ApplyRoundQuestions(List<QuestionRecord> selected)
        {
            var mockQuestionType = ResolveMockQuestionType();
            var mockApiHandlerType = mockQuestionType == null ? null : mockQuestionType.DeclaringType;

            if (mockApiHandlerType == null || mockQuestionType == null)
            {
                throw new InvalidOperationException("Assembly-CSharp runtime types could not be resolved.");
            }

            var stateField = mockApiHandlerType.GetField("_state", BindingFlags.Static | BindingFlags.NonPublic);
            if (stateField == null)
            {
                throw new InvalidOperationException("MockApiHandler._state could not be found.");
            }

            var state = stateField.GetValue(null);
            if (state == null)
            {
                throw new InvalidOperationException("MockApiHandler._state is null.");
            }

            lock (Gate)
            {
                _selectedRoundQuestions = selected
                    .Select(record => new QuestionRecord
                    {
                        Id = record.Id,
                        Question = record.Question,
                        Answer = record.Answer,
                        Aliases = record.Aliases == null ? new List<string>() : new List<string>(record.Aliases),
                        Category = record.Category,
                        Era = record.Era,
                        Source = record.Source
                    })
                    .ToList();

                var typedList = CreateMockQuestionList(mockQuestionType, _selectedRoundQuestions);
                SetPropertyOrField(state, "questions", typedList);
                SetPropertyOrField(state, "currentQuestionIndex", 1);
            }

        }

        private static object CreateMockQuestion(Type mockQuestionType, QuestionRecord record, int index)
        {
            var question = Activator.CreateInstance(mockQuestionType);
            var meta = CreateQuestionMetaValue(mockQuestionType, record);

            TrySetPropertyOrField(question, "index", index);
            SetPropertyOrField(question, "id", record.Id);
            SetPropertyOrField(question, "question", record.Question);
            TrySetPropertyOrField(question, "answer", record.Answer ?? string.Empty);
            SetPropertyOrField(question, "meta", meta);

            return question;
        }

        private static object CreateQuestionMetaValue(Type questionType, QuestionRecord record)
        {
            var category = record.Category ?? string.Empty;
            var era = record.Era ?? string.Empty;
            var source = string.IsNullOrWhiteSpace(record.Source) ? "codex" : record.Source;
            var metaMember = (MemberInfo)questionType.GetProperty("meta", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                ?? questionType.GetField("meta", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            var metaType = metaMember is PropertyInfo
                ? ((PropertyInfo)metaMember).PropertyType
                : (metaMember is FieldInfo ? ((FieldInfo)metaMember).FieldType : typeof(object));

            if (metaType == typeof(JObject) || metaType == typeof(object))
            {
                var json = new JObject();
                json["category"] = category;
                json["era"] = era;
                json["source"] = source;
                return json;
            }

            var typedMeta = Activator.CreateInstance(metaType);
            TrySetPropertyOrField(typedMeta, "category", category);
            TrySetPropertyOrField(typedMeta, "era", era);
            TrySetPropertyOrField(typedMeta, "source", source);
            return typedMeta;
        }

        private static Type ResolveMockQuestionType()
        {
            var mockApiHandlerType = FindAssemblyType("MockApiHandler");
            return mockApiHandlerType == null
                ? null
                : mockApiHandlerType.GetNestedType("Question", BindingFlags.Public | BindingFlags.NonPublic);
        }

        private static IList CreateMockQuestionList(Type mockQuestionType, IList<QuestionRecord> selected)
        {
            var typedListType = typeof(List<>).MakeGenericType(mockQuestionType);
            var typedList = (IList)Activator.CreateInstance(typedListType);

            for (var i = 0; i < selected.Count; i++)
            {
                typedList.Add(CreateMockQuestion(mockQuestionType, selected[i], i + 1));
            }

            return typedList;
        }

        private static Type ResolveLiveQuestionType()
        {
            return FindAssemblyType("QuestionData");
        }

        private static void EnsureRoundQuestionsAppliedToLiveGame()
        {
            if (_selectedRoundQuestions == null || _selectedRoundQuestions.Count == 0)
            {
                return;
            }

            try
            {
                var mockQuestionType = ResolveMockQuestionType();
                if (mockQuestionType == null)
                {
                    return;
                }

                ApplyRoundQuestionsToLiveGame(mockQuestionType);
            }
            catch (Exception ex)
            {
                Debug.LogWarning("[CodexPatch] EnsureRoundQuestionsAppliedToLiveGame failed: " + ex.Message);
            }
        }

        private static void ApplyRoundQuestionsToLiveGame(Type mockQuestionType)
        {
            var gameFlowManagerType = FindAssemblyType("GameFlowManager");
            if (gameFlowManagerType == null)
            {
                return;
            }

            var gameFlowManager = GetStaticPropertyValue(gameFlowManagerType, "Instance");
            if (gameFlowManager == null)
            {
                return;
            }

            var liveQuestions = GetInstanceFieldValue(gameFlowManager, "questions") as IList;
            if (LiveQuestionsMatchSelected(liveQuestions))
            {
                return;
            }

            lock (Gate)
            {
                var liveQuestionType = ResolveLiveQuestionType();
                if (liveQuestionType == null)
                {
                    return;
                }

                var typedList = CreateMockQuestionList(liveQuestionType, _selectedRoundQuestions);
                if (!TryInvokeInstanceMethod(gameFlowManager, "SetQuestions", typedList))
                {
                    SetPropertyOrField(gameFlowManager, "questions", typedList);
                }

                TrySetPropertyOrField(gameFlowManager, "TotalQuestions", typedList.Count);
                TrySetPropertyOrField(gameFlowManager, "totalQuestions", typedList.Count);

                var currentQuestionIndex = 1;
                var currentValue = GetInstancePropertyValue(gameFlowManager, "CurrentQuestionIndex");
                if (currentValue != null)
                {
                    currentQuestionIndex = Math.Max(1, Convert.ToInt32(currentValue));
                }

                if (currentQuestionIndex > typedList.Count)
                {
                    currentQuestionIndex = 1;
                }

                TrySetPropertyOrField(gameFlowManager, "CurrentQuestionIndex", currentQuestionIndex);
                TrySetPropertyOrField(gameFlowManager, "currentQuestionIndex", currentQuestionIndex);
                TryInvokeInstanceMethod(gameFlowManager, "UpdateCurrentQuestionText");
            }
        }

        private static bool LiveQuestionsMatchSelected(IList liveQuestions)
        {
            if (liveQuestions == null || liveQuestions.Count != _selectedRoundQuestions.Count)
            {
                return false;
            }

            for (var i = 0; i < liveQuestions.Count; i++)
            {
                var liveId = Convert.ToString(ReadPropertyOrField(liveQuestions[i], "id")) ?? string.Empty;
                if (!string.Equals(liveId, _selectedRoundQuestions[i].Id, StringComparison.Ordinal))
                {
                    return false;
                }
            }

            return true;
        }

        private static QuestionRecord GetSelectedQuestion(int questionIndex)
        {
            if (_selectedRoundQuestions == null || questionIndex < 1 || questionIndex > _selectedRoundQuestions.Count)
            {
                return null;
            }

            return _selectedRoundQuestions[questionIndex - 1];
        }

        private static Sprite LoadSpriteForQuestion(string questionId)
        {
            lock (Gate)
            {
                Sprite cached;
                if (SpriteCache.TryGetValue(questionId, out cached))
                {
                    TouchSpriteCacheEntry(questionId);
                    return cached;
                }

                var imagePath = FindStreamReadyImagePath(questionId);
                if (string.IsNullOrWhiteSpace(imagePath))
                {
                    return null;
                }

                var bytes = File.ReadAllBytes(imagePath);
                var texture = new Texture2D(2, 2, TextureFormat.RGBA32, false);
                if (!ImageConversion.LoadImage(texture, bytes))
                {
                    UnityEngine.Object.Destroy(texture);
                    return null;
                }

                if (texture.width < MinStreamImageWidth || texture.height < MinStreamImageHeight)
                {
                    Debug.LogWarning(string.Format(
                        "[CodexPatch] Rejected undersized question image {0} ({1}x{2}).",
                        questionId,
                        texture.width,
                        texture.height));
                    UnityEngine.Object.Destroy(texture);
                    return null;
                }

                texture.name = "QuestionSprite_" + questionId;
                var sprite = Sprite.Create(
                    texture,
                    new Rect(0, 0, texture.width, texture.height),
                    new Vector2(0.5f, 0.5f),
                    100f);

                sprite.name = questionId;
                SpriteCache[questionId] = sprite;
                TouchSpriteCacheEntry(questionId);
                TrimSpriteCache();
                return sprite;
            }
        }

        private static void TouchSpriteCacheEntry(string questionId)
        {
            SpriteCacheOrder.RemoveAll(key => string.Equals(key, questionId, StringComparison.OrdinalIgnoreCase));
            SpriteCacheOrder.Add(questionId);
        }

        private static void TrimSpriteCache()
        {
            while (SpriteCacheOrder.Count > MaxCachedQuestionSprites)
            {
                var oldestKey = SpriteCacheOrder[0];
                SpriteCacheOrder.RemoveAt(0);

                Sprite oldestSprite;
                if (!SpriteCache.TryGetValue(oldestKey, out oldestSprite))
                {
                    continue;
                }

                SpriteCache.Remove(oldestKey);
                if (oldestSprite != null)
                {
                    var texture = oldestSprite.texture;
                    UnityEngine.Object.Destroy(oldestSprite);
                    if (texture != null)
                    {
                        UnityEngine.Object.Destroy(texture);
                    }
                }
            }
        }

        private static ProductSnapshot CaptureSnapshot()
        {
            var snapshot = new ProductSnapshot
            {
                TotalQuestions = RoundSize,
                MaxHealth = 100
            };

            try
            {
                var gameFlowManagerType = FindAssemblyType("GameFlowManager");
                var uiManagerType = FindAssemblyType("UIManager");
                if (gameFlowManagerType == null || uiManagerType == null)
                {
                    return snapshot;
                }

                var gameFlowManager = GetStaticPropertyValue(gameFlowManagerType, "Instance");
                var uiManager = GetStaticPropertyValue(uiManagerType, "Instance");

                if (gameFlowManager != null)
                {
                    snapshot.IsGameActive = Convert.ToBoolean(GetInstancePropertyValue(gameFlowManager, "IsGameActive"));
                    snapshot.CurrentQuestionIndex = Convert.ToInt32(GetInstancePropertyValue(gameFlowManager, "CurrentQuestionIndex"));
                    if (snapshot.CurrentQuestionIndex < 1 && _selectedRoundQuestions != null && _selectedRoundQuestions.Count > 0)
                    {
                        snapshot.CurrentQuestionIndex = 1;
                    }
                    snapshot.TotalQuestions = Math.Max(1, Convert.ToInt32(GetInstancePropertyValue(gameFlowManager, "TotalQuestions")));
                    snapshot.BlueHealth = Convert.ToInt32(GetInstanceFieldValue(gameFlowManager, "blueHealth"));
                    snapshot.RedHealth = Convert.ToInt32(GetInstanceFieldValue(gameFlowManager, "redHealth"));
                    snapshot.MaxHealth = Math.Max(1, Convert.ToInt32(GetInstanceFieldValue(gameFlowManager, "maxHealth")));

                    var selectedQuestion = GetSelectedQuestion(snapshot.CurrentQuestionIndex);
                    var questions = GetInstanceFieldValue(gameFlowManager, "questions") as IList;
                    if (selectedQuestion != null)
                    {
                        snapshot.QuestionId = selectedQuestion.Id ?? string.Empty;
                        snapshot.QuestionText = selectedQuestion.Question ?? string.Empty;
                        snapshot.Category = selectedQuestion.Category ?? string.Empty;
                        snapshot.Era = selectedQuestion.Era ?? string.Empty;
                        snapshot.Source = selectedQuestion.Source ?? string.Empty;
                        snapshot.TotalQuestions = Math.Max(snapshot.TotalQuestions, _selectedRoundQuestions.Count);
                    }
                    else if (questions != null && snapshot.CurrentQuestionIndex >= 1 && snapshot.CurrentQuestionIndex <= questions.Count)
                    {
                        var question = questions[snapshot.CurrentQuestionIndex - 1];
                        snapshot.QuestionId = Convert.ToString(ReadPropertyOrField(question, "id")) ?? string.Empty;
                        snapshot.QuestionText = Convert.ToString(ReadPropertyOrField(question, "question")) ?? string.Empty;
                        snapshot.Meta = ReadPropertyOrField(question, "meta");
                    }

                    if (questions != null && questions.Count > 0)
                    {
                        snapshot.TotalQuestions = Math.Max(snapshot.TotalQuestions, questions.Count);
                    }
                }

                if (uiManager != null)
                {
                    var timerText = ReadPropertyOrField(uiManager, "timerText") as Text;
                    var subjectText = ReadPropertyOrField(uiManager, "SubjectText") as Text;
                    var questionText = ReadPropertyOrField(uiManager, "QUESTIONText") as Text;
                    var infoText = ReadPropertyOrField(uiManager, "infoText") as Text;

                    snapshot.TimerText = timerText == null ? string.Empty : timerText.text;
                    snapshot.SubjectText = subjectText == null ? string.Empty : subjectText.text;
                    snapshot.DisplayQuestionText = questionText == null ? string.Empty : questionText.text;
                    snapshot.InfoText = infoText == null ? string.Empty : infoText.text;
                }

                if (snapshot.Meta != null)
                {
                    snapshot.Category = Convert.ToString(ReadPropertyOrField(snapshot.Meta, "category")) ?? string.Empty;
                    snapshot.Era = Convert.ToString(ReadPropertyOrField(snapshot.Meta, "era")) ?? string.Empty;
                    snapshot.Source = Convert.ToString(ReadPropertyOrField(snapshot.Meta, "source")) ?? string.Empty;
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning("[CodexPatch] Snapshot capture failed: " + ex.Message);
            }

            return snapshot;
        }

        private static string GetWorkspaceRoot()
        {
            var appRoot = Directory.GetParent(Application.dataPath).FullName;
            if (File.Exists(Path.Combine(appRoot, "questions.json")) || File.Exists(Path.Combine(appRoot, "stream_questions_100.json")))
            {
                return appRoot;
            }

            var parent = Directory.GetParent(appRoot);
            if (parent != null)
            {
                return parent.FullName;
            }

            return appRoot;
        }

        private static bool IsPersonalMode()
        {
            return !string.IsNullOrWhiteSpace(Application.dataPath)
                && Application.dataPath.IndexOf("nonteam", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static bool EnsureLivestreamBackendMode()
        {
            if (!IsPersonalMode() || _personalBackendConfigured)
            {
                return true;
            }

            try
            {
                var httpCenterType = FindAssemblyType("HttpCenter");
                var httpCenter = httpCenterType == null ? null : GetStaticPropertyValue(httpCenterType, "Instance");
                if (httpCenter == null)
                {
                    return false;
                }

                // The original HTTP backend has one global team-game state. A team round therefore
                // makes /game/start return GAME_ACTIVE to the non-team build. The packaged game
                // already includes MockApiHandler, whose state is isolated per Unity process; use it
                // for the solo round lifecycle while retaining the shared Twitch WebSocket/chat feed.
                SetPropertyOrField(httpCenter, "useMockData", true);
                _personalBackendConfigured = Convert.ToBoolean(ReadPropertyOrField(httpCenter, "useMockData"));
                if (_personalBackendConfigured)
                {
                    Debug.Log("[CodexPatch] Personal livestream backend ready (isolated round state; shared Twitch chat retained).");
                }

                return _personalBackendConfigured;
            }
            catch (Exception ex)
            {
                Debug.LogWarning("[CodexPatch] Personal livestream backend setup failed: " + ex.Message);
                return false;
            }
        }

        private static void ResetLocalRoundState()
        {
            lock (Gate)
            {
                LocalScores.Clear();
                AnsweredUsersForQuestion.Clear();
                _answeredUsersQuestionIndex = -1;
            }
        }

        private static Type FindAssemblyType(string typeName)
        {
            return AppDomain.CurrentDomain
                .GetAssemblies()
                .Where(assembly => string.Equals(assembly.GetName().Name, "Assembly-CSharp", StringComparison.OrdinalIgnoreCase))
                .Select(assembly => assembly.GetType(typeName, false))
                .FirstOrDefault(type => type != null);
        }

        private static object GetStaticPropertyValue(Type type, string name)
        {
            var property = type.GetProperty(name, BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
            return property == null ? null : property.GetValue(null, null);
        }

        private static object GetInstancePropertyValue(object instance, string name)
        {
            var property = instance.GetType().GetProperty(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            return property == null ? null : property.GetValue(instance, null);
        }

        private static object GetInstanceFieldValue(object instance, string name)
        {
            var field = instance.GetType().GetField(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            return field == null ? null : field.GetValue(instance);
        }

        private static object ReadPropertyOrField(object instance, string name)
        {
            var type = instance.GetType();
            var property = type.GetProperty(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (property != null)
            {
                return property.GetValue(instance, null);
            }

            var field = type.GetField(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            return field == null ? null : field.GetValue(instance);
        }

        private static void SetPropertyOrField(object instance, string name, object value)
        {
            var type = instance.GetType();
            var property = type.GetProperty(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (property != null)
            {
                property.SetValue(instance, value, null);
                return;
            }

            var field = type.GetField(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (field != null)
            {
                field.SetValue(instance, value);
                return;
            }

            throw new MissingFieldException(type.FullName, name);
        }

        private static void TrySetPropertyOrField(object instance, string name, object value)
        {
            try
            {
                SetPropertyOrField(instance, name, value);
            }
            catch (MissingFieldException)
            {
            }
        }

        private static bool TryInvokeInstanceMethod(object instance, string name, params object[] args)
        {
            var method = instance.GetType().GetMethod(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (method == null)
            {
                return false;
            }

            method.Invoke(instance, args);
            return true;
        }

        private static object InvokeInstanceMethod(object instance, string name, params object[] args)
        {
            var method = instance.GetType().GetMethod(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            return method == null ? null : method.Invoke(instance, args);
        }

        private static void KeepPersonalHealthNeutral(object gameFlowManager)
        {
            var maxHealth = Math.Max(1, Convert.ToInt32(GetInstanceFieldValue(gameFlowManager, "maxHealth")));
            TrySetPropertyOrField(gameFlowManager, "redHealth", maxHealth);
            TrySetPropertyOrField(gameFlowManager, "blueHealth", maxHealth);

            var onHealthChanged = GetInstanceFieldValue(gameFlowManager, "OnHealthChanged") as Delegate;
            if (onHealthChanged != null)
            {
                onHealthChanged.DynamicInvoke(maxHealth, maxHealth);
            }
        }

        private static void ShowPersonalMessage(object gameFlowManager, string message, float duration)
        {
            var uiManager = GetInstanceFieldValue(gameFlowManager, "uiManager");
            if (uiManager == null)
            {
                var uiManagerType = FindAssemblyType("UIManager");
                uiManager = uiManagerType == null ? null : GetStaticPropertyValue(uiManagerType, "Instance");
            }

            if (uiManager != null)
            {
                TryInvokeInstanceMethod(uiManager, "ShowMessage", message, duration);
            }
        }

        private static void ShowGameOverMessage(object gameFlowManager, string message)
        {
            var uiManager = GetInstanceFieldValue(gameFlowManager, "uiManager");
            if (uiManager != null)
            {
                TryInvokeInstanceMethod(uiManager, "ShowGameOver", message);
            }
        }

        private static string ValueOrEmpty(JToken token)
        {
            return token == null ? string.Empty : Convert.ToString(token) ?? string.Empty;
        }

        private static string NormalizeLabelText(string value, int maxLength)
        {
            return NormalizeDisplayText(value, maxLength, false);
        }

        private static string NormalizeQuestionText(string value, int maxLength)
        {
            var normalized = NormalizeDisplayText(value, int.MaxValue, true);
            normalized = KeepTrailingSentences(normalized, QuestionSentenceCount);
            if (string.IsNullOrWhiteSpace(normalized))
            {
                return "Question unavailable.";
            }

            return normalized;
        }

        private static string NormalizeDisplayText(string value, int maxLength, bool keepQuestionPunctuation)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return string.Empty;
            }

            var source = value
                .Replace("[[", "(")
                .Replace("]]", ")")
                .Replace("\r", " ")
                .Replace("\n", " ")
                .Trim();

            var builder = new StringBuilder(source.Length);
            var previousWasSpace = false;

            for (var i = 0; i < source.Length; i++)
            {
                var c = source[i];
                if (char.IsWhiteSpace(c))
                {
                    if (!previousWasSpace)
                    {
                        builder.Append(' ');
                        previousWasSpace = true;
                    }

                    continue;
                }

                previousWasSpace = false;
                if (c >= 32 && c <= 126)
                {
                    builder.Append(c);
                    continue;
                }

                switch (c)
                {
                    case '\u2013':
                    case '\u2014':
                    case '\u2212':
                        builder.Append('-');
                        break;
                    case '\u2018':
                    case '\u2019':
                        builder.Append('\'');
                        break;
                    case '\u201C':
                    case '\u201D':
                        builder.Append('"');
                        break;
                    case '\u2026':
                        builder.Append("...");
                        break;
                    case '\u00B7':
                    case '\u2022':
                        builder.Append(' ');
                        break;
                    default:
                        builder.Append(' ');
                        break;
                }
            }

            var compact = string.Join(" ", builder.ToString().Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries));
            if (!keepQuestionPunctuation)
            {
                compact = compact.Trim(new[] { '.', ',', ';', ':', '-', '/', '\\' });
            }

            if (compact.Length > maxLength)
            {
                compact = compact.Substring(0, maxLength - 3).TrimEnd() + "...";
            }

            return compact;
        }

        private static string KeepTrailingSentences(string value, int sentenceCount)
        {
            if (string.IsNullOrWhiteSpace(value) || sentenceCount < 1)
            {
                return string.IsNullOrWhiteSpace(value) ? string.Empty : value.Trim();
            }

            var sentences = new List<string>();
            var currentSentence = new StringBuilder(value.Length);

            for (var i = 0; i < value.Length; i++)
            {
                currentSentence.Append(value[i]);
                if (!LooksLikeSentenceBoundary(value, i))
                {
                    continue;
                }

                var sentence = currentSentence.ToString().Trim();
                if (!string.IsNullOrWhiteSpace(sentence))
                {
                    sentences.Add(sentence);
                }

                currentSentence.Length = 0;
            }

            var trailingSentence = currentSentence.ToString().Trim();
            if (!string.IsNullOrWhiteSpace(trailingSentence))
            {
                sentences.Add(trailingSentence);
            }

            if (sentences.Count == 0)
            {
                return value.Trim();
            }

            var startIndex = Math.Max(0, sentences.Count - sentenceCount);
            return string.Join("\n", sentences.Skip(startIndex).ToArray());
        }

        private static bool LooksLikeSentenceBoundary(string value, int index)
        {
            var c = value[index];
            if (c != '.' && c != '!' && c != '?')
            {
                return false;
            }

            if (c == '.' && index + 1 < value.Length && value[index + 1] == '.')
            {
                return false;
            }

            if (c == '.' && IsAbbreviationPeriod(value, index))
            {
                return false;
            }

            var nextIndex = index + 1;
            while (nextIndex < value.Length && char.IsWhiteSpace(value[nextIndex]))
            {
                nextIndex++;
            }

            if (nextIndex >= value.Length)
            {
                return true;
            }

            var next = value[nextIndex];
            return char.IsUpper(next) || char.IsDigit(next) || next == '"' || next == '\'' || next == '(';
        }

        private static bool IsAbbreviationPeriod(string value, int index)
        {
            var tokenEnd = index - 1;
            while (tokenEnd >= 0 && char.IsWhiteSpace(value[tokenEnd]))
            {
                tokenEnd--;
            }

            if (tokenEnd < 0)
            {
                return false;
            }

            var tokenStart = tokenEnd;
            while (tokenStart >= 0 && char.IsLetter(value[tokenStart]))
            {
                tokenStart--;
            }

            tokenStart++;
            if (tokenStart > tokenEnd)
            {
                return false;
            }

            var token = value.Substring(tokenStart, tokenEnd - tokenStart + 1);
            if (string.Equals(token, "Mr", StringComparison.OrdinalIgnoreCase)
                || string.Equals(token, "Mrs", StringComparison.OrdinalIgnoreCase)
                || string.Equals(token, "Ms", StringComparison.OrdinalIgnoreCase)
                || string.Equals(token, "Dr", StringComparison.OrdinalIgnoreCase)
                || string.Equals(token, "St", StringComparison.OrdinalIgnoreCase)
                || string.Equals(token, "No", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            if (token.Length == 1 && char.IsUpper(token[0]))
            {
                var nextIndex = index + 1;
                while (nextIndex < value.Length && char.IsWhiteSpace(value[nextIndex]))
                {
                    nextIndex++;
                }

                if (nextIndex + 1 < value.Length
                    && char.IsUpper(value[nextIndex])
                    && value[nextIndex + 1] == '.')
                {
                    return true;
                }
            }

            return tokenStart >= 2
                && value[tokenStart - 1] == '.'
                && char.IsUpper(value[tokenStart - 2]);
        }

        private static void ApplyQuestionTextStyle(Text text, string question)
        {
            if (text == null)
            {
                return;
            }

            text.font = GetBuiltInFont();
            text.fontStyle = FontStyle.Bold;
            text.supportRichText = false;
            text.alignment = TextAnchor.UpperLeft;
            text.horizontalOverflow = HorizontalWrapMode.Wrap;
            text.verticalOverflow = VerticalWrapMode.Truncate;
            text.lineSpacing = 0.86f;
            text.resizeTextForBestFit = false;
            FitQuestionTextWithinBounds(text, question, ResolveQuestionMaxFontSize(question), QuestionMinFontSize);
        }

        private static int FitQuestionTextWithinBounds(Text text, string question, int desiredSize, int minimumSize)
        {
            if (text == null)
            {
                return minimumSize;
            }

            var value = string.IsNullOrWhiteSpace(question) ? "Question unavailable." : question;
            var bounds = text.rectTransform == null ? Vector2.zero : text.rectTransform.rect.size;
            var maxSize = Math.Max(minimumSize, desiredSize);
            if (bounds.x <= 1f || bounds.y <= 1f)
            {
                text.fontSize = maxSize;
                return maxSize;
            }

            var generator = new TextGenerator(Math.Max(64, value.Length));
            var selectedSize = minimumSize;
            for (var fontSize = maxSize; fontSize >= minimumSize; fontSize--)
            {
                var settings = text.GetGenerationSettings(bounds);
                settings.fontSize = fontSize;
                settings.resizeTextForBestFit = false;
                settings.resizeTextMinSize = fontSize;
                settings.resizeTextMaxSize = fontSize;
                settings.horizontalOverflow = HorizontalWrapMode.Wrap;
                settings.verticalOverflow = VerticalWrapMode.Overflow;
                settings.generationExtents = bounds;

                var preferredHeight = generator.GetPreferredHeight(value, settings) / Math.Max(0.001f, text.pixelsPerUnit);
                if (preferredHeight <= bounds.y - 1f)
                {
                    selectedSize = fontSize;
                    break;
                }
            }

            text.fontSize = selectedSize;
            text.resizeTextForBestFit = false;
            text.resizeTextMinSize = minimumSize;
            text.resizeTextMaxSize = maxSize;
            return selectedSize;
        }

        private static int ResolveQuestionMaxFontSize(string question)
        {
            var questionLength = string.IsNullOrWhiteSpace(question) ? 0 : question.Trim().Length;
            if (questionLength > 320)
            {
                return 21;
            }

            if (questionLength > 280)
            {
                return 22;
            }

            if (questionLength > 240)
            {
                return 24;
            }

            if (questionLength > 200)
            {
                return 26;
            }

            if (questionLength > 160)
            {
                return 28;
            }

            if (questionLength > 120)
            {
                return 32;
            }

            return QuestionBaseFontSize;
        }

        private static void FitQuestionTextToBluePanel(Text text)
        {
            if (text == null)
            {
                return;
            }

            var rect = text.rectTransform;
            if (rect == null)
            {
                return;
            }

            var questionFrame = FindRuntimeRectTransform(QuestionFramePath);
            if (questionFrame != null && rect.parent != questionFrame)
            {
                rect.SetParent(questionFrame, false);
            }

            rect.localScale = Vector3.one;
            rect.localRotation = Quaternion.identity;
            rect.SetAsLastSibling();
            rect.anchorMin = new Vector2(0.5f, 0.5f);
            rect.anchorMax = new Vector2(0.5f, 0.5f);
            rect.pivot = new Vector2(0.5f, 0.5f);

            if (questionFrame != null)
            {
                var width = Mathf.Max(320f, questionFrame.rect.width - 110f);
                var height = Mathf.Max(96f, questionFrame.rect.height - 90f);
                rect.anchoredPosition = new Vector2(0f, 58f);
                rect.sizeDelta = new Vector2(width, height);
            }
            else
            {
                rect.anchorMin = new Vector2(0f, 0f);
                rect.anchorMax = new Vector2(1f, 1f);
                rect.offsetMin = new Vector2(48f, 34f);
                rect.offsetMax = new Vector2(-48f, -34f);
            }
        }

        private static void UpdateQuestionOverlay(string question)
        {
            EnsureQuestionOverlay();
            if (_questionOverlayText == null)
            {
                return;
            }

            _questionOverlayText.text = question ?? string.Empty;
            ApplyQuestionTextStyle(_questionOverlayText, _questionOverlayText.text);
            _questionOverlayText.enabled = true;
        }

        private static void EnsureQuestionOverlay()
        {
            if (_questionOverlayText != null)
            {
                return;
            }

            var canvasGo = new GameObject("CodexQuestionTextOverlay");
            UnityEngine.Object.DontDestroyOnLoad(canvasGo);

            _questionOverlayCanvas = canvasGo.AddComponent<Canvas>();
            _questionOverlayCanvas.renderMode = RenderMode.ScreenSpaceOverlay;
            _questionOverlayCanvas.sortingOrder = 9000;

            var scaler = canvasGo.AddComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920f, 1080f);
            scaler.matchWidthOrHeight = 0.5f;

            var textGo = new GameObject("QuestionText");
            textGo.transform.SetParent(canvasGo.transform, false);
            _questionOverlayText = textGo.AddComponent<Text>();
            _questionOverlayText.color = Color.white;
            _questionOverlayText.raycastTarget = false;

            var rect = _questionOverlayText.rectTransform;
            rect.anchorMin = new Vector2(0.5f, 0f);
            rect.anchorMax = new Vector2(0.5f, 0f);
            rect.pivot = new Vector2(0.5f, 0.5f);
            rect.anchoredPosition = new Vector2(26f, 194f);
            rect.sizeDelta = QuestionAuditBoxSize;

            var shadow = textGo.AddComponent<Shadow>();
            shadow.effectColor = MakeColor(0, 24, 48, 190);
            shadow.effectDistance = new Vector2(2f, -2f);
        }

        private static RectTransform FindRuntimeRectTransform(string path)
        {
            var go = GameObject.Find(path);
            if (go != null)
            {
                return go.GetComponent<RectTransform>();
            }

            var name = path;
            var lastSlash = path.LastIndexOf('/');
            if (lastSlash >= 0 && lastSlash + 1 < path.Length)
            {
                name = path.Substring(lastSlash + 1);
            }

            return Resources.FindObjectsOfTypeAll<RectTransform>()
                .Where(candidate => candidate != null && candidate.gameObject != null && candidate.gameObject.scene.IsValid())
                .Where(candidate => candidate.gameObject.activeInHierarchy)
                .FirstOrDefault(candidate => string.Equals(candidate.name, name, StringComparison.Ordinal));
        }

        private static void WriteQuestionFitAudit()
        {
            if (_questionFitAuditWritten)
            {
                return;
            }

            _questionFitAuditWritten = true;

            try
            {
                var pool = LoadQuestionPool();
                var probeRoot = new GameObject("__CodexQuestionAuditProbe__");
                UnityEngine.Object.DontDestroyOnLoad(probeRoot);
                var probeRect = probeRoot.AddComponent<RectTransform>();
                probeRect.sizeDelta = QuestionAuditBoxSize;
                var probeText = probeRoot.AddComponent<Text>();
                ApplyQuestionTextStyle(probeText, string.Empty);
                probeText.alignment = TextAnchor.UpperLeft;
                probeText.horizontalOverflow = HorizontalWrapMode.Wrap;
                probeText.verticalOverflow = VerticalWrapMode.Overflow;
                probeText.rectTransform.sizeDelta = QuestionAuditBoxSize;
                var rows = new JArray();
                var anyOverflow = false;

                for (var i = 0; i < pool.Count; i++)
                {
                    var question = pool[i];
                    var text = question == null || string.IsNullOrWhiteSpace(question.Question)
                        ? "Question unavailable."
                        : question.Question;
                    ApplyQuestionTextStyle(probeText, text);
                    probeText.text = text;
                    var settings = probeText.GetGenerationSettings(QuestionAuditBoxSize);
                    settings.fontSize = probeText.fontSize;
                    settings.resizeTextForBestFit = false;
                    settings.resizeTextMinSize = probeText.fontSize;
                    settings.resizeTextMaxSize = probeText.fontSize;
                    settings.horizontalOverflow = HorizontalWrapMode.Wrap;
                    settings.verticalOverflow = VerticalWrapMode.Overflow;
                    settings.generationExtents = QuestionAuditBoxSize;
                    var generator = probeText.cachedTextGenerator;
                    generator.Populate(text, settings);
                    var preferredHeight = generator.GetPreferredHeight(text, settings) / Math.Max(0.001f, probeText.pixelsPerUnit);
                    var preferredWidth = Math.Min(
                        QuestionAuditBoxSize.x,
                        generator.GetPreferredWidth(text, settings) / Math.Max(0.001f, probeText.pixelsPerUnit));
                    var renderedRect = generator.rectExtents;
                    var fits = preferredHeight <= QuestionAuditBoxSize.y + 0.5f;
                    anyOverflow |= !fits;

                    var row = new JObject();
                    row["index"] = i + 1;
                    row["id"] = question == null ? string.Empty : question.Id ?? string.Empty;
                    row["length"] = text.Length;
                    row["fontSize"] = probeText.fontSize;
                    row["lineCount"] = generator.lineCount;
                    row["preferredWidth"] = Math.Round(preferredWidth, 2);
                    row["preferredHeight"] = Math.Round(preferredHeight, 2);
                    row["renderedWidth"] = Math.Round(renderedRect.width, 2);
                    row["renderedHeight"] = Math.Round(renderedRect.height, 2);
                    row["fits"] = fits;
                    row["text"] = text;
                    rows.Add(row);
                }

                var summary = new JObject();
                summary["questionCount"] = pool.Count;
                summary["boxWidth"] = QuestionAuditBoxSize.x;
                summary["boxHeight"] = QuestionAuditBoxSize.y;
                summary["baseFontSize"] = QuestionBaseFontSize;
                summary["minFontSize"] = QuestionMinFontSize;
                summary["allFit"] = !anyOverflow;
                summary["questions"] = rows;

                var outputPath = Path.Combine(GetWorkspaceRoot(), "tmp", "question_fit_audit.json");
                Directory.CreateDirectory(Path.GetDirectoryName(outputPath));
                File.WriteAllText(outputPath, summary.ToString());
                UnityEngine.Object.Destroy(probeRoot);
                Debug.Log("[CodexPatch] Question fit audit written to " + outputPath);
            }
            catch (Exception ex)
            {
                Debug.LogWarning("[CodexPatch] Question fit audit failed: " + ex.Message);
            }
        }

        private static Font GetBuiltInFont()
        {
            if (_builtInFont == null)
            {
                _builtInFont = Resources.GetBuiltinResource<Font>("Arial.ttf");
            }

            return _builtInFont;
        }

        private static Color MakeColor(byte r, byte g, byte b, byte a)
        {
            return new Color32(r, g, b, a);
        }

        private sealed class QuestionRecord
        {
            public string Id;
            public string Question;
            public string Answer;
            public List<string> Aliases = new List<string>();
            public string Category;
            public string Era;
            public string Source;
        }

        private sealed class LocalScoreRecord
        {
            public string UserId;
            public string Name;
            public string Team;
            public int Score;
        }

        private sealed class LiveViewerRecord
        {
            public string UserId;
            public string Name;
            public string Team;
            public DateTime LastSeenUtc;
        }

        private sealed class ProductSnapshot
        {
            public string Category;
            public int BlueHealth;
            public int CurrentQuestionIndex;
            public string DisplayQuestionText;
            public string Era;
            public string InfoText;
            public bool IsGameActive;
            public int MaxHealth;
            public object Meta;
            public string QuestionId;
            public string QuestionText;
            public int RedHealth;
            public string Source;
            public string SubjectText;
            public string TimerText;
            public int TotalQuestions;
        }

        private sealed class AutonomousSessionDriver : MonoBehaviour
        {
            private const float PollIntervalSeconds = 0.25f;
            private const float InactiveRecoverySeconds = 7f;
            private const float PendingTransitionRecoverySeconds = 6f;
            private const float TimedOutQuestionRecoverySeconds = 8f;
            private const float CompletedRoundRecoverySeconds = 18f;

            private float _nextPollAt;
            private float _inactiveSince = -1f;
            private float _pendingSince = -1f;
            private float _completedSince = -1f;
            private float _lastRecoveryAt = -100f;
            private int _observedQuestionIndex = -1;

            private void Update()
            {
                if (Time.unscaledTime < _nextPollAt)
                {
                    return;
                }

                _nextPollAt = Time.unscaledTime + PollIntervalSeconds;

                try
                {
                    var gameFlowManagerType = FindAssemblyType("GameFlowManager");
                    var gameFlowManager = gameFlowManagerType == null ? null : GetStaticPropertyValue(gameFlowManagerType, "Instance");
                    if (gameFlowManager == null)
                    {
                        return;
                    }

                    var active = Convert.ToBoolean(GetInstancePropertyValue(gameFlowManager, "IsGameActive"));
                    if (!active)
                    {
                        WatchInactiveSession();
                        return;
                    }

                    _inactiveSince = -1f;
                    WatchActiveRound(gameFlowManager);
                }
                catch (Exception ex)
                {
                    Debug.LogWarning("[CodexPatch] Continuity watchdog update failed: " + ex.Message);
                }
            }

            private void WatchInactiveSession()
            {
                if (_inactiveSince < 0f)
                {
                    _inactiveSince = Time.unscaledTime;
                    _observedQuestionIndex = -1;
                    _pendingSince = -1f;
                    _completedSince = -1f;
                    return;
                }

                if (Time.unscaledTime - _inactiveSince < InactiveRecoverySeconds
                    || Time.unscaledTime - _lastRecoveryAt < InactiveRecoverySeconds)
                {
                    return;
                }

                // The lobby owns the first bounded start attempt. This path is the offline/backend
                // fallback and also restarts later rounds if an HTTP callback never arrives.
                if (Time.unscaledTime < LobbyAutoStartSeconds + StartRequestFallbackSeconds)
                {
                    return;
                }

                _lastRecoveryAt = Time.unscaledTime;
                if (TryStartLocalRound("inactive session recovery"))
                {
                    _inactiveSince = -1f;
                }
            }

            private void WatchActiveRound(object gameFlowManager)
            {
                var questionIndex = Math.Max(1, Convert.ToInt32(GetInstancePropertyValue(gameFlowManager, "CurrentQuestionIndex")));
                var totalQuestions = Math.Max(1, Convert.ToInt32(GetInstancePropertyValue(gameFlowManager, "TotalQuestions")));
                if (_selectedRoundQuestions != null && _selectedRoundQuestions.Count > 0)
                {
                    totalQuestions = Math.Max(totalQuestions, _selectedRoundQuestions.Count);
                }

                if (questionIndex != _observedQuestionIndex)
                {
                    _observedQuestionIndex = questionIndex;
                    _pendingSince = -1f;
                    _completedSince = -1f;
                }

                var waiting = Convert.ToBoolean(GetInstanceFieldValue(gameFlowManager, "_isWaitingForNextQuestion"));
                var answered = Convert.ToBoolean(GetInstanceFieldValue(gameFlowManager, "hasFirstAnswerInCurrentQuestion"));
                if (waiting || answered)
                {
                    if (_pendingSince < 0f)
                    {
                        _pendingSince = Time.unscaledTime;
                    }
                }
                else
                {
                    _pendingSince = -1f;
                }

                var questionStartTime = Convert.ToSingle(GetInstanceFieldValue(gameFlowManager, "questionStartTime"));
                var timeLimit = Math.Max(1f, Convert.ToSingle(GetInstanceFieldValue(gameFlowManager, "timeLimitPerQuestion")));
                var questionElapsed = Math.Max(0f, Time.time - questionStartTime);

                if (questionIndex >= totalQuestions)
                {
                    if (_completedSince < 0f && (waiting || answered || questionElapsed >= timeLimit))
                    {
                        _completedSince = Time.unscaledTime;
                    }

                    if (_completedSince >= 0f
                        && Time.unscaledTime - _completedSince >= CompletedRoundRecoverySeconds
                        && Time.unscaledTime - _lastRecoveryAt >= PendingTransitionRecoverySeconds)
                    {
                        _lastRecoveryAt = Time.unscaledTime;
                        TryEndRoundLocally(gameFlowManager, "round-end callback timeout");
                    }

                    return;
                }

                var pendingStalled = _pendingSince >= 0f
                    && Time.unscaledTime - _pendingSince >= PendingTransitionRecoverySeconds;
                var timerStalled = questionElapsed >= timeLimit + TimedOutQuestionRecoverySeconds;
                if ((pendingStalled || timerStalled)
                    && Time.unscaledTime - _lastRecoveryAt >= PendingTransitionRecoverySeconds)
                {
                    _lastRecoveryAt = Time.unscaledTime;
                    TryAdvanceQuestionLocally(
                        gameFlowManager,
                        questionIndex + 1,
                        pendingStalled ? "next-question callback timeout" : "countdown timeout recovery");
                    _pendingSince = -1f;
                }
            }
        }

        private sealed class EnduranceQaDriver : MonoBehaviour
        {
            // A normal clue runs for two minutes. 720 full image/question transitions therefore
            // represents 24 uninterrupted hours at the maximum unattended timeout cadence.
            private const int QuestionsToSimulate = 720;
            private const int QuestionsPerFrame = 12;
            private const int QuestionsPerRound = RoundSize;

            private int _simulatedQuestions;
            private int _simulatedRounds;
            private float _startedAt;
            private bool _complete;

            private void Start()
            {
                _startedAt = Time.realtimeSinceStartup;
                Debug.Log(string.Format(
                    "[CodexPatch][Endurance] Accelerated 24-hour simulation started: {0} questions.",
                    QuestionsToSimulate));
            }

            private void Update()
            {
                if (_complete)
                {
                    return;
                }

                try
                {
                    var batchEnd = Math.Min(QuestionsToSimulate, _simulatedQuestions + QuestionsPerFrame);
                    while (_simulatedQuestions < batchEnd)
                    {
                        if (_selectedRoundQuestions == null || _selectedRoundQuestions.Count == 0
                            || _simulatedQuestions % QuestionsPerRound == 0)
                        {
                            EnsureRoundSelectionPrepared(true);
                            _simulatedRounds++;
                        }

                        var index = (_simulatedQuestions % Math.Max(1, _selectedRoundQuestions.Count)) + 1;
                        var question = GetSelectedQuestion(index);
                        if (question == null || LoadSpriteForQuestion(question.Id) == null)
                        {
                            throw new InvalidOperationException("Endurance question/image load failed at simulated question " + _simulatedQuestions);
                        }

                        _simulatedQuestions++;
                    }

                    if (_simulatedQuestions < QuestionsToSimulate)
                    {
                        return;
                    }

                    _complete = true;
                    var elapsed = Math.Max(0.001f, Time.realtimeSinceStartup - _startedAt);
                    Debug.Log(string.Format(
                        "[CodexPatch][Endurance] PASS questions={0} rounds={1} spriteCache={2}/{3} elapsed={4:0.00}s memory={5}MB",
                        _simulatedQuestions,
                        _simulatedRounds,
                        SpriteCache.Count,
                        MaxCachedQuestionSprites,
                        elapsed,
                        GC.GetTotalMemory(false) / (1024 * 1024)));
                }
                catch (Exception ex)
                {
                    _complete = true;
                    Debug.LogError("[CodexPatch][Endurance] FAIL " + ex);
                }
            }
        }

        private sealed class QuestionImageDriver : MonoBehaviour
        {
            private float _nextRefreshAt;

            private void Update()
            {
                if (Time.unscaledTime < _nextRefreshAt)
                {
                    return;
                }

                _nextRefreshAt = Time.unscaledTime + 0.15f;
                EnsureRoundSelectionPrepared(false);
                RefreshQuestionImage();
            }
        }

        private sealed class DanmakuLayerDriver : MonoBehaviour
        {
            private const int TopmostSortingOrder = 32767;
            private const float RefreshIntervalSeconds = 0.1f;

            private object _danmakuManager;
            private Transform _danmakuContainer;
            private Canvas _danmakuCanvas;
            private float _nextRefreshAt;

            private void OnEnable()
            {
                Canvas.willRenderCanvases += EnsureTopmost;
            }

            private void OnDisable()
            {
                Canvas.willRenderCanvases -= EnsureTopmost;
            }

            private void Update()
            {
                if (Time.unscaledTime < _nextRefreshAt)
                {
                    return;
                }

                _nextRefreshAt = Time.unscaledTime + RefreshIntervalSeconds;
                EnsureTopmost();
            }

            private void EnsureTopmost()
            {
                try
                {
                    var managerType = FindAssemblyType("DanmakuManager");
                    if (managerType == null)
                    {
                        return;
                    }

                    var manager = GetStaticPropertyValue(managerType, "Instance");
                    if (manager == null)
                    {
                        _danmakuManager = null;
                        _danmakuContainer = null;
                        _danmakuCanvas = null;
                        return;
                    }

                    if (!ReferenceEquals(_danmakuManager, manager) || _danmakuContainer == null)
                    {
                        _danmakuManager = manager;
                        _danmakuContainer = GetInstanceFieldValue(manager, "danmakuContainer") as Transform;
                        _danmakuCanvas = null;
                    }

                    if (_danmakuContainer == null)
                    {
                        return;
                    }

                    if (_danmakuCanvas == null)
                    {
                        _danmakuCanvas = _danmakuContainer.GetComponent<Canvas>();
                        if (_danmakuCanvas == null)
                        {
                            _danmakuCanvas = _danmakuContainer.gameObject.AddComponent<Canvas>();
                        }
                    }

                    _danmakuCanvas.enabled = true;
                    _danmakuCanvas.overrideSorting = true;
                    _danmakuCanvas.sortingLayerID = GetTopSortingLayerId();
                    _danmakuCanvas.sortingOrder = TopmostSortingOrder;
                    _danmakuContainer.SetAsLastSibling();
                }
                catch (Exception ex)
                {
                    Debug.LogWarning("[CodexPatch] Danmaku top-layer guard failed: " + ex.Message);
                }
            }

            private static int GetTopSortingLayerId()
            {
                var layers = SortingLayer.layers;
                if (layers == null || layers.Length == 0)
                {
                    return 0;
                }

                var topLayer = layers[0];
                for (var i = 1; i < layers.Length; i++)
                {
                    if (layers[i].value > topLayer.value)
                    {
                        topLayer = layers[i];
                    }
                }

                return topLayer.id;
            }
        }

        private sealed class TeamLivestreamDriver : MonoBehaviour
        {
            private float _nextRefreshAt;

            private void Update()
            {
                if (Time.unscaledTime < _nextRefreshAt)
                {
                    return;
                }

                _nextRefreshAt = Time.unscaledTime + 0.25f;
                try
                {
                    EnsureRootVisible("UI/up/Blood_Red");
                    EnsureRootVisible("UI/up/Blood_Blue");

                    var uiManagerType = FindAssemblyType("UIManager");
                    var uiManager = uiManagerType == null ? null : GetStaticPropertyValue(uiManagerType, "Instance");
                    if (uiManager == null)
                    {
                        return;
                    }

                    EnsureHealthImageVisible(ReadPropertyOrField(uiManager, "redHealthImage") as Image);
                    EnsureHealthImageVisible(ReadPropertyOrField(uiManager, "blueHealthImage") as Image);
                }
                catch (Exception ex)
                {
                    Debug.LogWarning("[CodexPatch] Team livestream UI recovery failed: " + ex.Message);
                }
            }

            private static void EnsureRootVisible(string path)
            {
                var rootObject = GameObject.Find(path);
                var root = rootObject == null ? null : rootObject.GetComponent<RectTransform>();
                if (root != null && !root.gameObject.activeSelf)
                {
                    root.gameObject.SetActive(true);
                }
            }

            private static void EnsureHealthImageVisible(Image image)
            {
                if (image == null)
                {
                    return;
                }

                image.enabled = true;
                var current = image.transform;
                while (current != null && !string.Equals(current.gameObject.name, "up", StringComparison.OrdinalIgnoreCase))
                {
                    if (!current.gameObject.activeSelf)
                    {
                        current.gameObject.SetActive(true);
                    }

                    current = current.parent;
                }
            }
        }

        private sealed class NonTeamLivestreamDriver : MonoBehaviour
        {
            private RectTransform _redBarRoot;
            private RectTransform _blueBarRoot;
            private RectTransform _rankListRoot;
            private Text _rankTitleText;
            private Canvas _personalCoverCanvas;
            private Image _topStripCover;
            private float _nextRefreshAt;

            private void Update()
            {
                if (Time.unscaledTime < _nextRefreshAt)
                {
                    return;
                }

                _nextRefreshAt = Time.unscaledTime + 0.25f;
                try
                {
                    EnsureLivestreamBackendMode();
                    ResolveSceneReferences();
                    EnsureTopStripCover();
                    HideTeamGraphics();
                    PolishPersonalRanking();

                    var gameFlowManagerType = FindAssemblyType("GameFlowManager");
                    var gameFlowManager = gameFlowManagerType == null ? null : GetStaticPropertyValue(gameFlowManagerType, "Instance");
                    if (gameFlowManager != null)
                    {
                        KeepPersonalHealthNeutral(gameFlowManager);
                    }
                }
                catch (Exception ex)
                {
                    Debug.LogWarning("[CodexPatch] Personal livestream update failed: " + ex.Message);
                }
            }

            private void ResolveSceneReferences()
            {
                if (_redBarRoot == null)
                {
                    _redBarRoot = FindRectTransform("UI/up/Blood_Red");
                }

                if (_blueBarRoot == null)
                {
                    _blueBarRoot = FindRectTransform("UI/up/Blood_Blue");
                }

                if (_rankListRoot == null)
                {
                    _rankListRoot = FindRectTransform("UI/center/Ranklist");
                }

                if (_rankTitleText == null)
                {
                    var titleObject = GameObject.Find("UI/center/Ranklist/text");
                    _rankTitleText = titleObject == null ? null : titleObject.GetComponent<Text>();
                }
            }

            private void HideTeamGraphics()
            {
                HideRoot(_redBarRoot);
                HideRoot(_blueBarRoot);

                var uiManagerType = FindAssemblyType("UIManager");
                var uiManager = uiManagerType == null ? null : GetStaticPropertyValue(uiManagerType, "Instance");
                if (uiManager == null)
                {
                    return;
                }

                var redHealthImage = ReadPropertyOrField(uiManager, "redHealthImage") as Image;
                var blueHealthImage = ReadPropertyOrField(uiManager, "blueHealthImage") as Image;
                if (redHealthImage != null)
                {
                    HideHealthImageRoot(redHealthImage);
                    redHealthImage.enabled = false;
                }

                if (blueHealthImage != null)
                {
                    HideHealthImageRoot(blueHealthImage);
                    blueHealthImage.enabled = false;
                }

                HideUpperTeamStrip(uiManager);
            }

            private void EnsureTopStripCover()
            {
                if (_topStripCover == null)
                {
                    var canvasGo = new GameObject("CodexPersonalTopCover");
                    UnityEngine.Object.DontDestroyOnLoad(canvasGo);
                    _personalCoverCanvas = canvasGo.AddComponent<Canvas>();
                    _personalCoverCanvas.renderMode = RenderMode.ScreenSpaceOverlay;
                    _personalCoverCanvas.sortingOrder = 6000;

                    var raycaster = canvasGo.AddComponent<GraphicRaycaster>();
                    raycaster.enabled = false;

                    var coverRect = new GameObject("TopTeamStripCover", typeof(RectTransform));
                    coverRect.transform.SetParent(canvasGo.transform, false);
                    _topStripCover = coverRect.AddComponent<Image>();
                    _topStripCover.color = MakeColor(0, 139, 211, 255);
                    _topStripCover.raycastTarget = false;
                }

                var rect = _topStripCover.rectTransform;
                rect.anchorMin = new Vector2(0f, 1f);
                rect.anchorMax = new Vector2(0f, 1f);
                rect.pivot = new Vector2(0f, 1f);
                rect.anchoredPosition = new Vector2(450f, -145f);
                rect.sizeDelta = new Vector2(Math.Max(0f, Screen.width - 450f), 76f);
            }

            private void PolishPersonalRanking()
            {
                if (_rankTitleText != null)
                {
                    _rankTitleText.text = "PERSONAL RANK";
                    _rankTitleText.resizeTextForBestFit = true;
                    _rankTitleText.resizeTextMinSize = 18;
                    _rankTitleText.resizeTextMaxSize = Math.Max(24, _rankTitleText.fontSize);
                }

                if (_rankListRoot == null)
                {
                    return;
                }

                var images = _rankListRoot.GetComponentsInChildren<Image>(true);
                for (var i = 0; i < images.Length; i++)
                {
                    var image = images[i];
                    if (image != null && string.Equals(image.gameObject.name, "Color", StringComparison.OrdinalIgnoreCase))
                    {
                        image.color = MakeColor(27, 190, 230, 255);
                    }
                }
            }

            private void HideRoot(RectTransform root)
            {
                if (root == null)
                {
                    return;
                }

                if (root.gameObject.activeSelf)
                {
                    root.gameObject.SetActive(false);
                }
            }

            private void HideHealthImageRoot(Image image)
            {
                if (image == null)
                {
                    return;
                }

                var root = image.rectTransform;
                if (string.Equals(root.gameObject.name, "value", StringComparison.OrdinalIgnoreCase) && root.parent != null)
                {
                    var parentRect = root.parent.GetComponent<RectTransform>();
                    if (parentRect != null)
                    {
                        root = parentRect;
                    }
                }

                if (!string.Equals(root.gameObject.name, "up", StringComparison.OrdinalIgnoreCase))
                {
                    HideRoot(root);
                }
            }

            private void HideUpperTeamStrip(object uiManager)
            {
                var upperRoot = FindRectTransform("UI/up");
                if (upperRoot == null)
                {
                    return;
                }

                var roundText = ReadPropertyOrField(uiManager, "roundText") as Text;
                var timerText = ReadPropertyOrField(uiManager, "timerText") as Text;
                var graphics = upperRoot.GetComponentsInChildren<Graphic>(true);
                for (var i = 0; i < graphics.Length; i++)
                {
                    var graphic = graphics[i];
                    if (graphic == null || graphic == roundText || graphic == timerText)
                    {
                        continue;
                    }

                    var rectTransform = graphic.rectTransform;
                    var rect = GetScreenRect(rectTransform);
                    var isTopStripGraphic = rect.width > 300f && rect.xMin > Screen.width * 0.25f && rect.yMin > Screen.height - 300f;
                    var text = graphic as Text;
                    var isVersusText = text != null && string.Equals((text.text ?? string.Empty).Trim(), "VS", StringComparison.OrdinalIgnoreCase);
                    if (isTopStripGraphic || isVersusText)
                    {
                        graphic.enabled = false;
                    }
                }
            }

            private Rect GetScreenRect(RectTransform rect)
            {
                if (rect == null)
                {
                    return new Rect(0f, 0f, 0f, 0f);
                }

                var corners = new Vector3[4];
                rect.GetWorldCorners(corners);
                return Rect.MinMaxRect(corners[0].x, corners[0].y, corners[2].x, corners[2].y);
            }

            private RectTransform FindRectTransform(string path)
            {
                var go = GameObject.Find(path);
                return go == null ? null : go.GetComponent<RectTransform>();
            }
        }

        private sealed class StartMenuDriver : MonoBehaviour
        {
            private static readonly Dictionary<int, Sprite> RoundedCornerSprites = new Dictionary<int, Sprite>();
            private readonly Color _accentBlue = MakeColor(78, 210, 255, 255);
            private readonly Color _accentGold = MakeColor(255, 194, 78, 255);
            private readonly Color _cardFill = MakeColor(4, 83, 143, 248);
            private readonly Color _cardStroke = MakeColor(113, 207, 255, 110);
            private readonly Color _softText = MakeColor(192, 218, 238, 255);
            private Canvas _canvas;
            private RectTransform _root;
            private Button _startButton;
            private Text _startButtonText;
            private Text _startKeyHint;
            private Text _viewerHeader;
            private Text _viewerText;
            private string _lastViewerRoster = string.Empty;
            private float _nextRosterRefresh;
            private float _nextRosterUiRefresh;
            private float _nextViewerPageAt;
            private float _startRequestedAt = -1f;
            private float _menuReadyAt;
            private bool _menuReady;
            private bool _startRequested;
            private bool _qaCaptureComplete;
            private bool _qaAutoStartTriggered;
            private int _viewerPage;
            private int _lastViewerCount = -1;
            private int _lastCountdownSecond = -1;

            private void Update()
            {
                try
                {
                    EnsureMenu();
                    if (Time.unscaledTime >= _nextRosterUiRefresh)
                    {
                        _nextRosterUiRefresh = Time.unscaledTime + 0.5f;
                        RefreshViewerRoster();
                    }
                    CaptureLobbyForQaIfRequested();
                    UpdateAutoStartCountdown();

                    var qaAutoStart = Environment.GetCommandLineArgs()
                        .Any(argument => string.Equals(argument, "-codex-auto-start-test", StringComparison.OrdinalIgnoreCase));
                    var autoStartDelay = qaAutoStart ? 3f : LobbyAutoStartSeconds;
                    if (!_qaAutoStartTriggered
                        && !_startRequested
                        && Time.unscaledTime - _menuReadyAt >= autoStartDelay)
                    {
                        _qaAutoStartTriggered = true;
                        BeginRound();
                    }

                    if (!_startRequested && (Input.GetKeyDown(KeyCode.Return) || Input.GetKeyDown(KeyCode.Space)))
                    {
                        BeginRound();
                    }

                    if (!_startRequested)
                    {
                        var currentFlowType = FindAssemblyType("GameFlowManager");
                        var currentFlow = currentFlowType == null ? null : GetStaticPropertyValue(currentFlowType, "Instance");
                        if (currentFlow != null && Convert.ToBoolean(GetInstancePropertyValue(currentFlow, "IsGameActive")))
                        {
                            CloseLobbyAfterStart();
                            return;
                        }
                    }

                    if (_startRequested)
                    {
                        var gameFlowManagerType = FindAssemblyType("GameFlowManager");
                        var gameFlowManager = gameFlowManagerType == null ? null : GetStaticPropertyValue(gameFlowManagerType, "Instance");
                        var gameActive = gameFlowManager != null
                            && Convert.ToBoolean(GetInstancePropertyValue(gameFlowManager, "IsGameActive"));
                        if (gameActive)
                        {
                            CloseLobbyAfterStart();
                        }
                        else if (Time.unscaledTime - _startRequestedAt >= StartRequestFallbackSeconds)
                        {
                            if (TryStartLocalRound("lobby start request timeout"))
                            {
                                CloseLobbyAfterStart();
                            }
                            else
                            {
                                _startRequestedAt = Time.unscaledTime;
                                _startButtonText.text = "STARTING OFFLINE...";
                            }
                        }
                    }

                    if (Time.unscaledTime >= _nextRosterRefresh)
                    {
                        _nextRosterRefresh = Time.unscaledTime + 6f;
                        var apiType = FindAssemblyType("GameApiDemoUI");
                        var api = apiType == null ? null : GetStaticPropertyValue(apiType, "Instance");
                        if (api != null)
                        {
                            InvokeInstanceMethod(api, "OnGetTeams");
                        }
                    }
                }
                catch (Exception ex)
                {
                    Debug.LogWarning("[CodexPatch] Stream lobby update failed: " + ex.Message);
                }
            }

            private void EnsureMenu()
            {
                if (_menuReady)
                {
                    return;
                }

                var canvasObject = new GameObject("CodexStreamLobbyCanvas");
                UnityEngine.Object.DontDestroyOnLoad(canvasObject);
                _canvas = canvasObject.AddComponent<Canvas>();
                _canvas.renderMode = RenderMode.ScreenSpaceOverlay;
                _canvas.sortingOrder = 9600;
                var scaler = canvasObject.AddComponent<CanvasScaler>();
                scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
                scaler.referenceResolution = new Vector2(1920f, 1080f);
                scaler.matchWidthOrHeight = 0.5f;
                canvasObject.AddComponent<GraphicRaycaster>();

                _root = CreateRect("StreamLobbyRoot", canvasObject.transform);
                StretchRect(_root, Vector2.zero, Vector2.one, Vector2.zero, Vector2.zero);
                var backdrop = CreateImage("LobbyBackdrop", _root, MakeColor(0, 139, 211, 255));
                StretchRect(backdrop.rectTransform, Vector2.zero, Vector2.one, Vector2.zero, Vector2.zero);
                var shell = CreateCard("LobbyShell", _root, MakeColor(2, 41, 79, 255), 34);
                StretchRect(shell.rectTransform, Vector2.zero, Vector2.one, new Vector2(52f, 48f), new Vector2(-52f, -48f));

                var title = CreateText("LobbyTitle", _root, 48, FontStyle.Bold, Color.white, TextAnchor.MiddleCenter);
                PlaceRect(title.rectTransform, new Vector2(0.5f, 1f), new Vector2(0.5f, 1f), new Vector2(0f, -92f), new Vector2(1180f, 62f));
                title.text = "TWITCH HISTORY CHALLENGE";
                var subtitle = CreateText("LobbySubtitle", _root, 21, FontStyle.Bold, _softText, TextAnchor.MiddleCenter);
                PlaceRect(subtitle.rectTransform, new Vector2(0.5f, 1f), new Vector2(0.5f, 1f), new Vector2(0f, -142f), new Vector2(1100f, 34f));
                subtitle.text = "PLAY LIVE FROM TWITCH CHAT";

                var status = CreateCard("LobbyStatus", _root, MakeColor(15, 91, 119, 255), 18);
                PlaceRect(status.rectTransform, new Vector2(1f, 1f), new Vector2(1f, 1f), new Vector2(-92f, -82f), new Vector2(250f, 52f));
                var statusText = CreateText("LobbyStatusText", status.rectTransform, 21, FontStyle.Bold, Color.white, TextAnchor.MiddleCenter);
                StretchRect(statusText.rectTransform, Vector2.zero, Vector2.one, new Vector2(12f, 4f), new Vector2(-12f, -4f));
                statusText.text = "STREAM LOBBY";

                var instructions = CreateCard("InstructionsCard", _root, _cardFill);
                PlaceRect(instructions.rectTransform, new Vector2(0f, 1f), new Vector2(0f, 1f), new Vector2(104f, -212f), new Vector2(806f, 650f));
                var instructionsTitle = CreateText("InstructionsTitle", instructions.rectTransform, 30, FontStyle.Bold, _accentGold, TextAnchor.UpperLeft);
                PlaceRect(instructionsTitle.rectTransform, new Vector2(0f, 1f), new Vector2(0f, 1f), new Vector2(34f, -28f), new Vector2(700f, 46f));
                instructionsTitle.text = "HOW TO PLAY";
                var instructionsBody = CreateText("InstructionsBody", instructions.rectTransform, 25, FontStyle.Bold, Color.white, TextAnchor.UpperLeft);
                StretchRect(instructionsBody.rectTransform, Vector2.zero, Vector2.one, new Vector2(34f, 34f), new Vector2(-34f, -92f));
                instructionsBody.lineSpacing = 1.05f;
                instructionsBody.text = IsPersonalMode()
                    ? "1   WATCH THE CLUE\n     Read each question on the stream.\n\n2   TYPE YOUR ANSWER\n     Send the full answer in Twitch chat.\n     No command or prefix is needed.\n\n3   SCORE POINTS\n     Correct answers move you up the rankings.\n     Be quick—the first correct reply wins the clue."
                    : "1   JOIN THE MATCH\n     Your Twitch team decides which side you help.\n\n2   TYPE YOUR ANSWER\n     Send the full answer in Twitch chat.\n     No command or prefix is needed.\n\n3   STRIKE FIRST\n     The first correct reply damages the other team.\n     Work together to protect your team's health.";
                var readyHint = CreateText("ReadyHint", instructions.rectTransform, 19, FontStyle.Bold, _accentBlue, TextAnchor.MiddleLeft);
                PlaceRect(readyHint.rectTransform, new Vector2(0f, 0f), new Vector2(0f, 0f), new Vector2(34f, 24f), new Vector2(720f, 48f));
                readyHint.text = "TIP   SPELLING VARIATIONS AND COMMON ALIASES ARE ACCEPTED";

                var viewers = CreateCard("ViewersCard", _root, MakeColor(4, 83, 143, 248));
                PlaceRect(viewers.rectTransform, new Vector2(1f, 1f), new Vector2(1f, 1f), new Vector2(-104f, -212f), new Vector2(806f, 650f));
                _viewerHeader = CreateText("ViewerHeader", viewers.rectTransform, 30, FontStyle.Bold, _accentGold, TextAnchor.UpperLeft);
                PlaceRect(_viewerHeader.rectTransform, new Vector2(0f, 1f), new Vector2(0f, 1f), new Vector2(34f, -28f), new Vector2(700f, 46f));
                var viewerNote = CreateText("ViewerNote", viewers.rectTransform, 18, FontStyle.Bold, _softText, TextAnchor.UpperLeft);
                PlaceRect(viewerNote.rectTransform, new Vector2(0f, 1f), new Vector2(0f, 1f), new Vector2(34f, -76f), new Vector2(700f, 52f));
                viewerNote.text = "Current-session players from team joins and chat • pages rotate";

                var viewport = CreateImage("ViewerViewport", viewers.rectTransform, MakeColor(0, 36, 72, 120));
                ApplyRoundedShape(viewport, 20);
                PlaceRect(viewport.rectTransform, new Vector2(0f, 1f), new Vector2(0f, 1f), new Vector2(34f, -140f), new Vector2(738f, 446f));
                var mask = viewport.gameObject.AddComponent<Mask>();
                mask.showMaskGraphic = true;
                var content = CreateRect("ViewerContent", viewport.rectTransform);
                content.anchorMin = new Vector2(0f, 1f);
                content.anchorMax = new Vector2(1f, 1f);
                content.pivot = new Vector2(0.5f, 1f);
                content.anchoredPosition = Vector2.zero;
                content.sizeDelta = new Vector2(0f, 446f);
                _viewerText = CreateText("ViewerList", content, 24, FontStyle.Bold, Color.white, TextAnchor.UpperLeft);
                _viewerText.rectTransform.anchorMin = new Vector2(0f, 1f);
                _viewerText.rectTransform.anchorMax = new Vector2(1f, 1f);
                _viewerText.rectTransform.pivot = new Vector2(0.5f, 1f);
                _viewerText.rectTransform.anchoredPosition = new Vector2(0f, -18f);
                _viewerText.rectTransform.sizeDelta = new Vector2(-36f, 410f);
                var fitter = _viewerText.gameObject.AddComponent<ContentSizeFitter>();
                fitter.verticalFit = ContentSizeFitter.FitMode.PreferredSize;
                var scroll = viewers.gameObject.AddComponent<ScrollRect>();
                scroll.viewport = viewport.rectTransform;
                scroll.content = _viewerText.rectTransform;
                scroll.horizontal = false;
                scroll.vertical = true;
                scroll.scrollSensitivity = 34f;

                _startButton = CreateButton("StartRoundButton", _root, _accentGold);
                PlaceRect(_startButton.GetComponent<RectTransform>(), new Vector2(0.5f, 0f), new Vector2(0.5f, 0f), new Vector2(0f, 92f), new Vector2(470f, 82f));
                _startButtonText = CreateText("StartRoundText", _startButton.transform, 29, FontStyle.Bold, MakeColor(2, 41, 79, 255), TextAnchor.MiddleCenter);
                StretchRect(_startButtonText.rectTransform, Vector2.zero, Vector2.one, new Vector2(16f, 8f), new Vector2(-16f, -8f));
                _startButtonText.text = "START ROUND  |  AUTO 09s";
                _startButton.onClick.AddListener(BeginRound);
                _startKeyHint = CreateText("StartKeyHint", _root, 17, FontStyle.Bold, _softText, TextAnchor.MiddleCenter);
                PlaceRect(_startKeyHint.rectTransform, new Vector2(0.5f, 0f), new Vector2(0.5f, 0f), new Vector2(0f, 48f), new Vector2(760f, 28f));
                _startKeyHint.text = "HOST: CLICK START OR PRESS ENTER  |  AUTO-START ENABLED";

                _nextRosterRefresh = Time.unscaledTime + 0.8f;
                _nextRosterUiRefresh = Time.unscaledTime;
                _nextViewerPageAt = Time.unscaledTime + 6f;
                _menuReadyAt = Time.unscaledTime;
                _menuReady = true;
                RefreshViewerRoster();
                Debug.Log("[CodexPatch] Stream lobby ready.");
            }

            private void UpdateAutoStartCountdown()
            {
                if (!_menuReady || _startRequested || _startButtonText == null)
                {
                    return;
                }

                var remaining = Math.Max(0, (int)Math.Ceiling(LobbyAutoStartSeconds - (Time.unscaledTime - _menuReadyAt)));
                if (remaining == _lastCountdownSecond)
                {
                    return;
                }

                _lastCountdownSecond = remaining;
                _startButtonText.text = string.Format("START ROUND  |  AUTO {0:00}s", remaining);
            }

            private void CaptureLobbyForQaIfRequested()
            {
                if (_qaCaptureComplete || Time.unscaledTime - _menuReadyAt < 1.5f)
                {
                    return;
                }

                var requested = Environment.GetCommandLineArgs()
                    .Any(argument => string.Equals(argument, "-codex-capture-lobby", StringComparison.OrdinalIgnoreCase));
                if (!requested)
                {
                    _qaCaptureComplete = true;
                    return;
                }

                var path = Path.Combine(GetWorkspaceRoot(), "tmp", IsPersonalMode()
                    ? "stream_lobby_nonteam_qa.png"
                    : "stream_lobby_team_qa.png");
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                ScreenCapture.CaptureScreenshot(path);
                _qaCaptureComplete = true;
                Debug.Log("[CodexPatch] Stream lobby QA frame requested at " + path);
            }

            private IEnumerator CaptureGameplayForQa()
            {
                yield return new WaitForSecondsRealtime(4f);
                var path = Path.Combine(GetWorkspaceRoot(), "tmp", IsPersonalMode()
                    ? "stream_lobby_nonteam_started_qa.png"
                    : "stream_lobby_team_started_qa.png");
                ScreenCapture.CaptureScreenshot(path);
                Debug.Log("[CodexPatch] Post-lobby QA frame requested at " + path);
            }

            private void BeginRound()
            {
                if (_startRequested)
                {
                    return;
                }

                if (IsPersonalMode() && !EnsureLivestreamBackendMode())
                {
                    _startRequested = true;
                    _startRequestedAt = Time.unscaledTime;
                    _startButton.interactable = false;
                    _startButtonText.text = "STARTING OFFLINE...";
                    if (TryStartLocalRound("personal backend unavailable at lobby start"))
                    {
                        CloseLobbyAfterStart();
                    }
                    return;
                }

                var apiType = FindAssemblyType("GameApiDemoUI");
                var api = apiType == null ? null : GetStaticPropertyValue(apiType, "Instance");
                if (api == null)
                {
                    _startRequested = true;
                    _startRequestedAt = Time.unscaledTime;
                    _startButton.interactable = false;
                    _startButtonText.text = "STARTING OFFLINE...";
                    if (TryStartLocalRound("game API unavailable at lobby start"))
                    {
                        CloseLobbyAfterStart();
                    }
                    return;
                }

                _startRequested = true;
                _startRequestedAt = Time.unscaledTime;
                _startButton.interactable = false;
                _startButtonText.text = "STARTING...";
                InvokeInstanceMethod(api, "OnStartGame");
            }

            private void CloseLobbyAfterStart()
            {
                if (_root != null)
                {
                    _root.gameObject.SetActive(false);
                }

                if (Environment.GetCommandLineArgs().Any(argument => string.Equals(argument, "-codex-capture-lobby", StringComparison.OrdinalIgnoreCase)))
                {
                    StartCoroutine(CaptureGameplayForQa());
                }

                enabled = false;
                Debug.Log(string.Format(
                    "[CodexPatch] Stream lobby closed after {0:0.0}s; round is live.",
                    Math.Max(0f, Time.unscaledTime - _menuReadyAt)));
            }

            private void RefreshViewerRoster()
            {
                var viewers = GetLiveViewerSnapshot();
                const int pageSize = 16;
                var pageCount = Math.Max(1, (viewers.Count + pageSize - 1) / pageSize);
                if (viewers.Count != _lastViewerCount)
                {
                    _lastViewerCount = viewers.Count;
                    _viewerPage = 0;
                }
                else if (pageCount > 1 && Time.unscaledTime >= _nextViewerPageAt)
                {
                    _viewerPage = (_viewerPage + 1) % pageCount;
                    _nextViewerPageAt = Time.unscaledTime + 6f;
                }
                _viewerPage = Math.Max(0, Math.Min(pageCount - 1, _viewerPage));

                var visibleViewers = viewers.Skip(_viewerPage * pageSize).Take(pageSize).ToList();
                var roster = string.Join("\n", visibleViewers.Select(viewer =>
                {
                    var label = FirstNonBlank(viewer.Name, viewer.UserId, "Viewer");
                    if (string.Equals(viewer.Team, "red", StringComparison.OrdinalIgnoreCase))
                    {
                        return "RED     " + label;
                    }
                    if (string.Equals(viewer.Team, "blue", StringComparison.OrdinalIgnoreCase))
                    {
                        return "BLUE    " + label;
                    }
                    return "LIVE    " + label;
                }).ToArray());
                if (string.IsNullOrWhiteSpace(roster))
                {
                    roster = "WAITING FOR VIEWERS\n\nOpen Twitch chat and say hello to appear here.";
                }
                if (string.Equals(roster, _lastViewerRoster, StringComparison.Ordinal))
                {
                    return;
                }

                _lastViewerRoster = roster;
                _viewerHeader.text = pageCount > 1
                    ? string.Format("LIVE VIEWERS   {0}     PAGE {1}/{2}", viewers.Count, _viewerPage + 1, pageCount)
                    : string.Format("LIVE VIEWERS   {0}", viewers.Count);
                _viewerText.text = roster;
            }

            private RectTransform CreateRect(string name, Transform parent)
            {
                var go = new GameObject(name, typeof(RectTransform));
                go.transform.SetParent(parent, false);
                return go.GetComponent<RectTransform>();
            }

            private Image CreateImage(string name, Transform parent, Color color)
            {
                var image = CreateRect(name, parent).gameObject.AddComponent<Image>();
                image.color = color;
                image.raycastTarget = false;
                return image;
            }

            private Image CreateCard(string name, Transform parent, Color color, int cornerRadius = 28)
            {
                var image = CreateImage(name, parent, color);
                ApplyRoundedShape(image, cornerRadius);
                var outline = image.gameObject.AddComponent<Outline>();
                outline.effectColor = _cardStroke;
                outline.effectDistance = new Vector2(2f, -2f);
                var shadow = image.gameObject.AddComponent<Shadow>();
                shadow.effectColor = MakeColor(0, 24, 48, 180);
                shadow.effectDistance = new Vector2(0f, -8f);
                return image;
            }

            private Button CreateButton(string name, Transform parent, Color color)
            {
                var image = CreateImage(name, parent, color);
                ApplyRoundedShape(image, 24);
                image.raycastTarget = true;
                var button = image.gameObject.AddComponent<Button>();
                button.targetGraphic = image;
                var colors = button.colors;
                colors.highlightedColor = MakeColor(255, 220, 130, 255);
                colors.pressedColor = MakeColor(232, 166, 58, 255);
                colors.disabledColor = MakeColor(137, 190, 220, 180);
                button.colors = colors;
                return button;
            }

            private void ApplyRoundedShape(Image image, int cornerRadius)
            {
                if (image == null)
                {
                    return;
                }

                Sprite sprite;
                if (!RoundedCornerSprites.TryGetValue(cornerRadius, out sprite))
                {
                    const int textureSize = 128;
                    var texture = new Texture2D(textureSize, textureSize, TextureFormat.RGBA32, false);
                    texture.name = "CodexLobbyRounded" + cornerRadius + "Texture";
                    texture.filterMode = FilterMode.Bilinear;
                    texture.wrapMode = TextureWrapMode.Clamp;

                    var pixels = new Color[textureSize * textureSize];
                    for (var y = 0; y < textureSize; y++)
                    {
                        for (var x = 0; x < textureSize; x++)
                        {
                            var px = x + 0.5f;
                            var py = y + 0.5f;
                            var horizontal = Mathf.Min(px, textureSize - px);
                            var vertical = Mathf.Min(py, textureSize - py);
                            var dx = Mathf.Max(cornerRadius - horizontal, 0f);
                            var dy = Mathf.Max(cornerRadius - vertical, 0f);
                            var distance = Mathf.Sqrt(dx * dx + dy * dy);
                            var alpha = Mathf.Clamp01(cornerRadius + 0.75f - distance);
                            pixels[y * textureSize + x] = new Color(1f, 1f, 1f, alpha);
                        }
                    }

                    texture.SetPixels(pixels);
                    texture.Apply(false, true);
                    sprite = Sprite.Create(
                        texture,
                        new Rect(0f, 0f, textureSize, textureSize),
                        new Vector2(0.5f, 0.5f),
                        100f,
                        0,
                        SpriteMeshType.FullRect,
                        new Vector4(cornerRadius, cornerRadius, cornerRadius, cornerRadius));
                    sprite.name = "CodexLobbyRounded" + cornerRadius;
                    RoundedCornerSprites[cornerRadius] = sprite;
                }

                image.sprite = sprite;
                image.type = Image.Type.Sliced;
                image.preserveAspect = false;
            }

            private Text CreateText(string name, Transform parent, int fontSize, FontStyle style, Color color, TextAnchor alignment)
            {
                var text = CreateRect(name, parent).gameObject.AddComponent<Text>();
                text.font = GetBuiltInFont();
                text.fontSize = fontSize;
                text.fontStyle = style;
                text.color = color;
                text.alignment = alignment;
                text.supportRichText = false;
                text.horizontalOverflow = HorizontalWrapMode.Wrap;
                text.verticalOverflow = VerticalWrapMode.Overflow;
                text.raycastTarget = false;
                var shadow = text.gameObject.AddComponent<Shadow>();
                shadow.effectColor = MakeColor(0, 10, 24, 190);
                shadow.effectDistance = new Vector2(2f, -2f);
                return text;
            }

            private void StretchRect(RectTransform rect, Vector2 anchorMin, Vector2 anchorMax, Vector2 offsetMin, Vector2 offsetMax)
            {
                rect.anchorMin = anchorMin;
                rect.anchorMax = anchorMax;
                rect.offsetMin = offsetMin;
                rect.offsetMax = offsetMax;
            }

            private void PlaceRect(RectTransform rect, Vector2 anchor, Vector2 pivot, Vector2 anchoredPosition, Vector2 size)
            {
                rect.anchorMin = anchor;
                rect.anchorMax = anchor;
                rect.pivot = pivot;
                rect.anchoredPosition = anchoredPosition;
                rect.sizeDelta = size;
            }
        }

        private sealed class GameUxDriver : MonoBehaviour
        {
            private static Sprite _roundedTimerSprite;
            private static Sprite _roundedStateSprite;
            private static readonly Dictionary<int, Sprite> RoundedCardSprites = new Dictionary<int, Sprite>();
            private readonly Color _accentBlue = MakeColor(78, 210, 255, 255);
            private readonly Color _accentGold = MakeColor(255, 194, 78, 255);
            private readonly Color _cardFill = MakeColor(4, 83, 143, 248);
            private readonly Color _cardStroke = MakeColor(113, 207, 255, 110);
            private readonly Color _mutedRail = MakeColor(137, 190, 220, 180);
            private readonly Color _redColor = MakeColor(255, 91, 91, 255);
            private readonly Color _softText = MakeColor(192, 218, 238, 255);
            private readonly List<Image> _progressSegments = new List<Image>();

            private bool _chromeReady;
            private bool _largeText;
            private bool _presentationReady;
            private bool _shortcutsUnavailable;
            private bool _personalMode;
            private int _lastQuestionIndex = -1;
            private float _startedAt;
            private float _toastUntil;
            private float _nextUiRefreshAt;
            private float _nextLeaderboardRootScanAt;
            private bool _leaderboardRootsScanned;
            private string _lastFittedQuestionText = string.Empty;
            private readonly List<Transform> _leaderboardEntryRoots = new List<Transform>();
            private Canvas _canvas;
            private RectTransform _root;
            private RectTransform _rankListRoot;
            private Image _roundCard;
            private Image _personalTopCover;
            private Image _personalTopCleanup;
            private Image _personalRankHeaderCover;
            private Image _questionCard;
            private Image _questionAccent;
            private Image _stateCard;
            private Image _toastCard;
            private Image _leaderboardEmptyPanel;
            private Text _roundLabel;
            private Text _timerValue;
            private Text _redTeamLabel;
            private Text _blueTeamLabel;
            private Text _stateLabel;
            private Text _questionMeta;
            private Text _questionCounter;
            private Text _questionBody;
            private Text _leaderboardEmpty;
            private Text _personalRankHeader;
            private Text _shortcutHint;
            private Text _toastText;
            private Text _nativeRoundText;
            private Text _nativeTimerText;
            private Text _rankTitleText;
            private Image _redHealthImage;
            private Image _blueHealthImage;
            private Image _subjectImage;
            private CanvasGroup _toastGroup;
            private CanvasGroup _shortcutGroup;

            private void Update()
            {
                try
                {
                    if (!ResolveNativeUi())
                    {
                        return;
                    }

                    EnsureChrome();
                    HandleShortcuts();
                    if (Time.unscaledTime < _nextUiRefreshAt)
                    {
                        return;
                    }

                    _nextUiRefreshAt = Time.unscaledTime + 0.1f;

                    var snapshot = CaptureSnapshot();
                    if (!_presentationReady)
                    {
                        if (!IsPresentationReady(snapshot))
                        {
                            _root.gameObject.SetActive(false);
                            return;
                        }

                        _presentationReady = true;
                        _root.gameObject.SetActive(true);
                        _startedAt = Time.unscaledTime;
                    }

                    ApplyNativeUiPolish();
                    ApplySnapshot(snapshot);
                    UpdateLeaderboardEmptyState();
                    UpdateTransientFeedback(snapshot);
                }
                catch (Exception ex)
                {
                    Debug.LogWarning("[CodexPatch] UX enhancement update failed: " + ex.Message);
                }
            }

            private bool ResolveNativeUi()
            {
                if (_nativeRoundText != null && _nativeTimerText != null && _rankListRoot != null)
                {
                    return true;
                }

                var uiManagerType = FindAssemblyType("UIManager");
                var uiManager = uiManagerType == null ? null : GetStaticPropertyValue(uiManagerType, "Instance");
                if (uiManager == null)
                {
                    return false;
                }

                _nativeRoundText = ReadPropertyOrField(uiManager, "roundText") as Text;
                _nativeTimerText = ReadPropertyOrField(uiManager, "timerText") as Text;
                _redHealthImage = ReadPropertyOrField(uiManager, "redHealthImage") as Image;
                _blueHealthImage = ReadPropertyOrField(uiManager, "blueHealthImage") as Image;
                _subjectImage = ReadPropertyOrField(uiManager, "SubjectImage") as Image;
                _rankListRoot = FindRuntimeRectTransform("UI/center/Ranklist");

                var closeRoot = FindRuntimeRectTransform("UI/CLOSE");
                if (closeRoot != null)
                {
                    closeRoot.gameObject.SetActive(false);
                }

                var rankTitleObject = GameObject.Find("UI/center/Ranklist/text");
                _rankTitleText = rankTitleObject == null ? null : rankTitleObject.GetComponent<Text>();
                _personalMode = IsPersonalMode();
                return _nativeRoundText != null && _nativeTimerText != null && _rankListRoot != null;
            }

            private void EnsureChrome()
            {
                if (_chromeReady)
                {
                    return;
                }

                var canvasObject = new GameObject("CodexGameUxCanvas");
                UnityEngine.Object.DontDestroyOnLoad(canvasObject);
                _canvas = canvasObject.AddComponent<Canvas>();
                _canvas.renderMode = RenderMode.ScreenSpaceOverlay;
                _canvas.sortingOrder = 9200;

                var scaler = canvasObject.AddComponent<CanvasScaler>();
                scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
                scaler.referenceResolution = new Vector2(1920f, 1080f);
                scaler.matchWidthOrHeight = 0.5f;

                var raycaster = canvasObject.AddComponent<GraphicRaycaster>();
                raycaster.enabled = false;

                _root = CreateRect("UxRoot", canvasObject.transform);
                StretchRect(_root, Vector2.zero, Vector2.one, Vector2.zero, Vector2.zero);

                _roundCard = CreateCard("RoundCard", _root, MakeColor(4, 105, 169, 250));
                PlaceRect(_roundCard.rectTransform, new Vector2(0f, 1f), new Vector2(0f, 1f), new Vector2(42f, -24f), new Vector2(276f, 134f));
                ApplyRoundedCardShape(_roundCard, ref _roundedTimerSprite, 276, 134, 24f, "CodexRoundedTimerSprite");
                var roundAccent = CreateImage("RoundAccent", _roundCard.rectTransform, _accentBlue);
                StretchRect(roundAccent.rectTransform, new Vector2(0f, 0f), new Vector2(1f, 0f), new Vector2(18f, 0f), new Vector2(-18f, 6f));

                _roundLabel = CreateText("RoundLabel", _roundCard.rectTransform, 22, FontStyle.Bold, Color.white, TextAnchor.UpperLeft);
                PlaceRect(_roundLabel.rectTransform, new Vector2(0f, 1f), new Vector2(0f, 1f), new Vector2(22f, -16f), new Vector2(232f, 30f));
                _timerValue = CreateText("TimerValue", _roundCard.rectTransform, 60, FontStyle.Bold, Color.white, TextAnchor.MiddleLeft);
                PlaceRect(_timerValue.rectTransform, new Vector2(0f, 0f), new Vector2(0f, 0f), new Vector2(22f, 15f), new Vector2(232f, 76f));

                if (_personalMode)
                {
                    _personalTopCover = CreateImage("PersonalTeamStripCover", _root, MakeColor(0, 139, 211, 255));
                    PlaceRect(_personalTopCover.rectTransform, new Vector2(0.5f, 1f), new Vector2(0.5f, 1f), new Vector2(0f, -104f), new Vector2(360f, 120f));
                }

                _stateCard = CreateCard("StateCard", _root, MakeColor(4, 105, 169, 248));
                PlaceRect(_stateCard.rectTransform, new Vector2(0.5f, 1f), new Vector2(0.5f, 1f), new Vector2(0f, -24f), new Vector2(280f, 58f));
                ApplyRoundedCardShape(_stateCard, ref _roundedStateSprite, 280, 58, 18f, "CodexRoundedStateSprite");
                _stateLabel = CreateText("StateLabel", _stateCard.rectTransform, 25, FontStyle.Bold, Color.white, TextAnchor.MiddleCenter);
                StretchRect(_stateLabel.rectTransform, Vector2.zero, Vector2.one, new Vector2(10f, 5f), new Vector2(-10f, -5f));

                _redTeamLabel = CreateText("RedTeamLabel", _root, 30, FontStyle.Bold, _redColor, TextAnchor.MiddleCenter);
                PlaceRect(_redTeamLabel.rectTransform, new Vector2(0f, 1f), new Vector2(0f, 1f), new Vector2(292f, -60f), new Vector2(540f, 42f));
                _blueTeamLabel = CreateText("BlueTeamLabel", _root, 30, FontStyle.Bold, _accentBlue, TextAnchor.MiddleCenter);
                PlaceRect(_blueTeamLabel.rectTransform, new Vector2(1f, 1f), new Vector2(1f, 1f), new Vector2(-292f, -60f), new Vector2(540f, 42f));

                _questionCard = CreateCard("QuestionCard", _root, _cardFill);
                PlaceRect(_questionCard.rectTransform, new Vector2(0.5f, 0f), new Vector2(0.5f, 0f), new Vector2(0f, 72f), new Vector2(930f, 276f));
                _questionAccent = CreateImage("QuestionAccent", _questionCard.rectTransform, _accentBlue);
                StretchRect(_questionAccent.rectTransform, new Vector2(0f, 1f), new Vector2(1f, 1f), new Vector2(24f, -7f), new Vector2(-24f, 0f));

                _questionMeta = CreateText("QuestionMeta", _questionCard.rectTransform, 20, FontStyle.Bold, _accentGold, TextAnchor.UpperLeft);
                PlaceRect(_questionMeta.rectTransform, new Vector2(0f, 1f), new Vector2(0f, 1f), new Vector2(28f, -18f), new Vector2(590f, 30f));
                _questionCounter = CreateText("QuestionCounter", _questionCard.rectTransform, 20, FontStyle.Bold, Color.white, TextAnchor.UpperRight);
                PlaceRect(_questionCounter.rectTransform, new Vector2(1f, 1f), new Vector2(1f, 1f), new Vector2(-28f, -18f), new Vector2(270f, 30f));
                _questionBody = CreateText("QuestionBody", _questionCard.rectTransform, QuestionBaseFontSize, FontStyle.Bold, Color.white, TextAnchor.UpperLeft);
                PlaceRect(_questionBody.rectTransform, new Vector2(0f, 1f), new Vector2(0f, 1f), new Vector2(28f, -58f), QuestionAuditBoxSize);

                for (var i = 0; i < RoundSize; i++)
                {
                    var segment = CreateImage("ProgressSegment" + (i + 1), _questionCard.rectTransform, _mutedRail);
                    PlaceRect(segment.rectTransform, new Vector2(0f, 0f), new Vector2(0f, 0f), new Vector2(28f + i * 87f, 16f), new Vector2(79f, 9f));
                    _progressSegments.Add(segment);
                }

                _leaderboardEmptyPanel = CreateCard("LeaderboardEmptyPanel", _root, Color.white);
                _leaderboardEmptyPanel.enabled = false;

                _leaderboardEmpty = CreateText("LeaderboardEmpty", _root, 19, FontStyle.Bold, MakeColor(20, 73, 108, 255), TextAnchor.MiddleCenter);
                PlaceRect(_leaderboardEmpty.rectTransform, new Vector2(1f, 1f), new Vector2(0.5f, 0.5f), new Vector2(-280f, -430f), new Vector2(280f, 92f));
                _leaderboardEmpty.text = "Waiting for Players";
                var emptyStateShadow = _leaderboardEmpty.GetComponent<Shadow>();
                if (emptyStateShadow != null)
                {
                    emptyStateShadow.enabled = false;
                }

                if (_personalMode)
                {
                    _personalRankHeaderCover = CreateImage("PersonalRankHeaderCover", _root, MakeColor(255, 255, 255, 255));
                    PlaceRect(_personalRankHeaderCover.rectTransform, new Vector2(1f, 1f), new Vector2(0.5f, 0.5f), new Vector2(-280f, -282f), new Vector2(350f, 82f));

                    _personalRankHeader = CreateText("PersonalRankHeader", _root, 25, FontStyle.Bold, MakeColor(20, 73, 108, 255), TextAnchor.MiddleCenter);
                    PlaceRect(_personalRankHeader.rectTransform, new Vector2(1f, 1f), new Vector2(0.5f, 0.5f), new Vector2(-280f, -282f), new Vector2(320f, 48f));
                    _personalRankHeader.text = "PERSONAL RANK";
                    var rankHeaderShadow = _personalRankHeader.GetComponent<Shadow>();
                    if (rankHeaderShadow != null)
                    {
                        rankHeaderShadow.enabled = false;
                    }

                    _personalTopCleanup = CreateImage("PersonalTopCleanup", _root, MakeColor(0, 139, 211, 255));
                    PlaceRect(_personalTopCleanup.rectTransform, new Vector2(0.5f, 1f), new Vector2(0.5f, 1f), new Vector2(0f, -84f), new Vector2(420f, 68f));
                }

                _toastCard = CreateCard("QuestionToast", _root, MakeColor(4, 83, 143, 245));
                PlaceRect(_toastCard.rectTransform, new Vector2(0.5f, 1f), new Vector2(0.5f, 1f), new Vector2(0f, -172f), new Vector2(390f, 54f));
                _toastText = CreateText("QuestionToastText", _toastCard.rectTransform, 20, FontStyle.Bold, Color.white, TextAnchor.MiddleCenter);
                StretchRect(_toastText.rectTransform, Vector2.zero, Vector2.one, new Vector2(16f, 8f), new Vector2(-16f, -8f));
                _toastGroup = _toastCard.gameObject.AddComponent<CanvasGroup>();
                _toastGroup.alpha = 0f;

                _shortcutHint = CreateText("ShortcutHint", _root, 18, FontStyle.Bold, Color.white, TextAnchor.MiddleLeft);
                PlaceRect(_shortcutHint.rectTransform, new Vector2(0f, 0f), new Vector2(0f, 0f), new Vector2(46f, 24f), new Vector2(700f, 30f));
                _shortcutHint.text = "DISPLAY CONTROLS   F11  FULLSCREEN   |   F10  TEXT SIZE";
                _shortcutGroup = _shortcutHint.gameObject.AddComponent<CanvasGroup>();

                _largeText = PlayerPrefs.GetInt("CodexLargeQuestionText", 0) == 1;
                ApplyQuestionTypography();
                _root.gameObject.SetActive(false);
                _chromeReady = true;
                Debug.Log("[CodexPatch] UX enhancement layer ready.");
            }

            private bool IsPresentationReady(ProductSnapshot snapshot)
            {
                var question = FirstNonBlank(snapshot.QuestionText, snapshot.DisplayQuestionText);
                return snapshot.TotalQuestions >= RoundSize
                    && !string.IsNullOrWhiteSpace(question)
                    && _subjectImage != null
                    && _subjectImage.sprite != null;
            }

            private void ApplyNativeUiPolish()
            {
                if (_nativeRoundText != null)
                {
                    _nativeRoundText.enabled = false;
                }

                if (_nativeTimerText != null)
                {
                    _nativeTimerText.enabled = false;
                }

                if (_questionOverlayCanvas != null)
                {
                    _questionOverlayCanvas.enabled = false;
                }

                if (_rankTitleText != null)
                {
                    _rankTitleText.enabled = !_personalMode;
                }

                if (_personalMode)
                {
                    var nativeUpperRoot = FindRuntimeRectTransform("UI/up");
                    if (nativeUpperRoot != null && nativeUpperRoot.gameObject.activeSelf)
                    {
                        nativeUpperRoot.gameObject.SetActive(false);
                    }
                }

                HidePersonalVersusText();

                if (!_personalMode)
                {
                    if (_redHealthImage != null)
                    {
                        _redHealthImage.enabled = true;
                        _redHealthImage.color = _redColor;
                    }

                    if (_blueHealthImage != null)
                    {
                        _blueHealthImage.enabled = true;
                        _blueHealthImage.color = _accentBlue;
                    }
                }
            }

            private void HidePersonalVersusText()
            {
                if (!_personalMode)
                {
                    return;
                }

                var upperRoot = FindRuntimeRectTransform("UI/up");
                if (upperRoot == null)
                {
                    return;
                }

                var texts = upperRoot.GetComponentsInChildren<Text>(true);
                for (var i = 0; i < texts.Length; i++)
                {
                    var text = texts[i];
                    if (text != null && string.Equals((text.text ?? string.Empty).Trim(), "VS", StringComparison.OrdinalIgnoreCase))
                    {
                        text.enabled = false;
                    }
                }
            }

            private void ApplySnapshot(ProductSnapshot snapshot)
            {
                var total = Math.Max(1, snapshot.TotalQuestions);
                var questionNumber = Math.Max(1, Math.Min(total, snapshot.CurrentQuestionIndex <= 0 ? 1 : snapshot.CurrentQuestionIndex));
                var state = ResolveStateLabel(snapshot);
                var critical = IsCriticalTimer(snapshot.TimerText);

                _roundLabel.text = string.Format("ROUND {0:00} OF {1:00}", questionNumber, total);
                _timerValue.text = string.IsNullOrWhiteSpace(snapshot.TimerText) ? "--:--" : snapshot.TimerText.Trim();
                _timerValue.color = critical ? MakeColor(255, 226, 118, 255) : Color.white;
                _stateLabel.text = _personalMode ? state + "  |  SOLO" : state;
                _stateCard.color = ResolveStateColor(state);
                _questionAccent.color = critical ? _redColor : _accentBlue;
                _questionMeta.text = BuildQuestionMeta(snapshot);
                _questionCounter.text = string.Format("QUESTION {0:00} / {1:00}", questionNumber, total);
                var questionText = NormalizeQuestionText(FirstNonBlank(snapshot.QuestionText, snapshot.DisplayQuestionText), 300);
                if (!string.Equals(questionText, _lastFittedQuestionText, StringComparison.Ordinal))
                {
                    _lastFittedQuestionText = questionText;
                    _questionBody.text = questionText;
                    ApplyQuestionTypography();
                }

                _redTeamLabel.enabled = !_personalMode;
                _blueTeamLabel.enabled = !_personalMode;
                if (!_personalMode)
                {
                    _redTeamLabel.text = string.Format("RED TEAM   {0} HP", Math.Max(0, snapshot.RedHealth));
                    _blueTeamLabel.text = string.Format("BLUE TEAM   {0} HP", Math.Max(0, snapshot.BlueHealth));
                }

                for (var i = 0; i < _progressSegments.Count; i++)
                {
                    var position = i + 1;
                    _progressSegments[i].enabled = position <= total;
                    _progressSegments[i].color = position < questionNumber
                        ? _accentBlue
                        : (position == questionNumber ? _accentGold : _mutedRail);
                }

                if (_lastQuestionIndex != questionNumber)
                {
                    _toastText.text = _lastQuestionIndex < 0
                        ? string.Format("QUESTION {0:00} READY", questionNumber)
                        : string.Format("NEXT UP   QUESTION {0:00} OF {1:00}", questionNumber, total);
                    _toastUntil = Time.unscaledTime + 2.4f;
                    _lastQuestionIndex = questionNumber;
                }
            }

            private string BuildQuestionMeta(ProductSnapshot snapshot)
            {
                var category = string.IsNullOrWhiteSpace(snapshot.Category) ? string.Empty : snapshot.Category.Trim();
                var era = string.IsNullOrWhiteSpace(snapshot.Era) ? string.Empty : snapshot.Era.Trim();
                if (!ContainsLetter(era))
                {
                    era = string.Empty;
                }
                if (!string.IsNullOrWhiteSpace(category) && !string.IsNullOrWhiteSpace(era)
                    && !string.Equals(category, era, StringComparison.OrdinalIgnoreCase))
                {
                    return (category + "  |  " + era).ToUpperInvariant();
                }

                if (!string.IsNullOrWhiteSpace(category))
                {
                    return category.ToUpperInvariant();
                }

                if (!string.IsNullOrWhiteSpace(era))
                {
                    return era.ToUpperInvariant();
                }

                return "HISTORY CHALLENGE";
            }

            private bool ContainsLetter(string value)
            {
                if (string.IsNullOrWhiteSpace(value))
                {
                    return false;
                }

                for (var i = 0; i < value.Length; i++)
                {
                    if (char.IsLetter(value[i]))
                    {
                        return true;
                    }
                }

                return false;
            }

            private string ResolveStateLabel(ProductSnapshot snapshot)
            {
                if (snapshot.IsGameActive)
                {
                    return IsCriticalTimer(snapshot.TimerText) ? "FINAL SECONDS" : "LIVE";
                }

                if (snapshot.CurrentQuestionIndex > 0 && snapshot.CurrentQuestionIndex >= snapshot.TotalQuestions)
                {
                    return "ROUND COMPLETE";
                }

                if (!string.IsNullOrWhiteSpace(snapshot.QuestionId))
                {
                    return "READY";
                }

                return "STANDBY";
            }

            private Color ResolveStateColor(string state)
            {
                if (state == "FINAL SECONDS")
                {
                    return MakeColor(128, 39, 42, 244);
                }

                if (state == "LIVE")
                {
                    return MakeColor(15, 91, 119, 244);
                }

                if (state == "ROUND COMPLETE")
                {
                    return MakeColor(69, 76, 101, 244);
                }

                return MakeColor(27, 56, 78, 242);
            }

            private bool IsCriticalTimer(string timerValue)
            {
                TimeSpan remaining;
                return !string.IsNullOrWhiteSpace(timerValue)
                    && TimeSpan.TryParse(timerValue.Trim(), out remaining)
                    && remaining.TotalSeconds <= 10d;
            }

            private void UpdateLeaderboardEmptyState()
            {
                if (_leaderboardEmpty == null || _rankListRoot == null)
                {
                    return;
                }

                var hasLivePlayers = HasLivePlayers();
                var nativeRankRect = GetScreenRect(_rankListRoot);
                if (hasLivePlayers)
                {
                    if (!_rankListRoot.gameObject.activeSelf)
                    {
                        _rankListRoot.gameObject.SetActive(true);
                    }

                    if (_leaderboardEmptyPanel != null)
                    {
                        _leaderboardEmptyPanel.enabled = false;
                    }
                }
                else
                {
                    _rankListRoot.gameObject.SetActive(false);
                    if (_leaderboardEmptyPanel != null)
                    {
                        nativeRankRect.xMin = Math.Max(0f, nativeRankRect.xMin - 80f);
                        nativeRankRect.xMax = Math.Min(Screen.width, nativeRankRect.xMax + 80f);
                        nativeRankRect.yMin = Math.Max(0f, nativeRankRect.yMin - 240f);
                        nativeRankRect.yMax = Math.Min(Screen.height, nativeRankRect.yMax + 180f);
                        FitOverlayToScreenRect(_leaderboardEmptyPanel.rectTransform, nativeRankRect);
                        _leaderboardEmptyPanel.enabled = true;
                    }
                }

                SetLeaderboardEntryRowsVisible(hasLivePlayers);
                if (!hasLivePlayers)
                {
                    _leaderboardEmpty.text = "Waiting for Players";
                    _leaderboardEmpty.enabled = true;
                    return;
                }

                _leaderboardEmpty.enabled = !HasRealLeaderboardName();
            }

            private bool HasLivePlayers()
            {
                lock (Gate)
                {
                    if (LiveViewers.Values.Any(viewer => IsRealPlayerName(viewer.Name, viewer.UserId)))
                    {
                        return true;
                    }

                    if (LocalScores.Values.Any(score => IsRealPlayerName(score.Name, score.UserId)))
                    {
                        return true;
                    }
                }

                return HasRealLeaderboardName();
            }

            private bool HasRealLeaderboardName()
            {
                var entryRoots = GetLeaderboardEntryRoots();
                for (var i = 0; i < entryRoots.Count; i++)
                {
                    var entryRoot = entryRoots[i];
                    if (entryRoot == null)
                    {
                        continue;
                    }

                    var texts = entryRoot.GetComponentsInChildren<Text>(true);
                    for (var j = 0; j < texts.Length; j++)
                    {
                        var text = texts[j];
                        if (text != null
                            && string.Equals(text.gameObject.name, "Name", StringComparison.OrdinalIgnoreCase)
                            && IsRealPlayerName(text.text, null))
                        {
                            return true;
                        }
                    }
                }

                return false;
            }

            private List<Transform> GetLeaderboardEntryRoots()
            {
                if (_leaderboardRootsScanned && Time.unscaledTime < _nextLeaderboardRootScanAt)
                {
                    return _leaderboardEntryRoots;
                }

                _leaderboardRootsScanned = true;
                _nextLeaderboardRootScanAt = Time.unscaledTime + 0.25f;
                _leaderboardEntryRoots.Clear();
                var candidates = Resources.FindObjectsOfTypeAll<Transform>();
                for (var i = 0; i < candidates.Length; i++)
                {
                    var candidate = candidates[i];
                    if (candidate == null || !candidate.gameObject.scene.IsValid() || candidate.childCount < 3)
                    {
                        continue;
                    }

                    var hasColor = false;
                    var hasName = false;
                    var hasScore = false;
                    for (var j = 0; j < candidate.childCount; j++)
                    {
                        var childName = candidate.GetChild(j).name;
                        hasColor = hasColor || string.Equals(childName, "Color", StringComparison.OrdinalIgnoreCase);
                        hasName = hasName || string.Equals(childName, "Name", StringComparison.OrdinalIgnoreCase);
                        hasScore = hasScore || string.Equals(childName, "Score", StringComparison.OrdinalIgnoreCase);
                    }

                    if (hasColor && hasName && hasScore)
                    {
                        _leaderboardEntryRoots.Add(candidate);
                    }
                }

                return _leaderboardEntryRoots;
            }

            private bool IsRealPlayerName(string name, string fallbackId)
            {
                var value = string.IsNullOrWhiteSpace(name) ? fallbackId : name;
                if (string.IsNullOrWhiteSpace(value))
                {
                    return false;
                }

                var compact = new string(value.Trim().Where(char.IsLetterOrDigit).ToArray());
                if (compact.Length == 0)
                {
                    return false;
                }

                foreach (var prefix in new[] { "redplayer", "blueplayer" })
                {
                    if (!compact.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    var suffix = compact.Substring(prefix.Length);
                    if (suffix.Length > 0 && suffix.All(char.IsDigit))
                    {
                        return false;
                    }
                }

                return true;
            }

            private void SetLeaderboardEntryRowsVisible(bool visible)
            {
                var entryRoots = GetLeaderboardEntryRoots();
                for (var i = 0; i < entryRoots.Count; i++)
                {
                    var entryRoot = entryRoots[i];
                    if (entryRoot == null)
                    {
                        continue;
                    }

                    var graphics = entryRoot.GetComponentsInChildren<Graphic>(true);
                    for (var j = 0; j < graphics.Length; j++)
                    {
                        var graphic = graphics[j];
                        if (graphic != null)
                        {
                            graphic.enabled = visible;
                        }
                    }

                }
            }

            private Rect GetScreenRect(RectTransform rect)
            {
                if (rect == null)
                {
                    return new Rect(0f, 0f, 0f, 0f);
                }

                var corners = new Vector3[4];
                rect.GetWorldCorners(corners);
                return Rect.MinMaxRect(corners[0].x, corners[0].y, corners[2].x, corners[2].y);
            }

            private void FitOverlayToScreenRect(RectTransform rect, Rect screenRect)
            {
                if (rect == null || Screen.width <= 0 || Screen.height <= 0)
                {
                    return;
                }

                rect.anchorMin = new Vector2(
                    Mathf.Clamp01(screenRect.xMin / Screen.width),
                    Mathf.Clamp01(screenRect.yMin / Screen.height));
                rect.anchorMax = new Vector2(
                    Mathf.Clamp01(screenRect.xMax / Screen.width),
                    Mathf.Clamp01(screenRect.yMax / Screen.height));
                rect.pivot = new Vector2(0.5f, 0.5f);
                rect.offsetMin = Vector2.zero;
                rect.offsetMax = Vector2.zero;
            }

            private void UpdateTransientFeedback(ProductSnapshot snapshot)
            {
                if (_toastGroup != null)
                {
                    var remaining = _toastUntil - Time.unscaledTime;
                    _toastGroup.alpha = remaining <= 0f ? 0f : Mathf.Clamp01(remaining * 1.6f);
                }

                if (_shortcutGroup != null)
                {
                    var elapsed = Time.unscaledTime - _startedAt;
                    _shortcutGroup.alpha = elapsed < 7f ? 1f : Mathf.Clamp01((10f - elapsed) / 3f);
                }

                if (snapshot.IsGameActive && IsCriticalTimer(snapshot.TimerText))
                {
                    var pulse = 0.82f + Mathf.Abs(Mathf.Sin(Time.unscaledTime * 5f)) * 0.18f;
                    var color = _redColor;
                    color.a = pulse;
                    _timerValue.color = color;
                }
            }

            private void HandleShortcuts()
            {
                if (_shortcutsUnavailable)
                {
                    return;
                }

                try
                {
                    if (Input.GetKeyDown(KeyCode.F11))
                    {
                        Screen.fullScreen = !Screen.fullScreen;
                    }

                    if (Input.GetKeyDown(KeyCode.Escape) && Screen.fullScreen)
                    {
                        Screen.fullScreen = false;
                    }

                    if (Input.GetKeyDown(KeyCode.F10))
                    {
                        _largeText = !_largeText;
                        PlayerPrefs.SetInt("CodexLargeQuestionText", _largeText ? 1 : 0);
                        PlayerPrefs.Save();
                        ApplyQuestionTypography();
                        _toastText.text = _largeText ? "LARGE QUESTION TEXT ON" : "STANDARD QUESTION TEXT ON";
                        _toastUntil = Time.unscaledTime + 1.8f;
                    }
                }
                catch (Exception ex)
                {
                    _shortcutsUnavailable = true;
                    if (_shortcutHint != null)
                    {
                        _shortcutHint.enabled = false;
                    }
                    Debug.LogWarning("[CodexPatch] Display shortcuts unavailable: " + ex.Message);
                }
            }

            private void ApplyQuestionTypography()
            {
                if (_questionBody == null)
                {
                    return;
                }

                var desiredSize = _largeText ? 42 : ResolveQuestionMaxFontSize(_questionBody.text);
                var minimumSize = _largeText ? 24 : QuestionMinFontSize;
                _questionBody.resizeTextForBestFit = false;
                _questionBody.horizontalOverflow = HorizontalWrapMode.Wrap;
                _questionBody.verticalOverflow = VerticalWrapMode.Truncate;
                _questionBody.lineSpacing = 0.86f;
                FitQuestionTextWithinBounds(_questionBody, _questionBody.text, desiredSize, minimumSize);
            }

            private RectTransform CreateRect(string name, Transform parent)
            {
                var go = new GameObject(name, typeof(RectTransform));
                go.transform.SetParent(parent, false);
                return go.GetComponent<RectTransform>();
            }

            private Image CreateImage(string name, Transform parent, Color color)
            {
                var rect = CreateRect(name, parent);
                var image = rect.gameObject.AddComponent<Image>();
                image.color = color;
                image.raycastTarget = false;
                return image;
            }

            private Image CreateCard(string name, Transform parent, Color color)
            {
                var image = CreateImage(name, parent, color);
                ApplyRoundedSlicedShape(image, 24);
                AddOutline(image.gameObject, _cardStroke, new Vector2(1f, -1f));
                AddShadow(image.gameObject, MakeColor(0, 57, 105, 180), new Vector2(0f, -8f));
                return image;
            }

            private void ApplyRoundedSlicedShape(Image image, int cornerRadius)
            {
                if (image == null)
                {
                    return;
                }

                Sprite sprite;
                if (!RoundedCardSprites.TryGetValue(cornerRadius, out sprite))
                {
                    const int textureSize = 128;
                    var texture = new Texture2D(textureSize, textureSize, TextureFormat.RGBA32, false);
                    texture.name = "CodexGameRounded" + cornerRadius + "Texture";
                    texture.filterMode = FilterMode.Bilinear;
                    texture.wrapMode = TextureWrapMode.Clamp;

                    var pixels = new Color[textureSize * textureSize];
                    for (var y = 0; y < textureSize; y++)
                    {
                        for (var x = 0; x < textureSize; x++)
                        {
                            var px = x + 0.5f;
                            var py = y + 0.5f;
                            var horizontal = Mathf.Min(px, textureSize - px);
                            var vertical = Mathf.Min(py, textureSize - py);
                            var dx = Mathf.Max(cornerRadius - horizontal, 0f);
                            var dy = Mathf.Max(cornerRadius - vertical, 0f);
                            var distance = Mathf.Sqrt(dx * dx + dy * dy);
                            var alpha = Mathf.Clamp01(cornerRadius + 0.75f - distance);
                            pixels[y * textureSize + x] = new Color(1f, 1f, 1f, alpha);
                        }
                    }

                    texture.SetPixels(pixels);
                    texture.Apply(false, true);
                    sprite = Sprite.Create(
                        texture,
                        new Rect(0f, 0f, textureSize, textureSize),
                        new Vector2(0.5f, 0.5f),
                        100f,
                        0,
                        SpriteMeshType.FullRect,
                        new Vector4(cornerRadius, cornerRadius, cornerRadius, cornerRadius));
                    sprite.name = "CodexGameRounded" + cornerRadius;
                    RoundedCardSprites[cornerRadius] = sprite;
                }

                image.sprite = sprite;
                image.type = Image.Type.Sliced;
                image.preserveAspect = false;
            }

            private void ApplyRoundedCardShape(Image image, ref Sprite roundedSprite, int width, int height, float radius, string spriteName)
            {
                if (image == null)
                {
                    return;
                }

                if (roundedSprite == null)
                {
                    var texture = new Texture2D(width, height, TextureFormat.RGBA32, false);
                    texture.name = spriteName + "Texture";
                    texture.filterMode = FilterMode.Bilinear;
                    texture.wrapMode = TextureWrapMode.Clamp;

                    var pixels = new Color[width * height];
                    for (var y = 0; y < height; y++)
                    {
                        for (var x = 0; x < width; x++)
                        {
                            var px = x + 0.5f;
                            var py = y + 0.5f;
                            var dx = Mathf.Max(radius - px, px - (width - radius), 0f);
                            var dy = Mathf.Max(radius - py, py - (height - radius), 0f);
                            var distance = Mathf.Sqrt(dx * dx + dy * dy);
                            var alpha = Mathf.Clamp01(radius + 0.75f - distance);
                            pixels[y * width + x] = new Color(1f, 1f, 1f, alpha);
                        }
                    }

                    texture.SetPixels(pixels);
                    texture.Apply(false, true);
                    roundedSprite = Sprite.Create(
                        texture,
                        new Rect(0f, 0f, width, height),
                        new Vector2(0.5f, 0.5f),
                        100f);
                    roundedSprite.name = spriteName;
                }

                image.sprite = roundedSprite;
                image.type = Image.Type.Simple;
                image.preserveAspect = false;
            }

            private Text CreateText(string name, Transform parent, int fontSize, FontStyle style, Color color, TextAnchor alignment)
            {
                var rect = CreateRect(name, parent);
                var text = rect.gameObject.AddComponent<Text>();
                text.font = GetBuiltInFont();
                text.fontSize = fontSize;
                text.fontStyle = style;
                text.color = color;
                text.alignment = alignment;
                text.supportRichText = false;
                text.horizontalOverflow = HorizontalWrapMode.Wrap;
                text.verticalOverflow = VerticalWrapMode.Overflow;
                text.raycastTarget = false;
                AddShadow(text.gameObject, MakeColor(0, 10, 24, 190), new Vector2(2f, -2f));
                return text;
            }

            private void StretchRect(RectTransform rect, Vector2 anchorMin, Vector2 anchorMax, Vector2 offsetMin, Vector2 offsetMax)
            {
                rect.anchorMin = anchorMin;
                rect.anchorMax = anchorMax;
                rect.offsetMin = offsetMin;
                rect.offsetMax = offsetMax;
            }

            private void PlaceRect(RectTransform rect, Vector2 anchor, Vector2 pivot, Vector2 anchoredPosition, Vector2 size)
            {
                rect.anchorMin = anchor;
                rect.anchorMax = anchor;
                rect.pivot = pivot;
                rect.anchoredPosition = anchoredPosition;
                rect.sizeDelta = size;
            }

            private void AddShadow(GameObject target, Color color, Vector2 distance)
            {
                var shadow = target.GetComponent<Shadow>();
                if (shadow == null)
                {
                    shadow = target.AddComponent<Shadow>();
                }

                shadow.effectColor = color;
                shadow.effectDistance = distance;
            }

            private void AddOutline(GameObject target, Color color, Vector2 distance)
            {
                var outline = target.GetComponent<Outline>();
                if (outline == null)
                {
                    outline = target.AddComponent<Outline>();
                }

                outline.effectColor = color;
                outline.effectDistance = distance;
            }
        }

        private sealed class ProductShellDriver : MonoBehaviour
        {
            private readonly Color _accentBlue = MakeColor(77, 209, 255, 255);
            private readonly Color _accentGold = MakeColor(255, 196, 79, 255);
            private readonly Color _panelFill = MakeColor(7, 18, 33, 228);
            private readonly Color _panelStroke = MakeColor(101, 196, 255, 72);
            private readonly Color _redColor = MakeColor(255, 88, 88, 255);
            private readonly Color _softText = MakeColor(188, 214, 235, 255);

            private bool _chromeReady;
            private bool _nativeUiStyled;
            private bool _prototypeUiHidden;
            private string _lastSnapshotKey = string.Empty;
            private Canvas _productCanvas;
            private RectTransform _overlayRoot;
            private readonly List<GameObject> _prototypeContainers = new List<GameObject>();
            private Image _brandCard;
            private Image _timerCard;
            private Image _scoreCard;
            private Image _questionCard;
            private Image _visualCard;
            private Image _questionAccent;
            private Image _visualAccent;
            private Image _blueTrack;
            private Image _blueFill;
            private Image _redTrack;
            private Image _redFill;
            private Text _infoText;
            private Text _questionText;
            private Text _responseText;
            private Text _roundText;
            private Image _subjectImage;
            private Text _subjectText;
            private Text _timerText;
            private Image _blueHealthImage;
            private Image _redHealthImage;

            private void Update()
            {
                try
                {
                    if (!ResolveNativeUi())
                    {
                        return;
                    }

                    EnsureProductChrome();
                    if (!_nativeUiStyled)
                    {
                        ApplyNativeUiPolish();
                        _nativeUiStyled = true;
                    }

                    ApplySnapshot(CaptureSnapshot());
                }
                catch (Exception ex)
                {
                    Debug.LogWarning("[CodexPatch] Product shell update failed: " + ex.Message);
                }
            }

            private bool ResolveNativeUi()
            {
                if (_timerText != null && _roundText != null && _subjectText != null && _questionText != null && _infoText != null)
                {
                    return true;
                }

                var uiManagerType = FindAssemblyType("UIManager");
                if (uiManagerType == null)
                {
                    return false;
                }

                var uiManager = GetStaticPropertyValue(uiManagerType, "Instance");
                if (uiManager == null)
                {
                    return false;
                }

                _timerText = ReadPropertyOrField(uiManager, "timerText") as Text;
                _roundText = ReadPropertyOrField(uiManager, "roundText") as Text;
                _subjectText = ReadPropertyOrField(uiManager, "SubjectText") as Text;
                _questionText = ReadPropertyOrField(uiManager, "QUESTIONText") as Text;
                _infoText = ReadPropertyOrField(uiManager, "infoText") as Text;
                _subjectImage = ReadPropertyOrField(uiManager, "SubjectImage") as Image;
                _blueHealthImage = ReadPropertyOrField(uiManager, "blueHealthImage") as Image;
                _redHealthImage = ReadPropertyOrField(uiManager, "redHealthImage") as Image;

                var demoUiType = FindAssemblyType("GameApiDemoUI");
                if (demoUiType != null)
                {
                    var demoUi = GetStaticPropertyValue(demoUiType, "Instance");
                    if (demoUi != null)
                    {
                        _responseText = GetInstanceFieldValue(demoUi, "responseText") as Text;
                    }
                }

                return _timerText != null && _roundText != null && _subjectText != null && _questionText != null && _infoText != null;
            }

            private void EnsureProductChrome()
            {
                if (_chromeReady)
                {
                    return;
                }

                var canvasGo = new GameObject("CodexProductCanvas");
                UnityEngine.Object.DontDestroyOnLoad(canvasGo);

                _productCanvas = canvasGo.AddComponent<Canvas>();
                _productCanvas.renderMode = RenderMode.ScreenSpaceOverlay;
                _productCanvas.sortingOrder = 5000;

                var scaler = canvasGo.AddComponent<CanvasScaler>();
                scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
                scaler.referenceResolution = new Vector2(1920f, 1080f);
                scaler.matchWidthOrHeight = 0.5f;

                var raycaster = canvasGo.AddComponent<GraphicRaycaster>();
                raycaster.enabled = false;

                _overlayRoot = CreateRect("CodexOverlayRoot", canvasGo.transform);
                StretchRect(_overlayRoot, Vector2.zero, Vector2.one, Vector2.zero, Vector2.zero);

                _brandCard = CreateCard("BrandCard", _overlayRoot, new Vector2(0f, 1f), new Vector2(0f, 1f), new Vector2(54f, -34f), new Vector2(448f, 112f), MakeColor(6, 17, 31, 234));
                _timerCard = CreateCard("TimerCard", _overlayRoot, new Vector2(0.5f, 1f), new Vector2(0.5f, 1f), new Vector2(0f, -34f), new Vector2(228f, 112f), MakeColor(10, 20, 36, 238));
                _scoreCard = CreateCard("ScoreCard", _overlayRoot, new Vector2(1f, 1f), new Vector2(1f, 1f), new Vector2(-54f, -34f), new Vector2(448f, 132f), MakeColor(6, 17, 31, 234));
                _questionCard = CreateCard("QuestionCard", _overlayRoot, new Vector2(0f, 0f), new Vector2(0f, 0f), new Vector2(54f, 40f), new Vector2(844f, 340f), _panelFill);
                _visualCard = CreateCard("VisualCard", _overlayRoot, new Vector2(1f, 0f), new Vector2(1f, 0f), new Vector2(-54f, 52f), new Vector2(500f, 548f), MakeColor(10, 18, 31, 240));

                _questionAccent = CreateAccentBar("QuestionAccent", _questionCard.rectTransform, _accentBlue, new Vector2(28f, -6f), new Vector2(-28f, 0f));
                _visualAccent = CreateAccentBar("VisualAccent", _visualCard.rectTransform, _accentGold, new Vector2(28f, -6f), new Vector2(-28f, 0f));
                CreateAccentBar("BrandAccent", _brandCard.rectTransform, _accentBlue, new Vector2(24f, -6f), new Vector2(-160f, 0f));
                CreateAccentBar("TimerAccent", _timerCard.rectTransform, _accentBlue, new Vector2(28f, -6f), new Vector2(-28f, 0f));
                CreateAccentBar("ScoreAccent", _scoreCard.rectTransform, _accentGold, new Vector2(24f, -6f), new Vector2(-24f, 0f));

                _blueTrack = CreateHealthTrack("BlueTrack", _scoreCard.rectTransform, new Vector2(24f, -64f), 400f);
                _blueFill = CreateHealthFill("BlueFill", _blueTrack.rectTransform, _accentBlue);
                _redTrack = CreateHealthTrack("RedTrack", _scoreCard.rectTransform, new Vector2(24f, -92f), 400f);
                _redFill = CreateHealthFill("RedFill", _redTrack.rectTransform, _redColor);

                RehomeGraphic(_roundText, _brandCard.rectTransform);
                RehomeGraphic(_timerText, _timerCard.rectTransform);
                RehomeGraphic(_subjectText, _questionCard.rectTransform);
                RehomeGraphic(_questionText, _questionCard.rectTransform);
                RehomeGraphic(_infoText, _scoreCard.rectTransform);
                RehomeGraphic(_subjectImage, _visualCard.rectTransform);

                if (_responseText != null)
                {
                    QueuePrototypeContainer(_responseText);
                }

                HidePrototypeContainers();

                _chromeReady = true;
            }

            private void ApplyNativeUiPolish()
            {
                StyleText(_timerText, 46, FontStyle.Bold, _accentBlue, TextAnchor.MiddleCenter, new Vector2(176f, 74f));
                PlaceRect(_timerText.rectTransform, new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), new Vector2(0f, 8f), new Vector2(176f, 74f));

                if (_roundText != null)
                {
                    _roundText.enabled = true;
                    StyleText(_roundText, 22, FontStyle.Bold, Color.white, TextAnchor.UpperLeft, new Vector2(380f, 72f));
                    PlaceRect(_roundText.rectTransform, new Vector2(0f, 1f), new Vector2(0f, 1f), new Vector2(28f, -24f), new Vector2(380f, 72f));
                }

                if (_subjectText != null)
                {
                    _subjectText.enabled = false;
                }

                if (_questionText != null)
                {
                    _questionText.enabled = true;
                    StyleText(_questionText, QuestionBaseFontSize, FontStyle.Bold, Color.white, TextAnchor.UpperLeft, new Vector2(790f, 276f));
                    ApplyQuestionTextStyle(_questionText, _questionText.text);
                    PlaceRect(_questionText.rectTransform, new Vector2(0f, 1f), new Vector2(0f, 1f), new Vector2(30f, -14f), new Vector2(790f, 276f));
                }

                if (_infoText != null)
                {
                    _infoText.enabled = false;
                }

                if (_responseText != null)
                {
                    _responseText.enabled = false;
                }

                if (_subjectImage != null)
                {
                    _subjectImage.enabled = true;
                    _subjectImage.preserveAspect = true;
                    _subjectImage.color = Color.white;
                    StretchRect(_subjectImage.rectTransform, Vector2.zero, Vector2.one, new Vector2(22f, 22f), new Vector2(-22f, -22f));
                    AddOutline(_subjectImage.gameObject, MakeColor(255, 255, 255, 90), new Vector2(2f, -2f));

                    var shadow = _subjectImage.gameObject.GetComponent<Shadow>();
                    if (shadow == null)
                    {
                        shadow = _subjectImage.gameObject.AddComponent<Shadow>();
                    }

                    shadow.effectColor = MakeColor(4, 11, 25, 170);
                    shadow.effectDistance = new Vector2(10f, -10f);
                }

                if (_blueHealthImage != null)
                {
                    _blueHealthImage.enabled = false;
                }

                if (_redHealthImage != null)
                {
                    _redHealthImage.enabled = false;
                }
            }

            private void ApplySnapshot(ProductSnapshot snapshot)
            {
                var snapshotKey = string.Format(
                    "{0}|{1}|{2}|{3}|{4}|{5}|{6}",
                    snapshot.CurrentQuestionIndex,
                    snapshot.TotalQuestions,
                    snapshot.BlueHealth,
                    snapshot.RedHealth,
                    snapshot.TimerText,
                    snapshot.QuestionId,
                    snapshot.IsGameActive);

                if (snapshotKey == _lastSnapshotKey)
                {
                    return;
                }

                var stateLabel = ResolveStateLabel(snapshot);
                var stateColor = ResolveStateColor(snapshot, stateLabel);
                var timerIsCritical = IsCriticalTimer(snapshot.TimerText);
                var questionNumber = snapshot.CurrentQuestionIndex > 0
                    ? snapshot.CurrentQuestionIndex
                    : (string.IsNullOrWhiteSpace(snapshot.QuestionId) ? 0 : 1);

                _brandCard.color = snapshot.IsGameActive ? MakeColor(6, 17, 31, 242) : MakeColor(6, 17, 31, 228);
                _timerCard.color = timerIsCritical ? MakeColor(45, 22, 16, 240) : MakeColor(10, 20, 36, 238);
                _scoreCard.color = snapshot.IsGameActive ? MakeColor(8, 18, 32, 240) : MakeColor(8, 18, 32, 228);
                _questionCard.color = snapshot.IsGameActive ? MakeColor(8, 19, 34, 236) : _panelFill;
                _questionAccent.color = stateColor;
                _visualAccent.color = snapshot.IsGameActive ? _accentBlue : _accentGold;
                _roundText.text = string.Format(
                    "TRIVIA ARENA\nQuestion {0:00} / {1:00}",
                    questionNumber,
                    Math.Max(snapshot.TotalQuestions, RoundSize));
                _subjectText.text = string.Empty;
                ApplyQuestionCopy(snapshot);
                _infoText.text = string.Format(
                    "{0}  |  BLUE {1} HP  |  RED {2} HP",
                    stateLabel,
                    Math.Max(0, snapshot.BlueHealth),
                    Math.Max(0, snapshot.RedHealth));

                if (_timerText != null)
                {
                    _timerText.color = timerIsCritical ? _redColor : _accentBlue;
                }

                ApplyHealthFill(_blueFill.rectTransform, Math.Max(0f, Math.Min(1f, snapshot.BlueHealth / (float)Math.Max(1, snapshot.MaxHealth))));
                ApplyHealthFill(_redFill.rectTransform, Math.Max(0f, Math.Min(1f, snapshot.RedHealth / (float)Math.Max(1, snapshot.MaxHealth))));

                _lastSnapshotKey = snapshotKey;
            }

            private string BuildMetaLine(ProductSnapshot snapshot)
            {
                return string.Empty;
            }

            private string ResolveStateLabel(ProductSnapshot snapshot)
            {
                if (snapshot.IsGameActive)
                {
                    return IsCriticalTimer(snapshot.TimerText) ? "FINAL SECONDS" : "LIVE";
                }

                if (snapshot.CurrentQuestionIndex > 0 && snapshot.CurrentQuestionIndex >= snapshot.TotalQuestions)
                {
                    return "ROUND COMPLETE";
                }

                if (!string.IsNullOrWhiteSpace(snapshot.QuestionId))
                {
                    return "READY";
                }

                return "STANDBY";
            }

            private Color ResolveStateColor(ProductSnapshot snapshot, string stateLabel)
            {
                if (stateLabel == "LIVE")
                {
                    return MakeColor(20, 94, 126, 232);
                }

                if (stateLabel == "FINAL SECONDS")
                {
                    return MakeColor(138, 76, 22, 232);
                }

                if (stateLabel == "ROUND COMPLETE")
                {
                    return MakeColor(58, 72, 96, 232);
                }

                if (!string.IsNullOrWhiteSpace(snapshot.QuestionId))
                {
                    return MakeColor(34, 76, 102, 232);
                }

                return MakeColor(40, 55, 76, 232);
            }

            private bool IsCriticalTimer(string timerValue)
            {
                if (string.IsNullOrWhiteSpace(timerValue))
                {
                    return false;
                }

                TimeSpan remaining;
                if (!TimeSpan.TryParse(timerValue, out remaining))
                {
                    return false;
                }

                return remaining.TotalSeconds <= 10d;
            }

            private string FirstNonEmpty(params string[] values)
            {
                for (var i = 0; i < values.Length; i++)
                {
                    if (!string.IsNullOrWhiteSpace(values[i]))
                    {
                        return values[i].Trim();
                    }
                }

                return string.Empty;
            }

            private void ApplyQuestionCopy(ProductSnapshot snapshot)
            {
                if (_questionText == null)
                {
                    return;
                }

                var question = NormalizeQuestionText(FirstNonEmpty(snapshot.QuestionText, snapshot.DisplayQuestionText), 220);
                _questionText.text = question;
                ApplyQuestionTextStyle(_questionText, question);
            }

            private void RehomeGraphic(Graphic graphic, Transform newParent)
            {
                if (graphic == null || newParent == null)
                {
                    return;
                }

                Debug.Log(string.Format(
                    "[CodexPatch] Rehome {0} from {1} to {2}",
                    graphic.name,
                    BuildTransformPath(graphic.transform),
                    BuildTransformPath(newParent)));
                QueuePrototypeContainer(graphic);
                graphic.rectTransform.SetParent(newParent, false);
                graphic.rectTransform.localScale = Vector3.one;
            }

            private void QueuePrototypeContainer(Graphic graphic)
            {
                if (graphic == null)
                {
                    return;
                }

                var parent = graphic.transform.parent as RectTransform;
                if (parent == null)
                {
                    return;
                }

                var depth = 0;
                while (parent != null && depth < 3)
                {
                    var candidate = parent.gameObject;
                    if (candidate != null && candidate != _overlayRoot.gameObject && candidate.GetComponent<Canvas>() == null)
                    {
                        if (!_prototypeContainers.Any(existing => existing == candidate))
                        {
                            _prototypeContainers.Add(candidate);
                        }
                    }

                    parent = parent.parent as RectTransform;
                    depth++;
                }
            }

            private void HidePrototypeContainers()
            {
                if (_prototypeUiHidden)
                {
                    return;
                }

                if (_prototypeContainers.Count > 0)
                {
                    Debug.Log("[CodexPatch] Hiding prototype containers: " + string.Join(", ", _prototypeContainers.Select(container => container == null ? "<null>" : container.name).ToArray()));
                }

                for (var i = 0; i < _prototypeContainers.Count; i++)
                {
                    var candidate = _prototypeContainers[i];
                    if (candidate == null || candidate.GetComponent<Canvas>() != null)
                    {
                        continue;
                    }

                    candidate.SetActive(false);
                }

                _prototypeUiHidden = true;
            }

            private void StyleText(Text text, int fontSize, FontStyle fontStyle, Color color, TextAnchor alignment, Vector2 size)
            {
                if (text == null)
                {
                    return;
                }

                text.font = GetBuiltInFont();
                text.fontStyle = fontStyle;
                text.fontSize = fontSize;
                text.color = color;
                text.alignment = alignment;
                text.supportRichText = false;
                text.horizontalOverflow = HorizontalWrapMode.Wrap;
                text.verticalOverflow = VerticalWrapMode.Overflow;
                text.resizeTextForBestFit = false;
                text.lineSpacing = 1f;
                text.rectTransform.sizeDelta = size;
                AddOutline(text.gameObject, MakeColor(4, 11, 25, 180), new Vector2(2f, -2f));
            }

            private RectTransform CreateRect(string name, Transform parent)
            {
                var go = new GameObject(name, typeof(RectTransform));
                go.transform.SetParent(parent, false);
                return go.GetComponent<RectTransform>();
            }

            private Image CreatePanel(string name, Transform parent, Vector2 anchorMin, Vector2 anchorMax, Vector2 offsetMin, Vector2 offsetMax, Color color)
            {
                var rect = CreateRect(name, parent);
                if (anchorMin == anchorMax)
                {
                    rect.anchorMin = anchorMin;
                    rect.anchorMax = anchorMax;
                    rect.pivot = new Vector2(0.5f, 0.5f);
                    rect.anchoredPosition = new Vector2((offsetMin.x + offsetMax.x) * 0.5f, (offsetMin.y + offsetMax.y) * 0.5f);
                    rect.sizeDelta = new Vector2(Mathf.Abs(offsetMax.x - offsetMin.x), Mathf.Abs(offsetMax.y - offsetMin.y));
                }
                else
                {
                    StretchRect(rect, anchorMin, anchorMax, offsetMin, offsetMax);
                }

                var image = rect.gameObject.AddComponent<Image>();
                image.color = color;
                image.raycastTarget = false;
                return image;
            }

            private Image CreateCard(string name, Transform parent, Vector2 anchor, Vector2 pivot, Vector2 anchoredPosition, Vector2 size, Color color)
            {
                var card = CreatePanel(name, parent, anchor, anchor, Vector2.zero, size, color);
                PlaceRect(card.rectTransform, anchor, pivot, anchoredPosition, size);
                AddOutline(card.gameObject, _panelStroke, new Vector2(1f, -1f));
                AddShadow(card.gameObject, MakeColor(4, 11, 25, 120), new Vector2(0f, -10f));
                return card;
            }

            private Image CreateAccentBar(string name, Transform parent, Color color, Vector2 offsetMin, Vector2 offsetMax)
            {
                return CreatePanel(name, parent, new Vector2(0f, 1f), new Vector2(1f, 1f), offsetMin, offsetMax, color);
            }

            private Image CreateHealthTrack(string name, Transform parent, Vector2 anchoredPosition, float width)
            {
                var track = CreatePanel(name, parent, new Vector2(0f, 1f), new Vector2(0f, 1f), Vector2.zero, new Vector2(width, 16f), MakeColor(25, 43, 66, 255));
                PlaceRect(track.rectTransform, new Vector2(0f, 1f), new Vector2(0f, 1f), anchoredPosition, new Vector2(width, 16f));
                AddOutline(track.gameObject, MakeColor(255, 255, 255, 18), new Vector2(1f, -1f));
                return track;
            }

            private Image CreateHealthFill(string name, Transform parent, Color color)
            {
                var fill = CreatePanel(name, parent, new Vector2(0f, 0.5f), new Vector2(0f, 0.5f), Vector2.zero, new Vector2(parent.GetComponent<RectTransform>().sizeDelta.x, 16f), color);
                PlaceRect(fill.rectTransform, new Vector2(0f, 0.5f), new Vector2(0f, 0.5f), Vector2.zero, new Vector2(parent.GetComponent<RectTransform>().sizeDelta.x, 16f));
                return fill;
            }

            private Text CreateText(string name, Transform parent, int fontSize, FontStyle fontStyle, Color color, TextAnchor alignment)
            {
                var rect = CreateRect(name, parent);
                var text = rect.gameObject.AddComponent<Text>();
                text.font = GetBuiltInFont();
                text.fontSize = fontSize;
                text.fontStyle = fontStyle;
                text.color = color;
                text.alignment = alignment;
                text.supportRichText = false;
                text.horizontalOverflow = HorizontalWrapMode.Wrap;
                text.verticalOverflow = VerticalWrapMode.Overflow;
                text.raycastTarget = false;
                AddOutline(text.gameObject, MakeColor(4, 11, 25, 180), new Vector2(2f, -2f));
                return text;
            }

            private void StretchRect(RectTransform rect, Vector2 anchorMin, Vector2 anchorMax, Vector2 offsetMin, Vector2 offsetMax)
            {
                rect.anchorMin = anchorMin;
                rect.anchorMax = anchorMax;
                rect.offsetMin = offsetMin;
                rect.offsetMax = offsetMax;
            }

            private void PinRect(RectTransform rect, Vector2 anchor, Vector2 anchoredPosition, Vector2 size)
            {
                rect.anchorMin = anchor;
                rect.anchorMax = anchor;
                rect.pivot = new Vector2(0f, 1f);
                rect.anchoredPosition = anchoredPosition;
                rect.sizeDelta = size;
            }

            private void PlaceRect(RectTransform rect, Vector2 anchor, Vector2 pivot, Vector2 anchoredPosition, Vector2 size)
            {
                rect.anchorMin = anchor;
                rect.anchorMax = anchor;
                rect.pivot = pivot;
                rect.anchoredPosition = anchoredPosition;
                rect.sizeDelta = size;
            }

            private void ApplyHealthFill(RectTransform fill, float normalized)
            {
                if (fill == null)
                {
                    return;
                }

                var parent = fill.parent as RectTransform;
                if (parent == null)
                {
                    return;
                }

                var width = Mathf.Max(14f, parent.sizeDelta.x * normalized);
                PlaceRect(fill, new Vector2(0f, 0.5f), new Vector2(0f, 0.5f), Vector2.zero, new Vector2(width, parent.sizeDelta.y));
            }

            private void AddShadow(GameObject target, Color color, Vector2 distance)
            {
                var shadow = target.GetComponent<Shadow>();
                if (shadow == null)
                {
                    shadow = target.AddComponent<Shadow>();
                }

                shadow.effectColor = color;
                shadow.effectDistance = distance;
            }

            private string BuildTransformPath(Transform node)
            {
                if (node == null)
                {
                    return "<null>";
                }

                var names = new List<string>();
                var current = node;
                while (current != null)
                {
                    names.Add(current.name);
                    current = current.parent;
                }

                names.Reverse();
                return string.Join("/", names.ToArray());
            }

            private void AddOutline(GameObject target, Color color, Vector2 distance)
            {
                var outline = target.GetComponent<Outline>();
                if (outline == null)
                {
                    outline = target.AddComponent<Outline>();
                }

                outline.effectColor = color;
                outline.effectDistance = distance;
            }
        }

        private sealed class ScopedProductShellDriver : MonoBehaviour
        {
            private readonly Color _accentBlue = MakeColor(77, 209, 255, 255);
            private readonly Color _accentGold = MakeColor(255, 196, 79, 255);
            private readonly Color _cardFill = MakeColor(10, 20, 34, 230);
            private readonly Color _cardStroke = MakeColor(101, 196, 255, 104);
            private readonly Color _redColor = MakeColor(255, 88, 88, 255);
            private readonly Color _softText = MakeColor(188, 214, 235, 255);

            private bool _chromeReady;
            private bool _nativeUiStyled;
            private string _lastSnapshotKey = string.Empty;
            private Canvas _productCanvas;
            private RectTransform _overlayRoot;
            private Image _roundCard;
            private Image _timerCard;
            private Image _questionCard;
            private Image _questionAccent;
            private Image _visualCard;
            private Image _visualAccent;
            private Image _rankCard;
            private Image _rankAccent;
            private Image _blueBarCard;
            private Image _redBarCard;
            private Text _productRoundText;
            private Text _productTimerText;
            private Text _productQuestionMetaText;
            private Text _productQuestionText;
            private Text _productQuestionInfoText;
            private Text _productVisualLabelText;
            private Text _infoText;
            private Text _questionText;
            private Text _responseText;
            private Text _roundText;
            private Image _subjectImage;
            private Text _subjectText;
            private Text _timerText;
            private Image _blueHealthImage;
            private Image _redHealthImage;
            private RectTransform _downRootRect;
            private RectTransform _dialogueRect;
            private RectTransform _questionFrameRect;
            private RectTransform _visualFrameRect;
            private RectTransform _rankListRect;
            private Rect _roundSourceRect;
            private Rect _timerSourceRect;
            private Rect _questionSourceRect;
            private Rect _visualSourceRect;
            private Rect _rankSourceRect;
            private bool _widgetsReparented;

            private void Update()
            {
                try
                {
                    if (!ResolveNativeUi() || !ResolveSceneUi())
                    {
                        return;
                    }

                    EnsureProductChrome();
                    if (!_nativeUiStyled)
                    {
                        ApplyNativeUiPolish();
                        _nativeUiStyled = true;
                    }

                    UpdateBackdropLayout();
                    ApplySnapshot(CaptureSnapshot());
                }
                catch (Exception ex)
                {
                    Debug.LogWarning("[CodexPatch] Scoped product shell update failed: " + ex.Message);
                }
            }

            private bool ResolveNativeUi()
            {
                if (_timerText != null && _roundText != null && _subjectText != null && _questionText != null && _infoText != null && _subjectImage != null)
                {
                    return true;
                }

                var uiManagerType = FindAssemblyType("UIManager");
                if (uiManagerType == null)
                {
                    return false;
                }

                var uiManager = GetStaticPropertyValue(uiManagerType, "Instance");
                if (uiManager == null)
                {
                    return false;
                }

                _timerText = ReadPropertyOrField(uiManager, "timerText") as Text;
                _roundText = ReadPropertyOrField(uiManager, "roundText") as Text;
                _subjectText = ReadPropertyOrField(uiManager, "SubjectText") as Text;
                _questionText = ReadPropertyOrField(uiManager, "QUESTIONText") as Text;
                _infoText = ReadPropertyOrField(uiManager, "infoText") as Text;
                _subjectImage = ReadPropertyOrField(uiManager, "SubjectImage") as Image;
                _blueHealthImage = ReadPropertyOrField(uiManager, "blueHealthImage") as Image;
                _redHealthImage = ReadPropertyOrField(uiManager, "redHealthImage") as Image;

                var demoUiType = FindAssemblyType("GameApiDemoUI");
                if (demoUiType != null)
                {
                    var demoUi = GetStaticPropertyValue(demoUiType, "Instance");
                    if (demoUi != null)
                    {
                        _responseText = GetInstanceFieldValue(demoUi, "responseText") as Text;
                    }
                }

                return _timerText != null && _roundText != null && _subjectText != null && _questionText != null && _infoText != null && _subjectImage != null;
            }

            private bool ResolveSceneUi()
            {
                if (_downRootRect != null && _dialogueRect != null && _questionFrameRect != null && _visualFrameRect != null && _rankListRect != null)
                {
                    return true;
                }

                _downRootRect = FindRectTransform("UI/down");
                _dialogueRect = FindRectTransform("UI/down/Dialogue");
                _questionFrameRect = FindRectTransform(QuestionFramePath);
                _visualFrameRect = FindRectTransform("UI/center/Image");
                _rankListRect = FindRectTransform("UI/center/Ranklist");
                return _downRootRect != null && _dialogueRect != null && _questionFrameRect != null && _visualFrameRect != null && _rankListRect != null;
            }

            private void EnsureProductChrome()
            {
                if (_chromeReady)
                {
                    return;
                }

                var canvasGo = new GameObject("CodexScopedBackdrop");
                UnityEngine.Object.DontDestroyOnLoad(canvasGo);

                _productCanvas = canvasGo.AddComponent<Canvas>();
                _productCanvas.renderMode = RenderMode.ScreenSpaceOverlay;
                _productCanvas.sortingOrder = 5000;

                var scaler = canvasGo.AddComponent<CanvasScaler>();
                scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
                scaler.referenceResolution = new Vector2(1920f, 1080f);
                scaler.matchWidthOrHeight = 0.5f;

                var raycaster = canvasGo.AddComponent<GraphicRaycaster>();
                raycaster.enabled = false;

                _overlayRoot = CreateRect("CodexBackdropRoot", canvasGo.transform);
                StretchRect(_overlayRoot, Vector2.zero, Vector2.one, Vector2.zero, Vector2.zero);

                _roundCard = CreateCard("RoundCard", _overlayRoot, MakeColor(7, 18, 30, 232));
                _timerCard = CreateCard("TimerCard", _overlayRoot, MakeColor(244, 248, 252, 244));
                _questionCard = CreateCard("QuestionCard", _overlayRoot, _cardFill);
                _questionAccent = CreateCard("QuestionAccent", _overlayRoot, _accentBlue);
                _visualCard = CreateCard("VisualCard", _overlayRoot, MakeColor(247, 242, 232, 255));
                _visualAccent = CreateCard("VisualAccent", _overlayRoot, _accentGold);
                _rankCard = CreateCard("RankCard", _overlayRoot, MakeColor(242, 247, 251, 250));
                _rankAccent = CreateCard("RankAccent", _overlayRoot, _accentBlue);
                _blueBarCard = CreateCard("BlueBarCard", _overlayRoot, MakeColor(8, 23, 38, 220));
                _redBarCard = CreateCard("RedBarCard", _overlayRoot, MakeColor(36, 14, 14, 214));
                _productRoundText = CreateText("ProductRoundText", _roundCard.rectTransform, 28, FontStyle.Bold, Color.white, TextAnchor.MiddleCenter);
                _productTimerText = CreateText("ProductTimerText", _timerCard.rectTransform, 58, FontStyle.Bold, _accentBlue, TextAnchor.MiddleCenter);
                _productQuestionMetaText = CreateText("ProductQuestionMetaText", _questionCard.rectTransform, 18, FontStyle.Bold, _accentGold, TextAnchor.UpperLeft);
                _productQuestionText = CreateText("ProductQuestionText", _questionCard.rectTransform, QuestionBaseFontSize, FontStyle.Bold, Color.white, TextAnchor.UpperLeft);
                _productQuestionInfoText = CreateText("ProductQuestionInfoText", _questionCard.rectTransform, 15, FontStyle.Bold, _softText, TextAnchor.UpperLeft);
                _productVisualLabelText = CreateText("ProductVisualLabelText", _visualCard.rectTransform, 17, FontStyle.Bold, MakeColor(24, 72, 104, 255), TextAnchor.UpperLeft);

                _roundSourceRect = GetScreenRect(_roundText.rectTransform);
                _timerSourceRect = GetScreenRect(_timerText.rectTransform);
                _questionSourceRect = GetScreenRect(_questionFrameRect);
                _visualSourceRect = GetScreenRect(_visualFrameRect);
                _rankSourceRect = GetScreenRect(_rankListRect);

                ReparentRect(_subjectImage.rectTransform, _visualCard.rectTransform);
                ReparentRect(_rankListRect, _rankCard.rectTransform);
                _widgetsReparented = true;

                HideGraphicsRecursive(_downRootRect);
                HideNativeText(_roundText);
                HideNativeText(_timerText);
                HideNativeText(_subjectText);
                HideNativeText(_questionText);
                HideNativeText(_infoText);
                DisableStockImage(_rankListRect);

                _chromeReady = true;
            }

            private void ApplyNativeUiPolish()
            {
                HideNativeText(_roundText);
                HideNativeText(_timerText);
                HideNativeText(_subjectText);
                HideNativeText(_questionText);
                HideNativeText(_infoText);
                StyleText(_productRoundText, 28, FontStyle.Bold, Color.white, TextAnchor.MiddleCenter, _productRoundText.rectTransform.sizeDelta);
                StyleText(_productTimerText, 58, FontStyle.Bold, _accentBlue, TextAnchor.MiddleCenter, _productTimerText.rectTransform.sizeDelta);
                StyleText(_productQuestionMetaText, 18, FontStyle.Bold, _accentGold, TextAnchor.UpperLeft, _productQuestionMetaText.rectTransform.sizeDelta);
                StyleText(_productQuestionText, QuestionBaseFontSize, FontStyle.Bold, Color.white, TextAnchor.UpperLeft, _productQuestionText.rectTransform.sizeDelta);
                StyleText(_productQuestionInfoText, 15, FontStyle.Bold, _softText, TextAnchor.UpperLeft, _productQuestionInfoText.rectTransform.sizeDelta);
                StyleText(_productVisualLabelText, 17, FontStyle.Bold, MakeColor(24, 72, 104, 255), TextAnchor.UpperLeft, _productVisualLabelText.rectTransform.sizeDelta);
                _productQuestionMetaText.enabled = false;
                ApplyQuestionTextStyle(_productQuestionText, _productQuestionText.text);
                _productQuestionInfoText.enabled = false;

                if (_responseText != null)
                {
                    _responseText.enabled = false;
                }

                if (_subjectImage != null)
                {
                    _subjectImage.preserveAspect = true;
                    _subjectImage.color = Color.white;
                    AddOutline(_subjectImage.gameObject, MakeColor(255, 255, 255, 72), new Vector2(2f, -2f));
                }

                if (_blueHealthImage != null)
                {
                    _blueHealthImage.color = _accentBlue;
                }

                if (_redHealthImage != null)
                {
                    _redHealthImage.color = _redColor;
                }

                PolishRankList();
            }

            private void UpdateBackdropLayout()
            {
                if (_overlayRoot == null)
                {
                    return;
                }

                FitToScreenRect(_roundCard.rectTransform, ExpandRect(_roundSourceRect, 22f, 14f, 22f, 14f));
                FitToScreenRect(_timerCard.rectTransform, ExpandRect(_timerSourceRect, 18f, 16f, 18f, 16f));
                FitToScreenRect(_questionCard.rectTransform, ExpandRect(_questionSourceRect, 30f, 12f, 30f, 96f));
                FitToScreenRect(_questionAccent.rectTransform, TopSlice(GetScreenRect(_questionCard.rectTransform), 168f, 8f, 24f));
                FitToScreenRect(_visualCard.rectTransform, ExpandRect(_visualSourceRect, 18f, 18f, 18f, 18f));
                FitToScreenRect(_visualAccent.rectTransform, TopSlice(GetScreenRect(_visualCard.rectTransform), 146f, 8f, 18f));
                FitToScreenRect(_rankCard.rectTransform, ExpandRect(_rankSourceRect, 18f, 18f, 18f, 18f));
                FitToScreenRect(_rankAccent.rectTransform, TopSlice(GetScreenRect(_rankCard.rectTransform), 132f, 8f, 18f));

                if (_blueHealthImage != null)
                {
                    FitToScreenRect(_blueBarCard.rectTransform, ExpandRect(GetScreenRect(_blueHealthImage.rectTransform), 12f, 8f, 12f, 8f));
                }

                if (_redHealthImage != null)
                {
                    FitToScreenRect(_redBarCard.rectTransform, ExpandRect(GetScreenRect(_redHealthImage.rectTransform), 12f, 8f, 12f, 8f));
                }

                if (_widgetsReparented)
                {
                    StretchRect(_subjectImage.rectTransform, Vector2.zero, Vector2.one, new Vector2(16f, 16f), new Vector2(-16f, -16f));
                    StretchRect(_rankListRect, Vector2.zero, Vector2.one, new Vector2(14f, 16f), new Vector2(-14f, -16f));
                }

                PlaceRect(_productRoundText.rectTransform, new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), Vector2.zero, new Vector2(_roundCard.rectTransform.sizeDelta.x - 28f, _roundCard.rectTransform.sizeDelta.y - 18f));
                PlaceRect(_productTimerText.rectTransform, new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), new Vector2(0f, 2f), new Vector2(_timerCard.rectTransform.sizeDelta.x - 24f, _timerCard.rectTransform.sizeDelta.y - 18f));
                PlaceRect(_productQuestionMetaText.rectTransform, new Vector2(0f, 1f), new Vector2(0f, 1f), new Vector2(24f, -22f), new Vector2(_questionCard.rectTransform.sizeDelta.x - 48f, 0f));
                PlaceRect(_productQuestionText.rectTransform, new Vector2(0f, 1f), new Vector2(0f, 1f), new Vector2(44f, -10f), new Vector2(_questionCard.rectTransform.sizeDelta.x - 62f, _questionCard.rectTransform.sizeDelta.y - 20f));
                PlaceRect(_productQuestionInfoText.rectTransform, new Vector2(0f, 0f), new Vector2(0f, 0f), new Vector2(24f, 18f), new Vector2(_questionCard.rectTransform.sizeDelta.x - 48f, 0f));
                PlaceRect(_productVisualLabelText.rectTransform, new Vector2(0f, 1f), new Vector2(0f, 1f), new Vector2(22f, -22f), new Vector2(_visualCard.rectTransform.sizeDelta.x - 44f, 22f));
            }

            private void ApplySnapshot(ProductSnapshot snapshot)
            {
                var snapshotKey = string.Format(
                    "{0}|{1}|{2}|{3}|{4}|{5}|{6}",
                    snapshot.CurrentQuestionIndex,
                    snapshot.TotalQuestions,
                    snapshot.BlueHealth,
                    snapshot.RedHealth,
                    snapshot.TimerText,
                    snapshot.QuestionId,
                    snapshot.IsGameActive);

                if (snapshotKey == _lastSnapshotKey)
                {
                    return;
                }

                var stateLabel = ResolveStateLabel(snapshot);
                var timerIsCritical = IsCriticalTimer(snapshot.TimerText);
                var questionNumber = snapshot.CurrentQuestionIndex > 0
                    ? snapshot.CurrentQuestionIndex
                    : (string.IsNullOrWhiteSpace(snapshot.QuestionId) ? 0 : 1);

                _timerCard.color = timerIsCritical ? MakeColor(255, 241, 241, 244) : MakeColor(244, 248, 252, 244);
                _questionAccent.color = ResolveStateColor(snapshot, stateLabel);
                _roundCard.color = snapshot.IsGameActive ? MakeColor(7, 18, 30, 244) : MakeColor(7, 18, 30, 232);
                _productRoundText.text = string.Format("TRIVIA ARENA\nROUND {0:00} / {1:00}", questionNumber, Math.Max(snapshot.TotalQuestions, RoundSize));
                _productTimerText.text = string.IsNullOrWhiteSpace(snapshot.TimerText) ? "--:--" : snapshot.TimerText;
                _productQuestionMetaText.text = string.Empty;
                _productVisualLabelText.text = "CLUE VISUAL";
                ApplyQuestionCopy(snapshot);
                _productQuestionInfoText.text = string.Empty;

                if (_productTimerText != null)
                {
                    _productTimerText.color = timerIsCritical ? _redColor : _accentBlue;
                }

                _lastSnapshotKey = snapshotKey;
            }

            private void ApplyQuestionCopy(ProductSnapshot snapshot)
            {
                if (_productQuestionText == null)
                {
                    return;
                }

                var question = NormalizeQuestionText(FirstNonEmpty(snapshot.QuestionText, snapshot.DisplayQuestionText), 220);
                _productQuestionText.text = question;
                ApplyQuestionTextStyle(_productQuestionText, question);
            }

            private string BuildMetaLine(ProductSnapshot snapshot)
            {
                return string.Empty;
            }

            private string ResolveStateLabel(ProductSnapshot snapshot)
            {
                if (snapshot.IsGameActive)
                {
                    return IsCriticalTimer(snapshot.TimerText) ? "FINAL SECONDS" : "LIVE";
                }

                if (snapshot.CurrentQuestionIndex > 0 && snapshot.CurrentQuestionIndex >= snapshot.TotalQuestions)
                {
                    return "ROUND COMPLETE";
                }

                if (!string.IsNullOrWhiteSpace(snapshot.QuestionId))
                {
                    return "READY";
                }

                return "STANDBY";
            }

            private Color ResolveStateColor(ProductSnapshot snapshot, string stateLabel)
            {
                if (stateLabel == "LIVE")
                {
                    return MakeColor(20, 94, 126, 232);
                }

                if (stateLabel == "FINAL SECONDS")
                {
                    return MakeColor(138, 76, 22, 232);
                }

                if (stateLabel == "ROUND COMPLETE")
                {
                    return MakeColor(58, 72, 96, 232);
                }

                if (!string.IsNullOrWhiteSpace(snapshot.QuestionId))
                {
                    return MakeColor(34, 76, 102, 232);
                }

                return MakeColor(40, 55, 76, 232);
            }

            private bool IsCriticalTimer(string timerValue)
            {
                if (string.IsNullOrWhiteSpace(timerValue))
                {
                    return false;
                }

                TimeSpan remaining;
                if (!TimeSpan.TryParse(timerValue, out remaining))
                {
                    return false;
                }

                return remaining.TotalSeconds <= 10d;
            }

            private string FirstNonEmpty(params string[] values)
            {
                for (var i = 0; i < values.Length; i++)
                {
                    if (!string.IsNullOrWhiteSpace(values[i]))
                    {
                        return values[i].Trim();
                    }
                }

                return string.Empty;
            }

            private void PolishRankList()
            {
                if (_rankListRect == null)
                {
                    return;
                }

                var texts = _rankListRect.GetComponentsInChildren<Text>(true);
                for (var i = 0; i < texts.Length; i++)
                {
                    var text = texts[i];
                    if (text == null)
                    {
                        continue;
                    }

                    var parentName = text.transform.parent == null ? string.Empty : text.transform.parent.name;
                    if (string.Equals(parentName, "Name", StringComparison.OrdinalIgnoreCase))
                    {
                        StyleText(text, 16, FontStyle.Bold, MakeColor(25, 56, 82, 255), TextAnchor.MiddleLeft, text.rectTransform.sizeDelta);
                    }
                    else if (string.Equals(parentName, "Score", StringComparison.OrdinalIgnoreCase))
                    {
                        StyleText(text, 15, FontStyle.Bold, _accentGold, TextAnchor.MiddleCenter, text.rectTransform.sizeDelta);
                    }
                    else
                    {
                        StyleText(text, 24, FontStyle.Bold, MakeColor(24, 72, 104, 255), TextAnchor.MiddleCenter, text.rectTransform.sizeDelta);
                    }
                }

                var images = _rankListRect.GetComponentsInChildren<Image>(true);
                for (var i = 0; i < images.Length; i++)
                {
                    var image = images[i];
                    if (image == null)
                    {
                        continue;
                    }

                    if (string.Equals(image.gameObject.name, "Ranklist", StringComparison.OrdinalIgnoreCase))
                    {
                        image.enabled = false;
                    }
                    else if (string.Equals(image.gameObject.name, "Color", StringComparison.OrdinalIgnoreCase))
                    {
                        image.color = MakeColor(32, 180, 228, 255);
                    }
                }
            }

            private void StyleText(Text text, int fontSize, FontStyle fontStyle, Color color, TextAnchor alignment, Vector2 size)
            {
                if (text == null)
                {
                    return;
                }

                text.font = GetBuiltInFont();
                text.fontStyle = fontStyle;
                text.fontSize = fontSize;
                text.color = color;
                text.alignment = alignment;
                text.supportRichText = false;
                text.horizontalOverflow = HorizontalWrapMode.Wrap;
                text.verticalOverflow = VerticalWrapMode.Overflow;
                text.resizeTextForBestFit = false;
                text.rectTransform.sizeDelta = size;
                AddOutline(text.gameObject, MakeColor(4, 11, 25, 170), new Vector2(2f, -2f));
            }

            private RectTransform FindRectTransform(string path)
            {
                var go = GameObject.Find(path);
                return go == null ? null : go.GetComponent<RectTransform>();
            }

            private void DisableStockImage(RectTransform rect)
            {
                if (rect == null)
                {
                    return;
                }

                var image = rect.GetComponent<Image>();
                if (image != null)
                {
                    image.enabled = false;
                }
            }

            private void HideNativeText(Text text)
            {
                if (text != null)
                {
                    text.enabled = false;
                }
            }

            private void HideGraphicsRecursive(RectTransform rect)
            {
                if (rect == null)
                {
                    return;
                }

                var graphics = rect.GetComponentsInChildren<Graphic>(true);
                for (var i = 0; i < graphics.Length; i++)
                {
                    var graphic = graphics[i];
                    if (graphic != null)
                    {
                        graphic.enabled = false;
                    }
                }
            }

            private RectTransform CreateRect(string name, Transform parent)
            {
                var go = new GameObject(name, typeof(RectTransform));
                go.transform.SetParent(parent, false);
                return go.GetComponent<RectTransform>();
            }

            private Text CreateText(string name, Transform parent, int fontSize, FontStyle fontStyle, Color color, TextAnchor alignment)
            {
                var rect = CreateRect(name, parent);
                var text = rect.gameObject.AddComponent<Text>();
                text.font = GetBuiltInFont();
                text.fontSize = fontSize;
                text.fontStyle = fontStyle;
                text.color = color;
                text.alignment = alignment;
                text.supportRichText = false;
                text.horizontalOverflow = HorizontalWrapMode.Wrap;
                text.verticalOverflow = VerticalWrapMode.Overflow;
                text.raycastTarget = false;
                AddOutline(text.gameObject, MakeColor(4, 11, 25, 180), new Vector2(2f, -2f));
                return text;
            }

            private Image CreateCard(string name, Transform parent, Color color)
            {
                var rect = CreateRect(name, parent);
                var image = rect.gameObject.AddComponent<Image>();
                image.color = color;
                image.raycastTarget = false;
                AddOutline(image.gameObject, _cardStroke, new Vector2(1f, -1f));
                AddShadow(image.gameObject, MakeColor(4, 11, 25, 120), new Vector2(0f, -8f));
                return image;
            }

            private void StretchRect(RectTransform rect, Vector2 anchorMin, Vector2 anchorMax, Vector2 offsetMin, Vector2 offsetMax)
            {
                rect.anchorMin = anchorMin;
                rect.anchorMax = anchorMax;
                rect.offsetMin = offsetMin;
                rect.offsetMax = offsetMax;
            }

            private void PlaceRect(RectTransform rect, Vector2 anchor, Vector2 pivot, Vector2 anchoredPosition, Vector2 size)
            {
                rect.anchorMin = anchor;
                rect.anchorMax = anchor;
                rect.pivot = pivot;
                rect.anchoredPosition = anchoredPosition;
                rect.sizeDelta = size;
            }

            private void ReparentRect(RectTransform rect, Transform parent)
            {
                if (rect == null || parent == null || rect.parent == parent)
                {
                    return;
                }

                rect.SetParent(parent, false);
                rect.localScale = Vector3.one;
            }

            private Rect GetScreenRect(RectTransform rect)
            {
                if (rect == null)
                {
                    return new Rect(0f, 0f, 0f, 0f);
                }

                var corners = new Vector3[4];
                rect.GetWorldCorners(corners);
                return Rect.MinMaxRect(corners[0].x, corners[0].y, corners[2].x, corners[2].y);
            }

            private Rect ExpandRect(Rect rect, float left, float bottom, float right, float top)
            {
                return Rect.MinMaxRect(rect.xMin - left, rect.yMin - bottom, rect.xMax + right, rect.yMax + top);
            }

            private Rect TopSlice(Rect rect, float width, float height, float inset)
            {
                return Rect.MinMaxRect(rect.xMin + inset, rect.yMax - height, rect.xMin + inset + width, rect.yMax);
            }

            private void FitToScreenRect(RectTransform rect, Rect screenRect)
            {
                rect.anchorMin = Vector2.zero;
                rect.anchorMax = Vector2.zero;
                rect.pivot = Vector2.zero;
                rect.anchoredPosition = new Vector2(screenRect.xMin, screenRect.yMin);
                rect.sizeDelta = new Vector2(screenRect.width, screenRect.height);
            }

            private void AddShadow(GameObject target, Color color, Vector2 distance)
            {
                var shadow = target.GetComponent<Shadow>();
                if (shadow == null)
                {
                    shadow = target.AddComponent<Shadow>();
                }

                shadow.effectColor = color;
                shadow.effectDistance = distance;
            }

            private void AddOutline(GameObject target, Color color, Vector2 distance)
            {
                var outline = target.GetComponent<Outline>();
                if (outline == null)
                {
                    outline = target.AddComponent<Outline>();
                }

                outline.effectColor = color;
                outline.effectDistance = distance;
            }
        }
    }
}
