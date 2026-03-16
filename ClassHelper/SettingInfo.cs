namespace NewSanofi.ClassHelper
{
    public class SettingInfo
    {
        public bool FullScreen { get; set; }
        public string IpAddress { get; set; }
        public int LogoutTime { get; set; }
        public string PlaybackFolderPath { get; set; }
        public bool RecordCheck { get; set; }
        public string RecordFolderPath { get; set; }
        public int RecordModeIndex { get; set; }
        public static SettingInfo SettingInstance { get; set; }
        public string Type { get; set; }
        public string UserCell { get; set; }
        public bool UserCellCheck { get; set; }
        public string WathCell { get; set; }
    }
}
