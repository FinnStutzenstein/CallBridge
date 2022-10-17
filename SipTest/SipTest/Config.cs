using System.Runtime.InteropServices;
using System.Text;

namespace SipTest
{
    internal class Config
    {
        public string SipServer { get; private set; }
        public string SipUsername { get; private set; }
        public string SipPassword { get; private set; }
        public int SipRegistrationExpiry { get; private set; }
        public int SipPort { get; private set; }
        public string SpeechKey { get; private set; }
        public string SpeechRegion { get; private set; }
        public bool BargeInOnDtmf { get; private set; }
        public bool BargeInOnSpeech { get; private set; }
        public string DialogBitsUrl { get; private set; }

#pragma warning disable CS8618
        private Config() {}
#pragma warning restore CS8618

        public static Config Create()
        {
            var path = new FileInfo("./settings.ini").FullName;
            return new Config() {
                SipServer= Read("sip", "server", path),
                SipUsername= Read("sip", "username", path),
                SipPassword = Read("sip", "password", path),
                SipRegistrationExpiry = ReadInt("sip", "registrationExpiry", path),
                SipPort = ReadInt("sip", "port", path),
                SpeechKey= Read("speech", "key", path),
                SpeechRegion = Read("speech", "region", path),
                BargeInOnDtmf= ReadBool("general", "bargeInOnDtmf", path),
                BargeInOnSpeech = ReadBool("general", "bargeInOnSpeech", path),
                DialogBitsUrl= Read("general", "dialogBitsUrl", path)
            };
        }

        [DllImport("kernel32", CharSet = CharSet.Unicode)]
        static extern int GetPrivateProfileString(string Section, string Key, string Default, StringBuilder RetVal, int Size, string FilePath);

        private static string Read(string section, string key, string path)
        {
            var RetVal = new StringBuilder(255);
            GetPrivateProfileString(section, key, "", RetVal, 255, path);
            var value = RetVal.ToString();
            if (value.Length == 0)
            {
                throw new Exception($"Missing config for ${key} in section ${section}");
            }
            return value;
        }

        private static int ReadInt(string section, string key, string path)
        {
            var value = Read(section, key, path);
            int result;
            if (!int.TryParse(value, out result))
            {
                throw new Exception($"Cconfig for ${key} in section ${section} is not a valid integer");
            }
            else
            {
                return result;
            }
        }

        private static bool ReadBool(string section, string key, string path)
        {
            var value = Read(section, key, path);
            return value == "true" || value == "t" || value == "1" || value == "on";
        }
    }
}
