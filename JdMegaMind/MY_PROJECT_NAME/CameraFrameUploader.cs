using Newtonsoft.Json.Linq;
using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Net.Http;
using System.Threading.Tasks;

namespace JdMegaMind
{
    /// <summary>
    /// CameraFrameUploader — dumb, fire-and-forget frame delivery to the Python
    /// vision backend. Zero detection, zero policy checking — per design, all
    /// "is this a face, who is it" reasoning lives entirely in Python (app/vision/).
    /// This class's only job: take an already-cloned Bitmap, JPEG-encode it,
    /// POST it to /vision/stream, log the result.
    ///
    /// NOT a Stream subclass like AudioBridgeStream — that shape fits two
    /// consumers sharing a continuous blocking-pull feed (NAudio + System.Speech).
    /// This is single-bitmap-in, single-POST-out, fire-and-forget — closer to
    /// SendUtteranceToPython's shape than AudioBridgeStream's.
    ///
    /// HttpClient: ONE long-lived instance, reused across every frame POST —
    /// deliberately different from SpeakResponse/SendUtteranceToPython's
    /// per-call `using (var client = ...)`. Those fire once per conversation
    /// turn (~10-30s apart); this fires 15-30x/second. See tasks.md Risk 2.
    ///
    /// In-flight guard: its own dedicated volatile bool, deliberately NOT
    /// _isProcessing. Vision must keep working independent of conversation state.
    /// </summary>
    internal class CameraFrameUploader
    {
        // readonly = can only be assigned once, inside the constructor, never
        // again after that. This is a deliberate guarantee: nothing later in
        // this class can accidentally swap out the HttpClient or URL mid-run.
        // Similar spirit to Java's `final` on a field.
        private readonly HttpClient _visionHttpClient;
        private readonly string _streamUrl;

        // Action<string> is C#'s built-in generic delegate type for "a method
        // that takes one string argument and returns nothing" — i.e. a
        // reference TO a method, passed around like a value. We're using it
        // here to hold a reference to MainForm's Log(string) method, without
        // this class needing to know MainForm exists at all. This is what
        // "decoupling" actually looks like in code, not just a design word —
        // MainForm hands us a function pointer at construction time, and we
        // call it without ever importing/referencing MainForm's type.
        private readonly Action<string> _log;

        // volatile — same reasoning as _isProcessing and _isWakeWordDetected
        // elsewhere in MainForm.cs: this field gets WRITTEN on whatever
        // thread UploadFrameAsync happens to run on (a background Task
        // thread, fired from Camera_OnNewFrame), and READ from that same
        // kind of thread on the next frame. Without volatile, the CPU/JIT
        // compiler is allowed to cache a stale copy of this value per-thread
        // as a performance optimization — meaning one thread's write might
        // not be visible to another thread's read for an unpredictable
        // amount of time. volatile forces every read to go back to actual
        // memory, guaranteeing every thread sees the latest value immediately.
        private volatile bool _uploadInProgress = false;

        /// <param name="backendUrl">e.g. MainForm.BACKEND_URL</param>
        /// <param name="logger">MainForm's Log(string), passed in so this class
        /// isn't coupled to MainForm's RichTextBox directly.</param>
        public CameraFrameUploader(string backendUrl, Action<string> logger)
        {
            // String interpolation ($"...") — same idea as Java's
            // String.format or template literals. Builds the full endpoint
            // URL once, here, instead of re-concatenating it on every frame.
            _streamUrl = $"{backendUrl}/vision/stream";
            _log = logger;

            // Created exactly ONCE, here in the constructor — this instance
            // lives for as long as this CameraFrameUploader object lives
            // (which will be as long as MainForm itself lives, once wired
            // in). This is the entire point of Risk 2's fix: every call to
            // UploadFrameAsync below reuses THIS SAME instance instead of
            // creating a fresh one, avoiding the socket-exhaustion problem
            // that would happen at 15-30 frames/second.
            _visionHttpClient = new HttpClient();
        }

        /// <summary>
        /// Fire-and-forget entry point, called from Camera_OnNewFrame's
        /// Task.Run handoff with an ALREADY-CLONED bitmap (cloning must happen
        /// synchronously inside the event handler, before this is ever reached).
        ///
        /// Drops (does not queue/retry) the frame if an upload is already in
        /// flight — frames arrive far faster than recognition needs them
        /// (vision/state.py throttles real work via RETRY_INTERVAL_SECONDS
        /// regardless), so dropping under load is correct, not a bug.
        /// Caller hands over ownership of `frame` — always disposed here,
        /// whether uploaded or dropped.
        /// </summary>
        // async Task, not async void — same rule already established for
        // SpeakResponse in MainForm.cs: async void methods are "fire and
        // forget" in a bad way — if something throws inside them, the
        // exception has nowhere to go and gets silently swallowed by the
        // .NET runtime. async Task lets whoever calls this (via Task.Run)
        // actually observe exceptions if they choose to, and makes this
        // method properly awaitable instead of just launch-and-hope.
        public async Task UploadFrameAsync(Bitmap frame)
        {
            // Guard clause: if we're still mid-upload from a previous frame,
            // don't start a second one on top of it. Per the design decision
            // you confirmed above — DROP this new frame rather than queue it.
            if (_uploadInProgress)
            {
                // We were handed ownership of this Bitmap by the caller
                // (Camera_OnNewFrame). Bitmap wraps a native GDI+ resource —
                // same "unmanaged memory" category we discussed with AForge's
                // UnmanagedImage. If we don't call Dispose() here, this
                // dropped frame's memory never gets reclaimed — exactly the
                // kind of leak we just spent an entire testing cycle fixing
                // on the hearing side. Every exit path in this method disposes
                // `frame` exactly once — that discipline matters here as much
                // as it did for _waveIn.
                frame.Dispose();
                return;
            }

            _uploadInProgress = true;

            // try/finally (no catch here, since inner code below has its own
            // catch blocks) — this guarantees _uploadInProgress always gets
            // reset back to false and `frame` always gets disposed, no matter
            // which path through this method is taken, including if an
            // exception happens. Same "finally always runs" guarantee you've
            // already seen in SendUtteranceToPython's _isProcessing handling.
            try
            {
                // --- Step 1: turn the Bitmap into raw JPEG bytes in memory ---
                byte[] jpegBytes;

                // `using` here means: MemoryStream implements IDisposable
                // (it holds a resizable in-memory buffer that should be
                // cleaned up once we're done with it). The `using` block
                // guarantees ms.Dispose() runs automatically when we leave
                // the block, even if an exception happens inside it — this
                // is C#'s equivalent of Java's try-with-resources.
                using (var ms = new MemoryStream())
                {
                    // Bitmap.Save() with ImageFormat.Jpeg encodes the raw
                    // pixel data into an actual JPEG byte layout (compressed,
                    // with proper headers) and writes it into our MemoryStream.
                    // NOTE (per your ask #3): JPEG quality here is left at
                    // .NET's built-in default (~75 out of 100). This is NOT a
                    // deliberately tuned value — if frame size or upload
                    // latency becomes a real problem once we're testing
                    // against actual camera frame rates, THIS is the first
                    // knob to come back and adjust (Save() has an overload
                    // accepting an ImageCodecInfo + EncoderParameters to set
                    // quality explicitly — not used yet, flagged for later).
                    frame.Save(ms, ImageFormat.Jpeg);

                    // ToArray() copies everything written to the stream so
                    // far into a plain byte[] we can hand off freely — the
                    // MemoryStream itself gets disposed right after this
                    // block ends, but this byte[] copy survives independently.
                    jpegBytes = ms.ToArray();
                }

                // --- Step 2: package those bytes as a multipart HTTP upload ---
                // This mimics exactly what a browser does when you submit a
                // <form> with a file input, or what Swagger UI's file picker
                // does — it's the same multipart/form-data format Python's
                // FastAPI UploadFile expects on the other end. Same pattern
                // already used in SendUtteranceToPython for the WAV upload.
                using (var content = new ByteArrayContent(jpegBytes))
                using (var formData = new MultipartFormDataContent())
                {
                    // Tells the receiving server what kind of bytes these
                    // are, so it can decode them correctly. Matches what
                    // routers.py's cv2.imdecode expects to receive.
                    content.Headers.ContentType =
                        new System.Net.Http.Headers.MediaTypeHeaderValue("image/jpeg");

                    // "frame" is a FIELD NAME, not a filename — it MUST
                    // match vision/routers.py's function parameter exactly:
                    //     async def receive_frame(frame: UploadFile = File(...))
                    // FastAPI matches multipart fields by this name string.
                    // Get this wrong and Python will report a missing/
                    // unexpected field, not a decode error — a completely
                    // different failure mode, worth knowing to tell them apart
                    // if something goes wrong during testing.
                    formData.Add(content, "frame", "frame.jpg");

                    // await — this is the actual asynchronous part. PostAsync
                    // starts the network call and immediately gives control
                    // back to whoever's running this code (in our case, the
                    // background Task thread from Camera_OnNewFrame) instead
                    // of blocking that thread while waiting for the network.
                    // When the response actually arrives, execution resumes
                    // here, at this exact line, picking up where it left off.
                    var response = await _visionHttpClient.PostAsync(_streamUrl, formData);

                    // Recall from vision-feature.md: an undecodable frame is
                    // a DELIBERATE, EXPECTED 400 response, not a bug on
                    // either side. We check for it specifically so it gets
                    // logged calmly instead of looking like a real failure.
                    if (response.StatusCode == System.Net.HttpStatusCode.BadRequest)
                    {
                        _log?.Invoke("[Vision] Backend rejected an undecodable frame (400).");
                        return; // finally block below still runs
                    }

                    // Anything else non-2xx (500, connection refused
                    // surfaced as a status, etc.) — a real, unexpected
                    // problem, logged as such.
                    if (!response.IsSuccessStatusCode)
                    {
                        _log?.Invoke($"[Vision] /vision/stream error: {response.StatusCode}");
                        return;
                    }

                    // --- TEMPORARY TEST-ONLY LOGGING ---
                    // Parses StreamStatusResponse (vision/schemas.py) just enough to surface
                    // recognition_status in ARC's own log box during first-run testing, instead
                    // of relying solely on uvicorn's terminal output. Not part of the permanent
                    // design — CameraFrameUploader's job is upload-only, per its own docstring.
                    // Revisit/remove once the real camera→Python pipeline is confirmed working
                    // end-to-end; if this visibility turns out genuinely useful long-term,
                    // it should be redesigned deliberately (e.g. an event MainForm subscribes
                    // to) rather than left as a leftover debug print.
                    //string responseJson = await response.Content.ReadAsStringAsync();
                    //var parsed = JObject.Parse(responseJson);
                    //bool facePresent = parsed["face_present"]?.Value<bool>() ?? false;
                    //string status = parsed["recognition_status"]?.Value<string>() ?? "unknown";
                    //_log?.Invoke($"[Vision] face_present={facePresent}, recognition_status={status}");
                }
            }
            catch (HttpRequestException ex)
            {
                // Network-level failure — backend unreachable. Same
                // "log only, don't crash" treatment as every other backend
                // call in this project (SpeakResponse, SendUtteranceToPython).
                _log?.Invoke($"[Vision] Backend unreachable: {ex.Message}");
            }
            catch (Exception ex)
            {
                // Anything else unexpected. Deliberately no MessageBox here,
                // unlike SpeakResponse's critical-error popup — a single
                // dropped camera frame at 15-30fps is not a "stop everything
                // and alert the developer" situation the way a full
                // conversation-pipeline crash is. Logged only.
                _log?.Invoke($"[Vision] Unexpected upload error: {ex.Message}");
            }
            finally
            {
                // Runs on EVERY exit path above — success, both error
                // returns, and both catch blocks. This is what guarantees
                // frame.Dispose() always happens exactly once, and the lock
                // always releases, no matter what happened during the upload.
                frame.Dispose();
                _uploadInProgress = false;
            }
        }

        /// <summary>
        /// Called from MainForm_FormClosing. Disposes the long-lived
        /// HttpClient — the one resource in this class that genuinely needs
        /// explicit cleanup on shutdown, same category of concern as
        /// _waveIn/_speechRecognizer in MainForm's own FormClosing handler.
        /// </summary>
        public void Dispose()
        {
            // ?. — null-conditional operator. If _visionHttpClient somehow
            // ended up null (shouldn't happen given it's set in the
            // constructor and never reassigned, but defensive anyway),
            // this just skips the Dispose() call instead of throwing a
            // NullReferenceException. Same pattern already used throughout
            // MainForm.cs (_waveIn?.Dispose(), _bridgeStream?.StopFeeding()).
            _visionHttpClient?.Dispose();
        }
    }
}