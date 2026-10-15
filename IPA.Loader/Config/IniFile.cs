using System;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace IPA.Config
{
    /// <summary>
    /// Create a New INI file to store or load data
    /// </summary>
    [Obsolete("Jesus, this uses old 16-bit system calls!")]
    internal class IniFile
    {
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [DllImport("KERNEL32.DLL", EntryPoint = "GetPrivateProfileStringW",
        SetLastError = true,
        CharSet = CharSet.Unicode, ExactSpelling = true,
        CallingConvention = CallingConvention.StdCall)]
        private static extern int GetPrivateProfileString(
          string lpSection,
          string lpKey,
          string lpDefault,
          StringBuilder lpReturnString,
          int nSize,
          string lpFileName);

        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [DllImport("KERNEL32.DLL", EntryPoint = "WritePrivateProfileStringW",
          SetLastError = true,
          CharSet = CharSet.Unicode, ExactSpelling = true,
          CallingConvention = CallingConvention.StdCall)]
        private static extern int WritePrivateProfileString(
          string lpSection,
          string lpKey,
          string lpValue,
          string lpFileName);

        /*private string _path = "";
        public string Path
        {
            get
            {
                return _path;
            }
            set
            {
                if (!File.Exists(value))
                    File.WriteAllText(value, "", Encoding.Unicode);
                _path = value;
            }
        }*/

        private FileInfo _iniFileInfo;
        public FileInfo IniFileInfo {
            get => _iniFileInfo;
            set {
                _iniFileInfo = value;
                if (_iniFileInfo.Exists) return;
                _iniFileInfo.Directory?.Create();
                _iniFileInfo.Create().Dispose();
            }
        }

        /// <summary>
        /// INIFile Constructor.
        /// </summary>
        /// <PARAM name="iniPath"></PARAM>
        public IniFile(string iniPath)
        {
            IniFileInfo = new FileInfo(iniPath);
            //this.Path = INIPath;
        }

        /// <summary>
        /// Write Data to the INI File
        /// </summary>
        /// <PARAM name="section"></PARAM>
        /// Section name
        /// <PARAM name="key"></PARAM>
        /// Key Name
        /// <PARAM name="value"></PARAM>
        /// Value Name
        public void IniWriteValue(string section, string key, string value)
        {
            if (WritePrivateProfileString(section, key, value, IniFileInfo.FullName) == 0
                && (section != null || key != null || value != null))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not write the legacy plugin INI file.");
        }

        /// <summary>
        /// Read Data Value From the Ini File
        /// </summary>
        /// <PARAM name="section"></PARAM>
        /// <PARAM name="key"></PARAM>
        /// <returns></returns>
        public string IniReadValue(string section, string key)
        {
            const int maxChars = 1023;
            StringBuilder result = new StringBuilder(maxChars);
            int length = GetPrivateProfileString(section, key, "", result, maxChars, IniFileInfo.FullName);
            if (length == 0) return string.Empty;
            return result.ToString();
        }
    }
}
