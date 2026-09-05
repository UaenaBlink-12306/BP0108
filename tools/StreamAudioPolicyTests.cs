using System;
using System.Collections.Generic;
using System.Threading;

namespace CodexRuntimePatch.Tests
{
    internal static class StreamAudioPolicyTests
    {
        private static int _assertions;
        private static int _failures;

        private static int Main()
        {
            Run("welcome dedupe", TestWelcomeDedupe);
            Run("thread-safe welcome dedupe", TestConcurrentWelcomeDedupe);
            Run("priority and bounded regular queues", TestQueuePolicy);
            Run("interrupted welcome resumes after priority speech", TestInterruptedWelcomeResume);
            Run("single introduction", TestIntroductionDedupe);
            Run("first-correct per presentation token", TestFirstCorrectDedupe);
            Run("spoken-name sanitization", TestSpokenNameSanitization);
            Run("exact welcome speech", TestWelcomeSpeech);
            Run("exact congratulations speech", TestFirstCorrectSpeech);
            Run("music duck attack, hold, and recovery", TestMusicDuckEnvelope);
            Run("music failure recovery", TestMusicFailureRecovery);

            Console.WriteLine(
                _failures == 0
                    ? string.Format("Stream audio policy tests passed: {0} assertions.", _assertions)
                    : string.Format("Stream audio policy tests failed: {0} failure(s), {1} assertions.", _failures, _assertions));
            return _failures == 0 ? 0 : 1;
        }

        private static void TestWelcomeDedupe()
        {
            var policy = new StreamAudioPolicy(4);
            AssertFalse(policy.HasWelcomed(null), "null IDs are not welcomed");
            AssertFalse(policy.TryMarkWelcomed("   "), "blank IDs are rejected");
            AssertTrue(policy.TryMarkWelcomed("  User-42  "), "first stable ID is accepted");
            AssertTrue(policy.HasWelcomed("user-42"), "lookup is trimmed and case-insensitive");
            AssertFalse(policy.TryMarkWelcomed("USER-42"), "join/chat duplicate is rejected");
            AssertTrue(policy.TryMarkWelcomed("different-id"), "a different ID with the same possible display name is accepted");
        }

        private static void TestConcurrentWelcomeDedupe()
        {
            var policy = new StreamAudioPolicy(4);
            var start = new ManualResetEvent(false);
            var threads = new List<Thread>();
            var accepted = 0;
            for (var i = 0; i < 16; i++)
            {
                var localIndex = i;
                var thread = new Thread(delegate()
                {
                    start.WaitOne();
                    var id = localIndex % 2 == 0 ? "Concurrent-Viewer" : "concurrent-viewer";
                    if (policy.TryMarkWelcomed(id))
                    {
                        Interlocked.Increment(ref accepted);
                    }
                });
                thread.IsBackground = true;
                threads.Add(thread);
                thread.Start();
            }

            start.Set();
            for (var i = 0; i < threads.Count; i++)
            {
                AssertTrue(threads[i].Join(5000), "dedupe worker completed");
            }

            start.Close();
            AssertEqual(1, accepted, "exactly one concurrent event marks the viewer welcomed");
        }

        private static void TestQueuePolicy()
        {
            var policy = new StreamAudioPolicy(2);
            AssertTrue(policy.Enqueue("regular", "regular one", false), "first regular request is accepted");
            AssertTrue(policy.Enqueue("regular", "regular two", false), "second regular request is accepted");
            AssertFalse(policy.Enqueue("regular", "regular overflow", false), "bounded regular queue rejects overflow");
            AssertTrue(policy.Enqueue("priority", "priority one", true), "first priority request is accepted");
            AssertTrue(policy.Enqueue("priority", "priority two", true), "priority queue is independent of regular bound");
            AssertFalse(policy.Enqueue("empty", "   ", true), "blank speech is rejected");
            AssertEqual(4, policy.QueueCount, "queue count includes both queues");
            AssertEqual(2, policy.PriorityQueueCount, "priority count is exposed");
            AssertEqual(2, policy.RegularQueueCount, "regular count is exposed");

            AssertDequeue(policy, "priority one", true, "priority FIFO item one");
            AssertDequeue(policy, "priority two", true, "priority FIFO item two");
            AssertDequeue(policy, "regular one", false, "regular FIFO item one");
            AssertDequeue(policy, "regular two", false, "regular FIFO item two");

            StreamSpeechRequest request;
            AssertFalse(policy.TryDequeue(out request), "empty queue reports false");
            AssertTrue(request == null, "empty queue returns a null request");

            AssertTrue(policy.Enqueue("priority", "survives synth failure", true), "failure test priority is queued");
            AssertTrue(policy.Enqueue("regular", "plays after failure", false), "failure test regular is queued");
            AssertDequeue(policy, "survives synth failure", true, "consumer receives failing request");
            AssertDequeue(policy, "plays after failure", false, "a consumer failure cannot wedge the remaining queue");
        }

        private static void TestInterruptedWelcomeResume()
        {
            var policy = new StreamAudioPolicy(2);
            AssertTrue(policy.Enqueue(StreamAudioPolicy.WelcomeCategory, "welcome being spoken", false), "welcome is admitted");
            AssertTrue(policy.Enqueue("regular", "ordinary queued speech", false), "ordinary speech fills regular queue");

            StreamSpeechRequest interrupted;
            AssertTrue(policy.TryDequeue(out interrupted), "active welcome is dequeued");
            AssertEqual("welcome being spoken", interrupted.Text, "interrupted request is the welcome");
            AssertTrue(policy.Enqueue("regular", "new speech fills the freed slot", false), "regular queue can refill while welcome plays");
            AssertTrue(policy.TryQueueFirstCorrect("round-1/question-1", "Ada"), "first-correct announcement is queued as priority");
            AssertTrue(policy.RequeueInterrupted(interrupted), "interrupted welcome is retained even when regular queue refilled");
            AssertEqual(4, policy.QueueCount, "requeued admitted request is counted without dropping another request");

            AssertDequeue(policy, StreamAudioPolicy.BuildFirstCorrectSpeech("Ada"), true, "first-correct still runs before resumed welcome");
            AssertDequeue(policy, "welcome being spoken", false, "interrupted welcome resumes next");
            AssertDequeue(policy, "ordinary queued speech", false, "original regular FIFO continues");
            AssertDequeue(policy, "new speech fills the freed slot", false, "new regular speech remains queued");
        }

        private static void TestIntroductionDedupe()
        {
            var policy = new StreamAudioPolicy(2);
            var introduction = StreamAudioPolicy.BuildWelcomeSpeech(null, 0, true);
            AssertTrue(policy.TryQueueIntroduction(introduction), "introduction is queued once");
            AssertFalse(policy.TryQueueIntroduction(introduction), "duplicate bootstrap cannot queue another introduction");
            AssertEqual(1, policy.QueueCount, "only one introduction remains queued");
            AssertDequeue(policy, introduction, false, "the introduction is a regular request");
        }

        private static void TestFirstCorrectDedupe()
        {
            var policy = new StreamAudioPolicy(4);
            AssertFalse(policy.TryQueueFirstCorrect(" ", "Nobody"), "blank presentation token is rejected");
            AssertTrue(policy.TryQueueFirstCorrect("round-1/question-1", "Alice"), "first correct answer for a question is queued");
            AssertFalse(policy.TryQueueFirstCorrect("ROUND-1/QUESTION-1", "Bob"), "same presentation token is case-insensitively deduped");
            AssertTrue(policy.TryQueueFirstCorrect("round-1/question-2", "Carla"), "next question can announce its first answer");
            AssertTrue(policy.TryQueueFirstCorrect("round-2/question-1", "Diego"), "round-scoped token permits reused question index after reset");
            AssertEqual(3, policy.QueueCount, "only unique question presentations are queued");

            AssertDequeue(policy, StreamAudioPolicy.BuildFirstCorrectSpeech("Alice"), true, "round one question one announcement");
            AssertDequeue(policy, StreamAudioPolicy.BuildFirstCorrectSpeech("Carla"), true, "round one question two announcement");
            AssertDequeue(policy, StreamAudioPolicy.BuildFirstCorrectSpeech("Diego"), true, "round two reused index announcement");
        }

        private static void TestSpokenNameSanitization()
        {
            AssertEqual("viewer", StreamAudioPolicy.SanitizeSpokenName(null), "null name uses safe fallback");
            AssertEqual("viewer", StreamAudioPolicy.SanitizeSpokenName("<>/&!"), "punctuation-only name uses safe fallback");
            AssertEqual("Al ice", StreamAudioPolicy.SanitizeSpokenName("\u202e<Al_ice>\r\n"), "markup, controls, bidi marks, and underscores are removed or separated");
            AssertEqual("O'Connor-Jr", StreamAudioPolicy.SanitizeSpokenName(" O'Connor-Jr. "), "safe apostrophes and hyphens are retained");
            AssertEqual("张 三", StreamAudioPolicy.SanitizeSpokenName("张 三"), "Unicode letters are retained");

            var longName = new string('A', 100);
            var sanitized = StreamAudioPolicy.SanitizeSpokenName(longName);
            AssertEqual(48, sanitized.Length, "spoken names are length bounded");
            AssertFalse(sanitized.Contains("<"), "sanitized names cannot contain SSML opening brackets");
            AssertFalse(sanitized.Contains(">"), "sanitized names cannot contain SSML closing brackets");
        }

        private static void TestWelcomeSpeech()
        {
            var names = new List<string> { "<Alice>", "BOB" };
            AssertEqual(
                "Welcome, Alice and BOB! Type your answer in Twitch chat. No command is needed. The first correct answer wins.",
                StreamAudioPolicy.BuildWelcomeSpeech(names, 2, true),
                "personal welcome states direct-chat, no-command, and first-correct rules");
            AssertEqual(
                "Welcome, Alice and BOB! Your Twitch team decides your side. Type your answer in Twitch chat. No command is needed. The first correct answer damages the other team.",
                StreamAudioPolicy.BuildWelcomeSpeech(names, 2, false),
                "team welcome states team, direct-chat, no-command, and first-correct rules");
            AssertEqual(
                "Welcome, Alice, BOB, and 2 other viewers! Type your answer in Twitch chat. No command is needed. The first correct answer wins.",
                StreamAudioPolicy.BuildWelcomeSpeech(names, 4, true),
                "welcome includes an accurate unnamed-viewer remainder");
            AssertEqual(
                "Welcome to our new viewer! Type your answer in Twitch chat. No command is needed. The first correct answer wins.",
                StreamAudioPolicy.BuildWelcomeSpeech(null, 1, true),
                "unnamed single viewer has a natural greeting");
            AssertEqual(
                "Welcome to the Twitch History Challenge! Type your answer in Twitch chat. No command is needed. The first correct answer wins.",
                StreamAudioPolicy.BuildWelcomeSpeech(null, 0, true),
                "zero-name form is a complete game introduction");
        }

        private static void TestFirstCorrectSpeech()
        {
            AssertEqual(
                "Congratulations, Alice! You got the question correct first.",
                StreamAudioPolicy.BuildFirstCorrectSpeech("<Alice>"),
                "congratulations text is exact and name-safe");
            AssertEqual(
                "Congratulations, viewer! You got the question correct first.",
                StreamAudioPolicy.BuildFirstCorrectSpeech("!!!"),
                "congratulations text uses the fallback name");
        }

        private static void TestMusicDuckEnvelope()
        {
            var state = new StreamMusicDuckState(1f);
            AssertNear(0.8125f, state.Step(1f, 0.25f, 1f, 2f, 0.25f, true), 0.0001f, "duck attack step one");
            state.Step(1f, 0.25f, 1f, 2f, 0.25f, true);
            state.Step(1f, 0.25f, 1f, 2f, 0.25f, true);
            AssertNear(0.25f, state.Step(1f, 0.25f, 1f, 2f, 0.25f, true), 0.0001f, "attack reaches duck target");
            AssertNear(0.25f, state.Step(1f, 0.25f, 1f, 2f, 5f, true), 0.0001f, "music remains ducked while speech is active or queued");
            AssertNear(0.4375f, state.Step(1f, 0.25f, 1f, 2f, 0.5f, false), 0.0001f, "release begins only after speech and queue are empty");
            AssertNear(1f, state.Step(1f, 0.25f, 1f, 2f, 2f, false), 0.0001f, "release restores base volume");

            state.Reset(1f);
            AssertNear(0.25f, state.Step(1f, 0.25f, 0f, 2f, 1f, true), 0.0001f, "zero attack ducks immediately");
            AssertNear(1f, state.Step(1f, 0.25f, 1f, 0f, 1f, false), 0.0001f, "zero release restores immediately");

            state.Reset(0.5f);
            AssertNear(0.5f, state.Step(0.5f, 0.9f, 0f, 0f, 1f, true), 0.0001f, "duck target cannot exceed base volume");
        }

        private static void TestMusicFailureRecovery()
        {
            var state = new StreamMusicDuckState(1f);
            state.Step(1f, 0.2f, 0f, 1f, 1f, true);
            AssertNear(0.2f, state.CurrentVolume, 0.0001f, "precondition is fully ducked");
            AssertNear(1f, state.RecoverAfterFailure(1f), 0.0001f, "TTS failure recovery restores base immediately");
            AssertNear(1f, state.CurrentVolume, 0.0001f, "failure recovery updates numeric state");

            state.Reset(0.2f);
            AssertTrue(state.Step(1f, 0.2f, 1f, 1f, -1f, false) >= 0.2f, "negative delta cannot move volume in the wrong direction");
            AssertNear(1f, state.RecoverAfterFailure(float.NaN), 0.0001f, "invalid recovery volume uses a safe full-volume fallback");
        }

        private static void AssertDequeue(StreamAudioPolicy policy, string expectedText, bool expectedPriority, string label)
        {
            StreamSpeechRequest request;
            AssertTrue(policy.TryDequeue(out request), label + " dequeued");
            if (request == null)
            {
                AssertTrue(false, label + " request is non-null");
                return;
            }

            AssertEqual(expectedText, request.Text, label + " text");
            AssertEqual(expectedPriority, request.Priority, label + " priority");
        }

        private static void Run(string label, Action test)
        {
            try
            {
                test();
                Console.WriteLine(label + ": PASS");
            }
            catch (Exception ex)
            {
                _failures++;
                Console.WriteLine(label + ": FAIL - " + ex.Message);
            }
        }

        private static void AssertTrue(bool value, string label)
        {
            _assertions++;
            if (!value)
            {
                throw new InvalidOperationException(label + " (expected true)");
            }
        }

        private static void AssertFalse(bool value, string label)
        {
            _assertions++;
            if (value)
            {
                throw new InvalidOperationException(label + " (expected false)");
            }
        }

        private static void AssertEqual<T>(T expected, T actual, string label)
        {
            _assertions++;
            if (!EqualityComparer<T>.Default.Equals(expected, actual))
            {
                throw new InvalidOperationException(string.Format(
                    "{0} (expected={1}, actual={2})",
                    label,
                    expected,
                    actual));
            }
        }

        private static void AssertNear(float expected, float actual, float tolerance, string label)
        {
            _assertions++;
            if (Math.Abs(expected - actual) > tolerance)
            {
                throw new InvalidOperationException(string.Format(
                    "{0} (expected={1}, actual={2}, tolerance={3})",
                    label,
                    expected,
                    actual,
                    tolerance));
            }
        }
    }
}
