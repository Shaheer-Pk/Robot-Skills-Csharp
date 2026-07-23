using AForge.Imaging;
using ARC;
using NAudio.Wave;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Speech.Recognition;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace JdMegaMind
{
    public partial class MainForm : ARC.UCForms.FormPluginMaster
    {
        // -----------------------------------------------------------------------
        // CONFIGURATION CONSTANTS
        // These are the tunable values for the hearing pipeline.
        // Adjust ENERGY_THRESHOLD and SILENCE_TIMEOUT_MS after real-world testing.
        // -----------------------------------------------------------------------

        // The base URL of the Python FastAPI backend.
        // Uses mDNS .local hostname instead of a raw IP — works reliably over
        // Eduroam which reassigns a new IP on every reconnect.
        private const string BACKEND_URL = "http://DESKTOP-LJO38UV.local:8000";

        // Energy threshold for mic capture.
        // NAudio reports sample values as floats between -1.0 and 1.0.
        // 0.25 means 25% of max volume — tuned to filter out background noise
        // while still catching normal conversational speech directed at the robot.
        // TUNE THIS after real-world testing:
        //   - Triggering too easily on ambient noise? Raise it (e.g. 0.30)
        //   - Missing your voice? Lower it (e.g. 0.20)
        private const float ENERGY_THRESHOLD = 0.35f;

        // How many milliseconds of consecutive silence after speech ends
        // before we consider the utterance complete and send it to Python.
        // 1000ms = 1 second. Raise if JD cuts you off mid-sentence during pauses.
        // Lower if there's a noticeable dead gap after you finish speaking.
        private const int SILENCE_TIMEOUT_MS = 1000;

        // Extra milliseconds of buffer added AFTER the calculated playback duration
        // before the mic reactivates. Prevents the tail end of JD's voice from
        // being picked up by the mic and fed back into the pipeline as a new utterance.
        private const int PLAYBACK_BUFFER_MS = 500;

        // NAudio capture format: 16000 Hz, 16-bit, Mono.
        // 16000 Hz is Silero VAD's native sample rate — sending at this rate
        // means Python does zero resampling, which is faster and cleaner.
        private const int SAMPLE_RATE = 16000;
        private const int BITS_PER_SAMPLE = 16;
        private const int CHANNELS = 1;

        // EZB playback format constants — used for duration calculation.
        // JD's EZB hardware requires audio resampled to 14700 Hz, 8-bit, mono.
        // These values must match the targetFormat in SpeakResponse exactly.
        // Duration formula: bytes / (sampleRate * channels * (bitsPerSample / 8))
        // For 8-bit mono: simplifies to bytes / 14700
        private const int EZB_SAMPLE_RATE = 14700;
        private const int EZB_BITS = 8;
        private const int EZB_CHANNELS = 1;

        // -----------------------------------------------------------------------
        // STATE FIELDS
        // -----------------------------------------------------------------------

        // The NAudio mic capture device. Null when not actively listening.
        private WaveInEvent _waveIn = null;

        // Cancellation token for the current listening session.
        // Cancelled when the Stop button is clicked to cleanly abort
        // any in-flight work tied to the current session.
        private CancellationTokenSource _listenCts = null;

        // Raw PCM bytes accumulated while the user is speaking.
        // Cleared after each complete utterance is dispatched to Python.
        private readonly List<byte> _audioBuffer = new List<byte>();

        // Tracks the last moment audio exceeded the energy threshold.
        // Used to measure how long the current silence window has persisted.
        private DateTime _lastSpeechTime = DateTime.MinValue;

        // Whether we are currently in an active speech segment.
        // False = waiting for speech to begin.
        // True  = speech detected, actively buffering audio bytes.
        private bool _isSpeaking = false;

        // Processing lock — true while the pipeline is active (Python transcribing,
        // Gemini generating, Piper synthesizing, or JD physically speaking).
        //
        // WHY volatile: this field is read and written from multiple threads
        // simultaneously (NAudio background thread reads it; Task.Run threads write it).
        // volatile prevents the compiler/CPU from caching a stale value on any thread,
        // guaranteeing every read sees the most recently written value.
        //
        // While true, OnAudioDataAvailable silently discards all incoming audio.
        // This prevents:
        //   1. JD hearing his own voice and responding to himself.
        //   2. Background chatter interrupting mid-response.
        //   3. Multiple overlapping pipeline executions.
        //
        // Always released inside a finally block so crashes cannot leave it
        // permanently true (which would make JD permanently deaf for the session).
        private volatile bool _isProcessing = false;

        // -----------------------------------------------------------------------
        // WAKE WORD DETECTION
        // -----------------------------------------------------------------------

        // The bridge stream — NAudio pushes bytes in, speech engine pulls them out.
        // Lives for the duration of a listening session, recreated on each Start.
        // Our custom written AudioBridgeStream (within this project file)
        private AudioBridgeStream _bridgeStream = null;

        // The Windows SAPI5 speech recognition engine.
        // Configured with a single-phrase grammar ("Hello Robot") and fed audio
        // from _bridgeStream so it shares NAudio's mic ownership — no handoff gap.
        private SpeechRecognitionEngine _speechRecognizer = null;

        // Set to true when "Hello Robot" is detected by the speech engine.
        // OnAudioDataAvailable checks this before buffering any audio.
        // Reset to false after each utterance is dispatched to Python,
        // returning the system to wake word hunting mode immediately.
        //
        // WHY volatile: read by NAudio's background thread, written by the
        // speech engine's internal thread. volatile prevents stale cached reads.
        private volatile bool _isWakeWordDetected = false;

        // -----------------------------------------------------------------------
        // UI CONTROLS
        // -----------------------------------------------------------------------

        // UI Controls — held as fields so background threads can update them
        // safely via Invoke (marshalling back to the UI thread).
        private Button _btnListen;
        private Button _btnCamera;
        private RichTextBox _logBox;
        private const int MAX_LOG_LINES = 100;

        // -----------------------------------------------------------------------
        // CAMERA CONFIGURATION
        // -----------------------------------------------------------------------

        private ARC.UCForms.FormCameraDevice _camera = null;
        private CameraFrameUploader _cameraUploader = null;

        // -----------------------------------------------------------------------
        // MOOD CONFIGURATION
        // -----------------------------------------------------------------------

        private MoodPoller _moodPoller = null;   

        // -----------------------------------------------------------------------
        // CONSTRUCTOR & ARC LIFECYCLE
        // -----------------------------------------------------------------------

        public MainForm()
        {
            InitializeComponent();

            // Show the gear config button in the ARC skill title bar.
            ConfigButton = true;

            // Build the UI programmatically because the Visual Studio designer
            // cannot load x86-only ARC/EZB assemblies in its preview process.
            // This is functionally identical to using the designer — controls
            // added here behave identically to designer-placed controls.
            InitializeUI();

            // Increase the default connection limit for HttpClient to avoid throttling
            System.Net.ServicePointManager.DefaultConnectionLimit = 20;

            // Free resources on ARC shutdown, project close, or skill removal.
            // This is the only safe place to dispose _waveIn — disposing earlier risks
            // and is a seperate clean-up mechanism other than the button-gated StopListening() sequence.
            this.FormClosing += MainForm_FormClosing;

            // Mood polling starts immediately, tied to skill lifecycle — not
            // button-gated, per confirmed design (should reflect ambient decay
            // even with mic/camera both off).
            _moodPoller = new MoodPoller(BACKEND_URL, Log, RunMoodAction);
            _moodPoller.Start();
        }

        /// <summary>
        /// Adds all UI controls to the form in code.
        /// Called once from the constructor after InitializeComponent().
        ///
        /// The VS designer is unavailable due to x86 assembly constraints in
        /// the ARC SDK (EZ_B.dll is 32-bit; the designer runs in a 64-bit process).
        ///
        /// To add future feature buttons (camera, movement, etc):
        /// follow the same pattern below and place them below the log box.
        /// </summary>
        private void InitializeUI()
        {
            // --- Start/Stop Listening toggle button ---
            _btnListen = new Button();
            _btnListen.Text = "Start Listening";
            _btnListen.Size = new System.Drawing.Size(160, 40);
            _btnListen.Location = new System.Drawing.Point(10, 10);
            _btnListen.Click += BtnListen_Click;
            this.Controls.Add(_btnListen);

            // --- Log box: displays pipeline status messages in real time ---
            // RichTextBox chosen over TextBox for its built-in multiline support,
            // vertical scrollbar, and read-only mode (prevents accidental editing).
            // All writes go through Log() which marshals to the UI thread safely.
            _logBox = new RichTextBox();
            _logBox.Location = new System.Drawing.Point(10, 60);
            _logBox.Size = new System.Drawing.Size(440, 200);
            _logBox.ReadOnly = true;
            _logBox.BackColor = System.Drawing.Color.Black;
            _logBox.ForeColor = System.Drawing.Color.LimeGreen;
            _logBox.Font = new System.Drawing.Font("Consolas", 8.5f);
            _logBox.ScrollBars = RichTextBoxScrollBars.Vertical;
            this.Controls.Add(_logBox);

            // --- Camera button ---
            _btnCamera = new Button();
            _btnCamera.Text = "Start Camera";
            _btnCamera.Size = new System.Drawing.Size(160, 40);
            _btnCamera.Location = new System.Drawing.Point(180, 10);
            _btnCamera.Click += BtnCamera_Click;
            this.Controls.Add(_btnCamera);
        }

        /// <summary>
        /// ARC calls this when loading the project file.
        /// Restores the skill's saved configuration from disk.
        /// </summary>
        public override void SetConfiguration(ARC.Config.Sub.PluginV1 cf)
        {
            _config = (Configuration)cf.GetCustomObjectV2(typeof(Configuration));
            base.SetConfiguration(cf);
        }

        /// <summary>
        /// ARC calls this when saving the project file.
        /// Hands our current configuration back to ARC to serialize.
        /// </summary>
        public override ARC.Config.Sub.PluginV1 GetConfiguration()
        {
            _cf.SetCustomObjectV2(_config);
            return base.GetConfiguration();
        }

        /// <summary>
        /// Fires when the user clicks the gear icon in the ARC skill title bar.
        /// Opens the ConfigForm dialog for adjusting saved settings.
        /// </summary>
        public override void ConfigPressed()
        {
            using (var form = new ConfigForm())
            {
                form.SetConfiguration(_config);
                if (form.ShowDialog() != DialogResult.OK)
                    return;
                _config = form.GetConfiguration();
            }
        }

        // -----------------------------------------------------------------------
        // INTERNAL LOGGER
        // -----------------------------------------------------------------------

        /// <summary>
        /// Thread-safe log writer. Prepends a timestamp and appends the message
        /// to the log box, trimming old lines when the cap is exceeded.
        ///
        /// THREAD SAFETY: Windows Forms controls can only be touched by the UI thread.
        /// NAudio's DataAvailable event and Task.Run continuations fire on background
        /// threads. InvokeRequired detects this and marshals the write back to the
        /// UI thread via Invoke — preventing the InvalidOperationException that would
        /// otherwise crash the skill silently.
        /// </summary>
        private void Log(string message)
        {
            // Do not attempt to log if the form is already disposed or in the process of disposing.
            if (this.IsDisposed || this.Disposing)
                return;

            string line = $"[{DateTime.Now:HH:mm:ss}] {message}";

            if (this.InvokeRequired)
            {
                try
                {
                    // We are on a background thread — hand the write to the UI thread.
                    // We have to do this because windowsForm forces a single thread architecture
                    this.Invoke((Action)(() => AppendLog(line)));
                }
                catch (ObjectDisposedException)
                {
                    // Form was disposed between the IsDisposed check above and
                    // this Invoke call — a genuine narrow race, not a bug to chase
                    // further. Safe to ignore during shutdown.
                }
            }
            else
            {
                // We are already on the UI thread — write directly.
                AppendLog(line);
            }
        }

        /// <summary>
        /// Appends a line to the log box, trims old lines at the cap,
        /// and scrolls to the newest message.
        /// Must only be called from the UI thread (always via Log()).
        /// </summary>
        private void AppendLog(string line)
        {
            _logBox.AppendText(line + "\n");

            // Trim the oldest lines when we exceed the cap.
            // _logBox.Lines returns a string array of every line.
            // Skip() drops the oldest entries from the front,
            // leaving only the newest MAX_LOG_LINES lines.
            // string.Join rebuilds them into a single string and replaces
            // the entire textbox content in one operation — no manual shifting.
            if (_logBox.Lines.Length > MAX_LOG_LINES)
            {
                var trimmed = _logBox.Lines
                    .Skip(_logBox.Lines.Length - MAX_LOG_LINES)
                    .ToArray();
                _logBox.Text = string.Join("\n", trimmed);
            }

            // Force scroll to the bottom so the newest message is always visible.
            // SelectionStart moves the cursor to the end of all text.
            // ScrollToCaret scrolls the view to wherever the cursor is.
            _logBox.SelectionStart = _logBox.Text.Length;
            _logBox.ScrollToCaret();
        }
        // -----------------------------------------------------------------------
        // WAKE WORD DETECTION EVENT
        // -----------------------------------------------------------------------

        /// <summary>
        /// Fired by System.Speech.Recognition's internal thread when "Hello Robot"
        /// is detected in the audio stream with sufficient confidence.
        ///
        /// Sets _isWakeWordDetected = true which unblocks OnAudioDataAvailable
        /// from buffering audio. From this point the energy threshold and silence
        /// timeout logic runs normally until the utterance is dispatched to Python,
        /// at which point _isWakeWordDetected is reset to false.
        ///
        /// THREAD: fires on System.Speech's internal background thread.
        /// _isWakeWordDetected is volatile so the write is immediately visible
        /// to NAudio's thread which reads it in OnAudioDataAvailable.
        /// </summary>
        private void OnWakeWordDetected(object sender, SpeechRecognizedEventArgs e)
        {
            // Confidence threshold — reject low-confidence detections.
            // 0.7 means 70% confidence minimum. Reduces false positives from
            // similar-sounding phrases. Tune this if legitimate "Hello Robot" calls
            // are being rejected (lower it) or false triggers occur (raise it).
            if (e.Result.Confidence < 0.85f)
            {
                Log($"[Wake] Low confidence detection ({e.Result.Confidence:P0}), ignoring.");
                return;
            }

            _isWakeWordDetected = true;
            Log($"[Wake] 'Hello Robot' detected ({e.Result.Confidence:P0} confidence). Listening for your question...");
        }

        // -----------------------------------------------------------------------
        // BUTTON HANDLER
        // -----------------------------------------------------------------------

        /// <summary>
        /// Toggles the mic listening loop on and off.
        /// First click: starts listening. Second click: stops cleanly.
        ///
        /// The button is disabled during the stop sequence and re-enabled
        /// only after NAudio confirms full cleanup in OnRecordingStopped.
        /// This prevents the NoDriver race condition where a fast Stop→Start
        /// click sequence tries to open the mic before the previous session
        /// has fully released the audio driver.
        /// </summary>
        private void BtnListen_Click(object sender, EventArgs e)
        {
            if (_waveIn == null)
            {
                StartListening();

                // Only update to "Stop Listening" if StartListening actually succeeded.
                // If mic initialisation failed, _waveIn will be null again because the
                // catch block inside StartListening disposed and nulled it.
                // In that case the button text stays as "Start Listening" correctly.
                if (_waveIn != null)
                {
                    _btnListen.Text = "Stop Listening";
                }
            }
            else
            {
                // Disable immediately — re-enabled in OnRecordingStopped
                // after NAudio confirms the audio driver is fully released.
                _btnListen.Enabled = false;
                StopListening();
            }
        }

        /// <summary>
        /// Toggles the camera stream on/off — same click-to-toggle shape as
        /// BtnListen_Click. First click (camera not yet attached): calls
        /// AttachCamera() and, only if it actually succeeded (i.e. _camera is no
        /// longer null — AttachCamera can silently fail if no Camera Device skill
        /// exists), flips the button to "Stop Camera". Second click: calls
        /// DetachCamera() unconditionally and resets the button label.
        ///
        /// --- PURPOSE ---
        /// 
        /// Deliberately independent of BtnListen_Click / _isProcessing — the
        /// camera stream must keep running regardless of whether JD is currently
        /// listening, thinking, or speaking. This was a deliberate product
        /// decision made earlier in this project, not an oversight.
        /// </summary>
        private void BtnCamera_Click(object sender, EventArgs e)
        {
            if (_camera == null)
            {
                AttachCamera();
                if (_camera != null)
                    _btnCamera.Text = "Stop Camera";
            }
            else
            {
                DetachCamera();
                _btnCamera.Text = "Start Camera";
            }
        }

        // -----------------------------------------------------------------------
        // MIC CAPTURE LOOP
        // -----------------------------------------------------------------------

        /// <summary>
        /// Initializes NAudio mic capture and begins the listening loop.
        ///
        /// NAudio fires DataAvailable on its own background thread approximately
        /// every 100ms with a fresh chunk of PCM audio bytes from the system mic.
        /// Energy threshold detection runs inside that event handler.
        /// </summary>
        private void StartListening()
        {
            _listenCts = new CancellationTokenSource();

            // Reset all speech tracking state for the new session.
            _audioBuffer.Clear();
            _isSpeaking = false;
            _lastSpeechTime = DateTime.MinValue;

            // --- Initialise the bridge stream ---
            // Must be created BEFORE NAudio starts so the speech engine
            // has somewhere to read from the moment audio starts flowing.
            _bridgeStream = new AudioBridgeStream();

            // --- Initialise the speech recognizer ---
            // SpeechRecognitionEngine is the SAPI5 wrapper.
            // We set its input to our bridge stream instead of the mic directly —
            // this is what allows NAudio to own the mic while the speech engine
            // still receives audio. They share the same audio bytes via the queue.
            _speechRecognizer = new SpeechRecognitionEngine();

            // Define the grammar — the ONLY phrase the engine listens for.
            // GrammarBuilder constructs a simple phrase grammar from a string.
            // The engine ignores everything that doesn't match this phrase.
            var grammar = new Grammar(new GrammarBuilder("Hello Robot"));
            _speechRecognizer.LoadGrammar(grammar);

            // Wire the detection event — fires on the speech engine's internal thread
            // when "Hello Robot" is heard with sufficient confidence.
            _speechRecognizer.SpeechRecognized += OnWakeWordDetected;

            // Point the speech engine at our bridge stream.
            // AudioFormat tells it what sample rate and bit depth to expect.
            // Must match NAudio's capture format exactly — 16000Hz, 16-bit, mono.
            var audioFormat = new System.Speech.AudioFormat.SpeechAudioFormatInfo(
                SAMPLE_RATE, System.Speech.AudioFormat.AudioBitsPerSample.Sixteen,
                System.Speech.AudioFormat.AudioChannel.Mono);
            _speechRecognizer.SetInputToAudioStream(_bridgeStream, audioFormat);

            // RecognizeAsync runs the engine continuously on its own internal thread.
            // Multiple means it keeps recognizing repeatedly — not just once.
            // It will keep listening until we call RecognizeAsyncStop().
            _speechRecognizer.RecognizeAsync(RecognizeMode.Multiple);

            // --- Initialise NAudio ---
            _waveIn = new WaveInEvent();
            _waveIn.WaveFormat = new WaveFormat(SAMPLE_RATE, BITS_PER_SAMPLE, CHANNELS);
            _waveIn.DataAvailable += OnAudioDataAvailable;
            _waveIn.RecordingStopped += OnRecordingStopped;

            try
            {
                // StartRecording() is where NAudio actually tries to open the Windows
                // audio input driver. If no microphone is connected or the driver is
                // unavailable, NAudio throws MmException with message "NoDriver".
                // Without this try-catch, that exception propagates up to BtnListen_Click
                // which has no handler, and ARC catches it displaying its own crash dialog.
                // We catch it here instead and show a friendly log message.
                _waveIn.StartRecording();
                Log("[Hearing] Mic capture started. Listening for wake word 'Hello Robot'...");
            }
            catch (NAudio.MmException ex)
            {
                Log($"[Hearing] Microphone error: {ex.Message}. Is a mic connected and enabled in Windows Sound Settings?");

                // StopFeeding() MUST come before RecognizeAsyncStop().
                // StopFeeding() calls CompleteAdding() on the queue, which unblocks
                // any Read() call currently waiting on Take(). Without this,
                // RecognizeAsyncStop() blocks forever waiting for the engine to stop,
                // while the engine is stuck in Read() waiting for bytes that never come.
                // aka its trapped in a deadlock and ARC freezes
                _bridgeStream?.StopFeeding();
                _bridgeStream = null;

                // Now stop the speech engine and dispose it — no more audio will be coming.
                if (_speechRecognizer != null)
                {
                    _speechRecognizer.RecognizeAsyncStop();
                    _speechRecognizer.Dispose();
                    _speechRecognizer = null;
                }
                _bridgeStream = null;
                _waveIn.Dispose();
                _waveIn = null;
                _btnListen.Enabled = true;
            }
        }

        /// <summary>
        /// Signals NAudio to stop recording. Actual resource disposal happens
        /// in OnRecordingStopped after NAudio confirms the driver is released.
        /// </summary>
        private void StopListening()
        {
            _listenCts?.Cancel();

            // StopFeeding() MUST come before RecognizeAsyncStop().
            // StopFeeding() calls CompleteAdding() on the queue, which unblocks
            // any Read() call currently waiting on Take(). Without this,
            // RecognizeAsyncStop() blocks forever waiting for the engine to stop,
            // while the engine is stuck in Read() waiting for bytes that never come.
            // aka its trapped in a deadlock and ARC freezes
            _bridgeStream?.StopFeeding();
            _bridgeStream = null;

            if (_speechRecognizer != null)
            {
                _speechRecognizer.RecognizeAsyncStop();
                _speechRecognizer.Dispose();
                _speechRecognizer = null;
            }

            // Reset wake word flag so next session starts clean.
            _isWakeWordDetected = false;

            _waveIn?.StopRecording();
            Log("[Hearing] Mic capture stopping...");
        }

        /// <summary>
        /// Fires on NAudio's background thread every ~100ms with fresh PCM bytes.
        ///
        /// Pipeline:
        /// 1. Guard — discard if stopped or pipeline is currently processing
        /// 2. Calculate peak energy of this audio chunk
        /// 3. Above threshold → buffer bytes, mark as speaking
        /// 4. Below threshold after speech → track silence duration
        /// 5. Silence exceeds timeout → dispatch utterance to Python
        /// </summary>
        private void OnAudioDataAvailable(object sender, WaveInEventArgs e)
        {
            // Feed bytes to the bridge stream BEFORE any guard check.
            // The speech engine needs a continuous audio feed to detect "Hello Robot" —
            // even during _isProcessing (JD speaking) we keep feeding it so it stays
            // in sync and is ready to detect the wake word immediately after playback.
            // We only skip feeding if the session is fully cancelled.
            if (_listenCts != null && !_listenCts.IsCancellationRequested)
                _bridgeStream?.Write(e.Buffer, 0, e.BytesRecorded);

            // Now apply the full guard — discard from buffering pipeline if:
            // - Session cancelled
            // - Pipeline currently processing a previous utterance
            // - Wake word not yet detected
            if (_listenCts == null || _listenCts.IsCancellationRequested || _isProcessing || !_isWakeWordDetected)
                return;

            // Calculate peak energy of this audio chunk.
            // PCM 16-bit samples arrive as raw bytes, two bytes per sample.
            // BitConverter.ToInt16 reads each pair as a signed 16-bit integer.
            // Dividing by short.MaxValue (32767) normalizes to -1.0 to +1.0 range.
            // We take the absolute value and track the highest peak in the chunk.
            float peakEnergy = 0f;
            for (int i = 0; i < e.BytesRecorded - 1; i += 2)
            {
                short sample = BitConverter.ToInt16(e.Buffer, i);
                float normalized = sample / (float)short.MaxValue;
                float absolute = Math.Abs(normalized);
                if (absolute > peakEnergy)
                    peakEnergy = absolute;
            }

            bool isLoud = peakEnergy > ENERGY_THRESHOLD;

            if (isLoud)
            {
                // Audio is above threshold — potential speech detected.
                _lastSpeechTime = DateTime.UtcNow;

                if (!_isSpeaking)
                {
                    Log("[Hearing] Speech detected. Buffering...");
                    _isSpeaking = true;
                }

                // Only append real bytes — e.Buffer may be pre-allocated larger
                // than e.BytesRecorded. Taking extra bytes adds garbage to the WAV.
                for (int i = 0; i < e.BytesRecorded; i++)
                    _audioBuffer.Add(e.Buffer[i]);
            }
            else if (_isSpeaking)
            {
                // Below threshold but we were speaking — might be a natural pause.
                // Keep buffering so the WAV isn't cut off abruptly mid-sentence.
                for (int i = 0; i < e.BytesRecorded; i++)
                    _audioBuffer.Add(e.Buffer[i]);

                double silenceDurationMs = (DateTime.UtcNow - _lastSpeechTime).TotalMilliseconds;

                if (silenceDurationMs >= SILENCE_TIMEOUT_MS)
                {
                    // Silence has persisted long enough — utterance is complete.
                    // Snapshot the buffer and reset state BEFORE dispatching to Python
                    // so the mic loop immediately resets and stays ready.
                    byte[] utteranceBytes = _audioBuffer.ToArray();
                    _audioBuffer.Clear();
                    _isSpeaking = false;

                    Log($"[Hearing] Utterance complete ({utteranceBytes.Length} bytes). Sending to Python...");

                    // Reset wake word flag immediately after dispatch.
                    // This returns the system to wake word hunting mode while Python
                    // processes the utterance in the background. The user must say
                    // "Hello Robot" again for the next interaction.
                    _isWakeWordDetected = false;
                    Log("[Wake] Listening for 'Hello Robot' again...");

                    // Fire on a background thread — this is a synchronous event handler
                    // and must return immediately. The mic loop cannot block here.
                    _ = Task.Run(() => SendUtteranceToPython(utteranceBytes));
                }
            }
            // If isLoud is false AND _isSpeaking is false: pure silence — do nothing.
        }

        /// <summary>
        /// Fires when NAudio fully completes its shutdown sequence after StopRecording().
        /// This is the only safe place to dispose _waveIn — disposing earlier risks
        /// pulling resources out from under NAudio while it's still flushing.
        ///
        /// Also re-enables the Start Listening button here (not in StopListening)
        /// to prevent the NoDriver race condition on fast Stop→Start sequences.
        /// </summary>
        private void OnRecordingStopped(object sender, StoppedEventArgs e)
        {
            _waveIn?.Dispose();
            _waveIn = null;

            // Re-enable button and reset its label on the UI thread.
            // InvokeRequired because this fires on NAudio's background thread.
            if (this.InvokeRequired)
            {
                this.Invoke((Action)(() =>
                {
                    _btnListen.Text = "Start Listening";
                    _btnListen.Enabled = true;
                }));
            }
            else
            {
                _btnListen.Text = "Start Listening";
                _btnListen.Enabled = true;
            }

            if (e.Exception != null)
                Log($"[Hearing] Recording stopped with error: {e.Exception.Message}");
            else
                Log("[Hearing] Mic capture fully stopped.");
        }

        // -----------------------------------------------------------------------
        // CAMERA PIPELINE
        // -----------------------------------------------------------------------

        /// <summary>
        /// Fired by EZ_B.Camera whenever a new frame has been captured — the event
        /// itself carries no data (confirmed parameterless signature; there is no
        /// EventArgs to read a frame from). The actual pixel data lives on a live
        /// property, _camera.Camera.GetCurrentBitmapManaged, which this handler
        /// must read and copy IMMEDIATELY: ARC's own camera pipeline can update
        /// that property again the instant this method returns, so whatever we
        /// don't copy synchronously, right here, is not guaranteed to still be
        /// valid once we're on a different thread.
        ///
        /// `new Bitmap(original)` creates a fully independent copy (not just a
        /// reference) — deliberately NOT `.Clone()`, to sidestep a known GDI+
        /// OutOfMemoryException risk on a different Clone() overload, and because
        /// the constructor form is the more standard pattern for copying a live/
        /// streaming frame specifically.
        ///
        /// The actual upload work (JPEG encoding, HTTP POST, error handling) is
        /// deliberately NOT done here — it's handed off via Task.Run to
        /// CameraFrameUploader.UploadFrameAsync so this event handler returns as
        /// fast as possible. Following threading guidance, camera events are
        /// GUI-thread-adjacent — lengthy work here risks delaying/freezing ARC's
        /// own UI, same category of concern already solved once for SpeakResponse.
        /// </summary>
        private void Camera_OnNewFrame()
        {
            if (_cameraUploader == null)
            {
                Log("The camera uploader has not yet been initialized. " +
                    "Please check if the camera is attached and the uploader is set up correctly.");
                return;
            }
                

            var original = _camera.Camera.GetCurrentBitmapManaged;
            if (original == null)
            {
                Log("Failed to retrieve the current bitmap from the camera. " +
                    "Please ensure the camera is functioning correctly.");
                return;
            }

            // Synchronous copy — must happen HERE, on this thread, before Task.Run.
            // OnNewFrame carries no data of its own (confirmed parameterless
            // signature) — we're reading a live property that ARC's pipeline can
            // update again the moment this handler returns. This copy is our only
            // snapshot. Using the Bitmap(Image) constructor rather than .Clone() —
            // functionally equivalent for a full, unmodified copy, but avoids an
            // object cast and matches the more commonly recommended pattern for
            // copying a live/streaming frame specifically.
            Bitmap frameClone = new Bitmap(original);

            _ = Task.Run(async () =>
            {
                try
                {
                    await _cameraUploader.UploadFrameAsync(frameClone);
                }
                catch (Exception ex)
                {
                    Log($"[Vision] Frame upload error: {ex.Message}");
                }
            });
        }

        /// <summary>
        /// Locates ARC's existing Camera Device skill in the current project and
        /// wires this skill up to receive its frames.
        ///
        /// IMPORTANT: this does NOT create a camera or start any video capture —
        /// it only finds and attaches to a Camera Device skill that must already
        /// exist and be configured in the ARC project. If no such skill is present,
        /// GetControlByType returns an empty array and this logs a message and
        /// returns without throwing — a missing camera skill is a configuration
        /// problem to fix in ARC, not a code bug to crash over.
        ///
        /// A fresh CameraFrameUploader (and its long-lived visionHttpClient) is
        /// created here, per attach session — mirroring how _bridgeStream and
        /// _speechRecognizer are recreated each time StartListening() runs, rather
        /// than living for the entire app's lifetime. The HttpClient reuse
        /// requirement is about surviving per-frame churn within
        /// one session, not literal app-lifetime — one Attach/Detach session's
        /// worth of reuse is still enormous relative to 15-30 frames/second.
        ///
        /// Called from BtnCamera_Click when the button is toggled to "on".
        /// </summary>
        private void AttachCamera()
        {
            Control[] cameras = ARC.EZBManager.FormMain.GetControlByType(typeof(ARC.UCForms.FormCameraDevice));

            if (cameras.Length == 0)
            {
                Log("[Vision] No Camera Device skill found in this ARC project.");
                return;
            }

            _camera = (ARC.UCForms.FormCameraDevice)cameras[0];
            _cameraUploader = new CameraFrameUploader(BACKEND_URL, Log);

            _camera.Camera.OnNewFrame += Camera_OnNewFrame;

            Log("[Vision] Camera attached. Streaming frames to backend.");
        }

        // <summary>
        /// Reverses AttachCamera(): unsubscribes from OnNewFrame so no further
        /// frames are processed, releases the camera reference, and disposes
        /// CameraFrameUploader (which closes its visionHttpClient).
        ///
        /// Called from two places, deliberately: BtnCamera_Click (user manually
        /// toggles the camera off) AND MainForm_FormClosing (skill/ARC shutting
        /// down). Both paths reuse this exact same method rather than duplicating
        /// teardown logic — same discipline already established for
        /// StopListening()'s hearing-side cleanup.
        ///
        /// Safe to call even if AttachCamera() was never successfully run (e.g.
        /// no camera skill was found) — every step is null-checked/null-conditional,
        /// so calling this on an already-detached or never-attached state is a
        /// harmless no-op, not an error.
        /// </summary>
        private void DetachCamera()
        {
            if (_camera != null)
            {
                _camera.Camera.OnNewFrame -= Camera_OnNewFrame;
                _camera = null;
            }

            _cameraUploader?.Dispose();
            _cameraUploader = null;

            Log("[Vision] Camera detached.");
        }

        // -----------------------------------------------------------------------
        // PYTHON PIPELINE
        // -----------------------------------------------------------------------

        /// <summary>
        /// Full hearing pipeline: raw PCM bytes in, spoken robot response out.
        ///
        /// Steps:
        /// 1. Set _isProcessing = true — mic goes deaf for the duration
        /// 2. Wrap PCM bytes into a WAV container in memory (no disk write)
        /// 3. POST WAV to /hearing/transcribe — Silero VAD + Whisper on Python side
        /// 4. If noise: discard silently, release lock
        /// 5. If speech: await SpeakResponse(transcript) — full brain pipeline
        /// 6. SpeakResponse returns only after JD finishes speaking + buffer delay
        /// 7. Release _isProcessing = false in finally — guaranteed even on crash
        ///
        /// WHY finally: if SpeakResponse throws an unhandled exception, _isProcessing
        /// must still be released. Without finally, a crash leaves _isProcessing = true
        /// permanently and JD goes deaf for the rest of the session with no indication.
        /// </summary>
        private async Task SendUtteranceToPython(byte[] pcmBytes)
        {
            // Acquire the processing lock — mic is now deaf.
            // Set before any await so no new utterance can slip through
            // between this Task.Run starting and the first await point.
            _isProcessing = true;

            try
            {
                // --- Step 1: Wrap raw PCM bytes into a WAV container in memory ---
                // Python's soundfile expects a valid WAV file with a RIFF header,
                // not raw PCM bytes. WaveFileWriter writes the correct header +
                // PCM data into a MemoryStream — zero disk writes, zero temp files.
                // IMPORTANT: ToArray() must be called AFTER WaveFileWriter is disposed.
                // Dispose() flushes and finalizes the WAV header — calling ToArray()
                // before dispose gives you an incomplete, malformed WAV file.
                byte[] wavBytes;
                using (var wavStream = new MemoryStream())
                {
                    using (var writer = new WaveFileWriter(wavStream,
                        new WaveFormat(SAMPLE_RATE, BITS_PER_SAMPLE, CHANNELS)))
                    {
                        writer.Write(pcmBytes, 0, pcmBytes.Length);
                    }
                    wavBytes = wavStream.ToArray();
                }

                // --- Step 2: POST WAV to /hearing/transcribe ---
                // MultipartFormDataContent mimics a browser file upload.
                // FastAPI's UploadFile handler on the Python side receives it
                // identically to a real file — it never knows the difference.
                // "audio" must match the parameter name in hearing/routers.py:
                //   async def transcribe(audio: UploadFile = File(...))
                using (var client = new HttpClient())
                using (var formData = new MultipartFormDataContent())
                {
                    var audioContent = new ByteArrayContent(wavBytes);
                    audioContent.Headers.ContentType =
                        new System.Net.Http.Headers.MediaTypeHeaderValue("audio/wav");
                    formData.Add(audioContent, "audio", "utterance.wav");

                    var response = await client.PostAsync(
                        $"{BACKEND_URL}/hearing/transcribe", formData);

                    if (!response.IsSuccessStatusCode)
                    {
                        Log($"[Hearing] /hearing/transcribe error: {response.StatusCode}");
                        return; // finally releases _isProcessing
                    }

                    // --- Step 3: Parse TranscribeResponse JSON ---
                    // Expected shape: { "is_speech": bool, "transcript": string | null }
                    string json = await response.Content.ReadAsStringAsync();
                    var parsed = JObject.Parse(json);

                    bool isSpeech = parsed["is_speech"]?.Value<bool>() ?? false;    // ?. (Null-Conditional Operator ... returns null instead of crashing if key doesnt exist)
                                                                                    // ?? default back to false (our fallback value) instead of null
                    if (!isSpeech)
                    {
                        Log("[Hearing] VAD: noise detected, discarding.");
                        return; // finally releases _isProcessing
                    }

                    string transcript = parsed["transcript"]?.Value<string>();

                    if (string.IsNullOrWhiteSpace(transcript))
                    {
                        Log("[Hearing] VAD passed but transcript empty. Discarding.");
                        return; // finally releases _isProcessing
                    }

                    Log($"[Hearing] Transcript: \"{transcript}\"");

                    // --- Step 4: Hand transcript to brain pipeline ---
                    // await here is critical — we must NOT release _isProcessing
                    // until SpeakResponse fully completes (Gemini + Piper + playback
                    // duration delay). SpeakResponse is now async Task (not async void)
                    // so it is properly awaitable and exceptions propagate back here.
                    await SpeakResponse(transcript);
                }
            }
            catch (HttpRequestException ex)
            {
                // Network-level failure — backend unreachable. Recoverable.
                // Log only, no MessageBox — robot going silent is acceptable
                // for a transient network drop during development.
                Log($"[Hearing] Backend unreachable, Is Uvicorn running?\nError Message: {ex.Message}");
            }
            catch (Exception ex)
            {
                // Unexpected failure — log AND show MessageBox because something
                // genuinely broke that needs immediate developer attention.
                Log($"[Hearing] Unexpected error: {ex.Message}");
                MessageBox.Show(
                    $"Unexpected error in hearing pipeline:\n\n{ex.Message}",
                    "Hearing — Critical Error",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Error
                );
            }
            finally
            {
                // ALWAYS release the processing lock — no matter what happened above.
                // This is the single guarantee that _isProcessing cannot stay true
                // permanently. If SpeakResponse crashes, if a network call times out,
                // if anything throws — this finally block still runs and JD can hear again.
                _isProcessing = false;
            }
        }

        // -----------------------------------------------------------------------
        // ARC SCRIPT BRIDGE
        // -----------------------------------------------------------------------

        /// <summary>
        /// Bridges ARC's ControlCommand() script function to our C# methods.
        /// ARC calls this synchronously when an EZ-Script fires:
        ///   ControlCommand("JdMegaMind", "SpeakResponse", "hello")
        ///
        /// Cannot be made async — ARC's base class defines this as void and the
        /// script engine expects it to return immediately. Async work is offloaded
        /// to Task.Run so ARC's script thread is never blocked.
        ///
        /// The _isProcessing lock is set inside the Task.Run lambda so it covers
        /// the full SpeakResponse execution even via this manual trigger path.
        /// Without this, an ARC script triggering SpeakResponse would bypass the
        /// lock entirely and JD could hear himself if the mic was active.
        ///
        /// To add new script-triggerable commands in future:
        ///   else if (command.Equals("StartCamera", ...)) { ... }
        /// </summary>
        public override void SendCommand(string command, params string[] values)
        {
            if (command.Equals("SpeakResponse", StringComparison.InvariantCultureIgnoreCase))
            {
                if (values != null && values.Length > 0 && !string.IsNullOrWhiteSpace(values[0]))
                {
                    _ = Task.Run(async () =>
                    {
                        _isProcessing = true;
                        try
                        {
                            await SpeakResponse(values[0]);
                        }
                        finally
                        {
                            _isProcessing = false;
                        }
                    });
                }
                else
                {
                    Log("[Bridge] SpeakResponse received blank text. Ignoring.");
                }
            }
            else
            {
                base.SendCommand(command, values);
            }
        }

        // -----------------------------------------------------------------------
        // SPEAK RESPONSE
        // -----------------------------------------------------------------------

        /// <summary>
        /// Full brain pipeline: text in → audio out of JD's EZB speaker.
        ///
        /// Changed from async void to async Task so callers can properly await it.
        /// This is critical for the _isProcessing lock — async void returns to its
        /// caller at the first await, making it impossible to know when playback
        /// actually ends. async Task allows SendUtteranceToPython and SendCommand
        /// to await this method fully, including the playback duration delay.
        ///
        /// Steps:
        /// 1. POST text to /brain/chat — Gemini generates response
        /// 2. Receive raw WAV bytes from Piper TTS
        /// 3. Resample to EZB hardware spec: 14700 Hz, 8-bit, mono
        /// 4. GZip compress for reliable Wi-Fi streaming to EZB
        /// 5. Calculate playback duration from final byte count
        /// 6. Hand to SoundV4.PlayData() — audio plays through JD's speaker
        /// 7. await Task.Delay(duration + buffer) — hold _isProcessing true
        ///    for the exact duration JD is physically speaking, plus a buffer
        ///    to prevent the mic picking up the tail end of JD's voice
        ///
        /// Called by:
        ///   - SendUtteranceToPython (primary mic-driven path)
        ///   - SendCommand Task.Run lambda (manual ARC script override path)
        /// </summary>
        public async Task SpeakResponse(string text)
        {
            try
            {
                using (var client = new HttpClient())
                {
                    // Manual JSON construction — avoids System.Text.Json which
                    // is unavailable in .NET Framework 4.8. The Replace handles
                    // any quotation marks in the text that would break JSON formatting.
                    string json = "{\"text\":\"" + text.Replace("\"", "\\\"") + "\"}";
                    var content = new StringContent(json, Encoding.UTF8, "application/json");

                    var response = await client.PostAsync($"{BACKEND_URL}/brain/chat", content);

                    // NEW — read the action header BEFORE reading the body. HttpResponseMessage
                    // exposes headers and content as separate properties, not a tuple — this
                    // is not Python-style unpacking, these are two independent reads off the
                    // same response object.
                    string actionJson = response.Headers.TryGetValues("X-JD-Action", out var actionValues)
                        ? actionValues.FirstOrDefault()
                        : null;

                    // DEBUG — confirms what actually arrived on the C# side, independent of
                    // what Python believes it sent. If Python's log shows a header was set
                    // but this logs "no action header," the bug is in transit/header
                    // naming, not in verification logic — narrows the search immediately.
                    if (!string.IsNullOrEmpty(actionJson))
                        Log($"[Brain] Action header received: {actionJson}");
                    else
                        Log("[Brain] No action header this turn.");

                    // Handle HTTP errors gracefully — log and return, don't throw.
                    if (!response.IsSuccessStatusCode)
                    {
                        int code = (int)response.StatusCode;
                        if (code == 503)
                        {
                            // Gemini server overloaded — temporary, not a code bug.
                            Log("[Brain] Gemini overloaded (503). Try again in a moment.");
                            return;
                        }
                        else if (code == 429)
                        {
                            // Rate limit hit — too many requests in a short window.
                            Log("[Brain] Gemini rate limit hit (429). Too many requests.");
                            return;
                        }
                        else
                        {
                            // Unexpected HTTP error — treat as a real failure.
                            throw new Exception("Unexpected backend status: " + response.StatusCode);
                        }
                    }

                    // NEW — fire the verified action, if one was sent. Deliberately done
                    // BEFORE the audio playback block below, not after — per the confirmed
                    // design intent (point 7): action and speech should happen together,
                    // not sequentially. NOTE: whether firing this before vs. during vs.
                    // after PlayData() actually produces true simultaneous execution on
                    // real JD hardware is UNVERIFIED — flagged explicitly, needs real-robot
                    // testing, not assumed safe.
                    if (!string.IsNullOrEmpty(actionJson))
                    {
                        try
                        {
                            // In the header we did json.dumps to convert the Python list into a JSON array string.
                            // Here we parse it back into a JArray (the reverse of json.dumps) and extract the three elements.
                            // The reason of doing this was because the header is a string, so in the backend we converted the json into a string
                            // and here we need to convert it back to a list/array to extract the elements.
                            var actionArray = JArray.Parse(actionJson);
                            string gadget = actionArray[0].Value<string>();
                            string cmd = actionArray[1].Value<string>();
                            string param = actionArray[2].Value<string>();
                            RunAction(gadget, cmd, param);
                        }
                        catch (Exception ex)
                        {
                            // Malformed header would be a Python-side bug (verify_action()
                            // should never produce anything but a clean 3-element array or
                            // omit the header entirely) — logged, not fatal, action simply
                            // doesn't fire for this turn.
                            Log($"[Brain] Failed to parse X-JD-Action header: {ex.Message}");
                        }
                    }

                    // Fire-and-forget mood check — Python's apply_event() already ran
                    // inside the /brain/chat call that just succeeded (confirmed call
                    // order in emotions_module.md), so this is the freshest possible read.
                    // Not awaited — must never delay audio playback.
                    _moodPoller?.PollAfterSpeaking();

                    var audioBytes = await response.Content.ReadAsByteArrayAsync();

                    using (var rawStream = new MemoryStream(audioBytes))
                    using (var wavReader = new WaveFileReader(rawStream))
                    {
                        // Resample to EZB v4 hardware requirements: 14700 Hz, 8-bit, mono.
                        // The EZB PCB has a fixed hardware clock — audio at any other
                        // sample rate will play back at the wrong pitch and speed.
                        var targetFormat = new WaveFormat(EZB_SAMPLE_RATE, EZB_BITS, EZB_CHANNELS);
                        using (var conversionStream = new WaveFormatConversionStream(targetFormat, wavReader))
                        using (var compressedStream = new MemoryStream())
                        {
                            // GZip compress the resampled PCM data.
                            // The EZB communicates over Wi-Fi — compression reduces
                            // packet size and prevents audio glitches on busy networks.
                            using (var gzip = new System.IO.Compression.GZipStream(
                                compressedStream,
                                System.IO.Compression.CompressionMode.Compress))
                            {
                                conversionStream.CopyTo(gzip);
                            }

                            byte[] finalEzbAudio = compressedStream.ToArray();

                            // Calculate playback duration from the RAW PCM byte count BEFORE compression.
                            // conversionStream.Length gives the actual number of PCM bytes the EZB will
                            // decompress and play — this is the ground truth for real playback duration.
                            //
                            // WHY NOT finalEzbAudio.Length:
                            // GZip compresses audio by roughly 30-50%. Using the compressed size would
                            // underestimate duration by that same ratio — causing the mic to reactivate
                            // while JD is still physically speaking, especially on long responses.
                            //
                            // Formula: pcmBytes / (sampleRate * channels * bytesPerSample)
                            // For 8-bit mono: channels=1, bytesPerSample=1, simplifies to pcmBytes / sampleRate
                            int bytesPerSample = EZB_BITS / 8;
                            long rawPcmBytes = conversionStream.Length;
                            int durationMs = (int)((double)rawPcmBytes
                                / (EZB_SAMPLE_RATE * EZB_CHANNELS * bytesPerSample) * 1000);
                            int totalWaitMs = durationMs + PLAYBACK_BUFFER_MS; ;

                            // Decompress directly into the EZB playback driver.
                            // SoundV4.PlayData() is the only mechanism available for
                            // streaming audio to JD's onboard speaker from C# plugin code.
                            // It returns immediately — playback happens asynchronously
                            // on the EZB hardware. The Task.Delay below is what keeps
                            // _isProcessing true for the actual playback duration.
                            using (var playbackStream = new MemoryStream(finalEzbAudio))
                            using (var gzipDecompressor = new System.IO.Compression.GZipStream(
                                playbackStream,
                                System.IO.Compression.CompressionMode.Decompress))
                            {
                                EZBManager.EZBs[0].SoundV4.PlayData(gzipDecompressor, 100);     // Plays at 100 volume
                            }

                            Log($"[Brain] JD speaking ({durationMs}ms). Mic locked for {totalWaitMs}ms.");

                            // Hold execution here for the full playback duration.
                            // This is what makes async Task meaningful over async void —
                            // the caller (SendUtteranceToPython) awaits this method and
                            // only releases _isProcessing after this delay completes.
                            await Task.Delay(totalWaitMs);

                            Log("[Brain] Playback complete. Mic reactivated.");
                        }
                    }
                }
            }
            catch (HttpRequestException ex)
            {
                // Network-level failure — backend unreachable. Recoverable.
                Log($"[Brain] Backend unreachable: {ex.Message}. Is uvicorn running?");
            }
            catch (Exception ex)
            {
                // Unexpected failure — log AND alert. Something genuinely broke.
                Log($"[Brain] Unexpected error: {ex.Message}");
                MessageBox.Show(
                    $"Unexpected error in SpeakResponse:\n\n{ex.Message}",
                    "Brain — Critical Error",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Error
                );
            }
        }

        /// <summary>
        /// Executes an already-verified action by writing its gadget/cmd/param
        /// onto the shared variable shelf for the single always-running watcher
        /// script to pick up and execute.
        ///
        /// CHANGED — this method used to take a bare keyword and look it up
        /// against a local ActionCommands dictionary. That lookup/whitelist has
        /// moved entirely to Python (app/brain/services.py's verify_action()) —
        /// ARC no longer makes any decision about whether an action is valid; it
        /// blindly executes whatever three strings it's handed. This is the
        /// intended "keep ARC dumb" outcome of the refactor — all verification
        /// now happens once, server-side, not duplicated in two places.
        /// </summary>
        public void RunAction(string gadget, string cmd, string param)
        {
            ARC.Scripting.VariableManager.SetVariable("$JdGadget", gadget);
            ARC.Scripting.VariableManager.SetVariable("$JdCmd", cmd);
            ARC.Scripting.VariableManager.SetVariable("$JdParam", param);
            Log($"[Action] Queued: {gadget} / {cmd} / {param}");
        }

        /// <summary>
        /// Dispatches a mood-driven RGB Animator action via its OWN dedicated
        /// variable slot — $JdMoodParam — deliberately separate from
        /// $JdGadget/$JdCmd/$JdParam (physical action dispatch, see RunAction
        /// above). Two independent producers writing the same slot could have
        /// one silently overwrite the other before the watcher script's next
        /// 100ms poll consumes it — the same "two sources of truth" failure
        /// shape this project has already rejected elsewhere. A dedicated slot
        /// makes that collision structurally impossible.
        ///
        /// Only ONE parameter, not a full triple: unlike physical actions
        /// (which span multiple gadgets/commands), mood dispatch has exactly
        /// one target, always — the built-in "RGB Animator" skill, via its
        /// "AutoPositionAction" command. Confirmed empirically this session:
        /// ControlCommand("RGB Animator", "AutoPositionAction", "Stripes")
        /// against a real custom-built Action, visually confirmed firing.
        /// Gadget/cmd are hardcoded directly in the watcher script (see
        /// arc-csharp.md), not passed through here — they're constants for
        /// this use case, not variables.
        /// </summary>
        public void RunMoodAction(string actionName)
        {
            ARC.Scripting.VariableManager.SetVariable("$JdMoodParam", actionName);
            Log($"[Mood] Queued RGB Animator action: {actionName}");
        }

        /// <summary>
        /// Fires when ARC closes this skill (project closing, skill removed, or
        /// ARC shutting down). Per Synthiam's Plugin Compliance guidance: always
        /// unsubscribe/dispose here, wrapped in try/catch — an unhandled exception
        /// during shutdown can crash ARC itself, not just this skill.
        ///
        /// Deliberately does NOT call StopListening() and wait on
        /// OnRecordingStopped's async disposal — that chain assumes the form
        /// stays alive long enough for the callback to fire safely. During a
        /// real shutdown that's not guaranteed. Everything here is torn down
        /// directly, synchronously, instead.
        /// </summary>
        private void MainForm_FormClosing(object sender, FormClosingEventArgs e)
                {
                    try
                    {
                        _listenCts?.Cancel();

                        // Same ordering rule as StartListening()'s catch block and
                        // StopListening(): StopFeeding() BEFORE RecognizeAsyncStop(), or
                        // the speech engine's internal thread deadlocks waiting on bytes
                        // that will never come — freezing ARC's shutdown entirely.
                        _bridgeStream?.StopFeeding();
                        _bridgeStream = null;

                        if (_speechRecognizer != null)
                        {
                            _speechRecognizer.RecognizeAsyncStop();
                            _speechRecognizer.Dispose();
                            _speechRecognizer = null;
                        }

                        // Direct disposal — do NOT wait for OnRecordingStopped here.
                        if (_waveIn != null)
                        {
                            // Detach BEFORE Stop/Dispose — RecordingStopped fires
                            // asynchronously on a background thread even after this method
                            // returns. Without detaching first, that late notification
                            // tries to Log() into a RichTextBox that's already being torn
                            // down as part of this same form closing — confirmed by the
                            // ObjectDisposedException test run just.
                            // Also furthur refined log to handle this issue as an indepth safeguard.
                            _waveIn.DataAvailable -= OnAudioDataAvailable;
                            _waveIn.RecordingStopped -= OnRecordingStopped;

                            _waveIn.StopRecording();
                            _waveIn.Dispose();
                            _waveIn = null;
                        }

                        // Camera disposal
                        DetachCamera();

                        // Mood poller disposal
                        _moodPoller?.Dispose();
                    }
            catch (Exception ex)
                    {
                        // Never let a shutdown-path exception escape unhandled.
                        try { Log($"[Shutdown] Cleanup error: {ex.Message}"); } catch { }
                    }
                }

        // Field required by ARC scaffold — holds the loaded configuration object.
        Configuration _config;
    }
}