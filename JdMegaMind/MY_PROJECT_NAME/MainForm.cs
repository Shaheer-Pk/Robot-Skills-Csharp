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


namespace JdMegaMind
{

    public partial class MainForm : ARC.UCForms.FormPluginMaster
    {

        // -----------------------------------------------------------------------
        // CONFIGURATION
        // -----------------------------------------------------------------------

        // The base URL of the Python FastAPI backend.
        // Uses mDNS .local hostname instead of a raw IP — works reliably over
        // Eduroam which reassigns a new IP on every reconnect.
        private const string BACKEND_URL = "http://DESKTOP-LJO38UV.local:8000";

        // Energy threshold for mic capture.
        // NAudio reports sample values as floats between -1.0 and 1.0.
        // 0.02 means 2% of max volume — low enough to catch normal speech,
        // high enough to ignore mic hiss and faint background noise.
        // TUNE THIS after first real-world test if JD is triggering too easily
        // (raise it) or missing your voice (lower it).
        private const float ENERGY_THRESHOLD = 0.25f;

        // How many milliseconds of consecutive silence after speech ends
        // before we consider the utterance complete and send it to Python.
        // 1000ms = 1 second. Raise if JD cuts you off mid-sentence.
        // Lower if there's a noticeable dead gap after you finish speaking.
        private const int SILENCE_TIMEOUT_MS = 1000;

        // NAudio capture format: 16000 Hz, 16-bit, Mono.
        // 16000 Hz is Silero VAD's native sample rate — sending at this rate
        // means Python does zero resampling, which is faster and cleaner.
        private const int SAMPLE_RATE = 16000;
        private const int BITS_PER_SAMPLE = 16;
        private const int CHANNELS = 1;

        // -----------------------------------------------------------------------
        // STATE
        // -----------------------------------------------------------------------

        // The NAudio mic capture device. Null when not listening.
        private WaveInEvent _waveIn = null;

        // Thread-safe cancellation token so the Stop button can cleanly
        // abort the listening loop without killing the whole thread abruptly.
        private CancellationTokenSource _listenCts = null;

        // Raw PCM bytes accumulated while the user is speaking.
        // Gets cleared after each utterance is sent to Python.
        private readonly List<byte> _audioBuffer = new List<byte>();

        // Tracks when we last heard audio above the energy threshold.
        // Used to calculate how long the current silence window has been.
        private DateTime _lastSpeechTime = DateTime.MinValue;

        // Whether we are currently in an active speech segment.
        // False = waiting for speech to start.
        // True  = speech detected, buffering audio.
        private bool _isSpeaking = false;

        // The toggle button — held as a field so we can update its label
        // from the background thread (via Invoke) when listening stops.
        private Button _btnListen;

        // Log Box to display what the skill is doing and capped at 100 lines
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
            // This is functionally identical to using the designer.
            InitializeUI();
        }

        /// <summary>
        /// Adds UI controls to the form in code since the VS designer
        /// is unavailable due to x86 assembly constraints in the ARC SDK.
        /// Add new feature buttons here as the skill grows (camera, etc).
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

            // --- LogBox (RichTextBox) setup ---
            _logBox = new RichTextBox();
            _logBox.Location = new System.Drawing.Point(10, 60);
            _logBox.Size = new System.Drawing.Size(440, 200);
            _logBox.ReadOnly = true;
            _logBox.BackColor = System.Drawing.Color.Black;
            _logBox.ForeColor = System.Drawing.Color.LimeGreen;
            _logBox.Font = new System.Drawing.Font("Consolas", 8.5f);
            _logBox.ScrollBars = RichTextBoxScrollBars.Vertical;
            this.Controls.Add(_logBox);

            // Future buttons go here, e.g.:
            // _btnCamera = new Button();
            // _btnCamera.Text = "Start Camera";
            // ...
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
        /// Hands our current config back to ARC to serialize.
        /// </summary>
        public override ARC.Config.Sub.PluginV1 GetConfiguration()
        {
            _cf.SetCustomObjectV2(_config);
            return base.GetConfiguration();
        }

        /// <summary>
        /// Fires when the user clicks the gear icon in the skill title bar.
        /// Opens the config popup dialog.
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
        // BUTTON HANDLER
        // -----------------------------------------------------------------------

        // -----------------------------------------------------------------------
        // INTERNAL LOGGER
        // -----------------------------------------------------------------------

        /// <summary>
        /// Thread-safe logger. Marshals to UI thread via Invoke if called from
        /// a background thread (NAudio capture thread, Task.Run thread).
        /// Appends timestamped message, auto-scrolls, trims old lines at cap.
        /// </summary>
        private void Log(string message)
        {
            string line = $"[{DateTime.Now:HH:mm:ss}] {message}";

            // Used InvokeRequired so the UI thread doesn't deadlock
            // Tryng to wait for itself to complete the task
            // This checks if the thread which returned the textbox message
            // Is the UI thread itself or a background worker thread and queues
            // ONLY FOR BACKGROUND THREADS
            if (this.InvokeRequired)
            {
                this.Invoke((Action)(() => AppendLog(line)));
            }
            else
            {
                AppendLog(line);
            }
        }

        private void AppendLog(string line)
        {
            _logBox.AppendText(line + "\n");

            // Trim oldest lines if we exceed the cap
            if (_logBox.Lines.Length > MAX_LOG_LINES)
            {
                var trimmed = _logBox.Lines
                    .Skip(_logBox.Lines.Length - MAX_LOG_LINES)
                    .ToArray();
                _logBox.Text = string.Join("\n", trimmed);
            }

            // Auto scroll to newest message
            _logBox.SelectionStart = _logBox.Text.Length;
            _logBox.ScrollToCaret();
        }

        // -----------------------------------------------------------------------
        // RECORDING BUTTON CLICK
        // -----------------------------------------------------------------------

        /// <summary>
        /// Toggles the mic listening loop on and off.
        /// First click: starts listening. Second click: stops cleanly.
        /// </summary>
        private void BtnListen_Click(object sender, EventArgs e)
        {

            if (_waveIn == null)
            {
                // Not currently listening — start the loop
                StartListening();
                _btnListen.Text = "Stop Listening";
            }
            else
            {
                // Currently listening — stop cleanly
                StopListening();
                _btnListen.Text = "Start Listening";
            }
        }

        // -----------------------------------------------------------------------
        // MIC CAPTURE LOOP
        // -----------------------------------------------------------------------

        /// <summary>
        /// Initializes NAudio mic capture and begins the listening loop.
        /// NAudio fires DataAvailable on a background thread each time a
        /// new chunk of audio arrives from the mic (every ~100ms by default).
        /// </summary>
        private void StartListening()
        {

            // Create a fresh cancellation token for this listening session
            _listenCts = new CancellationTokenSource();

            // Clear any leftover audio from a previous session
            _audioBuffer.Clear();
            _isSpeaking = false;
            _lastSpeechTime = DateTime.MinValue;

            // Configure the NAudio capture device
            _waveIn = new WaveInEvent();
            _waveIn.WaveFormat = new WaveFormat(SAMPLE_RATE, BITS_PER_SAMPLE, CHANNELS);

            // NAudio fires this event each time a new audio chunk is ready.
            // This is where energy threshold detection lives.
            _waveIn.DataAvailable += OnAudioDataAvailable;

            // NAudio fires this when recording stops — used for cleanup.
            _waveIn.RecordingStopped += OnRecordingStopped;

            _waveIn.StartRecording();

            Log("[Hearing] Mic capture started. Listening for speech...");
        }

        /// <summary>
        /// Cleanly stops the mic capture loop and releases NAudio resources.
        /// </summary>
        private void StopListening()
        {

            // Signal the cancellation token so any in-flight async work knows to abort
            _listenCts?.Cancel();

            if (_waveIn != null)
            {
                _waveIn.StopRecording();
                // Actual disposal happens in OnRecordingStopped to avoid race conditions
            }

            Log("[Hearing] Mic capture stopped.");
        }

        /// <summary>
        /// Fires on a NAudio background thread every ~100ms with fresh PCM bytes.
        ///
        /// Responsibilities:
        /// 1. Calculate the peak energy of this audio chunk
        /// 2. If energy is above threshold — we're in speech, buffer the bytes
        /// 3. If energy drops below threshold after speech — track silence duration
        /// 4. If silence exceeds SILENCE_TIMEOUT_MS — utterance is complete, send to Python
        /// </summary>
        private void OnAudioDataAvailable(object sender, WaveInEventArgs e)
        {

            // Guard: if Stop was clicked while this event was in flight, discard
            if (_listenCts == null || _listenCts.IsCancellationRequested)
                return;

            // --- Calculate peak energy of this audio chunk ---
            // PCM 16-bit samples arrive as raw bytes, two bytes per sample.
            // We convert each pair of bytes into a short (Int16), then normalize
            // to a float between -1.0 and 1.0 by dividing by Int16.MaxValue (32767).
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
                // Audio is above threshold — this chunk contains potential speech.
                // Update last speech time and flip into speaking state.
                _lastSpeechTime = DateTime.UtcNow;

                if (!_isSpeaking)
                {
                    Log("[Hearing] Speech detected. Buffering...");
                    _isSpeaking = true;
                }

                // Append only the actual recorded bytes (not the full buffer capacity)
                // e.Buffer may be larger than e.BytesRecorded — only take what's real.
                for (int i = 0; i < e.BytesRecorded; i++)
                    _audioBuffer.Add(e.Buffer[i]);

            }
            else if (_isSpeaking)
            {
                // Audio dropped below threshold BUT we were previously in speech.
                // Keep buffering — this might be a natural pause mid-sentence.
                // Also buffer the quiet bytes so the WAV isn't cut off abruptly.
                for (int i = 0; i < e.BytesRecorded; i++)
                    _audioBuffer.Add(e.Buffer[i]);

                // Check if silence has persisted long enough to call it done
                double silenceDurationMs = (DateTime.UtcNow - _lastSpeechTime).TotalMilliseconds;

                if (silenceDurationMs >= SILENCE_TIMEOUT_MS)
                {
                    // Utterance is complete. Snapshot the buffer and reset state
                    // before firing the async send — so the mic loop keeps running
                    // immediately and doesn't miss the next utterance.
                    byte[] utteranceBytes = _audioBuffer.ToArray();
                    _audioBuffer.Clear();
                    _isSpeaking = false;

                    Log($"[Hearing] Utterance complete ({utteranceBytes.Length} bytes). Sending to Python...");

                    // Fire and forget — don't await here because this is a sync event handler.
                    // The mic loop must not block while Python is processing.
                    _ = Task.Run(() => SendUtteranceToPython(utteranceBytes));
                }
            }
            // If isLoud is false AND _isSpeaking is false: pure silence, do nothing.
        }

        /// <summary>
        /// Fires when NAudio recording fully stops (after StopRecording() completes).
        /// Safe place to dispose NAudio resources.
        /// </summary>
        private void OnRecordingStopped(object sender, StoppedEventArgs e)
        {
            // _waveIn != null, then dispose
            _waveIn?.Dispose();
            _waveIn = null;

            // Update button label back to "Start Listening" on the UI thread
            if (this.InvokeRequired)
            {
                this.Invoke((Action)(() => _btnListen.Text = "Start Listening"));
            }
            else
            {
                _btnListen.Text = "Start Listening";
            }

            if (e.Exception != null)
            {
                Log($"[Hearing] Recording stopped with error: {e.Exception.Message}");
            }
        }

        // -----------------------------------------------------------------------
        // PYTHON PIPELINE
        // -----------------------------------------------------------------------

        /// <summary>
        /// Takes raw PCM bytes from the mic buffer, wraps them in a proper WAV
        /// container entirely in memory (no disk write), and POSTs the WAV to
        /// /hearing/transcribe as a multipart file upload.
        ///
        /// If Python confirms real speech (is_speech: true), calls SpeakResponse()
        /// with the transcript. If noise (is_speech: false), discards silently.
        /// </summary>
        private async Task SendUtteranceToPython(byte[] pcmBytes)
        {

            try
            {
                // --- Step 1: Wrap raw PCM bytes into a proper WAV format in memory ---
                // Python's soundfile library expects a valid WAV file with a header,
                // not raw PCM bytes. WaveFileWriter writes the correct RIFF/WAV header
                // followed by the PCM data, all into a MemoryStream (no disk file).
                byte[] wavBytes;
                using (var wavStream = new MemoryStream())
                {
                    using (var writer = new WaveFileWriter(wavStream, new WaveFormat(SAMPLE_RATE, BITS_PER_SAMPLE, CHANNELS)))
                    {
                        writer.Write(pcmBytes, 0, pcmBytes.Length);
                    }
                    // Must read AFTER the WaveFileWriter is disposed — it flushes
                    // and finalizes the WAV header only on Dispose().
                    wavBytes = wavStream.ToArray();
                }

                // --- Step 2: POST WAV bytes to /hearing/transcribe ---
                // MultipartFormDataContent mimics a browser file upload.
                // Python's FastAPI UploadFile handler receives it identically
                // to a real file — it never knows the difference.
                using (var client = new HttpClient())
                using (var formData = new MultipartFormDataContent())
                {

                    var audioContent = new ByteArrayContent(wavBytes);
                    audioContent.Headers.ContentType =
                        new System.Net.Http.Headers.MediaTypeHeaderValue("audio/wav");

                    // "audio" must match the parameter name in /hearing/routers.py:
                    // async def transcribe(audio: UploadFile = File(...))
                    formData.Add(audioContent, "audio", "utterance.wav");

                    var response = await client.PostAsync($"{BACKEND_URL}/hearing/transcribe", formData);

                    if (!response.IsSuccessStatusCode)
                    {
                        Log($"[Hearing] /hearing/transcribe returned error: {response.StatusCode}");
                        return;
                    }

                    // --- Step 3: Parse the TranscribeResponse JSON ---
                    // Expected shape: { "is_speech": true, "transcript": "hello robot" }
                    string json = await response.Content.ReadAsStringAsync();
                    var parsed = Newtonsoft.Json.Linq.JObject.Parse(json);

                    bool isSpeech = parsed["is_speech"]?.Value<bool>() ?? false;

                    if (!isSpeech)
                    {
                        Log("[Hearing] VAD: noise detected, discarding.");
                        return;
                    }

                    string transcript = parsed["transcript"]?.Value<string>();

                    if (string.IsNullOrWhiteSpace(transcript))
                    {
                        Log("[Hearing] VAD passed but transcript was empty. Discarding.");
                        return;
                    }

                    Log($"[Hearing] Transcript: \"{transcript}\"");

                    // AT THE VERY END TRIGGER THE POST IN BRAIN DIR (python backend /app/brain/*)
                    SpeakResponse(transcript);
                }

            }
            catch (HttpRequestException ex)
            {
                Log($"[Hearing] Network error reaching Python backend: {ex.Message}");
            }
            catch (Exception ex)
            {
                Log($"[Hearing] Unexpected error: {ex.Message}");
                MessageBox.Show(
                    $"Unexpected error in hearing pipeline:\n\n{ex.Message}",
                    "Hearing — Error",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Error
                );
            }
        }

        // -----------------------------------------------------------------------
        // ARC SCRIPT BRIDGE — UNTOUCHED
        // -----------------------------------------------------------------------

        /// <summary>
        /// Overrided command to glue it with ControlCommand in EZ scripts.
        /// Kept intact as a manual override — ARC scripts can still trigger
        /// SpeakResponse directly via ControlCommand if needed.
        /// Expandable: add more command.Equals() branches for future features.
        /// </summary>
        public override void SendCommand(string command, params string[] values)
        {
            if (command.Equals("SpeakResponse", StringComparison.InvariantCultureIgnoreCase))
            {
                if (values != null && values.Length > 0 && !string.IsNullOrWhiteSpace(values[0]))
                {
                    SpeakResponse(values[0]);
                }
                else
                {
                    Log("SpeakResponse received a blank text token. Execution halted.");
                }
            }
            else
            {
                base.SendCommand(command, values);
            }
        }

        // -----------------------------------------------------------------------
        // SPEAK RESPONSE — UNTOUCHED
        // -----------------------------------------------------------------------

        /// <summary>
        /// Full brain pipeline: text in → audio out of JD's speaker.
        /// POSTs text to /brain/chat, receives WAV bytes, resamples to EZB
        /// hardware spec (14700Hz, 8-bit, mono), compresses via GZip, and
        /// plays through SoundV4.PlayData() on JD's onboard EZB speaker.
        ///
        /// Called by:
        ///   - HearAndRespond pipeline (primary path, driven by mic capture)
        ///   - SendCommand override (manual ARC script override path)
        /// </summary>
        public async void SpeakResponse(string text)
        {
            try
            {
                using (var client = new System.Net.Http.HttpClient())
                {
                    string json = "{\"text\":\"" + text.Replace("\"", "\\\"") + "\"}";
                    var content = new System.Net.Http.StringContent(json, System.Text.Encoding.UTF8, "application/json");

                    var response = await client.PostAsync($"{BACKEND_URL}/brain/chat", content);

                    if (!response.IsSuccessStatusCode)
                    {
                        int code = (int)response.StatusCode;
                        if (code == 503)
                        {
                            Log("[Brain] Gemini overloaded (503). Try again in a moment.");
                            return;
                        }
                        else if (code == 429)
                        {
                            Log("[Brain] Gemini rate limit hit (429). Too many requests.");
                            return;
                        }
                        else
                        {
                            throw new Exception("Unexpected backend status: " + response.StatusCode);
                        }
                    }

                    var audioBytes = await response.Content.ReadAsByteArrayAsync();

                    using (var rawStream = new System.IO.MemoryStream(audioBytes))
                    using (var wavReader = new NAudio.Wave.WaveFileReader(rawStream))
                    {
                        var targetFormat = new NAudio.Wave.WaveFormat(14700, 8, 1);
                        using (var conversionStream = new NAudio.Wave.WaveFormatConversionStream(targetFormat, wavReader))
                        {
                            using (var compressedStream = new System.IO.MemoryStream())
                            {
                                using (var gzipCompressor = new System.IO.Compression.GZipStream(
                                    compressedStream, System.IO.Compression.CompressionMode.Compress))
                                {
                                    conversionStream.CopyTo(gzipCompressor);
                                }
                                byte[] finalEzbAudio = compressedStream.ToArray();
                                using (var playbackStream = new System.IO.MemoryStream(finalEzbAudio))
                                using (var gzipDecompressor = new System.IO.Compression.GZipStream(
                                    playbackStream, System.IO.Compression.CompressionMode.Decompress))
                                {
                                    EZBManager.EZBs[0].SoundV4.PlayData(gzipDecompressor, 100);
                                }
                            }
                        }
                    }
                }
            }
            catch (System.Net.Http.HttpRequestException ex)
            {
                Log($"[Brain] Backend unreachable: {ex.Message}. Is uvicorn running?");
            }
            catch (Exception ex)
            {
                Log($"[Brain] Unexpected error: {ex.Message}");     // Log Message + MessageBox becauase critical issue
                MessageBox.Show(
                    "There is some issue in your custom method SpeakResponse",
                    "Custom Method Error — " + ex.Message,
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Warning
                );
            }
        }

        // Field required by ARC scaffold — holds the loaded configuration object
        Configuration _config;
    }
}