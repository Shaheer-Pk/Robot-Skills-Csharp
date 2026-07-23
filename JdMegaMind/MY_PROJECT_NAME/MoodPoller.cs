using System;
using System.Net.Http;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;

namespace JdMegaMind
{
    /// <summary>
    /// MoodPoller.cs
    ///
    /// Periodically (every 5 seconds) AND on-demand (right after JD finishes
    /// a conversational turn) asks Python's GET /emotion/state for JD's
    /// current mood, and — ONLY when the reported action_name has actually
    /// changed since the last time we dispatched one — hands it off to a
    /// caller-supplied dispatch delegate (MainForm.RunMoodAction) so the RGB
    /// eyes update.
    ///
    /// ARCHITECTURE — why this is its own file, with its own HttpClient and
    /// its own dedup state, separate from CameraFrameUploader and from
    /// physical-action dispatch:
    ///
    ///   - Decoupled from MainForm's internals via constructor injection
    ///     (backendUrl + Action&lt;string&gt; logger + Action&lt;string&gt;
    ///     dispatcher) — same pattern CameraFrameUploader already uses for
    ///     Log, see vision-csharp-feature.md.
    ///
    ///   - Dedicated HttpClient, matching the vision module's isolation
    ///     pattern — deliberately NOT justified by arc-csharp.md's stated
    ///     reason for vision's dedicated client (call-frequency /
    ///     socket-exhaustion risk at 15-30fps). Mood polls once per 5
    ///     seconds — nowhere near that volume, so that specific
    ///     justification does not actually apply here. This is purely a
    ///     module-isolation/consistency choice, a separate deliberate call.
    ///     Documented explicitly so a future reader doesn't go looking for
    ///     a socket-exhaustion justification that was never the real one.
    ///
    ///   - Mood is polled from TWO independent trigger points that funnel
    ///     into the SAME CheckAndDispatchMood() method: a 5-second
    ///     background timer (shows ambient mood decay even when nobody is
    ///     talking to JD — decay is a minutes-timescale phenomenon per
    ///     emotion_profile.json's decay_per_minute, so 5s is frequent
    ///     enough to feel live without being wasteful), and a
    ///     fire-and-forget call from inside SpeakResponse() right after
    ///     Python's /brain/chat call succeeds (the freshest possible read,
    ///     since Python's apply_event() has already run by that point —
    ///     confirmed in emotions_module.md's documented call order:
    ///     apply_event() runs BEFORE text_to_speech(), inside the same
    ///     request). Because these two triggers can genuinely fire close
    ///     together on two different threads, the compare-and-dispatch
    ///     step inside CheckAndDispatchMood() is wrapped in its own lock —
    ///     see _dedupLock below.
    ///
    /// Does NOT talk to ControlCommand/VariableManager directly — that
    /// stays MainForm's job (via the injected dispatch delegate), matching
    /// this project's existing convention of keeping the ARC-script-bridge
    /// surface centralized in MainForm.cs rather than scattered across
    /// every feature file.
    /// </summary>
    public class MoodPoller : IDisposable
    {
        private readonly string _backendUrl;
        private readonly Action<string> _log;

        // MainForm's RunMoodAction, passed in as a delegate rather than a
        // direct MainForm reference — same decoupling reason
        // CameraFrameUploader takes Log as Action<string> instead of a
        // concrete MainForm dependency. This class should never need to
        // know MainForm exists as a type.
        private readonly Action<string> _dispatchAction;

        private readonly HttpClient _httpClient;
        private readonly System.Timers.Timer _timer;

        private const int POLL_INTERVAL_MS = 5000;

        // Guards the compare-and-dispatch step in CheckAndDispatchMood().
        // Two independent triggers (the 5s timer, and SpeakResponse's
        // fire-and-forget post-turn call) can call CheckAndDispatchMood()
        // concurrently, on two different threads. Without this lock, both
        // could read the same stale _lastSentActionName and make
        // inconsistent dispatch decisions, or leave _lastSentActionName out
        // of sync with what was actually last sent to ARC.
        private readonly object _dedupLock = new object();

        // The action_name we last actually dispatched to ARC. Starts as
        // null — deliberately NOT "neutral" or any other real preset name.
        // If this were pre-seeded with a real preset, a genuine first read
        // of that same preset (e.g. JD's real starting mood happens to BE
        // neutral) would look like "no change" and the eyes would never
        // fire at all on skill load. null can never equal a real
        // action_name string, so the very first poll always dispatches.
        private string _lastSentActionName = null;

        /// <summary>
        /// dispatchAction is MainForm's RunMoodAction method (a method
        /// group, implicitly converted to Action&lt;string&gt; at the call
        /// site — no cast needed).
        /// </summary>
        public MoodPoller(string backendUrl, Action<string> logger, Action<string> dispatchAction)
        {
            _backendUrl = backendUrl;
            _log = logger;
            _dispatchAction = dispatchAction;
            _httpClient = new HttpClient();

            _timer = new System.Timers.Timer(POLL_INTERVAL_MS);
            _timer.AutoReset = true;
            _timer.Elapsed += async (sender, e) =>
            {
                // Timer.Elapsed's delegate signature (ElapsedEventHandler)
                // is fixed by .NET as void-returning — there is no
                // async-Task option available here, which makes this an
                // inherently "async void" handler. Exceptions thrown
                // inside an async void method are NOT observable by any
                // caller and, left unhandled, can crash ARC's entire
                // process, not just this skill — the exact risk
                // hearing-feature.md already documented and specifically
                // designed around (SpeakResponse is async Task, not async
                // void, for this reason). Since the Timer API gives no
                // async Task alternative, this try/catch is the
                // mitigation: guarantee nothing can ever escape this
                // specific boundary unhandled, regardless of what
                // CheckAndDispatchMood() might throw.
                try
                {
                    await CheckAndDispatchMood();
                }
                catch (Exception ex)
                {
                    _log($"[Mood] Unexpected timer error: {ex.Message}");
                }
            };
        }

        /// <summary>
        /// Starts the 5-second polling loop. Call once — e.g. from
        /// MainForm's constructor. Deliberately NOT button-gated, unlike
        /// the camera stream: mood should reflect ambient decay from skill
        /// load onward, tied to the skill's own lifecycle, regardless of
        /// whether the mic or camera happen to be toggled on.
        /// </summary>
        public void Start()
        {
            _timer.Start();
            _log("[Mood] Polling started (5s interval).");
        }

        /// <summary>
        /// Fire-and-forget entry point, meant to be called from inside
        /// SpeakResponse() right after Python's /brain/chat call succeeds.
        /// Deliberately NOT awaited by the caller — eye color has zero
        /// bearing on what JD says out loud next (same reasoning
        /// emotion-reverse-channel-csharp.md already used to justify its
        /// own original async-background-task design), so a slow or
        /// unreachable poll here must never delay JD's speech playback.
        /// The Task.Run body is wrapped in its own try/catch so an
        /// unobserved-task exception can never propagate back into
        /// SpeakResponse's flow.
        /// </summary>
        public void PollAfterSpeaking()
        {
            _ = Task.Run(async () =>
            {
                try
                {
                    await CheckAndDispatchMood();
                }
                catch (Exception ex)
                {
                    _log($"[Mood] Post-speech poll error: {ex.Message}");
                }
            });
        }

        /// <summary>
        /// The one real method. GETs /emotion/state, pulls out ONLY
        /// action_name via JObject.Parse — confirmed sufficient: color,
        /// preset, mood_value, and is_sleepy are either Python-internal or
        /// redundant with action_name for C#'s purposes, per the confirmed
        /// design decision (action_name alone is the ARC-facing contract).
        /// Compares it against _lastSentActionName under _dedupLock, and
        /// only invokes _dispatchAction when it has actually changed.
        ///
        /// Both the timer and PollAfterSpeaking() funnel through here —
        /// this is the single choke point where the compare-and-dispatch
        /// decision happens, which is exactly why the decision itself
        /// needs to be atomic (see _dedupLock above), not just each
        /// individual field access.
        /// </summary>
        private async Task CheckAndDispatchMood()
        {
            string json;
            try
            {
                var response = await _httpClient.GetAsync($"{_backendUrl}/emotion/state");
                if (!response.IsSuccessStatusCode)
                {
                    _log($"[Mood] /emotion/state error: {response.StatusCode}");
                    return;
                }
                json = await response.Content.ReadAsStringAsync();
            }
            catch (HttpRequestException ex)
            {
                // Backend unreachable — recoverable, matches the existing
                // "log only" handling hearing/brain already use for the
                // same failure category.
                _log($"[Mood] Backend unreachable: {ex.Message}");
                return;
            }

            string actionName;
            try
            {
                var parsed = JObject.Parse(json);
                actionName = parsed["action_name"]?.Value<string>();
            }
            catch (Exception ex)
            {
                // A malformed response here would indicate a Python-side
                // bug (EmotionStateResponse's schema should guarantee this
                // field always exists) — logged, non-fatal, matching the
                // existing non-fatal X-JD-Action parse-failure handling in
                // SpeakResponse.
                _log($"[Mood] Failed to parse /emotion/state response: {ex.Message}");
                return;
            }

            if (string.IsNullOrEmpty(actionName))
            {
                _log("[Mood] /emotion/state returned no action_name. Skipping.");
                return;
            }

            // Compare-and-dispatch, atomically — see _dedupLock's
            // declaration above for why this must be a single locked
            // operation, not a separate read-then-write.
            lock (_dedupLock)
            {
                if (actionName == _lastSentActionName)
                {
                    // No change since the last dispatch — silently do
                    // nothing. This is the EXPECTED, common case: mood
                    // decays on a minutes timescale (decay_per_minute in
                    // emotion_profile.json), so most 5-second polls will
                    // see no band change at all. Deliberately not logged
                    // here to avoid spamming the log box every 5 seconds
                    // during ordinary operation.
                    return;
                }

                _dispatchAction(actionName);
                _lastSentActionName = actionName;
                _log($"[Mood] Dispatched: {actionName}");
            }
        }

        /// <summary>
        /// Stops the timer and releases the HttpClient. MUST be called
        /// from MainForm_FormClosing — an undisposed System.Timers.Timer
        /// left running past skill shutdown is exactly the category of
        /// leak this project already found and fixed once, project-wide
        /// (see arc-csharp.md's Dispose/Cleanup Discipline section) — do
        /// not let this class quietly reintroduce it.
        /// </summary>
        public void Dispose()
        {
            _timer?.Stop();
            _timer?.Dispose();
            _httpClient?.Dispose();
        }
    }
}