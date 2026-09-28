namespace FritzBoxSync;

public static class App
{
    internal sealed class Config
    {
        public string FritzBoxUrl { get; set; } = "http://192.168.178.1:49000";
        public string FritzBoxHttpsUrl { get; set; } = "https://192.168.178.1:49443";
        public string FritzBoxWebUrl { get; set; } = "http://192.168.178.1";
        public string FritzUsername { get; set; } = "TechnitiumSync";
        public string FritzPassword { get; set; } = "";
    }
}
