namespace MyConnection;

public class WarningInfo
{
    public string Code { get; }
    public string Message { get; }
    public Exception? Exception { get; }
    public WarningInfo(string code, string message, Exception? exception = null)
    {
        Code = code;
        Message = message;
        Exception = exception;
    }
}

public sealed class ServerWarningInfo : WarningInfo
{
    public IConnection? Connection { get; }
    public ServerWarningInfo(string code, string message, IConnection? connection = null, Exception? exception = null)
        : base(code, message, exception)
    {
        Connection = connection;
    }
}
