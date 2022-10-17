using Microsoft.CognitiveServices.Speech.Audio;
using Microsoft.CognitiveServices.Speech;

namespace SipTest
{
    internal class STT
    {
        static string speechKey = "NOPE";
        static string speechRegion = "NOPE";

        static void OutputSpeechSynthesisResult(SpeechSynthesisResult speechSynthesisResult, string? text)
        {
            switch (speechSynthesisResult.Reason)
            {
                case ResultReason.SynthesizingAudioCompleted:
                    Console.WriteLine($"Speech synthesized for text: [{text}]");
                    break;
                case ResultReason.Canceled:
                    var cancellation = SpeechSynthesisCancellationDetails.FromResult(speechSynthesisResult);
                    Console.WriteLine($"CANCELED: Reason={cancellation.Reason}");

                    if (cancellation.Reason == CancellationReason.Error)
                    {
                        Console.WriteLine($"CANCELED: ErrorCode={cancellation.ErrorCode}");
                        Console.WriteLine($"CANCELED: ErrorDetails=[{cancellation.ErrorDetails}]");
                        Console.WriteLine($"CANCELED: Did you set the speech resource key and region values?");
                    }
                    break;
                default:
                    break;
            }
        }
        async static Task _Main(string[] args)
        {
            var speechConfig = SpeechConfig.FromSubscription(speechKey, speechRegion);
            // speechConfig.SpeechRecognitionLanguage = "de-DE";
            speechConfig.EnableDictation();
            var stopRecognition = new TaskCompletionSource<int>();

            // about silence: https://learn.microsoft.com/en-us/azure/cognitive-services/speech-service/how-to-recognize-speech?pivots=programming-language-csharp#change-how-silence-is-handled

            // Note:
            // - up to 4 languages for at-start detection
            // - up to 10 languages for continous detection
            var autoDetectSourceLanguageConfig = AutoDetectSourceLanguageConfig.FromLanguages(new string[] { "en-US", "de-DE", "it-IT" });

            using var audioConfig = AudioConfig.FromDefaultMicrophoneInput();
            using var speechRecognizer = new SpeechRecognizer(speechConfig, autoDetectSourceLanguageConfig, audioConfig);

            speechRecognizer.Recognizing += (s, e) =>
            {
                var autoDetectSourceLanguageResult =
                    AutoDetectSourceLanguageResult.FromResult(e.Result);
                Console.WriteLine($"RECOGNIZING ({autoDetectSourceLanguageResult.Language}) {e.SessionId}: Text={e.Result.Text}");
            };

            speechRecognizer.Recognized += (s, e) =>
            {
                if (e.Result.Reason == ResultReason.RecognizedSpeech)
                {
                    var autoDetectSourceLanguageResult =
                        AutoDetectSourceLanguageResult.FromResult(e.Result);
                    Console.WriteLine($"RECOGNIZED ({autoDetectSourceLanguageResult.Language}) {e.SessionId}: Text={e.Result.Text}\n");
                }
                else if (e.Result.Reason == ResultReason.NoMatch)
                {
                    Console.WriteLine($"NOMATCH: Speech could not be recognized.\n");
                }
            };

            speechRecognizer.Canceled += (s, e) =>
            {
                Console.WriteLine($"CANCELED: Reason={e.Reason}");

                if (e.Reason == CancellationReason.Error)
                {
                    Console.WriteLine($"CANCELED: ErrorCode={e.ErrorCode}");
                    Console.WriteLine($"CANCELED: ErrorDetails={e.ErrorDetails}");
                    Console.WriteLine($"CANCELED: Did you set the speech resource key and region values?");
                }

                stopRecognition.TrySetResult(0);
            };

            speechRecognizer.SessionStopped += (s, e) =>
            {
                Console.WriteLine("\n    Session stopped event.");
                stopRecognition.TrySetResult(0);
            };

            // Ctrl-c will gracefully exit the call at any point.
            Console.CancelKeyPress += async delegate (object? sender, ConsoleCancelEventArgs e)
            {
                e.Cancel = true;
                await speechRecognizer.StopContinuousRecognitionAsync();
            };

            Console.WriteLine("Speak into your microphone.");
            await speechRecognizer.StartContinuousRecognitionAsync();

            // Waits for completion. Use Task.WaitAny to keep the task rooted.
            Task.WaitAny(new[] { stopRecognition.Task });

            // var speechRecognitionResult = await speechRecognizer.RecognizeOnceAsync();
            // OutputSpeechRecognitionResult(speechRecognitionResult);
        }
    }
}
