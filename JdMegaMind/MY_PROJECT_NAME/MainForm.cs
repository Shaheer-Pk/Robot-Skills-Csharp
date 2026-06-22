using ARC;
using ARC.Scripting.Python.Helpers;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Data;
using System.Drawing;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace JdMegaMind {

  public partial class MainForm : ARC.UCForms.FormPluginMaster {

    Configuration _config;

    public MainForm() {

      InitializeComponent();

      // show a config button in the title bar. Set this to false if you do not have a config form.
      ConfigButton = true;
    }

    /// <summary>
    /// Set the configuration from the project file when loaded.
    /// We'll extract the _config class that's from the project file.
    /// </summary>
    public override void SetConfiguration(ARC.Config.Sub.PluginV1 cf) {

      _config = (Configuration)cf.GetCustomObjectV2(typeof(Configuration));

      base.SetConfiguration(cf);
    }

    /// <summary>
    /// When the project is saving, give it a copy of our config
    /// </summary>
    public override ARC.Config.Sub.PluginV1 GetConfiguration() {

      _cf.SetCustomObjectV2(_config);

      return base.GetConfiguration();
    }

    /// <summary>
    /// The user pressed the config button in the title bar. Show the config menu and handle the changes to the config.
    /// </summary>
    public override void ConfigPressed() {

      using (var form = new ConfigForm()) {

        form.SetConfiguration(_config);

        if (form.ShowDialog() != DialogResult.OK)
          return;

        _config = form.GetConfiguration();
      }
    }
        /// <summary>
        /// Overrided command to glue it with controlCommand in EZ scripts
        /// </summary>
        /// <param name="command"></param>
        /// <param name="values"></param>
        public override void SendCommand(string command, params string[] values)
        {
            if (command.Equals("SpeakResponse", StringComparison.InvariantCultureIgnoreCase))
            {
                if (values != null && values.Length > 0 && !string.IsNullOrWhiteSpace(values[0]))
                {
                    string spokenText = values[0];

                    // Because SendCommand is a synchronous void, this initiates the async task 
                    // in a non-blocking background context so ARC doesn't freeze while fetching audio.
                    SpeakResponse(spokenText);
                }
                else
                {
                    // Optional: Log an internal trace to the ARC window if a script fired the command empty
                    // Throwing an exception completely forces ARC to print the error directly inside your script text box!
                    throw new System.Exception("SpeakResponse received a blank text token. Execution halted.");
                }
            }
            else
            {
                // Critical fallback: If a script passes an internal ARC core command 
                // that we aren't explicitly modifying, forward it back up to Synthiam's base logic.
                base.SendCommand(command, values);
            }
        }

        public async void SpeakResponse(string text)
        {
            try
            {
                // To establish a connection with our backend python server 
                using (var client = new System.Net.Http.HttpClient())
                {
                    // The proper formatting our python backend expects to recive
                    // See /app/brain/schemas.py if confused
                    // The .Replace is to handle quotation marks in the text which can break json formatting
                    string json = "{\"text\":\"" + text.Replace("\"", "\\\"") + "\"}";
                    var content = new System.Net.Http.StringContent(json, System.Text.Encoding.UTF8, "application/json");

                    var response = await client.PostAsync("http://DESKTOP-LJO38UV.local:8000/brain/chat", content);

                    if (!response.IsSuccessStatusCode)
                    {

                        throw new System.Exception("Backend returned error status: " + response.StatusCode);
                    }

                    // PROCEDURE TO COMPRESS AND RESAMPLE ACCORDING TO JD PCB SPECIFICATIONS

                    var audioBytes = await response.Content.ReadAsByteArrayAsync();

                    // 1. Load the raw network bytes into a WAV stream container
                    using (var rawStream = new System.IO.MemoryStream(audioBytes))
                    using (var wavReader = new NAudio.Wave.WaveFileReader(rawStream))
                    {
                        // 2. Define the exact hardware format the EZ-B v4 demands: 14700Hz, 8-Bit, 1 Channel (Mono)
                        var targetFormat = new NAudio.Wave.WaveFormat(14700, 8, 1);

                        // 3. Resample the audio stream to match the hardware clock rate
                        using (var conversionStream = new NAudio.Wave.WaveFormatConversionStream(targetFormat, wavReader))
                        {
                            // 4. Compress the resampled PCM data into GZip format so the Wi-Fi connection can stream it safely
                            using (var compressedStream = new System.IO.MemoryStream())
                            {
                                using (var gzipCompressor = new System.IO.Compression.GZipStream(compressedStream, System.IO.Compression.CompressionMode.Compress))
                                {
                                    conversionStream.CopyTo(gzipCompressor);
                                }

                                // Extract the final optimized byte array
                                byte[] finalEzbAudio = compressedStream.ToArray();

                                // 5. Decompress on-the-fly straight into the hardware play driver
                                using (var playbackStream = new System.IO.MemoryStream(finalEzbAudio))
                                using (var gzipDecompressor = new System.IO.Compression.GZipStream(playbackStream, System.IO.Compression.CompressionMode.Decompress))
                                {
                                    // Play out cleanly at 100% volume!
                                    EZBManager.EZBs[0].SoundV4.PlayData(gzipDecompressor, 100);
                                }
                            }
                        }
                    }
                }

            }
            catch (System.Net.Http.HttpRequestException ex)
            {
                // This pops up a clean, native Windows alert window over ARC. 
                // It completely prevents ARC from crashing and explicitly tells you what to fix!
                System.Windows.Forms.MessageBox.Show(
                    "Your Python Backend Server appears to be offline!\n\nPlease run 'uvicorn main:app' in your terminal before speaking to the robot.",
                    "Backend Connection Error" + ex.Message,
                    System.Windows.Forms.MessageBoxButtons.OK,
                    System.Windows.Forms.MessageBoxIcon.Warning
                );
            }
            catch (Exception ex)
            {

                System.Windows.Forms.MessageBox.Show(
                    "There is some issue in your custom method SpeakResponse",
                    "Custom Method Error" + ex.Message,
                    System.Windows.Forms.MessageBoxButtons.OK,
                    System.Windows.Forms.MessageBoxIcon.Warning
                );
            }
        }
    }
}
