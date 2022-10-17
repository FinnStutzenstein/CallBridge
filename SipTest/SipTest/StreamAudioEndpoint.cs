using Microsoft.CognitiveServices.Speech.Audio;
using NAudio.Wave;
using SIPSorcery.Media;
using SIPSorceryMedia.Abstractions;
using System.Net;
using SIPSorcery.net.RTP;

// out -> sink (speaker)
// source (microphone) -> in

namespace SipTest
{
    internal class StreamAudioEndpoint : IAudioSink, IAudioSource, IDisposable
    {
        public event EncodedSampleDelegate? OnAudioSourceEncodedSample;
        public event RawAudioSampleDelegate? OnAudioSourceRawSample;

        public event SourceErrorDelegate? OnAudioSinkError;
        public event SourceErrorDelegate? OnAudioSourceError;

        private PushAudioInputStream _inStream;

        private bool _paused = true;
        private bool _stopped = false;

        private AudioExtrasSource audioExtraSource = new AudioExtrasSource();

        private MediaFormatManager<AudioFormat> _audioFormatManager = new MediaFormatManager<AudioFormat>(
            new List<AudioFormat> { new AudioFormat(SDPWellKnownMediaFormatsEnum.PCMA), new AudioFormat(SDPWellKnownMediaFormatsEnum.PCMU) }
        );

        public StreamAudioEndpoint(PushAudioInputStream inStream)
        {
            _inStream = inStream;
            audioExtraSource.OnAudioSourceEncodedSample += (uint durationRtpUnits, byte[] sample) => this.OnAudioSourceEncodedSample?.Invoke(durationRtpUnits, sample);
        }

        public MediaEndPoints ToMediaEndPoints()
        {
            return new MediaEndPoints
            {
                AudioSink = this,
                AudioSource = this
            };
        }

        public void RestrictFormats(Func<AudioFormat, bool> filter) {
            _audioFormatManager.RestrictFormats(filter);
            audioExtraSource.RestrictFormats(filter);
        }

        public List<AudioFormat> GetAudioSourceFormats() => audioExtraSource.GetAudioSourceFormats();
        public List<AudioFormat> GetAudioSinkFormats() => _audioFormatManager.GetSourceFormats();
        public void SetAudioSourceFormat(AudioFormat audioFormat) => audioExtraSource.SetAudioSourceFormat(audioFormat);
        public void SetAudioSinkFormat(AudioFormat audioFormat) => _audioFormatManager.SetSelectedFormat(audioFormat);
        public void ExternalAudioSourceRawSample(AudioSamplingRatesEnum samplingRate, uint durationMilliseconds, short[] sample)
        {
            throw new NotImplementedException();
        }

        public void GotAudioRtp(IPEndPoint remoteEndPoint, uint ssrc, uint seqnum, uint timestamp, int payloadID, bool marker, byte[] payload)
        {
            if (_paused || _stopped)
            {
                Console.WriteLine("! GotAudioRtp {0} {1}", _paused, _stopped);
                return;
            }
            // Console.WriteLine("Got audio from client");

            Func<byte, short> encoder;
            if (_audioFormatManager.SelectedFormat.Codec == AudioCodecsEnum.PCMA )
            {
                encoder = ALawDecoder.ALawToLinearSample;
            } else if (_audioFormatManager.SelectedFormat.Codec == AudioCodecsEnum.PCMU)
            {
                encoder = MuLawDecoder.MuLawToLinearSample;
            } else
            {
                throw new Exception($"Unknown codec {_audioFormatManager.SelectedFormat.Codec}");
            }

            byte[] samples = payload.SelectMany(value =>
            {
                short pcm = encoder(value);
                return new byte[] { (byte)(pcm & 0xFF), (byte)(pcm >> 8) };
            }).ToArray();
            _inStream.Write(samples, samples.Length);
        }

        public async Task BargeIn()
        {
            await SendRawPCM(new byte[160]);
        }

        public async Task SendRawPCM(byte[] pcm)
        {
            if (_paused || _stopped)
            {
                Console.WriteLine("! SendRawPCM {0} {1}", _paused, _stopped);
                return;
            }

            Stream outStream = new MemoryStream(pcm);
            await audioExtraSource.SendAudioFromStream(outStream, AudioSamplingRatesEnum.Rate8KHz);
        }

        public bool HasEncodedAudioSubscribers() => OnAudioSourceEncodedSample != null;

        public Task StartAudio()
        {
            if (_stopped)
            {
                throw new Exception("Not allowed, already stopped");
            }

            if (_paused)
            {
                _paused = false;
                audioExtraSource.StartAudio();

                _ = Task.Run(async () =>
                {
                    await Task.Delay(50);
                    // Hack: We do not recieve something until we send things. Maybe a Nat-Pinhole problem.
                    // So send some silence (only zeroes). Why 160 Bytes? Otherwise the packet will not be flushed.
                    await SendRawPCM(new byte[160]);
                });
            }

            return Task.CompletedTask;
        }


        public Task PauseAudio()
        {
            _paused = true;
            audioExtraSource.PauseAudio();
            return Task.CompletedTask;
        }

        public Task ResumeAudio()
        {
            _paused = false;
            audioExtraSource.ResumeAudio();
            return Task.CompletedTask;
        }
        public bool IsAudioSourcePaused()
        {
            return _paused;
        }

        // The ...Sink methods are never called???
        public Task StartAudioSink()
        {
            throw new NotImplementedException();
        }
        public Task PauseAudioSink()
        {
            throw new NotImplementedException();
        }
        public Task ResumeAudioSink()
        {
            throw new NotImplementedException();
        }
        public Task CloseAudioSink()
        {
            throw new NotImplementedException();
        }

        public Task CloseAudio()
        {
            if (!_stopped)
            {
                _inStream.Close();
            }
            _stopped = true;
            audioExtraSource.CloseAudio();
            return Task.CompletedTask;
        }

        public void Dispose()
        {
            CloseAudio().Wait();
            // CloseAudioSink().Wait();
        }
    }
}
