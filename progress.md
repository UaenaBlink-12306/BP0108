Original prompt: Make a systematic improvement to the game and add new features until the user interface and user experience is significantly increased

## Current focus

- Audit the packaged Unity game, its runtime patch layer, and the existing UI redesign artifacts.
- Prioritize improvements that materially help both the player-facing and second-display experiences.
- Verify changes against the actual Windows build after every meaningful iteration.

## Notes

- The project is a packaged Unity build rather than an HTML/JS game, so browser/Playwright-specific testing does not apply.
- Existing user files and question/image datasets must be preserved.
- Baseline captured at `tmp/goal_baseline_2026-07-16.png`: question copy is detached at the bottom, the top HUD lacks hierarchy, and empty rankings have no state messaging.
- First improvement tranche selected: compact round/timer HUD, live state and HP labels, structured question card, 10-step round progress, timer urgency, question-change toast, leaderboard empty state, and `F`/`T` display shortcuts.
- First live render compiled and launched successfully. Follow-up fixes identified: cover the remaining timer-panel sliver, restore explicit health-fill colors, replace the missing native ranking header with a stable overlay header, align the leaderboard empty state, and suppress numeric-only era metadata.
- Second live render verified the timer cover, health colors, metadata cleanup, and empty-state alignment. The attempted replacement ranking header duplicated the stock title, so the final pass keeps the stronger stock `RANKINGS` header and uses only the aligned empty-state copy.
- Cold-launch and immediate-keypress captures exposed a presentation race: overlay chrome could appear before the 10-question snapshot and clue sprite were ready. The runtime now keeps native UI visible until both are loaded, then switches the enhancement layer on atomically.
- Display shortcuts moved from potentially conflicting letter keys to `F11` (fullscreen), `F10` (text size), with `Esc` to leave fullscreen.
- Team-mode QA passed after the readiness fix. Verified standard and large question text, stable delayed renders, responsive 16:9 windowed layout, and the control sequence `F11 -> Esc -> F11` (`2880x1800 -> 2758x1730 -> 2880x1800`).
- User review requested larger text, exact coverage of the original question panel, and removal of black surfaces. Current refinement changes the timer/status and question cards to theme blue, raises HUD/body font sizes, and moves the question card to the original panel center with a 930x220 reference footprint.
- Live feedback render confirmed the larger text and blue timer/card direction. Final geometry expands the question card to 930x234 at y=100 so it covers the original panel's bottom cyan edge; card shadows and the question-change toast now use dark blue instead of black.
- The final timer card now uses a purpose-built rounded shape with a 24px reference corner radius. The status badge is also rounded, widened to 280x58, and increased to 25pt so `LIVE | SOLO` is prominent.
- Personal mode now removes the legacy team strip completely, provides a clean `PERSONAL RANK` header, and hides the old overlapping ranking/versus artifacts.
- Final cold-launch renders passed in both builds: `tmp/goal_nonteam_rounded_timer_clean_stable.png` and `tmp/goal_team_rounded_timer_final_stable.png`.
- Runtime log review found the UX layer initialized cleanly with no patch exceptions or repeated warnings. The only reported start error was the existing backend `GAME_ACTIVE` response caused by relaunching an already-active round during QA.

## TODO

- Continue collecting user feedback for any additional gameplay or presentation improvements.

## 2026-07-18 stream lobby

- Added a real starting lobby to both team and personal builds; the packaged game no longer auto-starts behind the menu.
- Matched the existing cyan, navy, blue-card, gold-accent visual language and added distinct team/solo Twitch-chat instructions.
- Added a live current-session viewer roster sourced from team-join and chat-message events. The legacy username dictionary is used only to resolve names because it contains thousands of historical entries rather than a trustworthy current-viewer list.
- Large live rosters rotate through 16-name pages every six seconds so every detected session viewer remains available without overloading Unity's text renderer.
- The host can click `START ROUND` or press Enter/Space. The button path was verified end-to-end against the real backend and transitioned cleanly into the enhanced 10-question gameplay screen.
- Game-only QA captures passed for team lobby (`tmp/stream_lobby_team_qa.png`), post-start team gameplay (`tmp/stream_lobby_team_started_qa.png`), and personal lobby (`tmp/stream_lobby_nonteam_qa.png`).
- Both `BP0108` and `BP0108_nonteam` runtime patch assemblies were rebuilt successfully. Dedicated QA flags are inert during normal launches.

## 2026-07-18 rounded lobby surfaces

- Rounded every visible lobby surface using a reusable sliced Unity sprite: the outer shell, stream-lobby badge, both large cards, the viewer window, and the Start Round button.
- The rounded sprite retains consistent corner radii at different screen sizes instead of stretching the corners.
- Rebuilt both builds and visually verified the team lobby in `tmp/stream_lobby_team_qa.png`.

## TODO

- The user's trailing phrase, `also can you check`, was incomplete; ask what additional item they wanted checked.

## 2026-08-12 final Twitch launch-readiness pass

- Added a visible six-second lobby countdown; the round starts automatically and the backend/local fallback path is bounded to fifteen seconds, below the user's twenty-second maximum.
- Added an unattended continuity watchdog that advances stalled questions, completes stalled rounds, and starts a local round if the backend does not respond. All recovery paths are idempotently rate-limited.
- Added strongly typed `QuestionMeta` creation for live `QuestionData`, fixing an offline-fallback conversion warning found during cold-launch QA.
- Replaced Unity's imprecise best-fit behavior with measured per-question sizing. The complete 300-question audit now reports `allFit: true`, zero overflow, a 21pt minimum actual size, and all questions within the 874x174 question body.
- Enlarged the rounded question container, applied reusable sliced rounded shapes to gameplay cards, kept images aspect-preserving, and hid the stray native red close icon from the Twitch presentation.
- Bounded decoded question sprites to a 14-item LRU cache and throttled reflection-heavy per-frame drivers.
- Repaired the two legacy blank clues in `stream_questions_100.json` (Singapore and Pope John Paul II); all 300 questions now have nonblank IDs/questions/answers and stream-ready images at or above 1024x576 and 1 MB.
- The actual Unity runtime passed an accelerated 24-hour workload simulation: 720 question/image transitions, 72 rounds, cache 14/14, 17 MB managed memory, and zero exceptions or patch failures.
- Final rebuild and cold-launch QA passed for both targets. Team auto-started in 10.0 seconds; solo auto-started in 11.2 seconds; both logs contained zero patch exceptions/failures.
- Verified the team game at 1920x1080 windowed and full-screen via F11, then exited full-screen via Escape. The question/image/card layout stayed contained; the stray native red close icon is gone.
- Added a supervised second-display launcher for both builds: duplicate-instance prevention, default full-screen 1920x1080 output, bounded runtime-log rotation, and automatic recovery after unexpected non-zero exits. Launcher argument and override contract tests passed.
- Final completion audit passed every requirement: 300/300 question fit, 300/300 complete question/image records, 24-hour workload simulation, team/solo cold launches, and launcher contract verification.

## TODO

- None for the requested launch-readiness goal.

## 2026-08-18 non-team livestream backend compatibility

- Root cause: both packages used the same remote `/game/start` state, so an active team round caused the non-team build to receive `GAME_ACTIVE` and wait for offline recovery.
- The non-team runtime now routes its round lifecycle through the packaged process-local `MockApiHandler` backend while retaining the shared Twitch WebSocket/chat feed. Team mode continues using the existing remote backend.
- Rebuilt `CodexRuntimePatch.dll` and the patched `Assembly-CSharp.dll` for both `BP0108` and `BP0108_nonteam`.
- Cold-launched the non-team build while the user's team process remained active. The solo backend initialized, its 10-question round became live in 5.5 seconds, and the QA log contained no remote `/game/start`, no `GAME_ACTIVE`, and no Codex patch failure.
- Answer-matcher regression tests passed.

## 2026-08-20 livestream music and TTS

- Added an original procedural 16-second stereo quiz-music loop on a persistent Unity `AudioSource`; it loops through lobby and gameplay and automatically restarts after an unexpected stop.
- Added queued Windows TTS rendered to WAV by `BP0108_SpeechHost.exe` and played back through Unity, so both music and speech stay in the game audio capture. Speech has an eight-second host deadline, bounded restart handling, and exact child-process cleanup.
- The startup introduction and each deduplicated viewer welcome explain that answers are typed directly in Twitch chat. Viewer joins are keyed by stable user ID, wait briefly for a resolved display name, and are batched to avoid overlapping speech.
- Only the first accepted correct answer for each round/question token queues `Congratulations, [viewer]! You got the question correct first.` Priority congratulations interrupts an introduction/welcome if necessary; the interrupted message is retained and resumes afterward.
- Background music ducks from `0.220` to `0.055` only while a Unity TTS clip is ready or playing, then restores over 0.70 seconds. Speech preparation and ordinary queue backlog do not keep the music unnecessarily quiet.
- The Unity-free audio policy suite passes 123 assertions. Isolated real-Unity audio QA passes for team and solo modes, including looping music, introduction/welcome copy, case-insensitive welcome dedupe, first-correct dedupe, interruption/resume, duck/recovery, valid Windows TTS, and helper cleanup. Final QA logs contain no backend requests, `GAME_ACTIVE`, exceptions, or patch failures.
- The team package and supervised launcher are deployed and pass end-to-end audio QA. The verified solo package is staged for exact-path deployment when the currently open solo window exits; the build tooling now refuses to patch any exact target whose game or speech-host process is still running.
