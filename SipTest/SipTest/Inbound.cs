using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Serilog;
using Serilog.Extensions.Logging;
using Serilog.Events;
using SIPSorcery.SIP;
using SIPSorcery.SIP.App;
using SIPSorcery.Media;
using System.Net;
using SIPSorcery.Net;
using SIPSorceryMedia.Abstractions;

namespace SipTest
{
    class Inbound
    {
        private const string SERVER = "NOPE";
        private const string USER = "NOPE";
        private const string PASSWORD = "NOPE";
        private const int EXPIRY = 120;
        private static int SIP_LISTEN_PORT = 5060;


        private static Microsoft.Extensions.Logging.ILogger Log = NullLogger.Instance;

        static void _Main(string[] args)
        {
            Console.WriteLine("SIPSorcery registration user agent example.");
            Console.WriteLine("Press ctrl-c to exit.");

            Log = AddConsoleLogger(LogEventLevel.Verbose);

            Console.WriteLine("Attempting registration with:");
            Console.WriteLine($" server: {SERVER}");
            Console.WriteLine($" username: {USER}");
            Console.WriteLine($" expiry: {EXPIRY}");

            // Set up a default SIP transport.
            var sipTransport = new SIPTransport();
            sipTransport.EnableTraceLogs();

            // Create a client user agent to maintain a periodic registration with a SIP server.
            var regUserAgent = new SIPRegistrationUserAgent(sipTransport, USER, PASSWORD, SERVER, EXPIRY);


            SIPServerUserAgent? uas = null;
            CancellationTokenSource? rtpCts = null; // Cancellation token to stop the RTP stream.
            VoIPMediaSession? rtpSession = null;
            bool isRegistered = false;

            // Event handlers for the different stages of the registration.
            regUserAgent.RegistrationFailed += (uri, response, err) => Log.LogWarning($"{uri}: {err}");
            regUserAgent.RegistrationTemporaryFailure += (uri, response, msg) => Log.LogWarning($"{uri}: {msg}");
            regUserAgent.RegistrationRemoved += (uri, response) => Log.LogWarning($"{uri} registration failed.");
            regUserAgent.RegistrationSuccessful += (uri, response) =>
            {
                Log.LogInformation($"{uri} registration succeeded.");

                if (isRegistered)
                {
                    Log.LogWarning("!!!");
                    return;
                }
                isRegistered = true;

                sipTransport.AddSIPChannel(new SIPUDPChannel(new IPEndPoint(IPAddress.Any, SIP_LISTEN_PORT)));
                // sipTransport.AddSIPChannel(new SIPTCPChannel(new IPEndPoint(listenAddress, SIP_LISTEN_PORT

                // Because this is a server user agent the SIP transport must start listening for client user agents.
                sipTransport.SIPTransportRequestReceived += async (SIPEndPoint localSIPEndPoint, SIPEndPoint remoteEndPoint, SIPRequest sipRequest) => {
                    try
                    {
                        if (sipRequest.Method == SIPMethodsEnum.INVITE)
                        {
                            Log.LogInformation($"Incoming call request: {localSIPEndPoint}<-{remoteEndPoint} {sipRequest.URI}.");

                            // Check there's a codec we support in the INVITE offer.
                            var offerSdp = SDP.ParseSDPDescription(sipRequest.Body);
                            IPEndPoint dstRtpEndPoint = SDP.GetSDPRTPEndPoint(sipRequest.Body);

                            if (offerSdp.Media.Any(x => x.Media == SDPMediaTypesEnum.audio && x.MediaFormats.Any(x => x.Key == (int)SDPWellKnownMediaFormatsEnum.PCMU)))
                            {
                                Log.LogDebug($"Client offer contained PCMU audio codec.");

                                // var audioSession = new WindowsAudioEndPoint(new AudioEncoder());
                                // audioSession.RestrictFormats(x => x.Codec == AudioCodecsEnum.PCMU);
                                // rtpSession = new VoIPMediaSession(audioSession.ToMediaEndPoints());

                                AudioExtrasSource extrasSource = new AudioExtrasSource(new AudioEncoder(), new AudioSourceOptions { AudioSource = AudioSourcesEnum.Music });
                                rtpSession = new VoIPMediaSession(new MediaEndPoints { AudioSource = extrasSource });
                                rtpSession.AcceptRtpFromAny = true;

                                var setResult = rtpSession.SetRemoteDescription(SdpType.offer, offerSdp);

                                if (setResult != SetDescriptionResultEnum.OK)
                                {
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
                                    rtpCts?.Cancel();
                                    rtpCts = new CancellationTokenSource();

                                    UASInviteTransaction uasTransaction = new UASInviteTransaction(sipTransport, sipRequest, null);
                                    uas = new SIPServerUserAgent(sipTransport, null, uasTransaction, null);
                                    uas.CallCancelled += (uasAgent) =>
                                    {
                                        rtpCts?.Cancel();
                                        rtpSession.Close(null);
                                    };
                                    rtpSession.OnRtpClosed += (reason) => uas?.Hangup(false);
                                    uas.Progress(SIPResponseStatusCodesEnum.Trying, null, null, null, null);
                                    await Task.Delay(100);
                                    uas.Progress(SIPResponseStatusCodesEnum.Ringing, null, null, null, null);
                                    await Task.Delay(100);

                                    var answerSdp = rtpSession.CreateAnswer(null);
                                    uas.Answer(SDP.SDP_MIME_CONTENTTYPE, answerSdp.ToString(), null, SIPDialogueTransferModesEnum.NotAllowed);

                                    await rtpSession.Start();
                                }
                            }
                            else
                            {
                                Log.LogWarning("No offering!!");
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
                            rtpCts?.Cancel();
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



            };

            // Start the thread to perform the initial registration and then periodically resend it.
            regUserAgent.Start();

            ManualResetEvent exitMRE = new ManualResetEvent(false);


            // Task to handle user key presses.
            Task.Run(() =>
            {
                try
                {
                    while (!exitMRE.WaitOne(0))
                    {
                        var keyProps = Console.ReadKey();
                        if (keyProps.KeyChar == 'h')
                        {
                            Console.WriteLine();
                            Console.WriteLine("Hangup requested by user...");

                            Hangup(uas).Wait();
                            rtpSession?.Close(null);
                            rtpCts?.Cancel();
                        }

                        if (keyProps.KeyChar == 'q')
                        {
                            Log.LogInformation("Quitting...");

                            if (sipTransport != null)
                            {
                                Log.LogInformation("Shutting down SIP transport...");
                                sipTransport.Shutdown();
                            }

                            exitMRE.Set();
                        }
                    }
                }
                catch (Exception excp)
                {
                    Log.LogError($"Exception Key Press listener. {excp.Message}.");
                }
            });

            Console.CancelKeyPress += (object? sender, ConsoleCancelEventArgs e) =>
            {
                e.Cancel = true;
                Log.LogInformation("Exiting...");

                Hangup(uas).Wait();
                rtpSession?.Close(null);
                rtpCts?.Cancel();

                exitMRE.Set();
            };

            exitMRE.WaitOne();

            regUserAgent.Stop();

            // Allow for unregister request to be sent (REGISTER with 0 expiry)
            Task.Delay(1500).Wait();

            sipTransport.Shutdown();
        }


        /// <summary>
        /// Hangs up the current call.
        /// </summary>
        /// <param name="uas">The user agent server to hangup the call on.</param>
        private static async Task Hangup(SIPServerUserAgent? uas)
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
            return factory.CreateLogger<Inbound>();
        }
    }
}