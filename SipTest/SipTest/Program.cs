using Microsoft.CognitiveServices.Speech;
using Microsoft.CognitiveServices.Speech.Audio;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Logging;
using Serilog.Events;
using Serilog.Extensions.Logging;
using Serilog;
using SIPSorcery.Media;
using SIPSorcery.SIP.App;
using SIPSorcery.SIP;
using System.Net;
using SIPSorcery.Net;
using SIPSorceryMedia.Abstractions;
using SipTest;
using SipTest.Responses;

namespace SipText
{
    class Program
    {
        private static Microsoft.Extensions.Logging.ILogger Log = NullLogger.Instance;

        private static PushAudioInputStream? audioInputStream; // do not declare locally since it will be disposed when it gets out of scope.... But we do not need about disposal since the streamAudioEntpoint will take care.
        private static StreamAudioEndpoint? streamAudioEndpoint;
        private static SpeechRecognizer? speechRecognizer;
        private static SpeechSynthesizer? speechSynthesizer;

        private static SIPServerUserAgent? uas = null;
        private static VoIPMediaSession? rtpSession = null;

        private static Config config = Config.Create();

        /// <summary>
        /// Used to keep track of received RTP events. An RTP event will typically span
        /// multiple packets but the application only needs to get informed once per event.
        /// </summary>
        private static uint _rtpEventSsrc;
        private static string[] DtmfByteToString = new string[] {"0", "1", "2", "3", "4", "5", "6", "7", "8", "9", "*", "#", "A", "B", "C", "D"};

        private static string? sessionId = null;
        private static DialogBits dialogBits = new DialogBits(config.DialogBitsUrl);

        static async Task<int> Main(string[] args)
        {
            Log = AddConsoleLogger(LogEventLevel.Information);
            Log.LogInformation("Welcome to the DialogBits CallBridge!");

            Log.LogInformation("Check, if DialogBits is reachable...");
            var isReachable = await dialogBits.Reachable();
            if (isReachable)
            {
                Log.LogInformation("DialogBits is reachable");
            } else
            {
                Log.LogCritical($"DialogBits is not reachable! (url: ${config.DialogBitsUrl}");
                return 1;
            }

            Log.LogInformation("Ongoing call: Press h to hangup");
            Log.LogInformation("Press ctrl-c or q to exit.");
            ManualResetEvent exitMRE = new ManualResetEvent(false);

            // Set up a default SIP transport.
            var sipTransport = new SIPTransport();
            sipTransport.EnableTraceLogs();

            // Create a client user agent to maintain a periodic registration with a SIP server.
            var regUserAgent = new SIPRegistrationUserAgent(sipTransport, config.SipUsername, config.SipPassword, config.SipServer, config.SipRegistrationExpiry);

            // Event handlers for the different stages of the registration.
            regUserAgent.RegistrationFailed += (uri, response, err) => Log.LogError($"RegistrationFailed {uri}: {err}");
            regUserAgent.RegistrationTemporaryFailure += (uri, response, msg) => Log.LogError($"RegistrationTemporaryFailure {uri}: {msg}");

            sipTransport.AddSIPChannel(new SIPUDPChannel(new IPEndPoint(IPAddress.Any, config.SipPort)));

            // Because this is a server user agent the SIP transport must start listening for client user agents.
            sipTransport.SIPTransportRequestReceived += async (SIPEndPoint localSIPEndPoint, SIPEndPoint remoteEndPoint, SIPRequest sipRequest) =>
            {
                try
                {
                    if (sipRequest.Method == SIPMethodsEnum.INVITE)
                    {
                        var from = sipRequest.Header.Contact[0].ContactURI.UnescapedUser;
                        Log.LogInformation($"Incoming call from {from}");

                        // Check there's a codec we support in the INVITE offer.
                        var offerSdp = SDP.ParseSDPDescription(sipRequest.Body);
                        IPEndPoint dstRtpEndPoint = SDP.GetSDPRTPEndPoint(sipRequest.Body);

                        if (offerSdp.Media.Any(x => x.Media == SDPMediaTypesEnum.audio && x.MediaFormats.Any(x => x.Key == (int)SDPWellKnownMediaFormatsEnum.PCMA || x.Key == (int)SDPWellKnownMediaFormatsEnum.PCMU)))
                        {
                            // Get Language
                            var language = await dialogBits.GetLanguage(from);
                            Log.LogInformation($"Using language {language}");

                            // Setup TTS
                            var ttsSpeechConfig = SpeechConfig.FromSubscription(config.SpeechKey, config.SpeechRegion);
                            ttsSpeechConfig.SetSpeechSynthesisOutputFormat(SpeechSynthesisOutputFormat.Raw8Khz16BitMonoPcm);
                            speechSynthesizer = new SpeechSynthesizer(ttsSpeechConfig, null); // null as audiodevice: Do explicitly not use the onboard speakers.

                            // STT setup
                            var sttSpeechConfig = SpeechConfig.FromSubscription(config.SpeechKey, config.SpeechRegion);
                            sttSpeechConfig.EnableDictation();
                            audioInputStream = AudioInputStream.CreatePushStream(AudioStreamFormat.GetWaveFormatPCM(8000, 16, 1));
                            using var audioConfig = AudioConfig.FromStreamInput(audioInputStream);
                            speechRecognizer = new SpeechRecognizer(sttSpeechConfig, language, audioConfig);
                            speechRecognizer.Recognizing += async (s, e) =>
                            {
                                Log.LogDebug($"Recognizing...: {e.Result.Text}");
                                if (config.BargeInOnSpeech && streamAudioEndpoint != null)
                                {
                                    await streamAudioEndpoint.BargeIn();
                                }
                            };
                            speechRecognizer.Recognized += async (s, e) =>
                            {
                                if (e.Result.Reason == ResultReason.RecognizedSpeech && e.Result.Text.Trim().Length > 0)
                                {
                                    Log.LogInformation($" --> Recognized: {e.Result.Text}");

                                    if (sessionId != null)
                                    {
                                        var actions = await dialogBits.OnText(sessionId, from);
                                        await HandleBotActions(actions);
                                    } else
                                    {
                                        Log.LogWarning("Want to send recognized speech, but the bot has not initialized yet!");
                                    }
                                }
                                else if (e.Result.Reason == ResultReason.NoMatch)
                                {
                                    Log.LogDebug($"Recognizing: Speech could not be recognized");
                                }
                            };
                            speechRecognizer.Canceled += (s, e) =>
                            {
                                Log.LogDebug($"CANCELED: Reason={e.Reason}");

                                if (e.Reason == CancellationReason.Error)
                                {
                                    Log.LogWarning($"Recognition cancelled with error: ErrorCode={e.ErrorCode} ErrorDetails={e.ErrorDetails}");
                                }
                            };

                            // Setup AudioStream
                            streamAudioEndpoint = new StreamAudioEndpoint(audioInputStream);
                            streamAudioEndpoint.RestrictFormats(x => x.Codec == AudioCodecsEnum.PCMU || x.Codec == AudioCodecsEnum.PCMA);
                            rtpSession = new VoIPMediaSession(streamAudioEndpoint.ToMediaEndPoints());
                            rtpSession.OnRtpEvent += async (IPEndPoint ip, RTPEvent rtpEvent, RTPHeader rtpHeader) =>
                            {
                                if (_rtpEventSsrc == 0)
                                {
                                    if (rtpEvent.EndOfEvent && rtpHeader.MarkerBit == 1 || !rtpEvent.EndOfEvent)
                                    {
                                        if (!rtpEvent.EndOfEvent)
                                        {
                                            _rtpEventSsrc = rtpHeader.SyncSource;
                                        }

                                        if (config.BargeInOnDtmf && streamAudioEndpoint != null)
                                        {
                                            await streamAudioEndpoint.BargeIn();
                                        }

                                        // OnDtmfTone.Invoke(rtpEvent.EventID, rtpEvent.Duration);
                                        string touchTone = DtmfByteToString[(int)rtpEvent.EventID];
                                        Log.LogInformation($" --> Send DTMF touch tone: {touchTone} (event id: {rtpEvent.EventID})");

                                        if (sessionId != null)
                                        {
                                            var actions = await dialogBits.OnDtmf(sessionId, touchTone);
                                            await HandleBotActions(actions, true);
                                        }
                                    }

                                    
                                }

                                if (_rtpEventSsrc != 0 && rtpEvent.EndOfEvent)
                                {
                                    _rtpEventSsrc = 0;
                                }
                            };

                            // Setup RTP-Session

                            // Computers Mic and Speakers:
                            // var audioSession = new WindowsAudioEndPoint(new AudioEncoder());
                            // audioSession.RestrictFormats(x => x.Codec == AudioCodecsEnum.PCMU || x.Codec == AudioCodecsEnum.PCMA);
                            // rtpSession = new VoIPMediaSession(audioSession.ToMediaEndPoints());

                            // Some music?
                            // AudioExtrasSource extrasSource = new AudioExtrasSource(new AudioEncoder(), new AudioSourceOptions { AudioSource = AudioSourcesEnum.Music });
                            // rtpSession = new VoIPMediaSession(new MediaEndPoints { AudioSource = extrasSource });

                            rtpSession.AcceptRtpFromAny = true;
                            var setResult = rtpSession.SetRemoteDescription(SdpType.offer, offerSdp);
                            if (setResult != SetDescriptionResultEnum.OK)
                            {
                                Log.LogWarning("SDP offer was not accepted");
                                speechRecognizer?.Dispose();
                                speechRecognizer = null;
                                speechSynthesizer?.Dispose();
                                speechSynthesizer = null;
                                streamAudioEndpoint?.Dispose();
                                streamAudioEndpoint = null;

                                // Didn't get a match on the codecs we support.
                                SIPResponse noMatchingCodecResponse = SIPResponse.GetResponse(sipRequest, SIPResponseStatusCodesEnum.NotAcceptableHere, setResult.ToString());
                                await sipTransport.SendResponseAsync(noMatchingCodecResponse);
                            }
                            else
                            {
                                // If there's already a call in progress hang it up. Of course this is not ideal for a real softphone or server but it 
                                // means this example can be kept simpler.
                                if (uas?.IsHungup == false)
                                {
                                    uas?.Hangup(false);
                                }

                                // Create a SIP Transaction to answer with "ringing" asap.
                                UASInviteTransaction uasTransaction = new UASInviteTransaction(sipTransport, sipRequest, null);
                                uas = new SIPServerUserAgent(sipTransport, null, uasTransaction, null);
                                uas.CallCancelled += (uasAgent) =>
                                {
                                    rtpSession.Close(null);
                                };
                                rtpSession.OnRtpClosed += (reason) => uas?.Hangup(false);
                                uas.Progress(SIPResponseStatusCodesEnum.Trying, null, null, null, null);

                                Log.LogInformation("Connected. Start speech recognition and anser the SIP invite");
                                _ = Task.Run(() => speechRecognizer.StartContinuousRecognitionAsync());

                                await Task.Delay(50);
                                uas.Progress(SIPResponseStatusCodesEnum.Ringing, null, null, null, null);
                                await Task.Delay(50);
                                var answerSdp = rtpSession.CreateAnswer(null);
                                uas.Answer(SDP.SDP_MIME_CONTENTTYPE, answerSdp.ToString(), null, SIPDialogueTransferModesEnum.NotAllowed);

                                Log.LogInformation("RTP session started");
                                await rtpSession.Start();

                                await Task.Delay(100);
                                sessionId = Guid.NewGuid().ToString();
                                Log.LogInformation($"Sending an call initiated to DialogBits. Session: ${sessionId}");
                                var actions = await dialogBits.OnCallInitiated(sessionId, from);
                                await HandleBotActions(actions);
                            }
                        }
                        else
                        {
                            Log.LogWarning("No offering of PCMU/PCMA. Do not accept");
                            SIPResponse noMatchingCodecResponse = SIPResponse.GetResponse(sipRequest, SIPResponseStatusCodesEnum.NotAcceptableHere, "Only PCMU");
                            await sipTransport.SendResponseAsync(noMatchingCodecResponse);
                        }
                    }
                    else if (sipRequest.Method == SIPMethodsEnum.BYE)
                    {
                        Log.LogInformation("Call hungup.");
                        SIPResponse byeResponse = SIPResponse.GetResponse(sipRequest, SIPResponseStatusCodesEnum.Ok, null);
                        await sipTransport.SendResponseAsync(byeResponse);
                        uas?.Hangup(true);
                        rtpSession?.Close(null);
                    }
                    else if (sipRequest.Method == SIPMethodsEnum.SUBSCRIBE)
                    {
                        SIPResponse notAllowededResponse = SIPResponse.GetResponse(sipRequest, SIPResponseStatusCodesEnum.MethodNotAllowed, null);
                        await sipTransport.SendResponseAsync(notAllowededResponse);
                    }
                    else if (sipRequest.Method == SIPMethodsEnum.OPTIONS || sipRequest.Method == SIPMethodsEnum.REGISTER)
                    {
                        SIPResponse optionsResponse = SIPResponse.GetResponse(sipRequest, SIPResponseStatusCodesEnum.Ok, null);
                        await sipTransport.SendResponseAsync(optionsResponse);
                    }
                }
                catch (Exception reqExcp)
                {
                    Log.LogWarning($"Exception handling {sipRequest.Method}. {reqExcp.Message}");
                }
            };

            // Start the thread to perform the initial registration and then periodically resend it.
            regUserAgent.Start();

            Log.LogInformation("SIP User Agent started");

            // Task to handle user key presses.
            _ = Task.Run(() =>
            {
                try
                {
                    while (!exitMRE.WaitOne(0))
                    {
                        var keyProps = Console.ReadKey();
                        if (keyProps.KeyChar == 'h')
                        {
                            Log.LogInformation("Hangup requested by user...");
                            Hangup().Wait();
                        }

                        if (keyProps.KeyChar == 'q')
                        {
                            Log.LogInformation("Quitting...");
                            exitMRE.Set();
                        }
                    }
                }
                catch (Exception excp)
                {
                    Log.LogError($"Exception Key Press listener. {excp.Message}");
                }
            });

            Console.CancelKeyPress += (object? sender, ConsoleCancelEventArgs e) =>
            {
                e.Cancel = true;
                Log.LogInformation("Exiting...");
                exitMRE.Set();
            };

            exitMRE.WaitOne();
            Log.LogInformation("Shutting down...");
            Hangup().Wait();
            regUserAgent.Stop();

            // Allow for unregister request to be sent (REGISTER with 0 expiry)
            Task.Delay(1500).Wait();

            sipTransport.Shutdown();
            return 0;
        }

        private static async Task HandleBotActions(Actions? actions, bool suppressNoAnswerWarning=false)
        {
            var text = actions?.getText();
            var speechDurationMs = 0;
            if (text == null)
            {
                if (!suppressNoAnswerWarning)
                {
                    Log.LogWarning("The Bot responded without text!");
                }
            }
            else
            {
                if (speechSynthesizer == null)
                {
                    throw new Exception("Wtf?");
                }
                Log.LogInformation($" <-- Got text from bot: {text}");
                var result = await speechSynthesizer.SpeakSsmlAsync(text);
                speechDurationMs = (int)result.AudioDuration.TotalMilliseconds + 1;
                streamAudioEndpoint?.SendRawPCM(result.AudioData);
            }

            if (actions?.hasHangup() ?? false)
            {
                await Task.Delay(speechDurationMs + 100);
                await Hangup();
            }
        }

        /// <summary>
        /// Hangs up the current call.
        /// </summary>
        /// <param name="uas">The user agent server to hangup the call on.</param>
        private static async Task Hangup()
        {
            try
            {
                if (uas?.IsHungup == false)
                {
                    uas?.Hangup(false);

                    // Give the BYE or CANCEL request time to be transmitted.
                    Log.LogInformation("Waiting 1s for call to hangup...");
                    await Task.Delay(1000);
                }
            }
            catch (Exception excp)
            {
                Log.LogError($"Exception Hangup. {excp.Message}");
            }

            rtpSession?.Close(null);

            // Cleanup speech services
            if (speechRecognizer != null)
            {
                await speechRecognizer.StopContinuousRecognitionAsync();
            }
            speechRecognizer?.Dispose();
            speechRecognizer = null;
            speechSynthesizer?.Dispose();
            speechSynthesizer = null;
            streamAudioEndpoint?.Dispose();
            streamAudioEndpoint = null;
            sessionId = null;
        }

        /// <summary>
        /// Adds a console logger. Can be omitted if internal SIPSorcery debug 
        /// and warning messages are not required.
        /// </summary>
        private static Microsoft.Extensions.Logging.ILogger AddConsoleLogger(
            LogEventLevel logLevel = LogEventLevel.Debug)
        {
            var serilogLogger = new LoggerConfiguration()
                .Enrich.FromLogContext()
                .MinimumLevel.Is(logLevel)
                .WriteTo.Console()
                .CreateLogger();
            var factory = new SerilogLoggerFactory(serilogLogger);
            SIPSorcery.LogFactory.Set(factory);
            return factory.CreateLogger<Program>();
        }

    }
}