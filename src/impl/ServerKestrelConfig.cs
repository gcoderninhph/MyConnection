namespace MyConnection;

public class ServerKestrelConfig : ServerConfig
{
    public string KestrelUrls { get; set; } = "http://0.0.0.0:9090";
}
