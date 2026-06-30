using ARC;
using NAudio.Wave;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using System.Speech.Recognition;

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
        // Configured with a single-phrase grammar ("Hello Clanker") and fed audio
        // from _bridgeStream so it shares NAudio's mic ownership — no handoff gap.
        private SpeechRecognitionEngine _speechRecognizer = null;

        // Set to true when "Hello Clanker" is detected by the speech engine.
        // OnAudioDataAvailable checks this before buffering any audio.
        // Reset to false after each utterance is dispatched to Python,
        // returning the system to wake word hunting mode immediately.
        //
        // WHY volatile: read by NAudio's background thread, written by the
        // speech engine's internal thread. volatile prevents stale cached reads.
        private volatile bool _isWakeWordDetected = false;

        // UI Controls — held as fields so background threads can update them
        // safely via Invoke (marshalling back to the UI thread).
        private Button _btnListen;
        private RichTextBox _logBox;
        private const int MAX_LOG_LINES = 100;

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

            // Future feature buttons go here, e.g.:
            // _btnCamera = new Button();
            // _btnCamera.Text = "Start Camera";
            // _btnCamera.Size = new System.Drawing.Size(160, 40);
            // _btnCamera.Location = new System.Drawing.Point(180, 10);
            // _btnCamera.Click += BtnCamera_Click;
            // this.Controls.Add(_btnCamera);
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
            string line = $"[{DateTime.Now:HH:mm:ss}] {message}";

            if (this.InvokeRequired)
            {
                // We are on a background thread — hand the write to the UI thread.
                // We have to do this because windowsForm forces a single thread architecture
                this.Invoke((Action)(() => AppendLog(line)));
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
        /// Fired by System.Speech.Recognition's internal thread when "Hello Clanker"
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
            // similar-sounding phrases. Tune this if legitimate "Hello Clanker" calls
            // are being rejected (lower it) or false triggers occur (raise it).
            if (e.Result.Confidence < 0.7f)
            {
                Log($"[Wake] Low confidence detection ({e.Result.Confidence:P0}), ignoring.");
                return;
            }

            _isWakeWordDetected = true;
            Log($"[Wake] 'Hello Clanker' detected ({e.Result.Confidence:P0} confidence). Listening for your question...");
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
            var grammar = new Grammar(new GrammarBuilder("Hello Clanker"));
            _speechRecognizer.LoadGrammar(grammar);

            // Wire the detection event — fires on the speech engine's internal thread
            // when "Hello Clanker" is heard with sufficient confidence.
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
                Log("[Hearing] Mic capture started. Listening for wake word 'Hello Clanker'...");
            }
            catch (NAudio.MmException ex)
            {
                Log($"[Hearing] Microphone error: {ex.Message}. Is a mic connected and enabled in Windows Sound Settings?");
                _speechRecognizer?.RecognizeAsyncStop();
                _speechRecognizer?.Dispose();
                _speechRecognizer = null;
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
            // The speech engine needs a continuous audio feed to detect "Hello Clanker" —
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
                    // "Hello Clanker" again for the next interaction.
                    _isWakeWordDetected = false;
                    Log("[Wake] Listening for 'Hello Clanker' again...");

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
                    // Task.Run fires SpeakResponse on a background thread so this
                    // synchronous method returns immediately to ARC's script engine.
                    // The async lambda allows us to properly await SpeakResponse
                    // and manage _isProcessing with a finally block — identical
                    // to how SendUtteranceToPython handles it.
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
                // Forward unrecognized commands to ARC's base implementation
                // so built-in ARC commands still work correctly.
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

        // Field required by ARC scaffold — holds the loaded configuration object.
        Configuration _config;
    }
}